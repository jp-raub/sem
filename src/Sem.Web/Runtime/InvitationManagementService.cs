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
    string Status, long Created, long? DecidedAt, string? DecidedBy, string DeciderName);
public sealed record InvitationManagementPage(
    string ActorId, bool IsAdmin, int Page, int PageSize, bool HasMoreInvitations,
    bool HasMoreRequests, InvitationManagementInvitation[] Invitations,
    InvitationManagementRequest[] Requests);
public sealed record InvitationManagementRevokeResult(string ActorId, string Id, bool Disabled);
public sealed record InvitationManagementIssueResult(string ActorId, string Id, string Purpose,
    string IssuanceSource, int Limit, int Uses, long Created, long Expires, string Link);
public sealed class InvitationManagementNotFoundException : Exception { }

/// <summary>Read-only invitation/request inventory plus audited revocation.</summary>
public sealed class InvitationManagementService(
    ApplicationDbContext db,
    IOptions<IdentityOptions> identityOptions)
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
                x.DecidedAt, x.DecidedBy
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
            x.Created, x.DecidedAt, x.DecidedBy, Name(x.DecidedBy))).ToArray();

        return new InvitationManagementPage(actor.Id, actor.IsAdmin, page, PageSize,
            invitationRows.Length > PageSize, requestRows.Length > PageSize, invitations, requests);
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
        var link = new Uri(new Uri(settings.PublicUrl), "/join#invite=" +
            Uri.EscapeDataString(token)).ToString();
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
}
