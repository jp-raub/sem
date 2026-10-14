using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-import-eligibility-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var clock = new TestClock(DateTimeOffset.Parse("2026-11-01T12:00:00Z"));
var services = new ServiceCollection();
services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + Path.Combine(root, "eligibility.db")), ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddSingleton<TimeProvider>(clock);
Microsoft.Extensions.DependencyInjection.DataProtectionServiceCollectionExtensions.AddDataProtection(services);
services.AddSingleton<BusinessNotificationStore>();
services.AddScoped<CoreFoundationService>();
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var core = scope.ServiceProvider.GetRequiredService<CoreFoundationService>();
var passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
async Task Deny<T>(Func<Task> action, string label) where T : Exception
{
    var denied = false;
    try { await action(); } catch (T) { denied = true; }
    Check(denied, label);
}
ClaimsPrincipal Who(string id) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier, id),
    new Claim("AspNet.Identity.SecurityStamp", id + "-stamp"),
    new Claim(PasskeyAdditionService.SessionClaim, id + "-session")], "Identity.Application"));
string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
EventEdit Edit(string title, string phase, long version) => new(title, "", "", clock.Now.AddDays(2), null, "", phase, ["manager"], version);
PhasePlanEdit Plan(PhasePlanView view, bool manual, CustomPhaseEdit[] custom, string? active = null) =>
    new(view.RowVersion, manual, active ?? view.Phases.Single(p => p.IsActive).Id, custom);
CustomPhaseEdit Custom(DateTimeOffset start, DateTimeOffset? end, string? id = null) =>
    new(id ?? Guid.NewGuid().ToString(), "Registration", 2, start, end);
async Task<SessionEvent> LoadEvent(string id)
{
    await using var db = await factory.CreateDbContextAsync();
    return await db.SessionEvents.Include(e => e.Phases).Include(e => e.Admins).SingleAsync(e => e.Id == id);
}
var admin = Who("admin");
var manager = Who("manager");

