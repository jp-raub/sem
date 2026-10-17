using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

public sealed record InvitationManagementActor(string Id, bool IsAdmin);
public sealed record InvitationManagementInvitation(
    string Id, string OwnerId, string OwnerName, string IssuerId, string IssuerName,
    string? RequestId, int Limit, int Uses, long Created, long Expires, bool Disabled,
    string? Purpose, string? IssuanceSource,
    long? RevokedAt, string? RevokedBy, string RevokerName, string? RevocationReason);
public sealed record InvitationManagementRequest(
    string Id, string UserId, string UserName, string Reason, int Count, int Days,
    string Status, long Created, long? DecidedAt, string? DecidedBy, string DeciderName,
    string? DecisionComment);
public sealed record InvitationManagementPage(
    string ActorId, bool IsAdmin, int Page, int PageSize, bool HasMoreInvitations,
    bool HasMoreRequests, InvitationManagementInvitation[] Invitations,
    InvitationManagementRequest[] Requests);
public sealed record InvitationManagementRevokeResult(string ActorId, string Id, bool Disabled);
public sealed record InvitationManagementIssueResult(string ActorId, string Id, string Purpose,
    string IssuanceSource, int Limit, int Uses, long Created, long Expires, string Link);
public sealed record InvitationManagementRequestResult(string ActorId, string Id, string Status);
public sealed record InvitationManagementDecisionResult(string ActorId, string Id, string Status,
    string? DecisionComment, string? InvitationId);
public sealed record InvitationManagementLinkResult(string ActorId, string Id, int Limit, int Uses,
    long Created, long Expires, string Link);
public sealed class InvitationManagementNotFoundException : Exception { }
public sealed class InvitationManagementUnavailableException : Exception { }

