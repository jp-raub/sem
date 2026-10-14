using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;

// Preparations pin the accepted audience's device set once. No worker is connected.
public sealed class ManualNotificationPushPreparation
{
    public string RecipientId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public long PreparedAt { get; set; }
    public int DeviceCount { get; set; }
}
public sealed class ManualNotificationPushDelivery
{
    public string Id { get; set; } = "";
    public string RecipientId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    // A digest only: never copy endpoint, subscription keys, stamp or session to the queue.
    public string BindingFingerprint { get; set; } = "";
    public long ExpiresAt { get; set; }
    public long DueAt { get; set; }
    public string State { get; set; } = "Pending";
    public string Reason { get; set; } = "Prepared";
    public int Attempts { get; set; }
    public string? Claim { get; set; }
    public long? LeaseUntil { get; set; }
    public long? ProviderAcceptedAt { get; set; }
}
public sealed record ManualNotificationPushPreparationResult(string State, int DeviceCount = 0, bool Replayed = false);
public sealed record ManualNotificationPushClaim(string DeliveryId, string Token, long LeaseUntil, int Attempt);
public enum ManualNotificationPushCompletion { ProviderAccepted, RetryableNotAccepted, AcceptanceUnknown, SubscriptionGone, PermanentFailure, FinalGateRejected, ExpiredBeforeSend }

