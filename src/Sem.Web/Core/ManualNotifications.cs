using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Runtime;
namespace Sem.Web.Core;

// Immutable accepted request and recipient snapshot. No worker consumes this outbox yet.
public sealed class ManualNotificationOperation
{
    public string Id { get; set; } = "";
    public string EventId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string PreviewFingerprint { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public int RecipientCount { get; set; }
    public long AcceptedAt { get; set; }
}
public sealed class ManualNotificationRecipient
{
    // Same deterministic ID as its inbox message. Snapshot plus dormant recipient-level Push outbox.
    public string Id { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string EventId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string PushState { get; set; } = "Pending";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManualNotificationSubmit(string RequestId, string PreviewFingerprint,
    string Subject, string Body, bool Confirmed);
public sealed record ManualNotificationAcceptance(string RequestId, int RecipientCount, long AcceptedAt, bool Replayed = false);

public sealed partial class CoreFoundationService
{
    public async Task<ManualNotificationAcceptance> AcceptEventManualNotificationAsync(
        ClaimsPrincipal principal, string id, ManualNotificationSubmit edit, CancellationToken ct = default)
    {
        // Canonical GUID avoids alternate text forms bypassing a durable identity.
        if (!Guid.TryParseExact(edit.RequestId, "D", out var request) || request.ToString("D") != edit.RequestId || !edit.Confirmed)
            throw new ValidationException("送信内容と操作Idを確認してください。");
        if (edit.PreviewFingerprint is null || edit.PreviewFingerprint.Length != 64 ||
            edit.PreviewFingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ValidationException("送信前のプレビューを取得してください。");
        var draft = ManualNotificationDraftPolicy.Normalize(edit.Subject, edit.Body);
        var hash = SemEndpoints.Hash(JsonSerializer.Serialize(new {
            Protocol = "manual-notification-submit-v1", EventId = id, edit.PreviewFingerprint,
            draft.Subject, draft.Body, edit.Confirmed
        }));
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (e, actor, _, manager) = await ParticipantEvent(db, principal, id, ct);
        if (!manager) throw new UnauthorizedAccessException();
        // Even recovery of an already accepted result requires CURRENT authorization.
        var previous = await db.Set<ManualNotificationOperation>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == edit.RequestId, ct);
        if (previous is not null) {
            if (previous.EventId != id || previous.ActorId != actor.Id || previous.RequestHash != hash)
                throw new ValidationException("操作Idは別の送信に再利用できません。");
            await tx.CommitAsync(ct);
            return new(previous.Id, previous.RecipientCount, previous.AcceptedAt, true);
        }
        var source = await BuildManualNotificationSource(db, e, actor, draft, ct);
        if (source.Preview.Fingerprint != edit.PreviewFingerprint) throw new CoreConflictException();
        var at = clock.GetUtcNow().ToUnixTimeSeconds();
        db.Add(new ManualNotificationOperation {
            Id = edit.RequestId, EventId = id, ActorId = actor.Id, RequestHash = hash,
            PreviewFingerprint = source.Preview.Fingerprint, Subject = draft.Subject, Body = draft.Body,
            RecipientCount = source.Recipients.Length, AcceptedAt = at
        });
        // Explicit durable admission, atomic with the original accept. An absent/disabled
        // activation leaves NO admission; later activation or replay must never backfill it.
        var pushActivation = await db.Set<ManualNotificationPushActivation>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (pushActivation is { Enabled: true } && at >= 0 && at <= long.MaxValue - ManualNotificationPushPolicy.LifetimeSeconds)
            db.Add(new ManualNotificationPushAdmission {
                OperationId = edit.RequestId, Epoch = pushActivation.Epoch, AcceptedAt = at,
                ExpiresAt = at + ManualNotificationPushPolicy.LifetimeSeconds
            });
        foreach (var userId in source.Recipients) {
            var messageId = SemEndpoints.Hash(JsonSerializer.Serialize(new[] { "manual-notification-inbox-v1", id, edit.RequestId, userId }));
            db.Messages.Add(new Message { Id = messageId, UserId = userId, Title = draft.Subject, Body = draft.Body, Created = at });
            db.Add(new ManualNotificationRecipient {
                Id = messageId, OperationId = edit.RequestId, EventId = id, UserId = userId
            });
        }
        Audit(db, actor.Id, "ManualNotificationAccepted", edit.RequestId);
        // One transaction: operation + inbox + immutable recipients/dormant outbox + audit.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(edit.RequestId, source.Recipients.Length, at);
    }
}