try
{
    // Seed a real prior schema, including business data, then upgrade through the new migration.
    await using (var db = await factory.CreateDbContextAsync())
    {
        await db.GetService<IMigrator>().MigrateAsync("20261014120648_AddManualNotificationHistoryIndex");
        db.Users.AddRange(
            new ApplicationUser { Id = "admin", UserName = "admin@test", Email = "admin@test", DisplayName = "Admin", Enabled = true, EmailConfirmed = true, IsAdmin = true, SecurityStamp = "admin-stamp" },
            new ApplicationUser { Id = "manager", UserName = "manager@test", Email = "manager@test", DisplayName = "Manager", Enabled = true, EmailConfirmed = true, SecurityStamp = "manager-stamp" },
            new ApplicationUser { Id = "member", UserName = "member@test", Email = "member@test", DisplayName = "Member", Enabled = true, EmailConfirmed = true, SecurityStamp = "member-stamp" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO SessionEvents(Id,Title,Description,Location,ScheduledDate,AnnouncementMessage,PhaseKind,ManualPhaseOverride,CreatedAt,RowVersion) VALUES ('legacy','Retained title','legacy description','Room','2026-10-01T00:00:00.000Z','announcement','Hidden',1,'2026-09-01T00:00:00.000Z',17);");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO EventAdmins(EventId,UserId,GrantedAt) VALUES ('legacy','manager','2026-09-01T00:00:00.000Z');");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Phases(Id,EventId,Kind,Name,MaxPicks,IsActive,SortOrder) VALUES ('legacy:hidden','legacy','Hidden','Hidden',0,1,0),('legacy:view','legacy','View','View',0,0,1);");
        await db.GetService<IMigrator>().MigrateAsync();
    }
    var legacy = await LoadEvent("legacy");
    Check(legacy.Title == "Retained title" && legacy.Description == "legacy description" && legacy.RowVersion == 17 && legacy.Admins.Single().UserId == "manager", "Additive upgrade preserves legacy event and manager data");
    Check(!legacy.ImportHistoryKnown && !legacy.HasEverRegistrationPhase, "Upgraded legacy rows default to unknown without claiming prior Custom use");
    var legacyAssessment = CoreEventImportHistory.Assess(legacy, legacy.Phases.Single(p => p.Kind == "Hidden"), clock.Now);
    Check(!legacyAssessment.IsEligible && legacyAssessment.RejectionReason == CoreEventImportHistory.ImportHistoryUnknown, "Legacy event is fail-closed even with no known registrations");

    var newId = await core.SaveEventAsync(admin, null, Edit("New known event", "Hidden", 0));
    var created = await LoadEvent(newId);
    Check(created.ImportHistoryKnown && !created.HasEverRegistrationPhase, "SaveEvent creates new event with known empty history");

    // A past window is only uncertainty. Preview stays pure; a later plan edit persists that uncertainty first.
    var pastId = await core.SaveEventAsync(admin, null, Edit("Past window", "Hidden", 0));
    var pastStart = clock.Now.AddHours(-3);
    var pastEnd = clock.Now.AddHours(-2);
    await using (var db = await factory.CreateDbContextAsync())
    {
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Phases(Id,EventId,Kind,Name,MaxPicks,ActivateAt,DeactivateAt,IsActive,SortOrder) VALUES ({0},{1},'Custom','Past registration',2,{2},{3},0,2);", "past-custom", pastId, Iso(pastStart), Iso(pastEnd));
    }
    var pastPlan = await core.ReadPhasePlanAsync(admin, pastId);
    var beforePreview = await LoadEvent(pastId);
    var pure = CoreEventImportHistory.Assess(beforePreview, beforePreview.Phases.Single(p => p.Kind == "Hidden"), clock.Now);
    Check(!pure.IsEligible && pure.RejectionReason == CoreEventImportHistory.UnobservedPastCustomWindow, "Unobserved elapsed Custom window is an explicit uncertainty refusal");
    Check(beforePreview.ImportHistoryKnown && !beforePreview.HasEverRegistrationPhase, "Read-only eligibility inspection does not persist or invent history");
    await core.SavePhasePlanAsync(admin, pastId, Plan(pastPlan, true, []));
    var afterRemoval = await LoadEvent(pastId);
    Check(!afterRemoval.ImportHistoryKnown && !afterRemoval.HasEverRegistrationPhase, "Removing a possible past window first makes unknown history sticky without claiming actual entry");
    var removedPlan = await core.ReadPhasePlanAsync(admin, pastId);
    await core.SavePhasePlanAsync(admin, pastId, Plan(removedPlan, true, []));
    afterRemoval = await LoadEvent(pastId);
    Check(!afterRemoval.ImportHistoryKnown && CoreEventImportHistory.Assess(afterRemoval, afterRemoval.Phases.Single(p => p.Kind == "Hidden"), clock.Now).RejectionReason == CoreEventImportHistory.ImportHistoryUnknown, "Deleting the schedule cannot erase prior uncertainty");

    // A future interval canceled before activation is not treated as an uncertain past entry.
    var canceledId = await core.SaveEventAsync(admin, null, Edit("Canceled future", "Hidden", 0));
    var canceledPlan = await core.ReadPhasePlanAsync(admin, canceledId);
    var future = Custom(clock.Now.AddHours(2), clock.Now.AddHours(4));
    canceledPlan = await core.SavePhasePlanAsync(admin, canceledId, Plan(canceledPlan, true, [future]));
    canceledPlan = await core.SavePhasePlanAsync(admin, canceledId, Plan(canceledPlan, true, []));
    var canceled = await LoadEvent(canceledId);
    var tombstone = canceled.Phases.Single(p => p.Kind == "Custom");
    Check(tombstone.DeletedAt is not null && DateTimeOffset.Parse(tombstone.DeletedAt) < DateTimeOffset.Parse(tombstone.ActivateAt!), "Future interval tombstone predates activation");
    var canceledAssessment = CoreEventImportHistory.Assess(canceled, canceled.Phases.Single(p => p.Kind == "Hidden"), clock.Now);
    Check(canceledAssessment.IsEligible && canceled.ImportHistoryKnown && !canceled.HasEverRegistrationPhase, "Canceled future interval does not invalidate known empty history");

    // The automatic Custom phase is effective before the periodic worker runs.
    var autoId = await core.SaveEventAsync(admin, null, Edit("Automatic Custom", "Hidden", 0));
    var autoPlan = await core.ReadPhasePlanAsync(admin, autoId);
    var activeCustom = Custom(clock.Now.AddHours(-1), clock.Now.AddHours(1));
    autoPlan = await core.SavePhasePlanAsync(admin, autoId, Plan(autoPlan, false, [activeCustom]));
    var autoRead = await core.ReadParticipationAsync(manager, autoId);
    Check(autoRead.PhaseKind == "Custom", "Automatic Custom is effective before relying on worker activation");
    var autoEvent = await LoadEvent(autoId);
    Check(autoEvent.HasEverRegistrationPhase, "Phase write observing effective Custom persists monotonic history");
    var autoAssessment = CoreEventImportHistory.Assess(autoEvent, autoEvent.Phases.Single(p => p.Kind == "Custom"), clock.Now);
    Check(!autoAssessment.IsEligible && autoAssessment.RejectionReason == CoreEventImportHistory.EffectivePhaseMustBeHidden, "Current Custom phase cannot pass the Hidden-only guard");

    // A no-op worker pass may only write a marker; it must not inflate changed-phase count.
    await using (var db = await factory.CreateDbContextAsync())
    {
        await db.Database.ExecuteSqlRawAsync("UPDATE SessionEvents SET HasEverRegistrationPhase=0 WHERE Id={0};", autoId);
    }
    var beforeWorker = await LoadEvent(autoId);
    Check(await core.EvaluateAutomaticPhasesAsync() == 0, "Worker reports only actual phase changes, not marker-only writes");
    var afterWorker = await LoadEvent(autoId);
    Check(afterWorker.HasEverRegistrationPhase && afterWorker.RowVersion == beforeWorker.RowVersion + 1, "Marker-only automatic observation advances RowVersion");

    // Compatibility overview switches must capture computed Custom before switching to a default.
    var compatId = await core.SaveEventAsync(admin, null, Edit("Compatibility path", "Hidden", 0));
    await using (var db = await factory.CreateDbContextAsync())
    {
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Phases(Id,EventId,Kind,Name,MaxPicks,ActivateAt,DeactivateAt,IsActive,SortOrder) VALUES ({0},{1},'Custom','Computed now',2,{2},{3},0,2);", "compat-custom", compatId, Iso(clock.Now.AddMinutes(-2)), Iso(clock.Now.AddHours(1)));
    }
    var compatBefore = await LoadEvent(compatId);
    Check(CoreEventImportHistory.Assess(compatBefore, compatBefore.Phases.Single(p => p.Kind == "Custom"), clock.Now).RejectionReason == CoreEventImportHistory.EffectivePhaseMustBeHidden, "Pure assessment denies computed Custom before worker persistence");
    var compatEdited = await core.SaveEventAsync(admin, compatId, Edit("Compatibility path", "View", compatBefore.RowVersion));
    Check(compatEdited == compatId && (await LoadEvent(compatId)).HasEverRegistrationPhase, "Compatibility visibility switch records computed Custom before replacing it");
    var compatNow = await LoadEvent(compatId);
    await core.SaveEventAsync(admin, compatId, Edit("Compatibility path", "Hidden", compatNow.RowVersion));
    compatNow = await LoadEvent(compatId);
    var compatGate = CoreEventImportHistory.Assess(compatNow, compatNow.Phases.Single(p => p.Kind == "Hidden"), clock.Now);
    Check(!compatGate.IsEligible && compatGate.RejectionReason == CoreEventImportHistory.CustomRegistrationPhaseAlreadyObserved, "Hidden compatibility switch remains blocked by actual observed Custom history");

    // Rejected participation does not observe history; only successful Custom writes do.
    var participationId = await core.SaveEventAsync(admin, null, Edit("Participation observation", "Hidden", 0));
    await using (var db = await factory.CreateDbContextAsync())
    {
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Phases(Id,EventId,Kind,Name,MaxPicks,ActivateAt,DeactivateAt,IsActive,SortOrder) VALUES ({0},{1},'Custom','Signup',2,{2},{3},0,2);", "participation-custom", participationId, Iso(clock.Now.AddMinutes(-1)), Iso(clock.Now.AddHours(1)));
    }
    var participation = await core.ReadParticipationAsync(manager, participationId);
    await Deny<System.ComponentModel.DataAnnotations.ValidationException>(() => core.SubmitParticipationAsync(manager, participationId,
        new ParticipationEdit(Guid.NewGuid().ToString(), participation.RowVersion, participation.PhaseId, "Answers", [], [])), "Rejected Custom participation write throws before observing history");
    Check(!(await LoadEvent(participationId)).HasEverRegistrationPhase, "Rejected participation leaves history markers unchanged");
    var successful = await core.SubmitParticipationAsync(manager, participationId,
        new ParticipationEdit(Guid.NewGuid().ToString(), participation.RowVersion, participation.PhaseId, "Spectate", [], []));
    Check(successful.Succeeded == 1 && (await LoadEvent(participationId)).HasEverRegistrationPhase, "Successful Custom participation write durably observes the phase");

    Check((await LoadEvent(participationId)).RowVersion == participation.RowVersion + 1, "Participation marker write advances event RowVersion");

    // A manager's current role does not bypass revocation of the authenticated session.
    await using (var db = await factory.CreateDbContextAsync())
    {
        await db.RevokedAuthSessions.AddAsync(new RevokedAuthSession { TokenHash = SemEndpoints.Hash("manager-session"), UserId = "manager", RevokedAt = Iso(clock.Now) });
        await db.SaveChangesAsync();
    }
    await Deny<UnauthorizedAccessException>(() => core.ReadPhasePlanAsync(manager, newId), "Revoked session is denied despite current manager assignment");

    Console.WriteLine("Event import eligibility checks passed: " + passed);
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, true);
}

sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
