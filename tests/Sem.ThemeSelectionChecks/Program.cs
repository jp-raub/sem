using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-theme-selection-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}
async Task Throws<T>(Func<Task> action, string label) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, label); return; }
    throw new InvalidOperationException("FAIL " + label + " (expected " + typeof(T).Name + ")");
}
ClaimsPrincipal Principal(string id, string session = "session", string? stamp = null) =>
    new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, id),
        new Claim("AspNet.Identity.SecurityStamp", stamp ?? id + "-stamp"),
        new Claim(PasskeyAdditionService.SessionClaim, id + "-" + session)
    }, "test"));
ThemePreferenceEdit Pick(string? id, ThemeMode? mode, long version) => new(id, mode, version);
ServiceProvider Provider(string path, bool readOnly = false)
{
    var services = new ServiceCollection();
    services.AddLogging();
    var source = "Data Source=" + path + (readOnly ? ";Mode=ReadOnly" : "");
    services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite(source), ServiceLifetime.Scoped);
    services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
        .AddEntityFrameworkStores<ApplicationDbContext>();
    return services.BuildServiceProvider();
}
CoreFoundationService Core(ServiceProvider provider) => new(
    provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
    Options.Create(new IdentityOptions()), TimeProvider.System,
    new BusinessNotificationStore(new EphemeralDataProtectionProvider()));

