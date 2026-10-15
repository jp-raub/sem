using System.Data;
using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Core;

public sealed partial class CoreFoundationService
{
    /// <summary>Returns the persisted theme catalog for a currently authenticated actor.</summary>
    public async Task<ThemeCatalogEntryView[]> ReadThemeCatalogAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await Actor(db, principal, ct);
        var entries = await db.ThemeCatalogEntries.AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => new ThemeCatalogEntryView(x.Id, x.Name, x.Accent, x.Background, x.Surface, x.Text, x.BuiltIn, x.RowVersion))
            .ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return entries;
    }

    /// <summary>Adds a server-ID custom theme or updates an exact-ID theme with optimistic concurrency.</summary>
    public async Task<ThemeCatalogEntryView> AddOrUpdateThemeAsync(
        ClaimsPrincipal principal, string? id, ThemeCatalogEdit edit, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var actor = await Actor(db, principal, ct);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("全体管理者のみ変更できます。");
        if (edit is null) throw new ValidationException("テーマ入力を確認してください。");
        if (edit.RowVersion < 0) throw new ValidationException("更新番号を確認してください。");

        ThemeDefinition definition;
        try
        {
            definition = new ThemeDefinition(id ?? Guid.NewGuid().ToString("D").ToLowerInvariant(), edit.Name,
                HexColor.Parse(edit.Accent), HexColor.Parse(edit.Background),
                HexColor.Parse(edit.Surface), HexColor.Parse(edit.Text));
        }
        catch (ArgumentException error)
        {
            throw new ValidationException("テーマID、名前、4色を確認してください。", error);
        }

        var entry = id is null
            ? null
            : await db.ThemeCatalogEntries.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (id is not null && entry is null) throw new CoreConflictException();
        if (id is null && edit.RowVersion != 0) throw new CoreConflictException();
        if (entry is not null && entry.RowVersion != edit.RowVersion) throw new CoreConflictException();
        if (entry?.RowVersion == long.MaxValue) throw new ValidationException("更新番号の上限に達しています。");

        var created = entry is null;
        entry ??= new ThemeCatalogEntry { Id = definition.Id, BuiltIn = false };
        entry.Name = definition.Name;
        entry.Accent = definition.Accent.Value;
        entry.Background = definition.Background.Value;
        entry.Surface = definition.Surface.Value;
        entry.Text = definition.Text.Value;
        entry.RowVersion = created ? 1 : checked(entry.RowVersion + 1);
        if (created) db.ThemeCatalogEntries.Add(entry);
        Audit(db, actor.Id, created ? "ThemeCatalogEntryCreated" : "ThemeCatalogEntryUpdated", entry.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(entry.Id, entry.Name, entry.Accent, entry.Background, entry.Surface, entry.Text, entry.BuiltIn, entry.RowVersion);
    }
}