using System.Data;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Core;

public sealed partial class CoreFoundationService
{
    /// <summary>Reads only this actor's own layers and, when requested, an authorized event layer.</summary>
    public async Task<ThemeAppearanceView> ReadThemeAppearanceAsync(
        ClaimsPrincipal principal, string? eventId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var actor = await Actor(db, principal, ct);
        var canManageEvent = false;
        if (eventId is not null)
        {
            var (_, eventActor, _, manager) = await ParticipantEvent(db, principal, eventId, ct);
            actor = eventActor;
            canManageEvent = manager;
        }

        var catalogRows = await db.ThemeCatalogEntries.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(ct);
        var catalog = BuildValidatedCatalog(catalogRows);
        var personalRow = await db.UserThemePreferences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == actor.Id, ct);
        var eventRow = eventId is null ? null : await db.EventThemePreferences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EventId == eventId, ct);
        var siteRow = await db.SiteThemePreferences.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
        var personal = View(personalRow);
        ThemePreferenceView? eventPreference = eventId is null ? null : View(eventRow);
        var site = View(siteRow);
        var resolved = ThemeResolver.Resolve(catalog,
            new ThemeLayer(personal.ThemeId, personal.Mode),
            eventPreference is null ? null : new ThemeLayer(eventPreference.ThemeId, eventPreference.Mode),
            new ThemeLayer(site.ThemeId, site.Mode));
        await tx.CommitAsync(ct);
        return new(actor.Id, canManageEvent, actor.IsAdmin,
            catalogRows.Select(x => new ThemeCatalogEntryView(x.Id, x.Name, x.Accent, x.Background, x.Surface, x.Text, x.BuiltIn, x.RowVersion)).ToArray(),
            personal, eventPreference, site, resolved);
    }

    /// <summary>Writes only the current actor's personal layer.</summary>
    public async Task<ThemePreferenceView> SaveUserThemePreferenceAsync(
        ClaimsPrincipal principal, ThemePreferenceEdit edit, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var actor = await Actor(db, principal, ct);
        await ValidateThemePreference(db, edit, ct);
        var row = await db.UserThemePreferences.SingleOrDefaultAsync(x => x.UserId == actor.Id, ct);
        CheckVersion(row?.RowVersion ?? 0, row is null, edit.RowVersion);
        if (row is null)
        {
            row = new UserThemePreference { UserId = actor.Id };
            db.UserThemePreferences.Add(row);
        }
        SetPreference(row, edit);
        Audit(db, actor.Id, "UserThemePreferenceChanged", actor.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return View(row);
    }

    /// <summary>Writes an event layer only for the authorized event's current manager.</summary>
    public async Task<ThemePreferenceView> SaveEventThemePreferenceAsync(
        ClaimsPrincipal principal, string eventId, ThemePreferenceEdit edit, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var (sessionEvent, actor, _, manager) = await ParticipantEvent(db, principal, eventId, ct);
        if (!manager) throw new UnauthorizedAccessException("イベント管理者のみ変更できます。");
        await ValidateThemePreference(db, edit, ct);
        var row = await db.EventThemePreferences.SingleOrDefaultAsync(x => x.EventId == sessionEvent.Id, ct);
        CheckVersion(row?.RowVersion ?? 0, row is null, edit.RowVersion);
        if (row is null)
        {
            row = new EventThemePreference { EventId = sessionEvent.Id };
            db.EventThemePreferences.Add(row);
        }
        SetPreference(row, edit);
        Audit(db, actor.Id, "EventThemePreferenceChanged", sessionEvent.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return View(row);
    }

    /// <summary>Writes the site layer only for the current global administrator.</summary>
    public async Task<ThemePreferenceView> SaveSiteThemePreferenceAsync(
        ClaimsPrincipal principal, ThemePreferenceEdit edit, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var actor = await Actor(db, principal, ct);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("全体管理者のみ変更できます。");
        await ValidateThemePreference(db, edit, ct);
        var row = await db.SiteThemePreferences.SingleOrDefaultAsync(x => x.Id == 1, ct);
        CheckVersion(row?.RowVersion ?? 0, row is null, edit.RowVersion);
        if (row is null)
        {
            row = new SiteThemePreference { Id = 1 };
            db.SiteThemePreferences.Add(row);
        }
        SetPreference(row, edit);
        Audit(db, actor.Id, "SiteThemePreferenceChanged", "1");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return View(row);
    }

    /// <summary>Deletes an unused custom catalog theme, with the database FKs as the race-safe backstop.</summary>
    public async Task DeleteThemeAsync(
        ClaimsPrincipal principal, string id, long expectedVersion, CancellationToken ct = default)
    {
        if (expectedVersion < 0) throw new ValidationException("更新番号を確認してください。");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var actor = await Actor(db, principal, ct);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("全体管理者のみ変更できます。");
        var entry = await db.ThemeCatalogEntries.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new CoreConflictException();
        if (entry.BuiltIn) throw new ValidationException("組み込みテーマは削除できません。");
        if (entry.RowVersion != expectedVersion) throw new CoreConflictException();
        if (await db.UserThemePreferences.AnyAsync(x => x.ThemeId == id, ct) ||
            await db.EventThemePreferences.AnyAsync(x => x.ThemeId == id, ct) ||
            await db.SiteThemePreferences.AnyAsync(x => x.ThemeId == id, ct))
            throw new ValidationException("テーマ設定で使用中のため削除できません。");
        db.ThemeCatalogEntries.Remove(entry);
        Audit(db, actor.Id, "ThemeCatalogEntryDeleted", id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static ThemeCatalog BuildValidatedCatalog(IEnumerable<ThemeCatalogEntry> rows)
    {
        try
        {
            return new ThemeCatalog(rows.Select(row => new ThemeDefinition(row.Id, row.Name,
                HexColor.Parse(row.Accent), HexColor.Parse(row.Background),
                HexColor.Parse(row.Surface), HexColor.Parse(row.Text))));
        }
        catch (ArgumentException error)
        {
            throw new ValidationException("保存済みテーマカタログが不正です。", error);
        }
    }

    private static ThemePreferenceView View(UserThemePreference? row) =>
        new(row?.ThemeId, row?.Mode is int mode ? (ThemeMode)mode : null, row?.RowVersion ?? 0);
    private static ThemePreferenceView View(EventThemePreference? row) =>
        new(row?.ThemeId, row?.Mode is int mode ? (ThemeMode)mode : null, row?.RowVersion ?? 0);
    private static ThemePreferenceView View(SiteThemePreference? row) =>
        new(row?.ThemeId, row?.Mode is int mode ? (ThemeMode)mode : null, row?.RowVersion ?? 0);

    private static void CheckVersion(long actual, bool absent, long expected)
    {
        if (expected < 0) throw new ValidationException("更新番号を確認してください。");
        if (expected != actual || absent && expected != 0) throw new CoreConflictException();
        if (!absent && actual == long.MaxValue) throw new ValidationException("更新番号の上限に達しています。");
    }

    private static async Task ValidateThemePreference(ApplicationDbContext db, ThemePreferenceEdit? edit, CancellationToken ct)
    {
        if (edit is null) throw new ValidationException("テーマ設定を確認してください。");
        if (edit.RowVersion < 0 || edit.Mode.HasValue && !Enum.IsDefined(edit.Mode.Value))
            throw new ValidationException("テーマ設定または表示モードを確認してください。");
        if (edit.ThemeId is not null)
        {
            try { _ = new ThemeLayer(edit.ThemeId, edit.Mode); }
            catch (ArgumentException error) { throw new ValidationException("テーマIDを確認してください。", error); }
            if (!await db.ThemeCatalogEntries.AnyAsync(x => x.Id == edit.ThemeId, ct))
                throw new ValidationException("選択できないテーマIDです。");
        }
    }

    private static void SetPreference(UserThemePreference row, ThemePreferenceEdit edit)
    {
        row.ThemeId = edit.ThemeId;
        row.Mode = edit.Mode is null ? null : (int)edit.Mode.Value;
        row.RowVersion = checked(row.RowVersion + 1);
    }
    private static void SetPreference(EventThemePreference row, ThemePreferenceEdit edit)
    {
        row.ThemeId = edit.ThemeId;
        row.Mode = edit.Mode is null ? null : (int)edit.Mode.Value;
        row.RowVersion = checked(row.RowVersion + 1);
    }
    private static void SetPreference(SiteThemePreference row, ThemePreferenceEdit edit)
    {
        row.ThemeId = edit.ThemeId;
        row.Mode = edit.Mode is null ? null : (int)edit.Mode.Value;
        row.RowVersion = checked(row.RowVersion + 1);
    }
}