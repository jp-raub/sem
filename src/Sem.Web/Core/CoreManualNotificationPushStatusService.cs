using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Core;

public sealed record ManualNotificationPushDeliveryCounts(
    int Pending, int Claimed, int Accepted, int Unknown, int Expired, int Suppressed, int Failed);

// Deliberately aggregate-only: no audience identities, device bindings, or message content.
public sealed record ManualNotificationPushStatus(
    string RequestId, string EventId, int RecipientCount, long AcceptedAt, long? ExpiresAt,
    string AdmissionGate, int PreparedRecipients, int UnpreparedRecipients, int NoEligibleDevices,
    int OtherSuppressedPreparations, ManualNotificationPushDeliveryCounts PersistedDeliveryCounts);

public sealed partial class CoreFoundationService
{
    private static readonly string[] KnownPreparationOutcomes = [
        "Prepared", "NoEligibleDevices", "NotAdmitted", "AdmissionInvalid", "Disabled",
        "ActivationChanged", "ClockBeforeAcceptance", "Expired", "MissingAcceptedRecipient",
        "MissingInbox", "AccountUnavailable", "ViewingDenied", "NoLongerRegistered"
    ];
    private static readonly string[] KnownDeliveryStates = [
        "Pending", "Claimed", "Accepted", "Unknown", "Expired", "Suppressed", "Failed"
    ];

    /// <summary>Reads only the current actor's own accepted operation and its immutable recipient snapshot.</summary>
    public async Task<ManualNotificationPushStatus> ReadEventManualNotificationPushStatusAsync(
        ClaimsPrincipal principal, string eventId, string requestId, CancellationToken ct = default)
    {
        if (requestId is null || !Guid.TryParseExact(requestId, "D", out var parsed) ||
            !StringComparer.Ordinal.Equals(parsed.ToString("D"), requestId))
            throw new ValidationException("操作Idは小文字のD形式GUIDで指定してください。");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (currentEvent, actor, _, manager) = await ParticipantEvent(db, principal, eventId, ct);
        if (!manager) throw new UnauthorizedAccessException();

        // Exact author binding intentionally applies even to event managers and global admins.
        var operation = await db.Set<ManualNotificationOperation>().AsNoTracking()
            .Where(x => x.Id == requestId && x.EventId == currentEvent.Id && x.ActorId == actor.Id)
            .Select(x => new { x.Id, x.EventId, x.RecipientCount, x.AcceptedAt })
            .SingleOrDefaultAsync(ct);
        if (operation is null) throw new UnauthorizedAccessException();

        var recipients = db.Set<ManualNotificationRecipient>().AsNoTracking()
            .Where(x => x.OperationId == operation.Id && x.EventId == operation.EventId);
        var recipientRows = await recipients.CountAsync(ct);
        if (recipientRows != operation.RecipientCount)
            throw new InvalidOperationException("Accepted recipient snapshot is inconsistent.");

        var preparationRows = db.Set<ManualNotificationPushPreparation>().AsNoTracking()
            .Where(p => recipients.Any(r => r.Id == p.RecipientId));
        var preparationSummary = await preparationRows.GroupBy(_ => 1).Select(g => new {
            Total = g.Count(),
            Prepared = g.Count(p => p.Outcome == "Prepared"),
            NoEligibleDevices = g.Count(p => p.Outcome == "NoEligibleDevices"),
            Unknown = g.Count(p => !KnownPreparationOutcomes.Contains(p.Outcome))
        }).SingleOrDefaultAsync(ct);
        if (preparationSummary?.Unknown > 0)
            throw new InvalidOperationException("Unsupported persisted preparation outcome.");
        var preparationCount = preparationSummary?.Total ?? 0;
        var prepared = preparationSummary?.Prepared ?? 0;
        var noDevices = preparationSummary?.NoEligibleDevices ?? 0;
        var otherSuppressed = preparationCount - prepared - noDevices;
        var unprepared = recipientRows - preparationCount;
        if (otherSuppressed < 0 || unprepared < 0)
            throw new InvalidOperationException("Persisted preparation snapshot is inconsistent.");

        var deliveryRows = db.Set<ManualNotificationPushDelivery>().AsNoTracking()
            .Where(d => recipients.Any(r => r.Id == d.RecipientId));
        var deliverySummary = await deliveryRows.GroupBy(_ => 1).Select(g => new {
            Total = g.Count(),
            Pending = g.Count(d => d.State == "Pending"),
            Claimed = g.Count(d => d.State == "Claimed"),
            Accepted = g.Count(d => d.State == "Accepted"),
            Unknown = g.Count(d => d.State == "Unknown"),
            Expired = g.Count(d => d.State == "Expired"),
            Suppressed = g.Count(d => d.State == "Suppressed"),
            Failed = g.Count(d => d.State == "Failed"),
            Unsupported = g.Count(d => !KnownDeliveryStates.Contains(d.State))
        }).SingleOrDefaultAsync(ct);
        if (deliverySummary?.Unsupported > 0)
            throw new InvalidOperationException("Unsupported persisted delivery state.");
        var deliveries = new ManualNotificationPushDeliveryCounts(
            deliverySummary?.Pending ?? 0, deliverySummary?.Claimed ?? 0, deliverySummary?.Accepted ?? 0,
            deliverySummary?.Unknown ?? 0, deliverySummary?.Expired ?? 0, deliverySummary?.Suppressed ?? 0,
            deliverySummary?.Failed ?? 0);
        var deliveryTotal = deliveries.Pending + deliveries.Claimed + deliveries.Accepted + deliveries.Unknown +
            deliveries.Expired + deliveries.Suppressed + deliveries.Failed;
        if (deliveryTotal != (deliverySummary?.Total ?? 0))
            throw new InvalidOperationException("Persisted delivery snapshot is inconsistent.");

        var admission = await db.Set<ManualNotificationPushAdmission>().AsNoTracking()
            .Where(x => x.OperationId == operation.Id)
            .Select(x => new { x.Epoch, x.AcceptedAt, x.ExpiresAt })
            .SingleOrDefaultAsync(ct);
        string gate;
        long? expiresAt = admission?.ExpiresAt;
        if (admission is null) gate = "NotAdmitted";
        else if (admission.AcceptedAt != operation.AcceptedAt || admission.AcceptedAt < 0 ||
                 admission.AcceptedAt > long.MaxValue - ManualNotificationPushPolicy.LifetimeSeconds ||
                 admission.ExpiresAt != admission.AcceptedAt + ManualNotificationPushPolicy.LifetimeSeconds)
            gate = "Stopped"; // Fail closed for malformed persisted admission; expose no epoch or internal reason.
        else
        {
            var activation = await db.Set<ManualNotificationPushActivation>().AsNoTracking()
                .Where(x => x.Id == 1).Select(x => new { x.Enabled, x.Epoch }).SingleOrDefaultAsync(ct);
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            gate = activation is null || !activation.Enabled || activation.Epoch != admission.Epoch || now < admission.AcceptedAt
                ? "Stopped"
                : now >= admission.ExpiresAt ? "Expired" : "Eligible";
        }

        await tx.CommitAsync(ct);
        return new(operation.Id, operation.EventId, operation.RecipientCount, operation.AcceptedAt, expiresAt,
            gate, prepared, unprepared, noDevices, otherSuppressed, deliveries);
    }
}
