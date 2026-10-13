using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;
var root = Path.Combine(Path.GetTempPath(), "sem-core-check-" + Guid.NewGuid()); Directory.CreateDirectory(root);
var path = Path.Combine(root, "core.db"); var services = new ServiceCollection();
services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + path), ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddSingleton(TimeProvider.System); Microsoft.Extensions.DependencyInjection.DataProtectionServiceCollectionExtensions.AddDataProtection(services);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var core = scope.ServiceProvider.GetRequiredService<CoreFoundationService>(); var passed = 0;
ClaimsPrincipal Principal(string id, string session = "session-1", string? stamp = null, bool forgedAdmin = false) => new(new ClaimsIdentity(new[]
{ new Claim(ClaimTypes.NameIdentifier, id), new Claim("AspNet.Identity.SecurityStamp", stamp ?? id + "-stamp"),
  new Claim(PasskeyAdditionService.SessionClaim, id + "-" + session), new Claim(ClaimTypes.Role, forgedAdmin ? "Admin" : "Member") }, "Identity.Application"));
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
async Task Deny<T>(Func<Task> operation, string name) where T : Exception
{ bool denied = false; try { await operation(); } catch (T) { denied = true; } Check(denied, name); }
try
{
    await using (var db = await factory.CreateDbContextAsync())
    {
        await SqliteStorageInitializer.InitializeAsync(db);
        db.Users.AddRange(new ApplicationUser { Id = "admin", DisplayName = "Admin", UserName = "admin@test.example", Email = "admin@test.example", SecurityStamp = "admin-stamp", Enabled = true, EmailConfirmed = true, IsAdmin = true },
            new ApplicationUser { Id = "manager", DisplayName = "Manager", UserName = "manager@test.example", Email = "manager@test.example", SecurityStamp = "manager-stamp", Enabled = true, EmailConfirmed = true },
            new ApplicationUser { Id = "member", DisplayName = "Member", UserName = "member@test.example", Email = "member@test.example", SecurityStamp = "member-stamp", Enabled = true, EmailConfirmed = true },
            new ApplicationUser { Id = "disabled", DisplayName = "Disabled", UserName = "disabled@test.example", Email = "disabled@test.example", SecurityStamp = "disabled-stamp", Enabled = false, EmailConfirmed = true });
        db.SiteStates.Add(new SiteState { Initialized = true, Name = "Retained bootstrap site", PendingAdminId = "not-public" }); await db.SaveChangesAsync();
    }
    var admin = Principal("admin"); var manager = Principal("manager"); var member = Principal("member");
    Check((await core.ReadAsync(admin)).Service.Name == "Retained bootstrap site", "Service name falls back to preserved bootstrap name without invented data");
    Check((await core.ReadAsync(member)).Profile.RowVersion == 0, "Missing profile is an empty editable draft, not a fabricated profile");
    await Deny<UnauthorizedAccessException>(() => core.ReadAsync(new ClaimsPrincipal(new ClaimsIdentity())), "Unauthenticated workspace is denied");
    await Deny<UnauthorizedAccessException>(() => core.ReadAsync(Principal("disabled")), "Disabled account is denied");
    await Deny<UnauthorizedAccessException>(() => core.ReadAsync(Principal("member", stamp: "stale")), "Stale security stamp is denied on every operation");
    var noSession = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "member"), new Claim("AspNet.Identity.SecurityStamp", "member-stamp") }, "Identity.Application"));
    await Deny<UnauthorizedAccessException>(() => core.ReadAsync(noSession), "Legacy session without unique claim must log in again for business operations");
    await Deny<UnauthorizedAccessException>(() => core.SaveServiceAsync(Principal("member", forgedAdmin: true), new("Forged", "", 0)), "Forged role claim does not grant service permission");
    await Deny<ValidationException>(() => core.SaveServiceAsync(admin, new(new string('n', 61), "", 0)), "Service name maximum 60 is enforced");
    await Deny<ValidationException>(() => core.SaveServiceAsync(admin, new("SEM", new string('d', 81), 0)), "Service description maximum 80 is enforced");
    await core.SaveServiceAsync(admin, new("SEM Core", "Description", 0));
    var savedService = (await core.ReadAsync(member)).Service;
    Check(savedService.Name == "SEM Core" && savedService.Description == "Description" && savedService.RowVersion == 1, "Global service save is shared and versioned");
    await using (var db = await factory.CreateDbContextAsync())
        Check((await db.SiteStates.SingleAsync()).Name == "SEM Core" && (await db.SiteStates.SingleAsync()).PendingAdminId == "not-public", "Display-name synchronization does not overwrite bootstrap-only fields");
    Check(!JsonSerializer.Serialize(savedService).Contains("not-public"), "Service DTO does not leak bootstrap details");
    await Deny<CoreConflictException>(() => core.SaveServiceAsync(admin, new("Lost update", "", 0)), "Stale service draft cannot overwrite a saved version");
    async Task<bool> Race(string value)
    {
        try { await core.SaveServiceAsync(admin, new(value, "Concurrent draft", 1)); return true; }
        catch (CoreConflictException) { return false; }
    }
    var outcomes = await Task.WhenAll(Task.Run(() => Race("Concurrent A")), Task.Run(() => Race("Concurrent B")));
    Check(outcomes.Count(x => x) == 1 && (await core.ReadAsync(admin)).Service.RowVersion == 2,
        "Concurrent service drafts commit once without lost updates");
    await core.SaveProfileAsync(member, new("Member updated", "ライオン", "ライオン", [new("X", "https://example.test/profile")], 0));
    var profile = await core.ReadProfileAsync(admin, "member");
    Check(profile.DisplayName == "Member updated" && profile.Reading == "らいおん" && profile.CallName == "ライオン", "Own profile stores normalized hiragana and valid call name");
    Check(profile.Social.Single().Url == "https://example.test/profile", "Social link persists as HTTP/HTTPS data without server fetching");
    Check(!JsonSerializer.Serialize(profile).Contains("member@test.example"), "Member-readable profile never includes authentication email");
    Check((await core.ReadAsync(admin)).Profile.DisplayName == "Admin", "Updating own profile does not edit the administrator profile");
    await Deny<ValidationException>(() => core.SaveProfileAsync(member, new("Member", "漢字", "", [], 1)), "Invalid reading is rejected");
    await Deny<ValidationException>(() => core.SaveProfileAsync(member, new("Member", "", "English", [], 1)), "Invalid call name is rejected");
    await Deny<ValidationException>(() => core.SaveProfileAsync(member, new("Member", "", "", [new("bad", "javascript:alert(1)")], 1)), "Script social URL is rejected");
    await Deny<ValidationException>(() => core.SaveProfileAsync(member, new("Member", "", "", [new("bad", "https://user:secret@example.test/")], 1)), "Credential-bearing social URL is rejected");
    await Deny<CoreConflictException>(() => core.SaveProfileAsync(member, new("Lost profile", "", "", [], 0)), "Stale profile cannot overwrite the saved profile");
    Check((await core.ReadProfileAsync(admin, "member")).DisplayName == "Member updated", "Invalid and stale profile writes have no partial effect");
    var starts = new DateTimeOffset(2026, 10, 20, 13, 0, 0, TimeSpan.FromHours(9));
    EventEdit Edit(string title, string[] managers, long version = 0, string phase = "Hidden") => new(title, "Overview", "Venue", starts, starts, "Notice", phase, managers, version);
    await Deny<UnauthorizedAccessException>(() => core.SaveEventAsync(member, null, Edit("Unauthorized", [])), "Ordinary member cannot create an event");
    await Deny<ValidationException>(() => core.SaveEventAsync(admin, null, Edit("Bad manager", ["disabled"])), "Disabled manager rejects the whole event creation");
    var event1 = await core.SaveEventAsync(admin, null, Edit("Hidden event", ["manager", "manager", "member"]));
    var event2 = await core.SaveEventAsync(admin, null, Edit("Another hidden event", []));
    var managerView = await core.ReadAsync(manager);
    Check(managerView.Events.Length == 1 && managerView.Events[0].Id == event1 && managerView.Events[0].CanManage, "Assigned manager sees only the assigned hidden event");
    Check(managerView.Events[0].ManagerIds.Length == 2, "Multiple managers are persisted with composite-key deduplication");
    Check(managerView.Events[0].ScheduledDate == "2026-10-20T04:00:00.000Z" && managerView.Events[0].OpensAt == managerView.Events[0].ScheduledDate, "UTC canonical storage and opening equal to start are supported");
    await Deny<UnauthorizedAccessException>(() => core.SaveEventAsync(manager, event2, Edit("Other event attack", ["manager"], 1)), "Manager cannot edit or acquire another event");
    await Deny<ValidationException>(() => core.SaveEventAsync(manager, event1, Edit("Invalid phase", ["manager"], 1, "Custom")), "Unsupported registration phase is not silently implemented");
    await core.SaveEventAsync(manager, event1, Edit("Published for viewing", ["manager"], 1, "View"));
    var memberEvents = (await core.ReadAsync(member)).Events;
    Check(memberEvents.Single().PhaseKind == "View" && !memberEvents.Single().CanManage, "Viewing a public event does not grant its management permission");
    await Deny<CoreConflictException>(() => core.SaveEventAsync(admin, event1, Edit("Stale event", ["manager"], 1)), "Stale event and manager assignment draft is rejected atomically");
    await core.SaveEventAsync(admin, event1, Edit("Zero managers allowed", [], 2, "Hidden"));
    Check((await core.ReadAsync(manager)).Events.Length == 0, "Manager removal takes effect on the next operation even with cached authentication");
    await Deny<UnauthorizedAccessException>(() => core.SaveEventAsync(manager, event1, Edit("After removal", ["manager"], 3)), "Removed manager cannot save through an existing identity");
    await using (var db = await factory.CreateDbContextAsync())
    {
        Check(await db.Users.Where(u => u.Id == "manager" || u.Id == "member").AllAsync(u => !u.IsAdmin), "Event assignments never promote global-admin flags");
        db.RevokedAuthSessions.Add(new() { TokenHash = SemEndpoints.Hash("member-session-1"), UserId = "member", RevokedAt = "2026-10-20T00:00:00.000Z" }); await db.SaveChangesAsync();
    }
    await Deny<UnauthorizedAccessException>(() => core.ReadAsync(member), "Logged-out session cannot read through a still-connected circuit");
    await Deny<UnauthorizedAccessException>(() => core.SaveProfileAsync(member, new("After logout", "", "", [], 1)), "Logged-out session cannot write through a still-connected circuit");
    Check((await core.ReadAsync(Principal("member", "session-2"))).Profile.DisplayName == "Member updated", "An independent session is not revoked by another session's logout");
    await using (var db = await factory.CreateDbContextAsync())
    {
        var a = await db.Users.FindAsync("admin"); a!.IsAdmin = false; await db.SaveChangesAsync();
    }
    await Deny<UnauthorizedAccessException>(() => core.SaveServiceAsync(admin, new("After demotion", "", 2)), "Global-admin demotion takes effect without trusting cached role claims");
    await using (var db = await factory.CreateDbContextAsync())
    {
        Check(await db.Audits.AnyAsync(a => a.Action == "OwnProfileUpdated" && a.ActorId == "member") && await db.Audits.AnyAsync(a => a.Action == "SessionEventUpdated" && a.ActorId == "manager"), "Successful writes persist actor/target audit actions");
        Check(!await db.Users.AnyAsync(u => !u.EmailConfirmed && u.Enabled), "Foundation does not bypass confirmed-account gates");
        await SqliteStorageInitializer.InitializeAsync(db);
        Check(await db.SessionEvents.CountAsync() == 2 && await db.UserProfiles.CountAsync() == 1, "Restart retains formal events and profiles without resetting them");
    }
    Console.WriteLine("Core foundation checks passed: " + passed);
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
