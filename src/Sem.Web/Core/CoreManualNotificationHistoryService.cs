using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Core;

public sealed record ManualNotificationHistoryCursor(long AcceptedAt, string RequestId);
// Own-authored subject only; no body, recipient/device identities, hashes or delivery claims.
public sealed record ManualNotificationHistoryEntry(string RequestId, string Subject, int RecipientCount, long AcceptedAt);
public sealed record ManualNotificationHistoryPage(string EventId, ManualNotificationHistoryEntry[] Operations,
    ManualNotificationHistoryCursor? Next);

public sealed partial class CoreFoundationService
{
    public async Task<ManualNotificationHistoryPage> ReadEventManualNotificationHistoryAsync(
        ClaimsPrincipal principal, string eventId, ManualNotificationHistoryCursor? before = null,
        CancellationToken ct = default)
    {
        if (before is not null && (before.AcceptedAt < 0 || before.AcceptedAt > 253402300799L ||
            before.RequestId is null || !Guid.TryParseExact(before.RequestId, "D", out var id) ||
            !StringComparer.Ordinal.Equals(id.ToString("D"), before.RequestId)))
            throw new ValidationException("履歴の続きを確認してください。");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (currentEvent, actor, _, manager) = await ParticipantEvent(db, principal, eventId, ct);
        if (!manager) throw new UnauthorizedAccessException();
        var rows = db.Set<ManualNotificationOperation>().AsNoTracking()
            .Where(x => x.EventId == currentEvent.Id && x.ActorId == actor.Id);
        if (before is not null)
        {
            // A cursor is not authorization: require an exact, own-author/event anchor on every page.
            if (!await rows.AnyAsync(x => x.Id == before.RequestId && x.AcceptedAt == before.AcceptedAt, ct))
                throw new ValidationException("履歴の続きを確認してください。");
            rows = rows.Where(x => x.AcceptedAt < before.AcceptedAt ||
                x.AcceptedAt == before.AcceptedAt && string.Compare(x.Id, before.RequestId) < 0);
        }
        var results = await rows.OrderByDescending(x => x.AcceptedAt).ThenByDescending(x => x.Id)
            .Select(x => new ManualNotificationHistoryEntry(x.Id, x.Subject, x.RecipientCount, x.AcceptedAt))
            .Take(101).ToArrayAsync(ct);
        var visible = results.Take(100).ToArray();
        var next = results.Length > 100
            ? new ManualNotificationHistoryCursor(visible[^1].AcceptedAt, visible[^1].RequestId) : null;
        await tx.CommitAsync(ct);
        return new(currentEvent.Id, visible, next);
    }
}
