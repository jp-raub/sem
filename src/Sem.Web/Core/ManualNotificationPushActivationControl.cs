using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;

public sealed class ManualNotificationPushActivationOperation
{
    public string Id { get; set; } = "";
    public string ActorId { get; set; } = "";
    public bool Enabled { get; set; }
    public long ExpectedEpoch { get; set; }
    public long ResultEpoch { get; set; }
    public bool Changed { get; set; }
    public long At { get; set; }
}
public sealed record ManualNotificationPushActivationView(bool Enabled, long Epoch);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManualNotificationPushActivationEdit(string RequestId, bool Enabled, long ExpectedEpoch, bool Confirmed);
public sealed record ManualNotificationPushActivationResult(string RequestId, bool Enabled, long Epoch, bool Changed, bool Replayed = false);

public sealed partial class CoreFoundationService
{
    // Current-cookie operational routes add Origin/CSRF/no-store and frozen actor binding.
    // Startup/migrations never enable activation; no manual Push worker is registered here.
    public async Task<ManualNotificationPushActivationView> ReadManualPushActivationAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = await Actor(db, principal, ct);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException();
        var state = await db.Set<ManualNotificationPushActivation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
        await tx.CommitAsync(ct);
        return new(state?.Enabled ?? false, state?.Epoch ?? 0);
    }
    public async Task<ManualNotificationPushActivationResult> SetManualPushActivationAsync(ClaimsPrincipal principal,
        ManualNotificationPushActivationEdit edit, CancellationToken ct = default)
    {
        if (!edit.Confirmed || edit.ExpectedEpoch < 0 || !Guid.TryParseExact(edit.RequestId, "D", out var id) || id.ToString("D") != edit.RequestId)
            throw new ValidationException("有効化方針・現在の版・操作Idを確認してください。");
        await using var db = await factory.CreateDbContextAsync(ct);
        // SQLite's non-deferred transaction serializes activation with original acceptance.
        // No timestamp comparison or backfill is used as an ordering surrogate.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = await Actor(db, principal, ct);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException();
        var previous = await db.Set<ManualNotificationPushActivationOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == edit.RequestId, ct);
        if (previous is not null) {
            if (previous.ActorId != actor.Id || previous.Enabled != edit.Enabled || previous.ExpectedEpoch != edit.ExpectedEpoch)
                throw new ValidationException("操作Idは別の有効化操作に再利用できません。");
            await tx.CommitAsync(ct);
            // Replay returns original result, not a new activation or current state.
            return new(previous.Id, previous.Enabled, previous.ResultEpoch, previous.Changed, true);
        }
        var state = await db.Set<ManualNotificationPushActivation>().SingleOrDefaultAsync(x => x.Id == 1, ct);
        var epoch = state?.Epoch ?? 0;var enabled = state?.Enabled ?? false;
        if (epoch != edit.ExpectedEpoch) throw new CoreConflictException();
        var changed = enabled != edit.Enabled;
        if (changed) {
            if (epoch == long.MaxValue) throw new ValidationException("有効化世代の上限です。");
            epoch++;
            if (state is null) { state = new();db.Add(state); }
            state.Epoch = epoch;state.Enabled = edit.Enabled;
        }
        db.Add(new ManualNotificationPushActivationOperation {
            Id = edit.RequestId, ActorId = actor.Id, Enabled = edit.Enabled, ExpectedEpoch = edit.ExpectedEpoch,
            ResultEpoch = epoch, Changed = changed, At = clock.GetUtcNow().ToUnixTimeSeconds()
        });
        Audit(db, actor.Id, changed ? "ManualPushActivationChanged" : "ManualPushActivationConfirmed", edit.RequestId);
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        return new(edit.RequestId, edit.Enabled, epoch, changed);
    }
}
