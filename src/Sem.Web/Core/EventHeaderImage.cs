using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;

/// <summary>Only server-normalized PNG bytes are stored; source filenames and metadata are discarded.</summary>
public sealed class EventHeaderImage
{
    public string EventId { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
}
public static class EventHeaderImageNormalizer
{
    public const int MaxInputBytes = 1024 * 1024;
    public const int MaxLongEdge = 1600;
    public static async Task<(byte[] Png, int Width, int Height)> NormalizeAsync(string dataUrl, CancellationToken ct = default)
    {
        if (dataUrl.Length > 4 * ((MaxInputBytes + 2) / 3) + 32)
            throw new ValidationException("ヘッダー画像は1MiB以内で選択してください。");
        var comma = dataUrl.IndexOf(',');
        if (comma < 0 || !dataUrl.StartsWith("data:", StringComparison.Ordinal) || !dataUrl[..comma].EndsWith(";base64", StringComparison.Ordinal))
            throw new ValidationException("画像形式を確認してください。");
        var mime = dataUrl[5..(comma - 7)];
        if (!ProfileImageNormalizer.IsAllowedMime(mime)) throw new ValidationException("PNG、JPEG、WebP画像のみ利用できます。");
        byte[] input;
        try { input = Convert.FromBase64String(dataUrl[(comma + 1)..]); }
        catch (FormatException) { throw new ValidationException("画像形式を確認してください。"); }
        return await ProfileImageNormalizer.NormalizeRasterAsync(input, mime, MaxInputBytes, MaxLongEdge, ct);
    }
    internal static string? DataUrl(EventHeaderImage? image) => image is null ? null : "data:image/png;base64," + Convert.ToBase64String(image.Data);
}
public sealed partial class CoreFoundationService
{
    public async Task<string?> ReadEventHeaderImageForEditAsync(System.Security.Claims.ClaimsPrincipal principal, string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await Actor(db, principal, ct);
        if (!await db.SessionEvents.AnyAsync(e => e.Id == id && (actor.IsAdmin || e.Admins.Any(a => a.UserId == actor.Id)), ct))
            throw new UnauthorizedAccessException("このイベントは管理できません。");
        return EventHeaderImageNormalizer.DataUrl(await db.EventHeaderImages.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == id, ct));
    }
}
