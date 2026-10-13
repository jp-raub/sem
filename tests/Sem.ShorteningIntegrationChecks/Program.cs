using System.ComponentModel.DataAnnotations;
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

var root = Path.Combine(Path.GetTempPath(), "sem-shortening-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var time = new Clock { Now = DateTimeOffset.Parse("2026-11-01T03:10:00.123Z") };
var services = new ServiceCollection();
services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o =>
    o.UseSqlite("Data Source=" + Path.Combine(root, "data.db")), ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
    .AddEntityFrameworkStores<ApplicationDbContext>();
services.AddSingleton<TimeProvider>(time);
Microsoft.Extensions.DependencyInjection.DataProtectionServiceCollectionExtensions.AddDataProtection(services);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var core = scope.ServiceProvider.GetRequiredService<CoreFoundationService>();
int passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
async Task Deny<T>(Func<Task> work, string label) where T : Exception {
    try { await work(); } catch (T) { Check(true, label); return; } throw new Exception("Expected denial: " + label);
}
ClaimsPrincipal Who(string id) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier, id),
    new Claim("AspNet.Identity.SecurityStamp", id + "-stamp"),
    new Claim(PasskeyAdditionService.SessionClaim, id + "-session")], "Identity.Application"));
ShorteningPolicyEdit Policy(ShorteningPlan p, string part, bool prep, int minimum) =>
    new(Guid.NewGuid().ToString(), p.RowVersion, p.ProgressVersion, part, prep, prep ? minimum : 0, !prep, prep ? 0 : minimum);
ShorteningApproveEdit Approve(ShorteningPlan p) =>
    new(Guid.NewGuid().ToString(), p.RowVersion, p.ProgressVersion, p.Proposal.Token);
