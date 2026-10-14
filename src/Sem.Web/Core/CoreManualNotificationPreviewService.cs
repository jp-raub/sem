using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;

public sealed record ManualNotificationDraft(string Subject, string Body);
public sealed record ManualNotificationPreview(string EventId, string EventTitle, long EventVersion,
    string Subject, string Body, int RecipientCount, string Fingerprint,
    string PushPreviewBody);

public static class ManualNotificationDraftPolicy
{
    public static ManualNotificationDraft Normalize(string? subject, string? body)
    {
        // Single-line subject; plain multiline message body. Never interpret markup.
        if (subject is null || subject.Any(char.IsControl))
            throw new ValidationException("件名を確認してください。");
        subject = subject.Trim();
        if (subject.Length is < 1 or > 80)
            throw new ValidationException("件名は1〜80文字で入力してください。");
        if (body is null || body.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new ValidationException("本文を確認してください。");
        body = body.Trim();
        if (body.Length is < 1 or > 2000)
            throw new ValidationException("本文は1〜2000文字で入力してください。");
        return new(subject, body);
    }
}

public sealed partial class CoreFoundationService
{
    // Deliberately not an HTTP endpoint or a send operation. This method writes nothing.
    public async Task<ManualNotificationPreview> PreviewEventManualNotificationAsync(
        ClaimsPrincipal principal, string id, string? subject, string? body,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (e, actor, _, manager) = await ParticipantEvent(db, principal, id, ct);
        if (!manager) throw new UnauthorizedAccessException();
        var draft = ManualNotificationDraftPolicy.Normalize(subject, body);
        var performers = from part in db.SongParts.AsNoTracking()
            join song in db.Songs.AsNoTracking() on part.SongId equals song.Id
            join definition in db.EventPartDefinitions.AsNoTracking() on part.PartDefinitionId equals definition.Id
            where part.EventId == id && song.EventId == id && definition.EventId == id
                && song.DeletedAt == null && definition.DeletedAt == null && part.AssignedUserId != null
            select part.AssignedUserId!;
        var candidates = performers.Union(db.EventSpectators.AsNoTracking()
            .Where(x => x.EventId == id).Select(x => x.UserId));
        // No email, profile, answers, receipt history, device keys or security-stamp projection.
        var users = await db.Users.AsNoTracking().Where(x => candidates.Contains(x.Id))
            .Select(x => new { x.Id, x.Enabled, x.EmailConfirmed, x.IsAdmin }).ToArrayAsync(ct);
        var now = clock.GetUtcNow();
        var recipients = users.Where(u => PushViewAllowed(e, new ApplicationUser {
                Id = u.Id, Enabled = u.Enabled, EmailConfirmed = u.EmailConfirmed, IsAdmin = u.IsAdmin
            }, now)).Select(u => u.Id).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (recipients.Length == 0)
            throw new ValidationException("現在、通知できる参加者がいません。");
        // Optimistic comparison only: NOT an authorization token, approval, operation ID or send guarantee.
        var fingerprint = SemEndpoints.Hash(JsonSerializer.Serialize(new {
            Protocol = "manual-notification-preview-v1", ActorId = actor.Id,
            EventId = id, EventTitle = e.Title, EventVersion = e.RowVersion,
            draft.Subject, draft.Body, Recipients = recipients
        }));
        await tx.CommitAsync(ct);
        return new(id, e.Title, e.RowVersion, draft.Subject, draft.Body, recipients.Length,
            fingerprint, "新しいメッセージがあります");
    }
}
