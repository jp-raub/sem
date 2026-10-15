using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-profile-history-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var connectionString = "Data Source=" + Path.Combine(root, "data.db");
var clock = new TestClock { Now = DateTimeOffset.Parse("2026-11-01T12:00:00Z") };
var services = new ServiceCollection();
services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite(connectionString), ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
    .AddEntityFrameworkStores<ApplicationDbContext>();
services.AddDataProtection();
services.AddSingleton<TimeProvider>(clock);
services.AddSingleton<BusinessNotificationStore>();
services.AddScoped<CoreFoundationService>();
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var core = scope.ServiceProvider.GetRequiredService<CoreFoundationService>();
var passed = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAIL " + label);
    passed++;
    Console.WriteLine("PASS " + label);
}
async Task Deny<T>(Func<Task> action, string label) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, label); return; }
    throw new Exception("FAIL " + label + " (not denied)");
}
ClaimsPrincipal Who(string id, string? stamp = null, string? session = null) => new(
    new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, id),
        new Claim("AspNet.Identity.SecurityStamp", stamp ?? id + "-stamp"),
        new Claim(PasskeyAdditionService.SessionClaim, session ?? id + "-session")
    ], "Identity.Application"));
string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
void AddEvent(ApplicationDbContext db, string id, DateTimeOffset scheduled, string activePhase = "Hidden",
    bool manual = false, bool customActive = false, bool misconfigured = false)
{
    db.SessionEvents.Add(new SessionEvent
    {
        Id = id, Title = "Title " + id, ScheduledDate = Utc(scheduled), CreatedAt = Utc(clock.Now.AddDays(-10)),
        PhaseKind = activePhase, ManualPhaseOverride = manual
    });
    db.Phases.AddRange(
        new EventPhase { Id = id + "-hidden", EventId = id, Kind = "Hidden", Name = "Hidden", SortOrder = 0,
            IsActive = !misconfigured && activePhase == "Hidden" },
        new EventPhase { Id = id + "-view", EventId = id, Kind = "View", Name = "View", SortOrder = 1,
            IsActive = !misconfigured && activePhase == "View" });
    if (customActive)
        db.Phases.Add(new EventPhase { Id = id + "-custom", EventId = id, Kind = "Custom", Name = "Custom",
            SortOrder = 2, ActivateAt = Utc(clock.Now.AddDays(-1)), DeactivateAt = Utc(clock.Now.AddDays(1)), IsActive = false });
}
void Spectate(ApplicationDbContext db, string eventId, string userId) => db.EventSpectators.Add(
    new EventSpectator { EventId = eventId, UserId = userId, RegisteredAt = Utc(clock.Now), RegisteredBy = userId });