// Infrastructure only: no DI, HTTP, scanning worker or transport invocation.
public sealed class ManualNotificationPushQueue(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock)
{
    public const int MaxAttempts = 5;
    public const int LeaseSeconds = 30;
    private static bool HashId(string id) => id is not null && id.Length == 64 && id.All(char.IsAsciiHexDigit);
    public async Task<ManualNotificationPushPreparationResult> PrepareAsync(string recipientId, CancellationToken ct = default)
    {
        if (!HashId(recipientId)) return new("InvalidInput");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var previous = await db.Set<ManualNotificationPushPreparation>().AsNoTracking().SingleOrDefaultAsync(x => x.RecipientId == recipientId, ct);
        if (previous is not null) return new(previous.Outcome, previous.DeviceCount, true);
        var recipient = await db.Set<ManualNotificationRecipient>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == recipientId, ct);
        if (recipient is null) return new("MissingAcceptedRecipient");
        var now = clock.GetUtcNow();
        var policy = await ManualNotificationPushPolicy.ReadRecipientInTransactionAsync(db, now, recipientId, ct);
        var outcome = policy.State;
        var count = 0;
        if (outcome == "EligibleForStaging") {
            var devices = await db.PushDevices.AsNoTracking().Where(x => x.UserId == recipient.UserId).OrderBy(x => x.Id).ToArrayAsync(ct);
            foreach (var device in devices) {
                var binding = PushBinding.Fingerprint(device);
                var gate = await ManualNotificationPushPolicy.ReadInTransactionAsync(db, now, recipientId, device.Id, binding, ct);
                if (gate.State != "EligibleForStaging") continue;
                db.Add(new ManualNotificationPushDelivery {
                    Id = SemEndpoints.Hash(JsonSerializer.Serialize(new[] { "manual-push-device-v1", recipientId, device.Id })),
                    RecipientId = recipientId, DeviceId = device.Id, BindingFingerprint = binding,
                    ExpiresAt = gate.ExpiresAt!.Value, DueAt = now.ToUnixTimeSeconds()
                });
                count++;
            }
            outcome = count == 0 ? "NoEligibleDevices" : "Prepared";
        }
        // Terminal preparation even when rejected/empty: a later join/device/activation
        // never expands or resurrects this original accepted notification.
        db.Add(new ManualNotificationPushPreparation {
            RecipientId = recipientId, Outcome = outcome, DeviceCount = count, PreparedAt = now.ToUnixTimeSeconds()
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(outcome, count);
    }
    // Bounded housekeeping must work even when the transport is unconfigured.
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var rows = await db.Set<ManualNotificationPushDelivery>()
            .Where(x => x.State == "Pending" && x.ExpiresAt <= now || x.State == "Claimed" && x.LeaseUntil <= now)
            .OrderBy(x => x.DueAt).ThenBy(x => x.Id).Take(20).ToArrayAsync(ct);
        foreach (var row in rows)
            End(row, row.State == "Claimed" ? "Unknown" : "Expired", row.State == "Claimed" ? "LeaseElapsed" : "Expired");
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return rows.Length;
    }
    public async Task<ManualNotificationPushClaim?> ClaimAsync(string deliveryId, CancellationToken ct = default)
    {
        if (!HashId(deliveryId)) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<ManualNotificationPushDelivery>().SingleOrDefaultAsync(x => x.Id == deliveryId, ct);
        if (row is null) return null;
        var now = clock.GetUtcNow();var seconds = now.ToUnixTimeSeconds();
        if (row.State == "Claimed" && row.LeaseUntil <= seconds) {
            // A crashed worker might already have sent. NEVER automatically re-send.
            End(row, "Unknown", "LeaseElapsed");
        } else if (row.State == "Pending" && row.DueAt <= seconds) {
            var policy = await ManualNotificationPushPolicy.ReadInTransactionAsync(db, now, row.RecipientId, row.DeviceId, row.BindingFingerprint, ct);
            if (policy.State != "EligibleForStaging" || policy.ExpiresAt != row.ExpiresAt) {
                End(row, policy.State == "Expired" ? "Expired" : "Suppressed", policy.State == "EligibleForStaging" ? "DeadlineChanged" : policy.State);
            } else if (row.Attempts >= MaxAttempts) {
                End(row, "Failed", "RetryLimit");
            } else {
                row.State = "Claimed";row.Reason = "Claimed";row.Claim = Guid.NewGuid().ToString("N");
                row.LeaseUntil = Math.Min(seconds + LeaseSeconds, row.ExpiresAt);row.Attempts++;
                await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
                return new(row.Id, row.Claim, row.LeaseUntil.Value, row.Attempts);
            }
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        return null;
    }
    // Future transport must call this immediately before external I/O. A claim alone
    // isn't sufficient. Rejected work is terminally suppressed. This cannot close the external-send race.
    public async Task<bool> RecheckAsync(string deliveryId, string token, CancellationToken ct = default)
    {
        if (!HashId(deliveryId) || !Guid.TryParseExact(token, "N", out _)) return false;
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<ManualNotificationPushDelivery>().SingleOrDefaultAsync(x => x.Id == deliveryId, ct);
        var now = clock.GetUtcNow();
        if (row is null || row.State != "Claimed" || row.Claim != token) return false;
        if (row.LeaseUntil <= now.ToUnixTimeSeconds()) {
            End(row, "Unknown", "LeaseElapsed");
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return false;
        }
        var policy = await ManualNotificationPushPolicy.ReadInTransactionAsync(db, now, row.RecipientId, row.DeviceId, row.BindingFingerprint, ct);
        var allowed = policy.State == "EligibleForStaging" && policy.ExpiresAt == row.ExpiresAt;
        if (!allowed) {
            End(row, policy.State == "Expired" ? "Expired" : "Suppressed", policy.State == "EligibleForStaging" ? "DeadlineChanged" : policy.State);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return allowed;
    }
    public async Task<bool> CompleteAsync(string deliveryId, string token, ManualNotificationPushCompletion completion, CancellationToken ct = default, long? notBeforeUnixSeconds = null)
    {
        if (!HashId(deliveryId) || !Guid.TryParseExact(token, "N", out _) || !Enum.IsDefined(completion)) return false;
        if (notBeforeUnixSeconds is < 0 || notBeforeUnixSeconds.HasValue && completion != ManualNotificationPushCompletion.RetryableNotAccepted)
            throw new ArgumentOutOfRangeException(nameof(notBeforeUnixSeconds));
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<ManualNotificationPushDelivery>().SingleOrDefaultAsync(x => x.Id == deliveryId, ct);
        if (row is null || row.State != "Claimed" || row.Claim != token) return false;
        var seconds = clock.GetUtcNow().ToUnixTimeSeconds();
        switch (completion) {
            case ManualNotificationPushCompletion.ProviderAccepted:
                End(row, "Accepted", "ProviderAccepted");row.ProviderAcceptedAt = seconds;break;
            case ManualNotificationPushCompletion.AcceptanceUnknown:
                End(row, "Unknown", "AcceptanceUnknown");break;
            case ManualNotificationPushCompletion.SubscriptionGone:
                End(row, "Suppressed", "SubscriptionGone");break;
            case ManualNotificationPushCompletion.PermanentFailure:
                End(row, "Failed", "PermanentFailure");break;
            case ManualNotificationPushCompletion.FinalGateRejected:
                End(row, "Suppressed", "FinalSnapshotRejected");break;
            case ManualNotificationPushCompletion.ExpiredBeforeSend:
                End(row, "Expired", "TooLateToSend");break;
            case ManualNotificationPushCompletion.RetryableNotAccepted:
                var due = Math.Max(seconds + (15L << Math.Min(row.Attempts - 1, 4)), notBeforeUnixSeconds ?? 0);
                if (row.Attempts >= MaxAttempts) End(row, "Failed", "RetryLimit");
                else if (due >= row.ExpiresAt) End(row, "Expired", "RetryPastDeadline");
                else if (row.LeaseUntil <= seconds) End(row, "Unknown", "LeaseElapsed");
                else { End(row, "Pending", "RetryableNotAccepted");row.DueAt = due; }
                break;
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;
    }
    private static void End(ManualNotificationPushDelivery row, string state, string reason)
    {
        row.State = state;row.Reason = reason;row.Claim = null;row.LeaseUntil = null;
    }
}