var manager = Who("manager"); var admin = Who("admin");
string oldJson = "";
try {
    await using (var db = await factory.CreateDbContextAsync()) {
        await db.GetService<IMigrator>().MigrateAsync("20261013041622_AddEventProgress");
        foreach (var id in new[] { "admin", "manager", "manager2", "member" })
            db.Users.Add(new() { Id = id, Email = id + "@test", UserName = id + "@test", DisplayName = id,
                Enabled = true, EmailConfirmed = true, SecurityStamp = id + "-stamp", IsAdmin = id == "admin" });
        await db.SaveChangesAsync();
        foreach (var id in new[] { "main", "race", "recordrace", "future", "legacy", "amend", "other" }) {
            var date = DateTimeOffset.Parse("2026-11-01T03:00:00Z").AddDays(id == "future" ? 1 : 0);
            var e = new SessionEvent { Id = id, Title = id, ScheduledDate = date.ToString("O"),
                PlannedEndAt = date.AddSeconds(600).ToString("O"), DefaultPrepSeconds = 60, RoundStarts = false,
                PhaseKind = "View", ManualPhaseOverride = true, CreatedAt = date.AddDays(-1).ToString("O"), RowVersion = 7 };
            db.SessionEvents.Add(e);
            db.Phases.Add(new() { Id = id + "-view", EventId = id, Name = "View", Kind = "View", IsActive = true });
            if (id != "other") {
                db.EventAdmins.Add(new() { EventId = id, UserId = "manager", GrantedAt = date.ToString("O") });
                db.EventAdmins.Add(new() { EventId = id, UserId = "manager2", GrantedAt = date.ToString("O") });
            }
            db.EventPartDefinitions.Add(new() { Id = id + "-voice", EventId = id, Name = "Vo", NormalizedName = "VO", Category = "Sing" });
            db.Songs.Add(new() { Id = id + "-song", EventId = id, Title = "Song", PerformanceSeconds = 60, PrepSeconds = null });
            db.SongParts.Add(new() { Id = id + "-slot", EventId = id, SongId = id + "-song",
                PartDefinitionId = id + "-voice", AssignedUserId = "member", PhaseId = id + "-view", RowVersion = 2 });
            db.EventParticipantStates.Add(new() { EventId = id, UserId = "member", RowVersion = 3 });
            db.EventQuestions.Add(new() { Id = id + "-q", EventId = id, Text = "Private", Type = "Text" });
            db.EventQuestionAnswers.Add(new() { QuestionId = id + "-q", UserId = "member", Value = "retained" });
            db.TimeParts.AddRange(
                new() { Id = id + "-break", EventId = id, Kind = "Transition", Name = "Break", DurationSeconds = 900 },
                new() { Id = id + "-song", EventId = id, Kind = "Song", SongId = id + "-song", SortOrder = 1 },
                new() { Id = id + "-tail", EventId = id, Kind = "Transition", Name = "Tail", DurationSeconds = 300, SortOrder = 2 });
        }
        await db.SaveChangesAsync();
        var legacy = await db.SessionEvents.SingleAsync(e => e.Id == "legacy");
        var rows = TimetableCalculator.Calculate(legacy, await db.TimeParts.Where(x => x.EventId == "legacy").ToArrayAsync(),
            await db.Songs.Where(x => x.EventId == "legacy").ToArrayAsync()).Rows;
        var old = new ProgressBaseline(legacy.ScheduledDate, false, 60, "seconds", legacy.PlannedEndAt, rows,
            new() { ["legacy-song"] = null });
        oldJson = JsonSerializer.Serialize(JsonSerializer.SerializeToElement(old).EnumerateObject()
            .Where(p => p.Name != "PlannedPolicies").ToDictionary(p => p.Name, p => p.Value));
        db.EventProgressStates.Add(new() { EventId = "legacy", SourceVersion = 7, RowVersion = 5,
            PlanJson = oldJson, CreatedAt = "2026-11-01T03:00:00Z" });
        await db.SaveChangesAsync();
        await SqliteStorageInitializer.InitializeAsync(db);
        Check(await db.EventShorteningPolicies.CountAsync() == 0 && await db.ShorteningOperations.CountAsync() == 0,
            "Populated PR14 migration starts with no invented policies or approvals");
        Check((await db.EventProgressStates.SingleAsync()).PlanJson == oldJson &&
            (await db.SongParts.SingleAsync(x => x.Id == "main-slot")).AssignedUserId == "member" &&
            (await db.EventQuestionAnswers.SingleAsync(x => x.QuestionId == "main-q")).Value == "retained" &&
            (await db.EventParticipantStates.SingleAsync(x => x.EventId == "main")).RowVersion == 3,
            "Migration preserves exact old snapshot assignments answers and participant version");
        Check(!db.Database.HasPendingModelChanges(), "EF model matches generated shortening migration");
    }
    var p = await core.ReadShorteningAsync(manager, "main");
    Check(!p.Live && p.RowVersion == 7 && p.ProgressVersion == 0 && p.Policies.Length == 3 &&
        !p.Proposal.HasCuts, "Read gives protected default policies without live write");
    await using (var db = await factory.CreateDbContextAsync())
        Check(await db.EventProgressStates.CountAsync() == 1 && await db.ShorteningOperations.CountAsync() == 0,
            "Read does not capture original or create operations");
    await Deny<UnauthorizedAccessException>(() => core.ReadShorteningAsync(Who("member"), "main"), "Member cannot read management proposal");
    await Deny<UnauthorizedAccessException>(() => core.ReadShorteningAsync(manager, "other"), "Event manager cannot read unassigned event");
    await Deny<UnauthorizedAccessException>(() => core.SaveShorteningPolicyAsync(Who("member"), "main", Policy(p, "main-break", false, 300)), "Member cannot forge policy save");
    await Deny<UnauthorizedAccessException>(() => core.ApproveShorteningAsync(Who("member"), "main", Approve(p)), "Member cannot forge approval");
    var firstPolicy = Policy(p, "main-break", false, 300);
    var result = await core.SaveShorteningPolicyAsync(manager, "main", firstPolicy);
    Check(result.RowVersion == 8 && result.ProgressVersion == 0, "Pre-live policy increments structural version only");
    Check((await core.SaveShorteningPolicyAsync(manager, "main", firstPolicy)).Replayed, "Identical policy retry returns saved result");
    await Deny<ValidationException>(() => core.SaveShorteningPolicyAsync(manager, "main", firstPolicy with { MinDurationSeconds = 301 }), "Policy request ID cannot change payload");
    await Deny<ValidationException>(() => core.SaveShorteningPolicyAsync(Who("manager2"), "main", firstPolicy), "Another authorized actor cannot reuse request ID");
    await Deny<CoreConflictException>(() => core.SaveShorteningPolicyAsync(manager, "main", firstPolicy with { RequestId = Guid.NewGuid().ToString() }), "Fresh-ID stale policy version rejected");
    p = await core.ReadShorteningAsync(manager, "main");
    await Deny<ValidationException>(() => core.SaveShorteningPolicyAsync(manager, "main", Policy(p, "main-song", false, 0)), "Performance shortening rejected by save service");
    await Deny<ValidationException>(() => core.SaveShorteningPolicyAsync(manager, "main", Policy(p, "other-break", false, 0)), "Other-event part cannot be targeted");
    await Deny<ValidationException>(() => core.SaveShorteningPolicyAsync(manager, "main", Policy(p, "main-break", false, 901)), "Minimum above latest duration rejected");
    await Deny<ValidationException>(() => core.SaveShorteningPolicyAsync(manager, "main", Policy(p, "main-break", false, -1)), "Negative policy floor rejected");
    await core.SaveShorteningPolicyAsync(manager, "main", Policy(p, "main-song", true, 0));
    p = await core.ReadShorteningAsync(manager, "main");
    await core.SaveShorteningPolicyAsync(manager, "main", Policy(p, "main-tail", false, 0));
    p = await core.ReadShorteningAsync(manager, "main");
    Check(p.RowVersion == 10 && p.ProgressVersion == 0 && p.Proposal.Cuts.Length == 3 &&
        p.Proposal.RemainingSeconds == 0, "Saved planned conditions produce sequential proposal before runtime capture");
    var approval = Approve(p);
    await Deny<CoreConflictException>(() => core.ApproveShorteningAsync(manager, "main", approval with { Token = new('A', 64) }), "Forged token rejected before mutation");
    var applied = await core.ApproveShorteningAsync(manager, "main", approval);
    Check(applied.ProgressVersion == 1 && applied.RowVersion == 10 && applied.Cuts.Length == 3,
        "One authorized approval atomically applies cuts and increments live version");
    Check((await core.ApproveShorteningAsync(manager, "main", approval)).Replayed, "Approval retry never applies cuts twice");
    await Deny<ValidationException>(() => core.ApproveShorteningAsync(Who("manager2"), "main", approval), "Another actor cannot replay approved result");
    await Deny<CoreConflictException>(() => core.ApproveShorteningAsync(manager, "main", approval with { RequestId = Guid.NewGuid().ToString() }), "Old approval with fresh request cannot be applied twice");
    p = await core.ReadShorteningAsync(manager, "main");
    Check(p.Live && !p.Proposal.HasCuts, "Successful approval captures live baseline and leaves no second proposal");
    await Deny<ValidationException>(() => core.ApproveShorteningAsync(manager, "main", Approve(p)), "Empty proposal cannot be approved");
    string captured;
    await using (var db = await factory.CreateDbContextAsync()) {
        captured = (await db.EventProgressStates.SingleAsync(s => s.EventId == "main")).PlanJson;
        Check(JsonSerializer.Deserialize<ProgressBaseline>(captured)!.PlannedPolicies!.Length == 3 &&
            await db.ProgressMarks.CountAsync(m => m.EventId == "main") == 0,
            "First approval snapshots original policy conditions but invents no actual marks");
        Check((await db.ProgressEstimates.SingleAsync(e => e.EventId == "main" && e.PartId == "main-song")).PrepSeconds == 0 &&
            (await db.Songs.SingleAsync(s => s.Id == "main-song")).PrepSeconds is null,
            "Approved explicit zero affects live estimate only, not inherited original");
    }
    await core.SaveShorteningPolicyAsync(manager, "main", Policy(p, "main-tail", false, 10));
    p = await core.ReadShorteningAsync(manager, "main");
    Check(p.RowVersion == 10 && p.ProgressVersion == 2, "Live policy uses live version not event structural version");
    await using (var db = await factory.CreateDbContextAsync()) {
        Check((await db.EventProgressStates.SingleAsync(s => s.EventId == "main")).PlanJson == captured &&
            (await db.EventShorteningPolicies.SingleAsync(x => x.EventId == "main" && x.PartId == "main-tail" && x.Scope == "Plan")).MinDurationSeconds == 0 &&
            (await db.EventShorteningPolicies.SingleAsync(x => x.EventId == "main" && x.PartId == "main-tail" && x.Scope == "Live")).MinDurationSeconds == 10,
            "Live override preserves byte-exact original snapshot and planned conditions");
    }
    var progress = await core.ReadProgressAsync(manager, "main");
    await core.RecordProgressAsync(manager, "main", new(Guid.NewGuid().ToString(), progress.RowVersion, progress.ProgressVersion,
        progress.NextAction!.PartId, progress.NextAction.Point));
    p = await core.ReadShorteningAsync(manager, "main");
    Check(!p.Policies.Single(x => x.Policy.PartId == "main-break").CanEdit, "Started transition policy editor disabled");
    await Deny<ValidationException>(() => core.SaveShorteningPolicyAsync(manager, "main", Policy(p, "main-break", false, 0)), "Started-part policy mutation rejected");
    await core.SaveProgressEstimateAsync(manager, "main", new(Guid.NewGuid().ToString(), p.RowVersion, p.ProgressVersion, "main-break", null, 0));
    Check((await core.ReadShorteningAsync(manager, "main")).Policies.Length == 3,
        "Existing manual estimate of started transition remains readable despite unused policy minimum");
    progress = await core.ReadProgressAsync(manager, "main");
    await Deny<ValidationException>(() => core.SaveProgressEstimateAsync(manager, "main",
        new(Guid.NewGuid().ToString(), progress.RowVersion, progress.ProgressVersion, "main-tail", null, 9)),
        "Unstarted estimate cannot violate active minimum");
    var future = await core.ReadShorteningAsync(manager, "future");
    await core.SaveShorteningPolicyAsync(manager, "future", Policy(future, "future-break", false, 300));
    future = await core.ReadShorteningAsync(manager, "future");
    Check(!future.Live && !future.EventDay && future.Policies.All(x => x.CanEdit), "Future planned policy edit allowed without live capture");
    await Deny<ValidationException>(() => core.ApproveShorteningAsync(manager, "future", Approve(future)), "Off-day approval denied");
    var legacyPlan = await core.ReadShorteningAsync(manager, "legacy");
    Check(legacyPlan.Live && legacyPlan.ProgressVersion == 5 && !legacyPlan.Proposal.HasCuts, "Old snapshot without policy field remains protected and readable");
    await core.SaveShorteningPolicyAsync(manager, "legacy", Policy(legacyPlan, "legacy-break", false, 300));
    await using (var db = await factory.CreateDbContextAsync())
        Check((await db.EventProgressStates.SingleAsync(s => s.EventId == "legacy")).PlanJson == oldJson, "Legacy live policy save never rewrites old snapshot JSON");
    var amend = await core.ReadShorteningAsync(manager, "amend");
    await core.SaveShorteningPolicyAsync(manager, "amend", Policy(amend, "amend-break", false, 300));
    var table = await core.ReadTimetableAsync(manager, "amend");
    await Deny<ValidationException>(() => core.SaveTimetableAsync(manager, "amend",
        new(table.RowVersion, table.RoundStarts, table.ClockFormat, table.DurationFormat, table.SongIcon, table.PlannedEndAt is null ? null : TimetableCalculator.Instant(table.PlannedEndAt),
            table.Parts.Select(x => x.Id == "amend-break" ? x with { DurationSeconds = 299 } : x).ToArray(), true)),
        "Ordinary plan edit cannot invalidate saved minimum");
    Check((await core.ReadTimetableAsync(manager, "amend")).RowVersion == table.RowVersion &&
        (await core.ReadTimetableAsync(manager, "amend")).Parts.Single(x => x.Id == "amend-break").DurationSeconds == 900,
        "Rejected plan change rolls back row version and contents");
    async Task Prepare(string id) {
        var plan = await core.ReadShorteningAsync(manager, id);
        await core.SaveShorteningPolicyAsync(manager, id, Policy(plan, id + "-break", false, 300));
    }
    async Task<bool> Race(Func<Task> work) {
        try { await work(); return true; } catch (CoreConflictException) { return false; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { return false; }
    }
    await Prepare("race");
    var race = await core.ReadShorteningAsync(manager, "race");
    var raceResults = await Task.WhenAll(Race(() => core.ApproveShorteningAsync(manager, "race", Approve(race))),
        Race(() => core.ApproveShorteningAsync(manager, "race", Approve(race))));
    Check(raceResults.Count(v => v) == 1, "Concurrent approvals produce exactly one success");
    await using (var db = await factory.CreateDbContextAsync())
        Check(await db.ShorteningOperations.CountAsync(x => x.EventId == "race" && x.Kind == "Approve") == 1 &&
            (await db.EventProgressStates.SingleAsync(x => x.EventId == "race")).RowVersion == 1,
            "Concurrent approval commits one operation and one live version");
    await Prepare("recordrace");
    var rr = await core.ReadShorteningAsync(manager, "recordrace");
    var rp = await core.ReadProgressAsync(manager, "recordrace");
    var mixed = await Task.WhenAll(Race(() => core.ApproveShorteningAsync(manager, "recordrace", Approve(rr))),
        Race(() => core.RecordProgressAsync(manager, "recordrace", new(Guid.NewGuid().ToString(), rp.RowVersion, rp.ProgressVersion,
            rp.NextAction!.PartId, rp.NextAction.Point))));
    Check(mixed.Count(v => v) == 1, "Record versus approval race consumes live version only once");
    await using (var db = await factory.CreateDbContextAsync()) {
        Check(await db.ProgressOperations.CountAsync(x => x.EventId == "recordrace") +
            await db.ShorteningOperations.CountAsync(x => x.EventId == "recordrace" && x.Kind == "Approve") == 1,
            "Record/approval race leaves no partial second operation");
        db.EventShorteningPolicies.Add(new() { EventId = "other", PartId = "main-break" });
        try { await db.SaveChangesAsync(); throw new Exception("Expected cross-event FK denial"); }
        catch (DbUpdateException) { Check(true, "Composite FK rejects cross-event policy reference"); }
    }
    try { JsonSerializer.Deserialize<ShorteningApproveEdit>("{\"RequestId\":\"x\",\"RowVersion\":1,\"ProgressVersion\":1,\"Token\":\"x\",\"Cuts\":[]}"); throw new Exception("Unexpected client cuts"); }
    catch (JsonException) { Check(true, "Strict approval DTO rejects client-submitted cuts"); }
    try { JsonSerializer.Deserialize<ShorteningPolicyEdit>("{\"RequestId\":\"x\",\"RowVersion\":1,\"ProgressVersion\":1,\"PartId\":\"x\",\"At\":\"2026-11-01\"}"); throw new Exception("Unexpected client clock"); }
    catch (JsonException) { Check(true, "Strict policy DTO rejects client timestamp"); }
    try { JsonSerializer.Deserialize<ShorteningPolicyEdit>("{\"RequestId\":\"x\",\"RowVersion\":1,\"ProgressVersion\":1,\"PartId\":\"x\",\"AllowPrep\":true,\"AllowDuration\":false,\"MinDurationSeconds\":0}"); throw new Exception("Unexpected implicit floor"); }
    catch (JsonException) { Check(true, "Missing prep floor is rejected rather than silently defaulting to zero"); }
    try { JsonSerializer.Deserialize<ShorteningApproveEdit>("{\"RequestId\":\"x\",\"RowVersion\":1,\"Token\":\"x\"}"); throw new Exception("Unexpected implicit version"); }
    catch (JsonException) { Check(true, "Approval requires explicit live version"); }
    await using (var db = await factory.CreateDbContextAsync()) {
        await SqliteStorageInitializer.InitializeAsync(db);
        Check((await db.EventProgressStates.SingleAsync(s => s.EventId == "main")).PlanJson == captured &&
            (await db.SongParts.SingleAsync(x => x.Id == "main-slot")).RowVersion == 2 &&
            (await db.EventParticipantStates.SingleAsync(x => x.EventId == "main")).RowVersion == 3 &&
            (await db.EventQuestionAnswers.SingleAsync(x => x.QuestionId == "main-q")).Value == "retained",
            "Restart preserves baseline and unrelated registration data");
        Check(await db.Audits.CountAsync(a => a.Action == "ShorteningApproved") >= 2, "Approval audited with real actor and request identity");
        Check((await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").ToListAsync()).Single() == 0 &&
            (await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").ToListAsync()).Single() == "ok",
            "SQLite integrity and all foreign keys remain valid");
        var user = await db.Users.SingleAsync(u => u.Id == "manager"); user.SecurityStamp = "changed"; await db.SaveChangesAsync();
    }
    await Deny<UnauthorizedAccessException>(() => core.ReadShorteningAsync(manager, "main"), "Changed security stamp blocks proposal reads");
    await Deny<UnauthorizedAccessException>(() => core.ApproveShorteningAsync(manager, "main", approval), "Changed stamp blocks completed approval replay");
    await using (var db = await factory.CreateDbContextAsync()) {
        (await db.Users.SingleAsync(u => u.Id == "manager")).SecurityStamp = "manager-stamp";
        db.RevokedAuthSessions.Add(new() { TokenHash = SemEndpoints.Hash("manager-session"), UserId = "manager", RevokedAt = time.Now.ToString("O") });
        await db.SaveChangesAsync();
    }
    await Deny<UnauthorizedAccessException>(() => core.SaveShorteningPolicyAsync(manager, "main", firstPolicy), "Revoked session cannot replay completed policy save");
    await Deny<UnauthorizedAccessException>(() => core.ApproveShorteningAsync(manager, "main", approval), "Revoked session cannot replay completed approval");
    Console.WriteLine("Shortening integration checks passed: " + passed);
} finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
sealed class Clock : TimeProvider { public DateTimeOffset Now; public override DateTimeOffset GetUtcNow() => Now; }