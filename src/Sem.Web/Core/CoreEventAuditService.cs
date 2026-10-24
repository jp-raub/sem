using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;

public sealed partial class CoreFoundationService
{
    // Only explicit ownership joins/known event-target actions, never a broad ID/prefix search.
    public async Task<AdminAuditPage> ReadEventAuditAsync(ClaimsPrincipal principal, string id, int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 200) throw new ValidationException("監査の取得件数は1〜200件で指定してください。");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Managed(db, principal, id, ct);
        string[] direct = ["SessionEventCreated", "SessionEventUpdated", "EventCapacityChanged", "EventThemePreferenceChanged", "SongPlanSaved", "EventQuestionPlanSaved", "TimetableSaved", "PhasePlanSaved", "PhaseAutoActivated", "EventPartDefinitionsSaved"];
        var comments = db.EventComments.Where(x => x.EventId == id).Select(x => x.Id);
        var deletedComments = db.CommentOperations.Where(x => x.EventId == id && x.Kind == "Delete").Select(x => x.CommentId);
        var registrations = db.RegistrationOperations.Where(x => x.EventId == id).Select(x => x.Id);
        var progress = db.ProgressOperations.Where(x => x.EventId == id).Select(x => x.Id);
        var shortening = db.ShorteningOperations.Where(x => x.EventId == id).Select(x => x.Id);
        var owned = db.Audits.AsNoTracking().Where(a =>
            direct.Contains(a.Action) && a.EntityId == id ||
            (a.Action == "CommentCreated" || a.Action == "CommentDeleted") && (comments.Contains(a.EntityId) || deletedComments.Contains(a.EntityId)) ||
            (a.Action == "ParticipationRegister" || a.Action == "ParticipationRelease" || a.Action == "ParticipationSpectate" || a.Action == "ParticipationRemoveSpectator" || a.Action == "ParticipationAnswers") && registrations.Contains(a.EntityId) ||
            (a.Action == "ProgressRecorded" || a.Action == "ProgressEstimateSaved") && progress.Contains(a.EntityId) ||
            (a.Action == "ShorteningPolicySaved" || a.Action == "ShorteningApproved") && shortening.Contains(a.EntityId) ||
            (a.Action == "EventPushRuleChanged" || a.Action == "PartStartPreferenceChanged" || a.Action == "EventQrIssued" || a.Action == "EventQrReissued") && a.EntityId.StartsWith(id + ":"));
        var rows = await (from audit in owned join user in db.Users.AsNoTracking() on audit.ActorId equals user.Id into actors
            from user in actors.DefaultIfEmpty() orderby audit.At descending, audit.Id descending
            select new { audit.Id, audit.At, audit.ActorId, Name = user == null ? null : user.DisplayName, audit.Action }).Take(limit + 1).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return new(rows.Take(limit).Select(x => new AdminAuditEntry(x.Id, x.At, x.Name ?? (x.ActorId == "system" ? "システム" : "削除済み・不明な操作者"), "このイベント", ActionLabel(x.Action), "このイベントへの操作。追加の詳細本文は記録なし。識別子・通知本文・認証情報は表示しません。")).ToArray(), rows.Length > limit);
    }
}