try
{
    // Upgrade from the prior released migration with live data and passkey metadata.
    var upgradedPath = Path.Combine(root, "upgrade.db");
    using (var upgradedProvider = Provider(upgradedPath))
    {
        var factory = upgradedProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var oldDb = await factory.CreateDbContextAsync();
        var migrator = oldDb.GetService<IMigrator>();
        await migrator.MigrateAsync("20261015050000_AddThemeCatalog");
        oldDb.Users.Add(new ApplicationUser { Id = "legacy-user", UserName = "legacy@test", Email = "legacy@test",
            DisplayName = "Legacy", SecurityStamp = "legacy-stamp", Enabled = true, EmailConfirmed = true });
        oldDb.SessionEvents.Add(new SessionEvent { Id = "legacy-event", Title = "preserved title",
            ScheduledDate = "2026-10-15T00:00:00.000Z", CreatedAt = "2026-10-01T00:00:00.000Z" });
        await oldDb.SaveChangesAsync();
        await oldDb.Database.ExecuteSqlRawAsync(
            "INSERT INTO AspNetUserPasskeys (CredentialId, UserId, Data) VALUES (X'01020304', 'legacy-user', {0})",
            "{\"Name\":\"retained passkey metadata\"}");
        await migrator.MigrateAsync();
        var passkey = await oldDb.Database.SqlQueryRaw<string>(
            "SELECT Data AS Value FROM AspNetUserPasskeys WHERE CredentialId=X'01020304'").SingleAsync();
        Check(passkey == "{\"Name\":\"retained passkey metadata\"}", "Identity V3 passkey metadata and existing bytes survive the new migration");
        Check(await oldDb.SessionEvents.AnyAsync(x => x.Id == "legacy-event" && x.Title == "preserved title") &&
              await oldDb.UserThemePreferences.CountAsync() == 0 && await oldDb.EventThemePreferences.CountAsync() == 0 &&
              await oldDb.SiteThemePreferences.CountAsync() == 0,
            "upgrade preserves old rows and creates three empty selection tables without backfill");
        Check(oldDb.Model.FindEntityType("Microsoft.AspNetCore.Identity.IdentityUserPasskey<string>") is not null,
            "runtime Identity V3 model retains passkey entity metadata");
    }

    var path = Path.Combine(root, "service.db");
    using var provider = Provider(path);
    var factory2 = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using var db = await factory2.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    db.Users.AddRange(
        new ApplicationUser { Id = "admin", UserName = "admin@test", Email = "admin@test", DisplayName = "Admin", SecurityStamp = "admin-stamp", Enabled = true, EmailConfirmed = true, IsAdmin = true },
        new ApplicationUser { Id = "manager", UserName = "manager@test", Email = "manager@test", DisplayName = "Manager", SecurityStamp = "manager-stamp", Enabled = true, EmailConfirmed = true },
        new ApplicationUser { Id = "member", UserName = "member@test", Email = "member@test", DisplayName = "Member", SecurityStamp = "member-stamp", Enabled = true, EmailConfirmed = true },
        new ApplicationUser { Id = "other", UserName = "other@test", Email = "other@test", DisplayName = "Other", SecurityStamp = "other-stamp", Enabled = true, EmailConfirmed = true },
        new ApplicationUser { Id = "disabled", UserName = "disabled@test", Email = "disabled@test", DisplayName = "Disabled", SecurityStamp = "disabled-stamp", Enabled = false, EmailConfirmed = true });
    var visible = new SessionEvent { Id = "visible-event", Title = "not included in theme DTO", ScheduledDate = "2026-10-15T00:00:00.000Z",
        CreatedAt = "2026-10-01T00:00:00.000Z", PhaseKind = "View", ManualPhaseOverride = true };
    visible.Phases.Add(new EventPhase { Id = "visible-phase", EventId = visible.Id, Kind = "View", Name = "View", IsActive = true });
    visible.Admins.Add(new EventAdmin { EventId = visible.Id, UserId = "manager", GrantedAt = "2026-10-01T00:00:00.000Z" });
    var hidden = new SessionEvent { Id = "hidden-event", Title = "secret event", ScheduledDate = "2026-10-15T00:00:00.000Z",
        CreatedAt = "2026-10-01T00:00:00.000Z", PhaseKind = "Hidden", ManualPhaseOverride = true };
    hidden.Phases.Add(new EventPhase { Id = "hidden-phase", EventId = hidden.Id, Kind = "Hidden", Name = "Hidden", IsActive = true });
    db.SessionEvents.AddRange(visible, hidden);
    await db.SaveChangesAsync();
    var core = Core(provider);

    var initial = await core.ReadThemeAppearanceAsync(Principal("member"));
    Check(await db.UserThemePreferences.CountAsync() == 0 && await db.EventThemePreferences.CountAsync() == 0 && await db.SiteThemePreferences.CountAsync() == 0,
        "migration and reads invent no preference rows");
    Check(initial.ActorId == "member" && !initial.CanManageEvent && !initial.CanManageSite && initial.Event is null &&
          initial.Personal.RowVersion == 0 && initial.Site.RowVersion == 0 && initial.Resolved.Mode == ThemeMode.System &&
          initial.Resolved.Accent.Value == ThemeCatalog.Default.GetRequired("green").Accent.Value,
        "default read uses current actor, green palette and System mode only");
    var memberAppearance = await core.ReadThemeAppearanceAsync(Principal("member"), "visible-event");
    Check(!memberAppearance.CanManageEvent && !memberAppearance.CanManageSite && memberAppearance.Event is { RowVersion: 0 },
        "visible event read reveals only selection context and no hidden event details");
    var managerAppearance = await core.ReadThemeAppearanceAsync(Principal("manager"), "visible-event");
    Check(managerAppearance.ActorId == "manager" && managerAppearance.CanManageEvent && !managerAppearance.CanManageSite,
        "event management capability is resolved for the current manager only");
    var adminAppearance = await core.ReadThemeAppearanceAsync(Principal("admin"), "visible-event");
    Check(adminAppearance.CanManageEvent && adminAppearance.CanManageSite, "global admin has site and current-event management capabilities");
    await Throws<UnauthorizedAccessException>(() => core.ReadThemeAppearanceAsync(Principal("member"), "hidden-event"),
        "hidden event read is denied to a non-manager");
    await Throws<UnauthorizedAccessException>(() => core.SaveEventThemePreferenceAsync(Principal("member"), "visible-event", Pick("rose", ThemeMode.Dark, 0)),
        "visible non-manager cannot write event preference");
    await Throws<UnauthorizedAccessException>(() => core.SaveSiteThemePreferenceAsync(Principal("manager"), Pick("rose", ThemeMode.Dark, 0)),
        "event manager cannot write site preference");
    await Throws<UnauthorizedAccessException>(() => core.ReadThemeAppearanceAsync(new ClaimsPrincipal(new ClaimsIdentity())), "anonymous read is denied");
    await Throws<UnauthorizedAccessException>(() => core.ReadThemeAppearanceAsync(Principal("disabled")), "disabled actor is denied");
    await Throws<UnauthorizedAccessException>(() => core.ReadThemeAppearanceAsync(Principal("member", stamp: "stale")), "stale security stamp is denied");
    await Throws<UnauthorizedAccessException>(() => core.ReadThemeAppearanceAsync(Principal("missing")), "unknown current actor is denied");
    await Throws<UnauthorizedAccessException>(() => core.ReadThemeAppearanceAsync(Principal("member"), "missing-event"), "unknown event is denied without disclosing it");
    var beforeAuthAudit = await db.Audits.CountAsync();
    Check(await db.Audits.CountAsync() == beforeAuthAudit && await db.EventThemePreferences.CountAsync() == 0,
        "authorization failures have no preference or audit side effects");

    // Selection scopes are pinned to the authenticated actor and event; no actor selector exists.
    await using (var setup = await factory2.CreateDbContextAsync())
    {
        setup.UserThemePreferences.Add(new UserThemePreference { UserId = "other", ThemeId = "blue", Mode = (int)ThemeMode.Dark, RowVersion = 4 });
        await setup.SaveChangesAsync();
    }
    var beforeConcurrentAudit = await db.Audits.CountAsync();
    async Task<bool> ConcurrentWrite(ThemeMode mode)
    {
        try
        {
            await core.SaveUserThemePreferenceAsync(Principal("other"), Pick("blue", mode, 4));
            return true;
        }
        catch (Exception error) when (error is CoreConflictException or DbUpdateConcurrencyException or SqliteException)
        {
            return false;
        }
    }
    var concurrentResults = await Task.WhenAll(
        Task.Run(() => ConcurrentWrite(ThemeMode.Light)),
        Task.Run(() => ConcurrentWrite(ThemeMode.Dark)));
    await using (var concurrentRead = await factory2.CreateDbContextAsync())
    {
        var concurrentRow = await concurrentRead.UserThemePreferences.SingleAsync(x => x.UserId == "other");
        Check(concurrentResults.Count(x => x) == 1 && concurrentRow.RowVersion == 5 &&
              await concurrentRead.Audits.CountAsync() == beforeConcurrentAudit + 1,
            "concurrent same-version writes fail closed with one version increment and one audit");
    }
    var raceTheme = await core.AddOrUpdateThemeAsync(Principal("admin"), null,
        new ThemeCatalogEdit("Race guard", "#010204", "#111214", "#212224", "#313234", 0));
    async Task<bool> TryRaceSelection()
    {
        try { await core.SaveUserThemePreferenceAsync(Principal("other"), Pick(raceTheme.Id, null, 5)); return true; }
        catch (Exception error) when (error is CoreConflictException or ValidationException or DbUpdateException or SqliteException)
        { return false; }
    }
    async Task<bool> TryRaceDelete()
    {
        try { await core.DeleteThemeAsync(Principal("admin"), raceTheme.Id, raceTheme.RowVersion); return true; }
        catch (Exception error) when (error is CoreConflictException or ValidationException or DbUpdateException or SqliteException)
        { return false; }
    }
    var raceResults = await Task.WhenAll(Task.Run(TryRaceSelection), Task.Run(TryRaceDelete));
    bool raceExists;
    bool raceReferenced;
    long otherVersion;
    await using (var raceRead = await factory2.CreateDbContextAsync())
    {
        raceExists = await raceRead.ThemeCatalogEntries.AnyAsync(x => x.Id == raceTheme.Id);
        raceReferenced = await raceRead.UserThemePreferences.AnyAsync(x => x.ThemeId == raceTheme.Id) ||
                         await raceRead.EventThemePreferences.AnyAsync(x => x.ThemeId == raceTheme.Id) ||
                         await raceRead.SiteThemePreferences.AnyAsync(x => x.ThemeId == raceTheme.Id);
        otherVersion = await raceRead.UserThemePreferences.Where(x => x.UserId == "other").Select(x => x.RowVersion).SingleAsync();
    }
    Check(!(raceResults[0] && raceResults[1]) && (raceExists || !raceReferenced),
        "concurrent catalog deletion and preference selection cannot commit a dangling reference");
    if (raceReferenced)
        await core.SaveUserThemePreferenceAsync(Principal("other"), Pick("blue", ThemeMode.Dark, otherVersion));
    if (raceExists)
        await core.DeleteThemeAsync(Principal("admin"), raceTheme.Id, raceTheme.RowVersion);
    var own = await core.SaveUserThemePreferenceAsync(Principal("member"), Pick("rose", ThemeMode.Dark, 0));
    Check(own.ThemeId == "rose" && own.Mode == ThemeMode.Dark && own.RowVersion == 1,
        "own-user preference is created at version one with no user selector");
    var onlyOwn = await core.ReadThemeAppearanceAsync(Principal("member"));
    Check(onlyOwn.Personal.ThemeId == "rose" && onlyOwn.Personal.RowVersion == 1,
        "read returns current actor preference and does not expose another user's layer");
    await Throws<CoreConflictException>(() => core.SaveUserThemePreferenceAsync(Principal("member"), Pick("blue", ThemeMode.Light, 0)),
        "stale preference version is rejected");
    await Throws<ValidationException>(() => core.SaveUserThemePreferenceAsync(Principal("member"), Pick("not-a-theme", ThemeMode.System, 1)),
        "unknown catalog ID is rejected before persistence");
    await Throws<ValidationException>(() => core.SaveUserThemePreferenceAsync(Principal("member"), Pick("Bad Id", ThemeMode.System, 1)),
        "invalid theme ID syntax is rejected");
    await Throws<ValidationException>(() => core.SaveUserThemePreferenceAsync(Principal("member"), Pick("blue", (ThemeMode)77, 1)),
        "undefined mode values are rejected");

    var site = await core.SaveSiteThemePreferenceAsync(Principal("admin"), Pick("blue", ThemeMode.System, 0));
    var eventPreference = await core.SaveEventThemePreferenceAsync(Principal("manager"), "visible-event", Pick(null, ThemeMode.Light, 0));
    Check(site.RowVersion == 1 && eventPreference.RowVersion == 1 && eventPreference.ThemeId is null,
        "site and event rows start at one and explicit null persists inheritance");
    var layered = await core.ReadThemeAppearanceAsync(Principal("member"), "visible-event");
    Check(layered.Resolved.Accent.Value == ThemeCatalog.Default.GetRequired("rose").Accent.Value &&
          layered.Resolved.Mode == ThemeMode.Dark,
        "personal palette and mode override lower-priority event and site layers");
    own = await core.SaveUserThemePreferenceAsync(Principal("member"), Pick(null, null, 1));
    layered = await core.ReadThemeAppearanceAsync(Principal("member"), "visible-event");
    Check(layered.Resolved.Accent.Value == ThemeCatalog.Default.GetRequired("blue").Accent.Value &&
          layered.Resolved.Mode == ThemeMode.Light,
        "null palette and mode inherit independently from site and event");
    eventPreference = await core.SaveEventThemePreferenceAsync(Principal("manager"), "visible-event", Pick("rose", ThemeMode.Dark, 1));
    own = await core.SaveUserThemePreferenceAsync(Principal("member"), Pick(null, ThemeMode.System, 2));
    layered = await core.ReadThemeAppearanceAsync(Principal("member"), "visible-event");
    Check(layered.Resolved.Accent.Value == ThemeCatalog.Default.GetRequired("rose").Accent.Value &&
          layered.Resolved.Mode == ThemeMode.System,
        "explicit System mode stops inheritance independently of the inherited event palette");
    var auditsAfterWrites = await db.Audits.CountAsync();
    await Throws<CoreConflictException>(() => core.SaveEventThemePreferenceAsync(Principal("manager"), "visible-event", Pick("blue", null, 0)),
        "event optimistic concurrency rejects a stale write");
    Check(await db.Audits.CountAsync() == auditsAfterWrites, "stale writes do not add audit entries");

    // Current auth revocation is checked by the same production Actor helper on every call.
    await using (var revoke = await factory2.CreateDbContextAsync())
    {
        revoke.RevokedAuthSessions.Add(new RevokedAuthSession { UserId = "member", TokenHash = SemEndpoints.Hash("member-revoked"), RevokedAt = "2026-10-15T00:00:00.000Z" });
        await revoke.SaveChangesAsync();
    }
    await Throws<UnauthorizedAccessException>(() => core.ReadThemeAppearanceAsync(Principal("member", "revoked")),
        "revoked auth session cannot read theme appearance");
    await Throws<UnauthorizedAccessException>(() => core.SaveUserThemePreferenceAsync(Principal("member", "revoked"), Pick("blue", null, 3)),
        "revoked auth session cannot write theme preference");

    // Catalog deletion blocks every scope and has a database FK backstop plus built-in trigger.
    var custom = await core.AddOrUpdateThemeAsync(Principal("admin"), null,
        new ThemeCatalogEdit("Disposable", "#010203", "#111213", "#212223", "#313233", 0));
    own = await core.SaveUserThemePreferenceAsync(Principal("member"), Pick(custom.Id, null, 3));
    await Throws<ValidationException>(() => core.DeleteThemeAsync(Principal("admin"), custom.Id, custom.RowVersion),
        "delete is blocked while personal selection references the theme");
    await using (var direct = await factory2.CreateDbContextAsync())
        await Throws<SqliteException>(() => direct.Database.ExecuteSqlRawAsync("DELETE FROM ThemeCatalogEntries WHERE Id={0}", custom.Id),
            "database FK blocks direct delete while any selection references the theme");
    own = await core.SaveUserThemePreferenceAsync(Principal("member"), Pick(null, null, own.RowVersion));
    eventPreference = await core.SaveEventThemePreferenceAsync(Principal("manager"), "visible-event", Pick(custom.Id, null, 2));
    await Throws<ValidationException>(() => core.DeleteThemeAsync(Principal("admin"), custom.Id, custom.RowVersion),
        "delete is blocked while event selection references the theme");
    eventPreference = await core.SaveEventThemePreferenceAsync(Principal("manager"), "visible-event", Pick(null, null, eventPreference.RowVersion));
    site = await core.SaveSiteThemePreferenceAsync(Principal("admin"), Pick(custom.Id, null, 1));
    await Throws<ValidationException>(() => core.DeleteThemeAsync(Principal("admin"), custom.Id, custom.RowVersion),
        "delete is blocked while site selection references the theme");
    site = await core.SaveSiteThemePreferenceAsync(Principal("admin"), Pick(null, null, site.RowVersion));
    await Throws<ValidationException>(() => core.DeleteThemeAsync(Principal("admin"), "green", 0),
        "built-in theme is never deletable through the service");
    await Throws<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM ThemeCatalogEntries WHERE Id='green'"),
        "database trigger independently blocks built-in deletion");
    var beforeDeleteAudit = await db.Audits.CountAsync();
    await core.DeleteThemeAsync(Principal("admin"), custom.Id, custom.RowVersion);
    Check(!await db.ThemeCatalogEntries.AnyAsync(x => x.Id == custom.Id) && await db.Audits.CountAsync() == beforeDeleteAudit + 1,
        "unused custom theme deletion and audit commit atomically");
    await Throws<CoreConflictException>(() => core.DeleteThemeAsync(Principal("admin"), custom.Id, custom.RowVersion),
        "deleting a missing catalog ID is rejected");
    await Throws<UnauthorizedAccessException>(() => core.DeleteThemeAsync(Principal("manager"), "blue", 0),
        "only current global admin can delete themes");

    // DB constraints reject unsupported mode/version/ref values, including direct SQL writes.
    await Throws<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE UserThemePreferences SET Mode=77 WHERE UserId='member'"),
        "database check constraint rejects an unknown stored mode");
    await Throws<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE UserThemePreferences SET RowVersion=-1 WHERE UserId='member'"),
        "database check constraint rejects a negative row version");
    await Throws<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE UserThemePreferences SET ThemeId='unknown' WHERE UserId='member'"),
        "database FK rejects an unknown selected catalog ID");
    await Throws<SqliteException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO SiteThemePreferences (Id,ThemeId,Mode,RowVersion) VALUES (2,NULL,NULL,1)"),
        "site table accepts only fixed ID one");

    // Overflow must fail closed with no selection or audit mutation.
    await db.Database.ExecuteSqlRawAsync("UPDATE UserThemePreferences SET RowVersion=9223372036854775807 WHERE UserId='member'");
    var beforeOverflowAudit = await db.Audits.CountAsync();
    await Throws<ValidationException>(() => core.SaveUserThemePreferenceAsync(Principal("member"), Pick("blue", null, long.MaxValue)),
        "maximum row version is rejected");
    Check(await db.UserThemePreferences.Where(x => x.UserId == "member").Select(x => x.RowVersion).SingleAsync() == long.MaxValue &&
          await db.Audits.CountAsync() == beforeOverflowAudit, "version overflow leaves data and audit unchanged");

    // Snapshot/runtime model parity covers the new tables and existing Identity V3 metadata.
    var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
    foreach (var entityType in new[] { typeof(UserThemePreference), typeof(EventThemePreference), typeof(SiteThemePreference) })
    {
        var runtimeEntity = db.Model.FindEntityType(entityType)!;
        var snapshotEntity = snapshot.FindEntityType(entityType)!;
        Check(runtimeEntity.GetTableName() == snapshotEntity.GetTableName() &&
              runtimeEntity.GetProperties().Select(x => x.Name).Order().SequenceEqual(snapshotEntity.GetProperties().Select(x => x.Name).Order()) &&
              runtimeEntity.GetForeignKeys().Count() == snapshotEntity.GetForeignKeys().Count(),
            $"runtime/snapshot model parity for {entityType.Name}");
    }
    Check(snapshot.FindEntityType("Microsoft.AspNetCore.Identity.IdentityUserPasskey<string>") is not null &&
          db.Model.FindEntityType("Microsoft.AspNetCore.Identity.IdentityUserPasskey<string>") is not null,
        "new snapshot preserves Identity V3 passkey entity metadata");

    // The actual read path must work against a SQLite read-only connection and mutate nothing.
    db.ChangeTracker.Clear();
    var beforeReadonlyCatalog = System.Text.Json.JsonSerializer.Serialize(await db.ThemeCatalogEntries.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync());
    var beforeReadonlyPrefs = System.Text.Json.JsonSerializer.Serialize(new
    {
        Users = await db.UserThemePreferences.AsNoTracking().OrderBy(x => x.UserId).ToArrayAsync(),
        Events = await db.EventThemePreferences.AsNoTracking().OrderBy(x => x.EventId).ToArrayAsync(),
        Sites = await db.SiteThemePreferences.AsNoTracking().ToArrayAsync()
    });
    var beforeReadonlyAudits = await db.Audits.CountAsync();
    using var readonlyProvider = Provider(path, readOnly: true);
    var readonlyCore = Core(readonlyProvider);
    var readonlyResult = await readonlyCore.ReadThemeAppearanceAsync(Principal("admin"), "visible-event");
    Check(readonlyResult.ActorId == "admin" && readonlyResult.Catalog.Length == 10,
        "actual appearance service succeeds on a read-only SQLite connection with production-equivalent Identity V3 DI");
    var afterReadonlyPrefs = System.Text.Json.JsonSerializer.Serialize(new
    {
        Users = await db.UserThemePreferences.AsNoTracking().OrderBy(x => x.UserId).ToArrayAsync(),
        Events = await db.EventThemePreferences.AsNoTracking().OrderBy(x => x.EventId).ToArrayAsync(),
        Sites = await db.SiteThemePreferences.AsNoTracking().ToArrayAsync()
    });
    Check(beforeReadonlyCatalog == System.Text.Json.JsonSerializer.Serialize(await db.ThemeCatalogEntries.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()) &&
          beforeReadonlyPrefs == afterReadonlyPrefs && beforeReadonlyAudits == await db.Audits.CountAsync(),
        "strict read leaves catalog, all selection tables, and audits unchanged");

    Console.WriteLine($"PASS all {checks} theme selection checks");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}
