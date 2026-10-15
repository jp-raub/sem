using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Core;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileImageDeleteEdit(long RowVersion);

public sealed partial class CoreFoundationService
{
    private static async Task<ProfileView> ProfileWithImageAsync(ApplicationDbContext db, ApplicationUser user, ProfileView view, CancellationToken ct)
    {
        var image = await db.ProfileImages.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
        if (image is null) return view;
        var dataUrl = image.Data.Length == 0 ? null : "data:image/png;base64," + Convert.ToBase64String(image.Data);
        return view with { ImageDataUrl = dataUrl, ImageRowVersion = image.RowVersion };
    }

    public async Task AuthorizeProfileImageRequestAsync(ClaimsPrincipal principal, string expectedActor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await Actor(db, principal, ct);
        if (!string.Equals(expectedActor, actor.Id, StringComparison.Ordinal)) throw new UnauthorizedAccessException();
    }

    public async Task<ProfileImageResult> SaveProfileImageAsync(ClaimsPrincipal principal, long expectedVersion,
        byte[] png, int width, int height, CancellationToken ct = default)
    {
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || png.Length is < 8 or > ProfileImageNormalizer.MaxOutputBytes ||
            !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || width is < 1 or > 512 || height is < 1 or > 512)
            throw new ValidationException("画像または更新版を確認してください。");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = await Actor(db, principal, ct);
        var row = await db.ProfileImages.SingleOrDefaultAsync(x => x.UserId == actor.Id, ct);
        if ((row?.RowVersion ?? 0) != expectedVersion) throw new CoreConflictException();
        if (row is null) { row = new ProfileImage { UserId = actor.Id }; db.ProfileImages.Add(row); }
        row.Data = png;
        row.Width = width;
        row.Height = height;
        row.RowVersion = checked(expectedVersion + 1);
        Audit(db, actor.Id, "ProfileImageSaved", actor.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(row.RowVersion, width, height);
    }

    public async Task<ProfileImageResult> DeleteProfileImageAsync(ClaimsPrincipal principal, long expectedVersion, CancellationToken ct = default)
    {
        if (expectedVersion < 0 || expectedVersion == long.MaxValue) throw new ValidationException("画像の更新版を確認してください。");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = await Actor(db, principal, ct);
        var row = await db.ProfileImages.SingleOrDefaultAsync(x => x.UserId == actor.Id, ct);
        if ((row?.RowVersion ?? 0) != expectedVersion) throw new CoreConflictException();
        if (row is null) { row = new ProfileImage { UserId = actor.Id }; db.ProfileImages.Add(row); }
        row.Data = [];
        row.Width = 0;
        row.Height = 0;
        row.RowVersion = checked(expectedVersion + 1);
        Audit(db, actor.Id, "ProfileImageDeleted", actor.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(row.RowVersion, 0, 0);
    }
}
