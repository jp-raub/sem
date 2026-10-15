using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-theme-catalog-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var services = new ServiceCollection();
services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite("Data Source=" + Path.Combine(root, "catalog.db")), ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
    .AddEntityFrameworkStores<ApplicationDbContext>();
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var core = new CoreFoundationService(factory, Options.Create(new IdentityOptions()), TimeProvider.System,
    new BusinessNotificationStore(new EphemeralDataProtectionProvider()));
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
ThemeCatalogEdit Edit(string name = "Custom", string accent = "#010203", string background = "#111213",
    string surface = "#212223", string text = "#313233", long version = 0) =>
    new(name, accent, background, surface, text, version);

try
{
    // Upgrade an actual old schema with live legacy rows, then verify the catalog migration preserves them.
    await using (var db = await factory.CreateDbContextAsync())
    {
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261014181715_AddEventImportOperations");
        db.Users.AddRange(
            new ApplicationUser { Id = "admin", UserName = "admin@test", Email = "admin@test", DisplayName = "Admin", SecurityStamp = "admin-stamp", Enabled = true, EmailConfirmed = true, IsAdmin = true },
            new ApplicationUser { Id = "manager", UserName = "manager@test", Email = "manager@test", DisplayName = "Manager", SecurityStamp = "manager-stamp", Enabled = true, EmailConfirmed = true },
            new ApplicationUser { Id = "member", UserName = "member@test", Email = "member@test", DisplayName = "Member", SecurityStamp = "member-stamp", Enabled = true, EmailConfirmed = true },
            new ApplicationUser { Id = "disabled", UserName = "disabled@test", Email = "disabled@test", DisplayName = "Disabled", SecurityStamp = "disabled-stamp", Enabled = false, EmailConfirmed = true });
        db.ServiceSettings.Add(new ServiceSettings { Id = 1, Name = "Legacy setting", Description = "preserve", RowVersion = 7 });
        db.UserProfiles.Add(new UserProfile { UserId = "member", Reading = "legacy", CallName = "profile", SocialJson = "[]", RowVersion = 4 });
        db.SessionEvents.Add(new SessionEvent { Id = "legacy-event", Title = "Legacy event", ScheduledDate = "2026-10-01T00:00:00.000Z", CreatedAt = "2026-09-01T00:00:00.000Z", PhaseKind = "Hidden" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO AspNetUserPasskeys (CredentialId, UserId, Data) VALUES (X'01020304', 'member', {0})", "{\"Name\":\"synthetic retained passkey\"}");
        await migrator.MigrateAsync();
        Check((await db.Database.SqlQueryRaw<string>("SELECT Data AS Value FROM AspNetUserPasskeys WHERE CredentialId=X'01020304'").SingleAsync()) == "{\"Name\":\"synthetic retained passkey\"}",
            "Identity V3 passkey table and synthetic existing credential bytes survive catalog migration");

        Check(await db.ThemeCatalogEntries.CountAsync() == 10, "migration seeds exactly ten catalog entries once");
        var seeded = await db.ThemeCatalogEntries.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Check(ThemeCatalog.Default.Themes.All(t =>
        {
            var row = seeded.SingleOrDefault(x => x.Id == t.Id);
            return row is not null && row.Name == t.Name && row.Accent == t.Accent.Value &&
                row.Background == t.Background.Value && row.Surface == t.Surface.Value &&
                row.Text == t.Text.Value && row.BuiltIn && row.RowVersion == 0;
        }), "migration palette exactly matches all verified pure defaults");
        Check(await db.SessionEvents.AnyAsync(x => x.Id == "legacy-event" && x.Title == "Legacy event") &&
              await db.UserProfiles.AnyAsync(x => x.UserId == "member" && x.Reading == "legacy" && x.RowVersion == 4) &&
              await db.ServiceSettings.AnyAsync(x => x.Id == 1 && x.Name == "Legacy setting" && x.RowVersion == 7),
            "upgrade preserves existing event, profile, and settings rows");

        var read = await core.ReadThemeCatalogAsync(Principal("member"));
        Check(read.Length == 10 && read.All(x => x.BuiltIn), "authenticated non-admin read returns only catalog DTOs");
        await Throws<UnauthorizedAccessException>(() => core.ReadThemeCatalogAsync(new ClaimsPrincipal(new ClaimsIdentity())), "anonymous read is rejected");
        await Throws<UnauthorizedAccessException>(() => core.ReadThemeCatalogAsync(Principal("missing")), "unknown actor is rejected");
        await Throws<UnauthorizedAccessException>(() => core.ReadThemeCatalogAsync(Principal("disabled")), "disabled actor is rejected");
        await Throws<UnauthorizedAccessException>(() => core.ReadThemeCatalogAsync(Principal("member", stamp: "wrong")), "stale security stamp is rejected");
        await using (var db2 = await factory.CreateDbContextAsync())
        {
            const string revoked = "member-revoked";
            db2.RevokedAuthSessions.Add(new RevokedAuthSession { TokenHash = SemEndpoints.Hash(revoked), UserId = "member", RevokedAt = "2026-10-15T00:00:00.000Z" });
            await db2.SaveChangesAsync();
        }
        await Throws<UnauthorizedAccessException>(() => core.ReadThemeCatalogAsync(Principal("member", "revoked")), "revoked session cannot read");

        var auditsBefore = await db.Audits.CountAsync();
        await Throws<UnauthorizedAccessException>(() => core.AddOrUpdateThemeAsync(Principal("member"), null, Edit()), "non-admin cannot write");
        await Throws<UnauthorizedAccessException>(() => core.AddOrUpdateThemeAsync(Principal("manager"), null, Edit()), "event manager without global admin cannot write");
        Check(await db.Audits.CountAsync() == auditsBefore, "authorization failures have no audit side effects");

        var created = await core.AddOrUpdateThemeAsync(Principal("admin"), null, Edit());
        Check(created.Id.Length == 36 && created.Id == created.Id.ToLowerInvariant() && !created.BuiltIn && created.RowVersion == 1,
            "create assigns a server lowercase GUID and starts row version");
        Check(created.Accent == "#010203", "create canonicalizes validated hex values");
        var builtInUpdated = await core.AddOrUpdateThemeAsync(Principal("admin"), "green",
            Edit("Edited Forest", "#ABCDEF", "#010101", "#020202", "#030303", 0));
        Check(builtInUpdated.BuiltIn && builtInUpdated.RowVersion == 1 && builtInUpdated.Name == "Edited Forest",
            "built-in entries are editable without changing their marker");
        var edited = await core.AddOrUpdateThemeAsync(Principal("admin"), created.Id,
            Edit("Custom v2", "#040506", version: 1));
        Check(edited.RowVersion == 2 && edited.Name == "Custom v2", "custom edit increments optimistic version");

        var auditCount = await db.Audits.CountAsync();
        var catalogCount = await db.ThemeCatalogEntries.CountAsync();
        await Throws<CoreConflictException>(() => core.AddOrUpdateThemeAsync(Principal("admin"), created.Id, Edit(version: 1)), "stale update is rejected");
        await Throws<CoreConflictException>(() => core.AddOrUpdateThemeAsync(Principal("admin"), "not-present", Edit(version: 0)), "unknown exact ID is rejected");
        await Throws<CoreConflictException>(() => core.AddOrUpdateThemeAsync(Principal("admin"), null, Edit(version: 1)), "new entries require expected version zero");
        await Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => core.AddOrUpdateThemeAsync(Principal("admin"), null, Edit(name: new string('x', 31))), "overlong name is rejected");
        await Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => core.AddOrUpdateThemeAsync(Principal("admin"), null, Edit(name: "  ")), "blank name is rejected");
        await Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => core.AddOrUpdateThemeAsync(Principal("admin"), null, Edit(accent: "#GG0000")), "invalid color is rejected");
        await Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => core.AddOrUpdateThemeAsync(Principal("admin"), null, Edit(accent: "#１２３４５６")), "non-ASCII color is rejected");
        Check(await db.ThemeCatalogEntries.CountAsync() == catalogCount && await db.Audits.CountAsync() == auditCount,
            "invalid and stale operations leave catalog and audit unchanged");

        // DB-level constraints and immutable marker remain effective outside the service.
        await Throws<DbUpdateException>(async () =>
        {
            db.ThemeCatalogEntries.Add(new ThemeCatalogEntry { Id = "Bad_ID", Name = "Bad", Accent = "#abcdef", Background = "#111213", Surface = "#212223", Text = "#313233", RowVersion = 0 });
            await db.SaveChangesAsync();
        }, "database constraints reject malformed ID and lowercase colors");
        db.ChangeTracker.Clear();
        await Throws<DbUpdateException>(async () =>
        {
            var forest = await db.ThemeCatalogEntries.SingleAsync(x => x.Id == "green");
            forest.BuiltIn = false;
            await db.SaveChangesAsync();
        }, "database trigger prevents changing built-in marker");
        db.ChangeTracker.Clear();

        // A checked version overflow must not change either the row or its audit trail.
        await db.Database.ExecuteSqlRawAsync("UPDATE ThemeCatalogEntries SET RowVersion=9223372036854775807 WHERE Id={0}", created.Id);
        auditCount = await db.Audits.CountAsync();
        await Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => core.AddOrUpdateThemeAsync(Principal("admin"), created.Id, Edit(version: long.MaxValue)),
            "maximum row version is rejected before increment");
        Check(await db.ThemeCatalogEntries.Where(x => x.Id == created.Id).Select(x => x.RowVersion).SingleAsync() == long.MaxValue &&
              await db.Audits.CountAsync() == auditCount, "maximum-version rejection is atomic");

        // Re-applying migrations must not reseed over administrator edits.
        await using (var db3 = await factory.CreateDbContextAsync())
        {
            await db3.Database.MigrateAsync();
            Check((await db3.ThemeCatalogEntries.SingleAsync(x => x.Id == "green")).Name == "Edited Forest",
                "subsequent migrate does not reset administrator edits");
        }
        var model = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        var snapshotEntity = model.FindEntityType(typeof(ThemeCatalogEntry));
        Check(snapshotEntity?.GetTableName() == "ThemeCatalogEntries" &&
              snapshotEntity.GetProperties().Select(x => x.Name).Order().SequenceEqual(
                  db.Model.FindEntityType(typeof(ThemeCatalogEntry))!.GetProperties().Select(x => x.Name).Order()),
            "migration snapshot and runtime entity properties are in parity");

        await Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE ThemeCatalogEntries SET Name={0} WHERE Id='green'", " " + new string('x', 30) + " "),
            "database constraint rejects oversized names even with surrounding spaces");
        var beforeRead = System.Text.Json.JsonSerializer.Serialize(await db.ThemeCatalogEntries.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync());
        var beforeReadAudits = await db.Audits.CountAsync();
        var readonlyServices = new ServiceCollection();
        readonlyServices.AddLogging();
        readonlyServices.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite("Data Source=" + Path.Combine(root, "catalog.db") + ";Mode=ReadOnly"), ServiceLifetime.Scoped);
        readonlyServices.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        using var readonlyProvider = readonlyServices.BuildServiceProvider();
        using var readonlyScope = readonlyProvider.CreateScope();
        var readonlyCore = new CoreFoundationService(readonlyScope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            Options.Create(new IdentityOptions()), TimeProvider.System, new BusinessNotificationStore(new EphemeralDataProtectionProvider()));
        var actualServiceRead = await readonlyCore.ReadThemeCatalogAsync(Principal("admin"));
        Check(actualServiceRead.Length == 11, "actual ReadThemeCatalogAsync succeeds through Identity V3 read-only SQLite factory");
        Check(beforeRead == System.Text.Json.JsonSerializer.Serialize(await db.ThemeCatalogEntries.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()) &&
            beforeReadAudits == await db.Audits.CountAsync(), "actual service read leaves catalog and audit data unchanged");
    }
    Console.WriteLine($"PASS all {checks} theme catalog checks");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

