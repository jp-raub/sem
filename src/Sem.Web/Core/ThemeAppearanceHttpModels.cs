using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Sem.Web.Core;

public sealed record ThemeAppearanceHttpResponse(string? EventId, ThemeAppearanceView Appearance);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ThemePreferenceHttpEdit(
    [property: JsonRequired] string? ThemeId,
    [property: JsonRequired] ThemeMode? Mode,
    [property: JsonRequired] long RowVersion)
{
    public ThemePreferenceEdit ToEdit() => new(ThemeId, Mode, RowVersion);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ThemeCatalogHttpEdit(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Accent,
    [property: JsonRequired] string Background,
    [property: JsonRequired] string Surface,
    [property: JsonRequired] string Text,
    [property: JsonRequired] long RowVersion)
{
    public ThemeCatalogEdit ToEdit() => new(Name, Accent, Background, Surface, Text, RowVersion);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ThemeCatalogDeleteHttpEdit([property: JsonRequired] long RowVersion);

public sealed partial class CoreFoundationService
{
    /// <summary>
    /// Cheap pre-body authorization for theme writes. The corresponding write service repeats
    /// its checks inside its own transaction; this read-only gate only protects the HTTP boundary.
    /// </summary>
    public async Task AuthorizeThemeAppearanceRequestAsync(
        ClaimsPrincipal principal, string action, string? eventId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        switch (action)
        {
            case "theme-personal":
                await Actor(db, principal, ct);
                break;
            case "theme-event":
            {
                if (eventId is null) throw new ValidationException("イベントを確認してください。");
                var (_, _, _, manager) = await ParticipantEvent(db, principal, eventId, ct);
                if (!manager) throw new UnauthorizedAccessException();
                break;
            }
            case "theme-site":
            case "theme-catalog-save":
            case "theme-catalog-delete":
            {
                var actor = await Actor(db, principal, ct);
                if (!actor.IsAdmin) throw new UnauthorizedAccessException();
                break;
            }
            default:
                throw new ValidationException("テーマ操作を確認してください。");
        }
        await tx.CommitAsync(ct);
    }
}