try
{
    await using (var db = await factory.CreateDbContextAsync())
    {
        await db.GetService<IMigrator>().MigrateAsync();
        db.Users.AddRange(
            new ApplicationUser { Id = "viewer", UserName = "viewer@test.invalid", Email = "viewer-secret@test.invalid", DisplayName = "Viewer", Enabled = true, EmailConfirmed = true, SecurityStamp = "viewer-stamp" },
            new ApplicationUser { Id = "viewer2", UserName = "viewer2@test.invalid", Email = "viewer2@test.invalid", DisplayName = "Viewer Two", Enabled = true, EmailConfirmed = true, SecurityStamp = "viewer2-stamp" },
            new ApplicationUser { Id = "manager", UserName = "manager@test.invalid", Email = "manager@test.invalid", DisplayName = "Manager", Enabled = true, EmailConfirmed = true, SecurityStamp = "manager-stamp" },
            new ApplicationUser { Id = "admin", UserName = "admin@test.invalid", Email = "admin@test.invalid", DisplayName = "Admin", Enabled = true, EmailConfirmed = true, SecurityStamp = "admin-stamp", IsAdmin = true },
            new ApplicationUser { Id = "target", UserName = "target@test.invalid", Email = "target-private@test.invalid", DisplayName = "Target", Enabled = true, EmailConfirmed = true, SecurityStamp = "target-stamp" },
            new ApplicationUser { Id = "disabled", UserName = "disabled@test.invalid", Email = "disabled@test.invalid", Enabled = false, EmailConfirmed = true, SecurityStamp = "disabled-stamp" },
            new ApplicationUser { Id = "unconfirmed", UserName = "unconfirmed@test.invalid", Email = "unconfirmed@test.invalid", Enabled = true, EmailConfirmed = false, SecurityStamp = "unconfirmed-stamp" });

        var past = clock.Now.AddDays(-3);
        AddEvent(db, "visible", past); // Worker cache says Hidden; automatic phase is now View.
        AddEvent(db, "target-manager-hidden", clock.Now.AddDays(3));
        AddEvent(db, "viewer-manager-hidden", clock.Now.AddDays(3));
        AddEvent(db, "global-admin-hidden", clock.Now.AddDays(3));
        AddEvent(db, "auto-hidden-lag", clock.Now.AddDays(3));
        AddEvent(db, "auto-custom", clock.Now.AddDays(3), customActive: true);
        AddEvent(db, "manual-view", clock.Now.AddDays(3), activePhase: "View", manual: true);
        AddEvent(db, "misconfigured", clock.Now.AddDays(3), misconfigured: true);
        AddEvent(db, "foreign-owner", past);
        db.EventAdmins.AddRange(
            new EventAdmin { EventId = "target-manager-hidden", UserId = "target", GrantedAt = Utc(clock.Now) },
            new EventAdmin { EventId = "viewer-manager-hidden", UserId = "viewer", GrantedAt = Utc(clock.Now) });
        db.EventPartDefinitions.AddRange(
            new EventPartDefinition { Id = "def-live", EventId = "visible", Name = "Vocals", Category = "Sing", NormalizedName = "VOCALS" },
            new EventPartDefinition { Id = "def-deleted", EventId = "visible", Name = "Deleted definition", Category = "Play", NormalizedName = "DELETED", DeletedAt = Utc(clock.Now) },
            new EventPartDefinition { Id = "def-foreign", EventId = "foreign-owner", Name = "Foreign", Category = "Play", NormalizedName = "FOREIGN" });
        db.Songs.AddRange(
            new Song { Id = "song-live", EventId = "visible", Title = "Current song", SortOrder = 0 },
            new Song { Id = "song-deleted", EventId = "visible", Title = "Deleted song", SortOrder = 1, DeletedAt = Utc(clock.Now) },
            new Song { Id = "song-foreign", EventId = "foreign-owner", Title = "Foreign song", SortOrder = 2 });
        db.SongParts.AddRange(
            new SongPart { Id = "part-live", EventId = "visible", SongId = "song-live", PartDefinitionId = "def-live", AssignedUserId = "target", PhaseId = "visible-view" },
            new SongPart { Id = "part-deleted-song", EventId = "visible", SongId = "song-deleted", PartDefinitionId = "def-live", AssignedUserId = "target", PhaseId = "visible-view" },
            new SongPart { Id = "part-deleted-definition", EventId = "visible", SongId = "song-live", PartDefinitionId = "def-deleted", AssignedUserId = "target", PhaseId = "visible-view" });
        Spectate(db, "visible", "target"); // Same event must be grouped with the current part.
        Spectate(db, "target-manager-hidden", "target");
        Spectate(db, "viewer-manager-hidden", "target");
        Spectate(db, "global-admin-hidden", "target");
        Spectate(db, "auto-hidden-lag", "target");
        Spectate(db, "auto-custom", "target");
        Spectate(db, "manual-view", "target");
        Spectate(db, "misconfigured", "target");
        db.EventQuestions.Add(new EventQuestion { Id = "private-question", EventId = "visible", Text = "Private", Type = "Text" });
        db.EventQuestionAnswers.Add(new EventQuestionAnswer { QuestionId = "private-question", UserId = "target", Value = "secret-profile-answer", UpdatedAt = Utc(clock.Now) });
        await db.SaveChangesAsync();
        await SqliteStorageInitializer.InitializeAsync(db);
        // Bypass the SQLite FK guard only for two impossible-in-normal-operation corrupt rows,
        // proving that the query still enforces exact event-local song/definition joins.
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO SongParts(Id,EventId,SongId,PartDefinitionId,AssignedUserId,PhaseId,RowVersion)
            VALUES ('part-cross-event-song','visible','song-foreign','def-live','target','visible-view',0),
                   ('part-cross-event-definition','visible','song-live','def-foreign','target','visible-view',0)
            """);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        await db.Database.CloseConnectionAsync();
    }

    var viewer = Who("viewer");
    var history = await core.ReadProfileRegistrationHistoryAsync(viewer, "target");
    var byId = history.Events.ToDictionary(item => item.EventId, StringComparer.Ordinal);
    Check(history.UserId == "target" && byId.ContainsKey("visible"), "Returns current target registrations in visible events");
    Check(byId["visible"].Spectator && byId["visible"].Parts.Length == 1 && byId["visible"].Parts[0] is
        { SongId: "song-live", SongTitle: "Current song", PartDefinitionId: "def-live", PartName: "Vocals", Category: "Sing" },
        "Groups spectator and performer, retains only exact-event live joins, and projects minimal part fields");
    Check(byId.ContainsKey("auto-custom") && byId.ContainsKey("manual-view") && byId.ContainsKey("visible"),
        "Uses effective automatic phase despite worker lag and honors manual phase override");
    Check(!byId.ContainsKey("target-manager-hidden") && !byId.ContainsKey("auto-hidden-lag") && !byId.ContainsKey("misconfigured"),
        "Target manager privileges do not grant viewer access; hidden and misconfigured events are omitted");
    var adminHistory = await core.ReadProfileRegistrationHistoryAsync(Who("admin"), "target");
    Check(adminHistory.Events.Any(item => item.EventId == "global-admin-hidden"), "Current global administrator may view a hidden candidate");
    var managerHistory = await core.ReadProfileRegistrationHistoryAsync(viewer, "target");
    Check(managerHistory.Events.Any(item => item.EventId == "viewer-manager-hidden"), "Current assigned event manager may view a hidden candidate");
    var serialized = JsonSerializer.Serialize(history);
    Check(!serialized.Contains("target-private@test.invalid", StringComparison.Ordinal)
          && !serialized.Contains("secret-profile-answer", StringComparison.Ordinal)
          && !serialized.Contains("viewer-session", StringComparison.Ordinal),
        "Response excludes email, private answers, and session/token values");
    Check(typeof(ProfileRegistrationHistoryView).GetProperties().Select(property => property.Name).Order()
        .SequenceEqual(new[] { "Events", "UserId" }), "DTO exposes no total, hasMore, cursor, or hidden-item count");

    await Deny<UnauthorizedAccessException>(() => core.ReadProfileRegistrationHistoryAsync(new ClaimsPrincipal(new ClaimsIdentity()), "target"),
        "Requires an authenticated actor session before target lookup");
    await Deny<UnauthorizedAccessException>(() => core.ReadProfileRegistrationHistoryAsync(Who("viewer", "stale-stamp"), "target"),
        "Rejects a revoked security stamp");
    await Deny<UnauthorizedAccessException>(() => core.ReadProfileRegistrationHistoryAsync(viewer, "disabled"),
        "Rejects a disabled target");
    await Deny<UnauthorizedAccessException>(() => core.ReadProfileRegistrationHistoryAsync(viewer, "unconfirmed"),
        "Rejects an unconfirmed target");
    await using (var db = await factory.CreateDbContextAsync())
    {
        db.RevokedAuthSessions.Add(new RevokedAuthSession
        {
            UserId = "viewer", TokenHash = SemEndpoints.Hash("viewer-session"), RevokedAt = Utc(clock.Now)
        });
        await db.SaveChangesAsync();
    }
    await Deny<UnauthorizedAccessException>(() => core.ReadProfileRegistrationHistoryAsync(viewer, "target"),
        "Rejects a revoked current session");

    // Add 101 equally dated candidates. Deterministic ascending-ID tie ordering places the
    // visible candidate just outside the 100-item window; filtering first would expose it.
    await using (var db = await factory.CreateDbContextAsync())
    {
        for (var index = 0; index <= 100; index++)
        {
            var id = $"window-{index:000}";
            AddEvent(db, id, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
                activePhase: index == 100 ? "View" : "Hidden", manual: index == 100);
            Spectate(db, id, "target");
        }
        await db.SaveChangesAsync();
    }
    await using var observer = new SqliteConnection(connectionString);
    await observer.OpenAsync();
    async Task<long> DataVersion()
    {
        await using var command = observer.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    var dataVersionBefore = await DataVersion();
    var windowResult = await core.ReadProfileRegistrationHistoryAsync(Who("viewer2"), "target");
    var dataVersionAfter = await DataVersion();
    Check(windowResult.Events.Length == 0, "Applies deterministic 100-candidate window before visibility filtering");
    Check(!JsonSerializer.Serialize(windowResult).Contains("window-", StringComparison.Ordinal)
          && !JsonSerializer.Serialize(windowResult).Contains("Title window-", StringComparison.Ordinal),
        "Does not disclose hidden candidate IDs or titles when the visible event is outside the window");
    Check(dataVersionBefore == dataVersionAfter, "Read transaction does not write to the SQLite database");

    Console.WriteLine($"Profile registration history checks passed: {passed}");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, true);
}

sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
}
