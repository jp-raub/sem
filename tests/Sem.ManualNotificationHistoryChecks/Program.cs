using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-manual-history-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var path = Path.Combine(root, "history.db");
var clock = new TestClock(DateTimeOffset.Parse("2026-10-14T09:00:00Z"));
var services = new ServiceCollection();
services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + path), ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddDataProtection();
services.AddSingleton<TimeProvider>(clock);
services.AddSingleton<BusinessNotificationStore>();
services.AddScoped<CoreFoundationService>();
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var core = scope.ServiceProvider.GetRequiredService<CoreFoundationService>();
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}
async Task<T> Throws<T>(Func<Task> action, string label) where T : Exception
{
    try { await action(); }
    catch (T e) { checks++; Console.WriteLine("PASS " + label); return e; }
    throw new Exception("FAIL " + label + ": expected " + typeof(T).Name);
}
ClaimsPrincipal Principal(string id, string? stamp = null, string? session = null) => new(
    new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, id),
        new Claim("AspNet.Identity.SecurityStamp", stamp ?? id + "-stamp"),
        new Claim(PasskeyAdditionService.SessionClaim, session ?? id + "-session")
    ], "Identity.Application"));
var manager = Principal("manager");
var otherManager = Principal("other-manager");
var globalAdmin = Principal("global-admin");
var member = Principal("member-1");
async Task Change(Func<ApplicationDbContext, Task> update)
{
    await using var db = await factory.CreateDbContextAsync();
    await update(db);
    await db.SaveChangesAsync();
}
try
{
    await using (var db = await factory.CreateDbContextAsync())
        await db.GetService<IMigrator>().MigrateAsync("20261014081050_AddManualNotificationPushActivationControl");
    await using (var db = await factory.CreateDbContextAsync())
    {
        foreach (var id in new[] { "manager", "other-manager", "global-admin", "member-1", "member-2", "member-3", "member-4", "late-member" })
            db.Users.Add(new ApplicationUser { Id = id, UserName = id, Email = id + "@PRIVATE.invalid", DisplayName = id,
                Enabled = true, EmailConfirmed = true, SecurityStamp = id + "-stamp", IsAdmin = id == "global-admin" });
        var e = new SessionEvent { Id = "event", Title = "PRIVATE-EVENT", ScheduledDate = "2099-01-01T00:00:00Z",
            CreatedAt = "2026-10-01T00:00:00Z", PhaseKind = "View", ManualPhaseOverride = true };
        e.Phases.Add(new EventPhase { Id = "view", EventId = "event", Kind = "View", Name = "View", IsActive = true });
        e.Phases.Add(new EventPhase { Id = "hidden", EventId = "event", Kind = "Hidden", Name = "Hidden" });
        e.Admins.Add(new EventAdmin { EventId = "event", UserId = "manager", GrantedAt = "2026-10-01T00:00:00Z" });
        e.Admins.Add(new EventAdmin { EventId = "event", UserId = "other-manager", GrantedAt = "2026-10-01T00:00:00Z" });
        db.SessionEvents.Add(e);
        foreach (var n in Enumerable.Range(1, 4)) db.EventSpectators.Add(new EventSpectator {
            EventId = "event", UserId = "member-" + n, RegisteredBy = "manager", RegisteredAt = "2026-10-01T00:00:00Z" });
        var otherEvent = new SessionEvent { Id = "other-event", Title = "PRIVATE-OTHER-EVENT", ScheduledDate = "2099-01-01T00:00:00Z",
            CreatedAt = "2026-10-01T00:00:00Z", PhaseKind = "View", ManualPhaseOverride = true };
        otherEvent.Phases.Add(new EventPhase { Id = "other-view", EventId = "other-event", Kind = "View", Name = "View", IsActive = true });
        otherEvent.Admins.Add(new EventAdmin { EventId = "other-event", UserId = "manager", GrantedAt = "2026-10-01T00:00:00Z" });
        db.SessionEvents.Add(otherEvent);
        db.EventSpectators.Add(new EventSpectator { EventId = "other-event", UserId = "member-1", RegisteredBy = "manager", RegisteredAt = "2026-10-01T00:00:00Z" });
        await db.SaveChangesAsync();
    }

    Check((await core.ReadEventManualNotificationHistoryAsync(manager, "event")).Operations.Length == 0,
        "empty own history is valid");
    var at = clock.GetUtcNow().ToUnixTimeSeconds();
    var ownIds = Enumerable.Range(1, 105).Select(i => $"00000000-0000-0000-0000-{i:x12}").ToArray();
    await Change(db => {
        foreach (var id in ownIds)
            db.Add(new ManualNotificationOperation { Id = id, EventId = "event", ActorId = "manager",
                Subject = "OWN <img src=x> " + id, Body = "PRIVATE-BODY", RecipientCount = 4,
                AcceptedAt = at, RequestHash = new string('a',64), PreviewFingerprint = new string('b',64) });
        db.Add(new ManualNotificationOperation { Id = Guid.NewGuid().ToString("D"), EventId = "event", ActorId = "other-manager",
            Subject = "OTHER-SECRET", Body = "OTHER-BODY", RecipientCount = 3, AcceptedAt = at + 1,
            RequestHash = new string('c',64), PreviewFingerprint = new string('d',64) });
        db.Add(new ManualNotificationOperation { Id = Guid.NewGuid().ToString("D"), EventId = "other-event", ActorId = "manager",
            Subject = "OTHER-EVENT-SECRET", Body = "PRIVATE-BODY", RecipientCount = 1, AcceptedAt = at + 1,
            RequestHash = new string('e',64), PreviewFingerprint = new string('f',64) });
        return Task.CompletedTask;
    });
    var beforeIndex = await Snapshot();
    await using (var db = await factory.CreateDbContextAsync())
        await db.GetService<IMigrator>().MigrateAsync();
    Check(beforeIndex == await Snapshot(), "additive index migration preserves populated operations and all business state");
    await using (var db = await factory.CreateDbContextAsync()) {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA index_info('IX_ManualNotificationOperations_EventId_ActorId_AcceptedAt_Id')";
        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) columns.Add(reader.GetString(2));
        Check(columns.SequenceEqual(new[]{"EventId","ActorId","AcceptedAt","Id"}), "composite index matches own-author event cursor order");
        command.CommandText = "EXPLAIN QUERY PLAN SELECT Id, Subject, RecipientCount, AcceptedAt FROM ManualNotificationOperations WHERE EventId='event' AND ActorId='manager' ORDER BY AcceptedAt DESC, Id DESC LIMIT 101";
        var plans = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) plans.Add(reader.GetString(3));
        Check(plans.Any(x=>x.Contains("IX_ManualNotificationOperations_EventId_ActorId_AcceptedAt_Id",StringComparison.Ordinal)) && !plans.Any(x=>x.Contains("TEMP B-TREE",StringComparison.Ordinal)), "SQLite latest page uses supporting index without temporary sort");
    }
    var first = await core.ReadEventManualNotificationHistoryAsync(manager, "event");
    Check(first.EventId == "event" && first.Operations.Length == 100 && first.Next is not null,
        "first page bounded at 100 with continuation");
    Check(first.Operations.Select(x => x.RequestId).SequenceEqual(ownIds.Reverse().Take(100)),
        "same-second GUID tie ordering is stable descending");
    var second = await core.ReadEventManualNotificationHistoryAsync(manager, "event", first.Next);
    Check(second.Operations.Length == 5 && second.Next is null, "final page has five with no continuation");
    Check(first.Operations.Concat(second.Operations).Select(x => x.RequestId).Distinct().Count() == 105,
        "all 105 own operations retrieved without duplicate or omission");
    Check(second.Operations.Select(x => x.RequestId).SequenceEqual(ownIds.Reverse().Skip(100)),
        "cursor comparison agrees with SQL ordering");
    Check(JsonSerializer.Serialize(first) == JsonSerializer.Serialize(await core.ReadEventManualNotificationHistoryAsync(manager,"event")),
        "repeated latest read is stable");
    var other = await core.ReadEventManualNotificationHistoryAsync(otherManager, "event");
    Check(other.Operations.Length == 1 && other.Operations[0].Subject == "OTHER-SECRET", "other manager sees only own operation");
    Check((await core.ReadEventManualNotificationHistoryAsync(globalAdmin,"event")).Operations.Length == 0,
        "global admin cannot list another author's operations");
    Check((await core.ReadEventManualNotificationHistoryAsync(manager,"other-event")).Operations.Single().Subject == "OTHER-EVENT-SECRET",
        "same actor's other event isolated");
    var json = JsonSerializer.Serialize(first);
    Check(!json.Contains("PRIVATE-BODY") && !json.Contains("OTHER-SECRET") && !json.Contains("OTHER-EVENT-SECRET") &&
        !json.Contains("RequestHash") && !json.Contains("PreviewFingerprint") && !json.Contains("ActorId") && !json.Contains("UserId") &&
        !json.Contains("Epoch") && !json.Contains("Endpoint"), "projection excludes body, other authors, binding identities and secrets");
    Check(first.Operations[0].Subject.StartsWith("OWN <img src=x> ",StringComparison.Ordinal) && first.Operations[0].RecipientCount == 4,
        "own subject remains literal and count is original acceptance, not delivery");
    await Throws<ValidationException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event",new(-1,ownIds[0])), "negative cursor time refused");
    await Throws<ValidationException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event",new(long.MaxValue,ownIds[0])), "unrepresentable cursor time refused");
    await Throws<ValidationException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event",new(at,"BAD")), "malformed cursor ID refused");
    await Throws<ValidationException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event",new(at,null!)), "null cursor ID refused");
    await Throws<ValidationException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event",new(at,"AAAAAAAA-0000-0000-0000-000000000001")), "noncanonical cursor ID refused");
    await Throws<ValidationException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event",new(at,Guid.NewGuid().ToString("D"))), "unknown cursor anchor refused");
    await Throws<ValidationException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event",new(at+1,ownIds[0])), "cursor timestamp must match own anchor");
    await Throws<ValidationException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event",new(other.Operations[0].AcceptedAt,other.Operations[0].RequestId)), "other author's cursor rejected");
    var otherEventPage = await core.ReadEventManualNotificationHistoryAsync(manager,"other-event");
    await Throws<ValidationException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event",new(otherEventPage.Operations[0].AcceptedAt,otherEventPage.Operations[0].RequestId)), "other event cursor rejected");
    await Change(db => { db.Add(new ManualNotificationOperation { Id=Guid.NewGuid().ToString("D"),EventId="event",ActorId="manager",Subject="LATER",Body="PRIVATE-BODY",RecipientCount=1,AcceptedAt=at+1,RequestHash=new string('a',64),PreviewFingerprint=new string('b',64) }); return Task.CompletedTask; });
    Check((await core.ReadEventManualNotificationHistoryAsync(manager,"event",first.Next)).Operations.Select(x=>x.RequestId).SequenceEqual(second.Operations.Select(x=>x.RequestId)), "newer insertion does not shift continuation");
    Check((await core.ReadEventManualNotificationHistoryAsync(manager,"event")).Operations[0].Subject == "LATER", "latest read includes newer acceptance");
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationHistoryAsync(member,"event"), "ordinary viewer denied");
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"missing-event"), "missing event denied");
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationHistoryAsync(Principal("manager","wrong-stamp"),"event",first.Next), "current stamp rechecked on continuation");
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationHistoryAsync(new ClaimsPrincipal(),"event"), "anonymous reader denied");
    await Change(db => { db.RevokedAuthSessions.Add(new RevokedAuthSession {UserId="manager",TokenHash=SemEndpoints.Hash("manager-session"),RevokedAt="2026-10-14T09:00:00Z"}); return Task.CompletedTask; });
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event",first.Next), "revoked session denied on continuation");
    await Change(async db => db.RevokedAuthSessions.Remove(await db.RevokedAuthSessions.SingleAsync()));
    await Change(async db => (await db.Users.SingleAsync(x=>x.Id=="manager")).Enabled=false);
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event"), "disabled author denied");
    await Change(async db => (await db.Users.SingleAsync(x=>x.Id=="manager")).Enabled=true);
    await Change(async db => (await db.Users.SingleAsync(x=>x.Id=="manager")).EmailConfirmed=false);
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event"), "unconfirmed author denied");
    await Change(async db => (await db.Users.SingleAsync(x=>x.Id=="manager")).EmailConfirmed=true);
    await Change(async db => db.EventAdmins.Remove(await db.EventAdmins.SingleAsync(x=>x.EventId=="event" && x.UserId=="manager")));
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event",first.Next), "removed manager denied on continuation");
    await Change(db => { db.EventAdmins.Add(new EventAdmin{EventId="event",UserId="manager",GrantedAt="2026-10-14T09:00:00Z"}); return Task.CompletedTask; });
    await Change(async db => (await db.Phases.SingleAsync(x=>x.Id=="view")).IsActive=false);
    await Throws<UnauthorizedAccessException>(() => core.ReadEventManualNotificationHistoryAsync(manager,"event"), "invalid effective phase denied");
    await Change(async db => (await db.Phases.SingleAsync(x=>x.Id=="view")).IsActive=true);
    async Task<string> Snapshot() {
        await using var db=await factory.CreateDbContextAsync();
        return JsonSerializer.Serialize(new {
            Operations=await db.Set<ManualNotificationOperation>().AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),
            Recipients=await db.Set<ManualNotificationRecipient>().AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),
            Messages=await db.Messages.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),
            Audits=await db.Audits.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),
            Admissions=await db.Set<ManualNotificationPushAdmission>().AsNoTracking().OrderBy(x=>x.OperationId).ToArrayAsync(),
            Preparations=await db.Set<ManualNotificationPushPreparation>().AsNoTracking().OrderBy(x=>x.RecipientId).ToArrayAsync(),
            Deliveries=await db.Set<ManualNotificationPushDelivery>().AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),
            Activation=await db.Set<ManualNotificationPushActivation>().AsNoTracking().ToArrayAsync()
        });
    }
    var snapshot=await Snapshot();
    _=await core.ReadEventManualNotificationHistoryAsync(manager,"event");
    _=await core.ReadEventManualNotificationHistoryAsync(manager,"event",first.Next);
    Check(snapshot==await Snapshot(), "reads mutate no operations/inbox/recipients/audit/admission/preparation/delivery/activation");
    await using(var db=await factory.CreateDbContextAsync()) {
        await db.Database.OpenConnectionAsync();
        await using var cmd=db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText="PRAGMA integrity_check";
        Check((string?)await cmd.ExecuteScalarAsync()=="ok", "SQLite integrity ok");
        cmd.CommandText="PRAGMA foreign_key_check";
        await using var reader=await cmd.ExecuteReaderAsync();
        Check(!await reader.ReadAsync(), "no foreign key violation");
    }
    Console.WriteLine($"Manual notification history: {checks} synthetic checks passed.");
}
finally {
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(root,true); } catch { }
}
sealed class TestClock(DateTimeOffset now):TimeProvider {
    public override DateTimeOffset GetUtcNow()=>now;
}