/// <summary>Invitation requests, issuance, owner-only link retrieval, and audited administration.</summary>
public sealed class InvitationManagementService(
    ApplicationDbContext db,
    IOptions<IdentityOptions> identityOptions,
    NotificationService notifications)
{
    public const int PageSize = 50;
    public const int MaximumPage = 10_000;

    public async Task<InvitationManagementActor> RequireActorAsync(HttpContext context,
        string? expectedActor = null, bool adminOnly = false, CancellationToken ct = default)
    {
        if (!await LiveAuthenticationSession.ValidAsync(db, context.User, identityOptions.Value, ct))
            throw new UnauthorizedAccessException();

        var id = context.User.FindFirstValue(identityOptions.Value.ClaimsIdentity.UserIdClaimType);
        var stamp = context.User.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(stamp) ||
            expectedActor is not null && !string.Equals(id, expectedActor, StringComparison.Ordinal))
            throw new UnauthorizedAccessException();

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is not { Enabled: true, EmailConfirmed: true } || user.SecurityStamp != stamp ||
            adminOnly && !user.IsAdmin)
            throw new UnauthorizedAccessException();
        return new InvitationManagementActor(user.Id, user.IsAdmin);
    }

    public async Task<InvitationManagementPage> ListAsync(HttpContext context, int page,
        CancellationToken ct = default)
    {
        if (page is < 1 or > MaximumPage) throw new ValidationException("ページ番号を確認してください。");
        var actor = await RequireActorAsync(context, ct: ct);
        var skip = checked((page - 1) * PageSize);

        var invitationQuery = db.Invitations.AsNoTracking().AsQueryable();
        if (!actor.IsAdmin) invitationQuery = invitationQuery.Where(x => x.OwnerId == actor.Id);
        var invitationRows = await invitationQuery
            .OrderByDescending(x => x.Created).ThenBy(x => x.Id)
            .Skip(skip).Take(PageSize + 1)
            .Select(x => new
            {
                x.Id, x.OwnerId, x.IssuerId, x.RequestId, x.Limit, x.Uses, x.Created, x.Expires,
                x.Disabled, x.Purpose, x.IssuanceSource, x.RevokedAt, x.RevokedBy, x.RevocationReason
            }).ToArrayAsync(ct);

        var requestQuery = db.InvitationRequests.AsNoTracking().AsQueryable();
        if (!actor.IsAdmin) requestQuery = requestQuery.Where(x => x.UserId == actor.Id);
        var requestRows = await requestQuery
            .OrderByDescending(x => x.Created).ThenBy(x => x.Id)
            .Skip(skip).Take(PageSize + 1)
            .Select(x => new
            {
                x.Id, x.UserId, x.Reason, x.Count, x.Days, x.Status, x.Created,
                x.DecidedAt, x.DecidedBy, x.DecisionComment
            }).ToArrayAsync(ct);

        var userIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in invitationRows)
        {
            userIds.Add(row.OwnerId);
            userIds.Add(row.IssuerId);
            if (row.RevokedBy is not null) userIds.Add(row.RevokedBy);
        }
        foreach (var row in requestRows)
        {
            userIds.Add(row.UserId);
            if (row.DecidedBy is not null) userIds.Add(row.DecidedBy);
        }
        var relatedIds = userIds.ToArray();
        var names = await db.Users.AsNoTracking().Where(x => relatedIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        string Name(string? userId) => userId is not null && names.TryGetValue(userId, out var name) &&
            !string.IsNullOrWhiteSpace(name) ? name : "不明";

        var invitations = invitationRows.Take(PageSize).Select(x => new InvitationManagementInvitation(
            x.Id, x.OwnerId, Name(x.OwnerId), x.IssuerId, Name(x.IssuerId), x.RequestId,
            x.Limit, x.Uses, x.Created, x.Expires, x.Disabled, x.Purpose,
            x.IssuanceSource, x.RevokedAt, x.RevokedBy, Name(x.RevokedBy),
            x.RevocationReason)).ToArray();
        var requests = requestRows.Take(PageSize).Select(x => new InvitationManagementRequest(
            x.Id, x.UserId, Name(x.UserId), x.Reason, x.Count, x.Days, x.Status,
            x.Created, x.DecidedAt, x.DecidedBy, Name(x.DecidedBy), x.DecisionComment)).ToArray();

        return new InvitationManagementPage(actor.Id, actor.IsAdmin, page, PageSize,
            invitationRows.Length > PageSize, requestRows.Length > PageSize, invitations, requests);
    }

    public async Task<InvitationManagementRequestResult> RequestAsync(HttpContext context,
        string expectedActor, string reason, int count, int days, CancellationToken ct = default)
    {
        var cleanReason = ValidateReason(reason);
        if (count is < 1 or > 5) throw new ValidationException("人数は1～5で指定してください。");
        if (days is < 1 or > 7) throw new ValidationException("期間は1～7日で指定してください。");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var actor = await RequireActorAsync(context, expectedActor, ct: ct);
        if (actor.IsAdmin) throw new ValidationException("全体管理者は直接発行してください。");
        var now = SemEndpoints.Now;
        var request = new InvitationRequest
        {
            UserId = actor.Id,
            Reason = cleanReason,
            Count = count,
            Days = days,
            Status = "Pending",
            Created = now
        };
        db.InvitationRequests.Add(request);
        var admins = await db.Users.AsNoTracking().Where(x => x.Enabled && x.IsAdmin)
            .Select(x => x.Id).ToArrayAsync(ct);
        var requester = await db.Users.AsNoTracking().Where(x => x.Id == actor.Id)
            .Select(x => x.DisplayName).SingleAsync(ct);
        var body = $"{requester}：{cleanReason}\n希望人数：{count}名／有効期間：{days}日";
        foreach (var adminId in admins)
            notifications.Message(db, adminId, "招待リンク発行リクエスト", body);
        db.Audits.Add(new Audit { ActorId = actor.Id, Action = "InvitationRequested", EntityId = request.Id, At = now });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new InvitationManagementRequestResult(actor.Id, request.Id, request.Status);
    }

    public async Task<InvitationManagementDecisionResult> DecideAsync(HttpContext context,
        string expectedActor, string id, bool approve, string? comment, IDataProtector protector,
        DeploymentSettings settings, CancellationToken ct = default)
    {
        var cleanId = ValidateId(id);
        var cleanComment = ValidateOptionalComment(comment);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var actor = await RequireActorAsync(context, expectedActor, adminOnly: true, ct);
        var request = await db.InvitationRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == cleanId, ct);
        if (request is null) throw new InvitationManagementNotFoundException();
        if (approve && (request.Count is < 1 or > 5 || request.Days is < 1 or > 7))
            throw new ValidationException("保存済みの人数・期間が現在の許容範囲外のため、許可できません。");

        var now = SemEndpoints.Now;
        var status = approve ? "Approved" : "Rejected";
        var claimed = await db.InvitationRequests.Where(x => x.Id == cleanId && x.Status == "Pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.DecidedAt, (long?)now)
                .SetProperty(x => x.DecidedBy, actor.Id)
                .SetProperty(x => x.DecisionComment, cleanComment), ct);
        if (claimed != 1)
            throw new DbUpdateConcurrencyException("依頼はすでに判断済みです。");

        Invitation? invitation = null;
        if (approve)
        {
            var token = SemEndpoints.Secret();
            invitation = new Invitation
            {
                TokenHash = SemEndpoints.Hash(token),
                ProtectedToken = protector.Protect(token),
                OwnerId = request.UserId,
                IssuerId = actor.Id,
                RequestId = request.Id,
                Limit = request.Count,
                Uses = 0,
                Created = now,
                Expires = now + request.Days * 86400L,
                Purpose = null,
                IssuanceSource = "Request"
            };
            db.Invitations.Add(invitation);
        }

        var messageTitle = approve ? "招待リクエストが許可されました" : "招待リクエストが拒否されました";
        var messageBody = $"{request.Count}名／{request.Days}日\n理由：{request.Reason}\n" +
            (cleanComment is null ? "" : $"管理者コメント：{cleanComment}\n") +
            (approve
                ? "招待管理から本人のリンクを取得できます。人数上限または期限で失効します。"
                : "招待リンクは発行されていません。");
        notifications.Message(db, request.UserId, messageTitle, messageBody);
        db.Audits.Add(new Audit
        {
            ActorId = actor.Id,
            Action = approve ? "InvitationApproved" : "InvitationRejected",
            EntityId = request.Id,
            At = now
        });
        if (invitation is not null)
            db.Audits.Add(new Audit
            {
                ActorId = actor.Id,
                Action = "InvitationIssued",
                EntityId = invitation.Id,
                At = now
            });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new InvitationManagementDecisionResult(actor.Id, request.Id, status,
            cleanComment, invitation?.Id);
    }

    public async Task<InvitationManagementLinkResult> GetOwnerLinkAsync(HttpContext context,
        string expectedActor, string id, IDataProtector protector, DeploymentSettings settings,
        CancellationToken ct = default)
    {
        var cleanId = ValidateId(id);
        var actor = await RequireActorAsync(context, expectedActor, ct: ct);
        var invitation = await db.Invitations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == cleanId && x.OwnerId == actor.Id, ct);
        if (invitation is null) throw new InvitationManagementNotFoundException();
        if (invitation.Disabled || invitation.Uses >= invitation.Limit || invitation.Expires <= SemEndpoints.Now)
            throw new InvitationManagementUnavailableException();
        var token = protector.Unprotect(invitation.ProtectedToken);
        var link = CreateJoinLink(settings, token);
        return new InvitationManagementLinkResult(actor.Id, invitation.Id, invitation.Limit,
            invitation.Uses, invitation.Created, invitation.Expires, link);
    }

    public async Task<InvitationManagementRevokeResult> RevokeAsync(HttpContext context,
        string expectedActor, string id, string reason, bool explicitlyConfirmed,
        CancellationToken ct = default)
    {
        var cleanId = ValidateId(id);
        var cleanReason = ValidateReason(reason);
        if (!explicitlyConfirmed) throw new ValidationException("失効の明示的な確認が必要です。");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var actor = await RequireActorAsync(context, expectedActor, adminOnly: true, ct);
        var now = SemEndpoints.Now;
        var changed = await db.Invitations.Where(x => x.Id == cleanId && !x.Disabled)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Disabled, true)
                .SetProperty(x => x.RevokedBy, actor.Id)
                .SetProperty(x => x.RevokedAt, (long?)now)
                .SetProperty(x => x.RevocationReason, cleanReason), ct);

        if (changed == 0)
        {
            if (await db.Invitations.AsNoTracking().AnyAsync(x => x.Id == cleanId && x.Disabled, ct))
            {
                await transaction.CommitAsync(ct);
                return new InvitationManagementRevokeResult(actor.Id, cleanId, true);
            }
            if (!await db.Invitations.AsNoTracking().AnyAsync(x => x.Id == cleanId, ct))
                throw new InvitationManagementNotFoundException();
            throw new DbUpdateConcurrencyException("招待の状態が同時に変更されました。");
        }

        db.Audits.Add(new Audit { ActorId = actor.Id, Action = "InvitationRevoked", EntityId = cleanId, At = now });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new InvitationManagementRevokeResult(actor.Id, cleanId, true);
    }

    public async Task<InvitationManagementIssueResult> IssueAsync(HttpContext context, string expectedActor,
        string purpose, int count, int days, IDataProtector protector, DeploymentSettings settings,
        CancellationToken ct = default)
    {
        var cleanPurpose = ValidatePurpose(purpose);
        if (count is < 1 or > 5) throw new ValidationException("人数は1～5で指定してください。");
        if (days is < 1 or > 7) throw new ValidationException("期間は1～7日で指定してください。");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var actor = await RequireActorAsync(context, expectedActor, adminOnly: true, ct);
        var now = SemEndpoints.Now;
        var expires = now + days * 86400L;
        var token = SemEndpoints.Secret();
        var link = CreateJoinLink(settings, token);
        var invitation = new Invitation
        {
            TokenHash = SemEndpoints.Hash(token),
            ProtectedToken = protector.Protect(token),
            OwnerId = actor.Id,
            IssuerId = actor.Id,
            RequestId = null,
            Limit = count,
            Uses = 0,
            Created = now,
            Expires = expires,
            Purpose = cleanPurpose,
            IssuanceSource = "Direct"
        };
        db.Invitations.Add(invitation);
        db.Audits.Add(new Audit
        {
            ActorId = actor.Id,
            Action = "InvitationIssued",
            EntityId = invitation.Id,
            At = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new InvitationManagementIssueResult(actor.Id, invitation.Id, cleanPurpose, "Direct",
            count, 0, now, expires, link);
    }

    private static string ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl))
            throw new ValidationException("招待IDを確認してください。");
        return id;
    }

    private static string ValidateReason(string? reason)
    {
        if (reason is null) throw new ValidationException("失効理由を指定してください。");
        if (reason.Any(char.IsControl)) throw new ValidationException("失効理由に制御文字は使用できません。");
        var clean = reason.Trim();
        if (clean.Length is < 1 or > 500) throw new ValidationException("失効理由は1～500文字で指定してください。");
        return clean;
    }

    private static string ValidatePurpose(string? purpose)
    {
        if (purpose is null || purpose.Any(char.IsControl))
            throw new ValidationException("目的を確認してください。");
        var clean = purpose.Trim();
        if (clean.Length is < 1 or > 500)
            throw new ValidationException("目的は1～500文字で指定してください。");
        return clean;
    }

    private static string? ValidateOptionalComment(string? comment)
    {
        if (comment is null) return null;
        if (comment.Any(char.IsControl))
            throw new ValidationException("判断コメントに制御文字は使用できません。");
        var clean = comment.Trim();
        if (clean.Length > 500)
            throw new ValidationException("判断コメントは500文字以内で指定してください。");
        return clean.Length == 0 ? null : clean;
    }

    private static string CreateJoinLink(DeploymentSettings settings, string token) =>
        new Uri(new Uri(settings.PublicUrl), "/join#invite=" +
            Uri.EscapeDataString(token)).ToString();
}
