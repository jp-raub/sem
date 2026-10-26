using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sem.Authentication;
using Sem.Web.Core;
using Sem.Web.Data;
namespace Sem.Web.Runtime;

// 一覧専用の最小DTO。本文、メール、他人の割当、管理者ID、非公開設問、鍵を投影しない。
public sealed record EventListItem(string Id, string Title, string Location, string ScheduledDate,
    string PhaseKind, string PhaseName, int MaxPicks, string AnnouncementMessage,
    int FreeSlots, int TotalSlots, int OwnSlots);
public sealed record EventListView(string SiteName, EventListItem[] Events);
public static class EventListReader
{
    internal static DateTimeOffset? Instant(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,7})?)?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
    // cacheのPhaseKindを信頼しない。worker未接続でも読取り時点の有効期間で判定し、DBは変更しない。
    public static EventPhase? Effective(SessionEvent e, DateTimeOffset now)
    {
        var phases = e.Phases.Where(p => p.DeletedAt is null).ToArray();
        if (phases.Count(p => p.Kind == "Hidden") != 1 || phases.Count(p => p.Kind == "View") != 1 ||
            phases.Any(p => p.EventId != e.Id || p.Kind is not ("Hidden" or "View" or "Custom") || p.MaxPicks < -1 ||
                p.Kind != "Custom" && p.MaxPicks != 0 ||
                p.ActivateAt is not null && Instant(p.ActivateAt) is null || p.DeactivateAt is not null && Instant(p.DeactivateAt) is null ||
                p.ActivateAt is not null && p.DeactivateAt is not null && Instant(p.DeactivateAt) <= Instant(p.ActivateAt))) return null;
        if (e.ManualPhaseOverride) return phases.Count(p => p.IsActive) == 1 && !e.Phases.Any(p => p.DeletedAt is not null && p.IsActive) ? phases.Single(p => p.IsActive) : null;
        var start = Instant(e.ScheduledDate); if (start is null) return null;
        return phases.Where(p => p.Kind == "Custom" && Instant(p.ActivateAt) is { } at && at <= now &&
                (p.DeactivateAt is null || Instant(p.DeactivateAt) > now))
            .OrderByDescending(p => Instant(p.ActivateAt)).ThenBy(p => p.SortOrder).ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault()
            ?? phases.Single(p => p.Kind == (now >= start ? "View" : "Hidden"));
    }
    public static async Task<EventListView?> ReadAsync(ApplicationDbContext db, ClaimsPrincipal principal, IdentityOptions identity, DateTimeOffset now, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        try {
            // deferred read transactionで認証・現ロール・全列・枠数を同じsnapshotから取得する。書込みleaseを要求しない。
            using var snapshot = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var transaction = await db.Database.UseTransactionAsync(snapshot, ct);
            if (!await AuthenticationSession.ValidAsync(db, principal, identity, ct)) return null;
            var id = AuthenticationSession.SingleClaim(principal, identity.ClaimsIdentity.UserIdClaimType)!;
            var admin = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.IsAdmin).SingleAsync(ct);
            var events = await db.SessionEvents.AsNoTracking().Include(e => e.Phases).Include(e => e.Admins).ToListAsync(ct);
            var visible = events.Select(e => (Event: e, Phase: Effective(e, now)))
                .Where(x => x.Phase is not null && Instant(x.Event.ScheduledDate) is not null &&
                    (admin || x.Phase.Kind != "Hidden" || x.Event.Admins.Any(a => a.UserId == id))).ToArray();
            var ids = visible.Select(x => x.Event.Id).ToArray();
            var slots = await db.SongParts.AsNoTracking().Where(p => ids.Contains(p.EventId) &&
                db.Songs.Any(s => s.Id == p.SongId && s.EventId == p.EventId && s.DeletedAt == null) &&
                db.EventPartDefinitions.Any(d => d.Id == p.PartDefinitionId && d.EventId == p.EventId && d.DeletedAt == null) &&
                db.TimeParts.Any(t => t.EventId == p.EventId && t.SongId == p.SongId && t.Kind == "Song" && t.DeletedAt == null))
                .GroupBy(p => p.EventId).Select(g => new { Id = g.Key, Total = g.Count(), Free = g.Count(p => p.AssignedUserId == null), Own = g.Count(p => p.AssignedUserId == id) }).ToDictionaryAsync(x => x.Id, ct);
            var name = await db.ServiceSettings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.Name).SingleOrDefaultAsync(ct)
                ?? await db.SiteStates.AsNoTracking().Where(s => s.Id == 1).Select(s => s.Name).SingleOrDefaultAsync(ct) ?? "SESSION ROOM";
            var result = new EventListView(name, visible.OrderByDescending(x => Instant(x.Event.ScheduledDate)).ThenBy(x => x.Event.Id, StringComparer.Ordinal)
                .Select(x => { var n = slots.GetValueOrDefault(x.Event.Id); return new EventListItem(x.Event.Id, x.Event.Title, x.Event.Location,
                    Instant(x.Event.ScheduledDate)!.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), x.Phase!.Kind,
                    x.Phase.Kind == "Hidden" ? "非公開" : x.Phase.Kind == "View" ? "閲覧のみ" : x.Phase.Name,
                    x.Phase.MaxPicks, x.Event.AnnouncementMessage, n?.Free ?? 0, n?.Total ?? 0, n?.Own ?? 0); }).ToArray());
            await snapshot.CommitAsync(ct); return result;
        } finally { await db.Database.CloseConnectionAsync(); }
    }
}
