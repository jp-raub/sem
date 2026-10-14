using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
using WebPush;
namespace Sem.Web.Core;

public sealed record ManualNotificationPushDispatchResult(string State);
// Infrastructure, intentionally NOT registered in DI/HTTP/background services.
// Actual transport is injectable; verification only uses synthetic spies/handlers.
public sealed class ManualNotificationPushDispatcher(IDbContextFactory<ApplicationDbContext> factory,
    ManualNotificationPushQueue queue, IEventPushTransport transport, TimeProvider clock)
{
    private sealed record Snapshot(PushDevice Device, EventPushPayload Payload, long LeaseUntil);
    public async Task<ManualNotificationPushDispatchResult> DispatchOneAsync(string deliveryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!transport.Configured) return new("TransportUnavailable");
        var claim = await queue.ClaimAsync(deliveryId, ct);
        if (claim is null) return new("NotClaimed");
        var invoked = false;
        try {
            var snapshot = await PrepareAsync(claim, ct);
            if (snapshot is null) {
                // Persist final current-policy rejection (or fence an elapsed lease).
                await queue.RecheckAsync(deliveryId, claim.Token, ct);
                await FinishAsync(claim, ManualNotificationPushCompletion.FinalGateRejected);
                return new("SnapshotRejected");
            }
            if (!transport.Configured) {
                await FinishAsync(claim, ManualNotificationPushCompletion.RetryableNotAccepted);
                return new("DeferredBeforeSend");
            }
            // Last authorization/binding/registration/epoch/expiry/owner check before I/O.
            if (!await queue.RecheckAsync(deliveryId, claim.Token, ct)) return new("FinalGateRejected");
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var remaining = Math.Min(snapshot.Payload.ExpiresAt, snapshot.LeaseUntil * 1000) - now;
            var ttl = (int)Math.Min(ManualNotificationPushPolicy.LifetimeSeconds, (snapshot.Payload.ExpiresAt - now) / 1000);
            if (remaining <= 0 || ttl < 1) {
                await FinishAsync(claim, ManualNotificationPushCompletion.ExpiredBeforeSend);
                return new("TooLateToSend");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(15000, remaining)));
            invoked = true;
            await transport.Send(snapshot.Device, snapshot.Payload, ttl, timeout.Token);
            return new(await FinishAsync(claim, ManualNotificationPushCompletion.ProviderAccepted) ? "ProviderAccepted" : "OutcomeNotRecorded");
        } catch (WebPushException ex) when ((int)ex.StatusCode is 404 or 410) {
            // Delete only the still-identical subscription. Never remove a replacement.
            await RemoveUnchangedBindingAsync(claim);
            return new(await FinishAsync(claim, ManualNotificationPushCompletion.SubscriptionGone) ? "SubscriptionGone" : "OutcomeNotRecorded");
        } catch (WebPushException ex) when ((int)ex.StatusCode is 400 or 401 or 403 or 413) {
            return new(await FinishAsync(claim, ManualNotificationPushCompletion.PermanentFailure) ? "PermanentFailure" : "OutcomeNotRecorded");
        } catch (WebPushException ex) when ((int)ex.StatusCode == 429) {
            // Explicit provider rate-limit rejection establishes nonacceptance, unlike a lost response.
            return new(await FinishAsync(claim, ManualNotificationPushCompletion.RetryableNotAccepted, RetryNotBefore(ex)) ? "RateLimited" : "OutcomeNotRecorded");
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            await FinishAsync(claim, invoked ? ManualNotificationPushCompletion.AcceptanceUnknown : ManualNotificationPushCompletion.RetryableNotAccepted);
            throw;
        } catch (Exception) {
            // A timeout, network failure, 5xx or unexpected failure after invocation
            // cannot establish nonacceptance. Do not retry merely because a task threw.
            var completion = invoked ? ManualNotificationPushCompletion.AcceptanceUnknown : ManualNotificationPushCompletion.RetryableNotAccepted;
            return new(await FinishAsync(claim, completion) ? (invoked ? "AcceptanceUnknown" : "DeferredBeforeSend") : "OutcomeNotRecorded");
        }
    }
    private async Task<Snapshot?> PrepareAsync(ManualNotificationPushClaim claim, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<ManualNotificationPushDelivery>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == claim.DeliveryId, ct);
        var now = clock.GetUtcNow();
        if (row is null || row.State != "Claimed" || row.Claim != claim.Token || row.LeaseUntil <= now.ToUnixTimeSeconds()) return null;
        var policy = await ManualNotificationPushPolicy.ReadInTransactionAsync(db, now, row.RecipientId, row.DeviceId, row.BindingFingerprint, ct);
        if (policy.State != "EligibleForStaging" || policy.ExpiresAt != row.ExpiresAt) return null;
        var recipient = await db.Set<ManualNotificationRecipient>().AsNoTracking().SingleAsync(x => x.Id == row.RecipientId, ct);
        var subject = await db.Set<ManualNotificationOperation>().AsNoTracking().Where(x => x.Id == recipient.OperationId)
            .Select(x => x.Subject).SingleAsync(ct); // never load private multiline body for Push
        var device = await db.PushDevices.AsNoTracking().SingleAsync(x => x.Id == row.DeviceId && x.UserId == recipient.UserId, ct);
        if (PushBinding.Fingerprint(device) != row.BindingFingerprint) return null;
        await tx.CommitAsync(ct);
        return new(device, new(subject, "新しいメッセージがあります", "/?messages=1", recipient.Id, checked(row.ExpiresAt * 1000)), row.LeaseUntil!.Value);
    }
    private long? RetryNotBefore(WebPushException ex)
    {
        // Never persist/log provider headers. Missing, malformed or multiple values use bounded local backoff.
        if (!ex.Headers.TryGetValues("Retry-After", out var raw)) return null;
        var values = raw.Take(2).ToArray();
        if (values.Length != 1 || values[0].Length > 128 || !RetryConditionHeaderValue.TryParse(values[0], out var retry)) return null;
        if (retry.Date is { } date) return Math.Max(0, date.ToUnixTimeSeconds());
        if (retry.Delta is not { } delta || delta < TimeSpan.Zero) return null;
        // Any wait above the original maximum600s is necessarily past this job's deadline.
        var wait = (long)Math.Min(601, Math.Ceiling(delta.TotalSeconds));
        return (clock.GetUtcNow().ToUnixTimeMilliseconds() + wait * 1000 + 999) / 1000;
    }
    private async Task<bool> FinishAsync(ManualNotificationPushClaim claim, ManualNotificationPushCompletion result, long? notBeforeUnixSeconds = null)
    {
        // Save outcome even if the request cancellation fired after external I/O.
        // If persistence fails, propagate: lease expiry will become Unknown, not resend.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await queue.CompleteAsync(claim.DeliveryId, claim.Token, result, cleanup.Token, notBeforeUnixSeconds);
    }
    private async Task RemoveUnchangedBindingAsync(ManualNotificationPushClaim claim)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));var ct = cleanup.Token;
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<ManualNotificationPushDelivery>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == claim.DeliveryId && x.State == "Claimed" && x.Claim == claim.Token, ct);
        if (row is null) return;
        var d = await db.PushDevices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.DeviceId, ct);
        if (d is not null && PushBinding.Fingerprint(d) == row.BindingFingerprint)
            await db.PushDevices.Where(x => x.Id == d.Id && x.UserId == d.UserId && x.Endpoint == d.Endpoint && x.P256dh == d.P256dh && x.Auth == d.Auth && x.SecurityStamp == d.SecurityStamp && x.SessionHash == d.SessionHash).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }
}
