using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Core;

// No production writer, DI, endpoint or dispatcher is connected in this foundation.
// Missing state is OFF. A future audited activation transition MUST use a fresh,
// monotonically increasing Epoch, including when re-enabling after a disable.
public sealed class ManualNotificationPushActivation
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public long Epoch { get; set; } = 1;
}
// Created ONLY in the original acceptance transaction, never during replay/backfill.
// No timestamp cutoff: even older accepts within the same second stay excluded.
public sealed class ManualNotificationPushAdmission
{
    public string OperationId { get; set; } = "";
    public long Epoch { get; set; }
    public long AcceptedAt { get; set; }
    public long ExpiresAt { get; set; }
}
public sealed record ManualNotificationPushPolicyResult(string State, long? ExpiresAt = null)
{
    // Observation for future staging, NEVER a transport authorization.
    public bool DispatchConnected => false;
}
public sealed class ManualNotificationPushPolicy(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock)
{
    public const int LifetimeSeconds = 600;
    public async Task<ManualNotificationPushPolicyResult> ReadAsync(string recipientId, string deviceId,
        string expectedBindingFingerprint, CancellationToken ct = default)
    {
        if (!ManualNotificationDevicePreflight.ValidInput(recipientId, deviceId, expectedBindingFingerprint))
            return new("InvalidInput");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await ReadInTransactionAsync(db, clock.GetUtcNow(), recipientId, deviceId, expectedBindingFingerprint, ct);
        await tx.CommitAsync(ct);
        return result;
    }
    internal static async Task<ManualNotificationPushPolicyResult> ReadInTransactionAsync(ApplicationDbContext db,
        DateTimeOffset now, string recipientId, string deviceId, string expectedBindingFingerprint, CancellationToken ct)
    {
        var policy = await ReadRecipientInTransactionAsync(db, now, recipientId, ct);
        if (policy.State != "EligibleForStaging") return policy;
        var facts = await ManualNotificationDevicePreflight.ReadInTransactionAsync(db, now, recipientId,
            deviceId, expectedBindingFingerprint, ct);
        return facts.State == "PolicyPending" ? policy : new(facts.State);
    }
    internal static async Task<ManualNotificationPushPolicyResult> ReadRecipientInTransactionAsync(ApplicationDbContext db,
        DateTimeOffset now, string recipientId, CancellationToken ct)
    {
        var recipient = await db.Set<ManualNotificationRecipient>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == recipientId, ct);
        if (recipient is null) return new("MissingAcceptedRecipient");
        var admission = await db.Set<ManualNotificationPushAdmission>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == recipient.OperationId, ct);
        if (admission is null) return new("NotAdmitted");
        var operation = await db.Set<ManualNotificationOperation>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == recipient.OperationId && x.EventId == recipient.EventId, ct);
        if (operation is null || operation.AcceptedAt != admission.AcceptedAt ||
            admission.AcceptedAt < 0 || admission.AcceptedAt > long.MaxValue - LifetimeSeconds ||
            admission.ExpiresAt != admission.AcceptedAt + LifetimeSeconds)
            return new("AdmissionInvalid");
        var activation = await db.Set<ManualNotificationPushActivation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (activation is null || !activation.Enabled) return new("Disabled");
        if (activation.Epoch != admission.Epoch) return new("ActivationChanged");
        // At the exact deadline no further Push may be sent. Clock rollback fails closed.
        if (now.ToUnixTimeSeconds() < admission.AcceptedAt) return new("ClockBeforeAcceptance");
        if (now.ToUnixTimeSeconds() >= admission.ExpiresAt) return new("Expired", admission.ExpiresAt);
        var (facts, _) = await ManualNotificationDevicePreflight.ReadRecipientInTransactionAsync(db, now, recipientId, ct);
        if (facts.State != "PolicyPending") return new(facts.State);
        if (facts.CurrentlyRegistered != true) return new("NoLongerRegistered");
        return new("EligibleForStaging", admission.ExpiresAt);
    }
}
