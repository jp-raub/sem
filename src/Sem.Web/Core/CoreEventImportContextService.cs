using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Sem.Web.Core;

public sealed record EventImportContext(string EventId, string ActorId, string Title);

public sealed partial class CoreFoundationService
{
    public async Task<EventImportContext> ReadEventImportContextAsync(
        ClaimsPrincipal principal, string eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (currentEvent, actor, _, manager) = await ParticipantEvent(db, principal, eventId, ct);
        if (!manager) throw new UnauthorizedAccessException();
        var context = new EventImportContext(currentEvent.Id, actor.Id, currentEvent.Title);
        await tx.CommitAsync(ct);
        return context;
    }
}