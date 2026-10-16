using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Data;

namespace Sem.Web.Runtime;

public sealed record InboxMessageSummary(string Id, string Title, long Created, long? ReadAt);
public sealed record InboxPage(string ActorId, int Page, int PageSize, bool HasMore,
    InboxMessageSummary[] Messages);
public sealed record InboxMessageDetail(string ActorId, string Id, string Title, string Body,
    long Created, long? ReadAt);
public sealed record InboxReadResult(string ActorId, string Id, long ReadAt);

public sealed class InboxNotFoundException : Exception { }

/// <summary>Lists and marks only messages belonging to the live authenticated user.</summary>
public sealed class InboxService(ApplicationDbContext db, IOptions<IdentityOptions> identityOptions)
{
    public const int PageSize = 50;
    public const int MaximumPage = 10_000;

    public async Task<string> RequireActorAsync(HttpContext context, string? expectedActor = null,
        CancellationToken ct = default)
    {
        var principal = context.User;
        if (!await LiveAuthenticationSession.ValidAsync(db, principal, identityOptions.Value, ct))
            throw new UnauthorizedAccessException();

        var id = principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.UserIdClaimType);
        var stamp = principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(stamp) ||
            expectedActor is not null && !string.Equals(id, expectedActor, StringComparison.Ordinal))
            throw new UnauthorizedAccessException();

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is not { Enabled: true, EmailConfirmed: true } || user.SecurityStamp != stamp)
            throw new UnauthorizedAccessException();
        return user.Id;
    }

    public async Task<InboxPage> ListAsync(HttpContext context, int page, CancellationToken ct = default)
    {
        ValidatePage(page);
        var actorId = await RequireActorAsync(context, ct: ct);
        var skip = checked((page - 1) * PageSize);
        var rows = await db.Messages.AsNoTracking()
            .Where(x => x.UserId == actorId)
            .OrderByDescending(x => x.Created)
            .ThenBy(x => x.Id)
            .Skip(skip)
            .Take(PageSize + 1)
            .Select(x => new InboxMessageSummary(x.Id, x.Title, x.Created, x.ReadAt))
            .ToArrayAsync(ct);
        return new InboxPage(actorId, page, PageSize, rows.Length > PageSize, rows.Take(PageSize).ToArray());
    }

    public async Task<InboxMessageDetail> ReadDetailAsync(HttpContext context, string expectedActor,
        string id, CancellationToken ct = default)
    {
        ValidateId(id);
        var actorId = await RequireActorAsync(context, expectedActor, ct);
        var message = await db.Messages.AsNoTracking()
            .Where(x => x.UserId == actorId && x.Id == id)
            .Select(x => new InboxMessageDetail(actorId, x.Id, x.Title, x.Body, x.Created, x.ReadAt))
            .SingleOrDefaultAsync(ct);
        return message ?? throw new InboxNotFoundException();
    }

    public async Task<InboxReadResult> MarkReadAsync(HttpContext context, string expectedActor,
        string id, CancellationToken ct = default)
    {
        ValidateId(id);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var actorId = await RequireActorAsync(context, expectedActor, ct);
        var now = SemEndpoints.Now;
        var changed = await db.Messages
            .Where(x => x.UserId == actorId && x.Id == id && x.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ReadAt, (long?)now), ct);

        long readAt;
        if (changed == 1)
        {
            readAt = now;
        }
        else
        {
            var existing = await db.Messages.AsNoTracking()
                .Where(x => x.UserId == actorId && x.Id == id)
                .Select(x => new { x.Id, x.ReadAt })
                .SingleOrDefaultAsync(ct);
            if (existing is null) throw new InboxNotFoundException();
            if (existing.ReadAt is not long timestamp)
                throw new DbUpdateConcurrencyException("既読状態を更新できませんでした。");
            readAt = timestamp;
        }

        // Recheck the live account/session immediately before committing the mutation.
        _ = await RequireActorAsync(context, expectedActor, ct);
        await transaction.CommitAsync(ct);
        return new InboxReadResult(actorId, id, readAt);
    }

    public static void ValidatePage(int page)
    {
        if (page is < 1 or > MaximumPage)
            throw new ValidationException("ページ番号は1～10000で指定してください。");
    }

    public static void ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length is < 1 or > 64 || id.Any(char.IsControl))
            throw new ValidationException("メッセージIDを確認してください。");
    }
}