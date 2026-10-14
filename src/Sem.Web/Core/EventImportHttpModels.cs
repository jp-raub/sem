using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Sem.Web.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EventImportPreviewRequest([property: JsonRequired] string Csv);

public sealed partial class CoreFoundationService
{
    // Cheap pre-body gate only. Preview/apply repeat this check inside their own transaction
    // so authorization cannot be relied on across the HTTP-to-core time-of-check boundary.
    public async Task AuthorizeEventImportRequestAsync(ClaimsPrincipal principal, string eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (_, _, _, manager) = await ParticipantEvent(db, principal, eventId, ct);
        if (!manager) throw new UnauthorizedAccessException();
        await tx.CommitAsync(ct);
    }
}
