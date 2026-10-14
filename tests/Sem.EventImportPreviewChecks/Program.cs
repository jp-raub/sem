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

var root = Path.Combine(Path.GetTempPath(), "sem-import-preview-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var services = new ServiceCollection(); services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + Path.Combine(root,"test.db")), ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddDataProtection(); services.AddSingleton<TimeProvider>(new TestClock());
services.AddSingleton<BusinessNotificationStore>(); services.AddScoped<CoreFoundationService>();
using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var core = scope.ServiceProvider.GetRequiredService<CoreFoundationService>(); var checks = 0;
void Check(bool ok,string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
async Task Deny<T>(Func<Task> action,string label) where T:Exception {
    try { await action(); } catch(T) { Check(true,label); return; } throw new Exception("FAIL " + label);
}
ClaimsPrincipal Who(string id,string? stamp=null) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier,id), new Claim("AspNet.Identity.SecurityStamp", stamp ?? id+"-stamp"),
    new Claim(PasskeyAdditionService.SessionClaim,id+"-session")],"Identity.Application"));
async Task Change(Func<ApplicationDbContext,Task> action) { await using var db = await factory.CreateDbContextAsync(); await action(db); await db.SaveChangesAsync(); }
async Task<string> Snapshot() {
    await using var db=await factory.CreateDbContextAsync();
    return JsonSerializer.Serialize(new { Events=await db.SessionEvents.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),
        Songs=await db.Songs.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(), Parts=await db.SongParts.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),
        Times=await db.TimeParts.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(), Audit=await db.Audits.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),
        Registrations=await db.RegistrationOperations.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync() });
}
const string csv="Kind,Title,Artist,Duration,Prep,Parts\nSong,New <img src=x>,Artist,4:30,,Vo|Gt\nTransition,Pause,,10:00,,\nSong,Second,Artist,1:02:03,0:00,Vo";
try {
    await using(var db=await factory.CreateDbContextAsync()) {
        await db.Database.MigrateAsync();
        foreach(var uid in new[]{"admin","manager","member","other"}) db.Users.Add(new(){Id=uid,UserName=uid,Email=uid+"@PRIVATE.invalid",DisplayName=uid,Enabled=true,EmailConfirmed=true,SecurityStamp=uid+"-stamp",IsAdmin=uid=="admin"});
        await db.SaveChangesAsync();
    }
    var id=await core.SaveEventAsync(Who("admin"),null,new("Import event","","",DateTimeOffset.Parse("2099-01-01T00:00:00Z"),null,"","Hidden",["manager"],0));
    var before=await Snapshot();
    var p=await core.ReadEventImportPreviewAsync(Who("manager"),id,csv);
    Check(p.IsValid && p.NewSongs==2 && p.NewTransitions==1 && p.SkippedRows==0,"valid mixed CSV preview");
    Check(p.Rows[0].PrepSeconds is null && p.Rows[2].PrepSeconds==0,"blank preparation inherits; explicit zero retained");
    Check(p.Rows[0].Title=="New <img src=x>" && p.Fingerprint.Length==64,"source title remains plain text and comparison is SHA-256");
    Check(p.ProposedEnd is not null && p.OverrunSeconds==0,"proposed timetable calculation available without storage");
    Check(before==await Snapshot(),"preview changes no event, rows, registration or audit");
    Check((await core.ReadEventImportPreviewAsync(Who("manager"),id,csv)).Fingerprint==p.Fingerprint,"unchanged preview comparison stable");
    Check((await core.ReadEventImportPreviewAsync(Who("admin"),id,csv)).Fingerprint!=p.Fingerprint,"comparison binds current actor");
    Check((await core.ReadEventImportPreviewAsync(Who("manager"),id,csv.Replace("Second","Third"))).Fingerprint!=p.Fingerprint,"comparison binds CSV content");
    await Deny<UnauthorizedAccessException>(()=>core.ReadEventImportPreviewAsync(Who("member"),id,csv),"ordinary user denied hidden import");
    await Deny<UnauthorizedAccessException>(()=>core.ReadEventImportPreviewAsync(Who("other"),id,csv),"foreign manager denied");
    await Deny<UnauthorizedAccessException>(()=>core.ReadEventImportPreviewAsync(Who("manager","wrong"),id,csv),"current security stamp required");
    await Deny<UnauthorizedAccessException>(()=>core.ReadEventImportPreviewAsync(Who("admin"),"missing",csv),"missing event denied");
    var undefined=await core.ReadEventImportPreviewAsync(Who("manager"),id,csv.Replace("Vo|Gt","Vo|Unknown"));
    Check(!undefined.IsValid && undefined.Rows[0].Disposition=="Invalid","undefined event-local parts invalidate batch");
    var dupe=await core.ReadEventImportPreviewAsync(Who("manager"),id,csv+"\nSong,Second,Artist,0:30,,Vo\nTransition,Pause,,0:00,,");
    Check(dupe.IsValid && dupe.NewSongs==2 && dupe.NewTransitions==1 && dupe.SkippedRows==2,"within-CSV duplicates skipped without overwriting");
    var assigned=await core.ReadEventImportPreviewAsync(Who("manager"),id,"Kind,Title,Duration,Parts,UserToken\nSong,Private assignment,1:00,Unknown,PRIVATE-TOKEN\nSong,Valid,1:00,Vo,");
    Check(assigned.IsValid && assigned.NewSongs==1 && assigned.SkippedRows==1,"assigned row skipped regardless of undefined part");
    Check(!JsonSerializer.Serialize(assigned).Contains("PRIVATE-TOKEN",StringComparison.Ordinal),"assignment values never returned");
    var sameAssignment=await core.ReadEventImportPreviewAsync(Who("manager"),id,"Kind,Title,Duration,Parts,UserToken\nSong,Private assignment,1:00,Unknown,ANOTHER-TOKEN\nSong,Valid,1:00,Vo,");
    Check(sameAssignment.Fingerprint==assigned.Fingerprint,"comparison excludes assignment plaintext and binds only presence");
    var malformedAssigned=await core.ReadEventImportPreviewAsync(Who("manager"),id,"Kind,Title,Duration,Parts,UserToken\nSong,Malformed,1:00,Vo,PRIVATE-TOKEN,EXTRA\nSong,Valid,1:00,Vo,");
    Check(!malformedAssigned.IsValid && malformedAssigned.Rows[0].Disposition=="Invalid","assigned row width mismatch remains blocking");
    var malformedRequired=await core.ReadEventImportPreviewAsync(Who("manager"),id,"Kind,Title,Duration,Parts,UserToken\nSong,Bad,invalid,Vo,PRIVATE-TOKEN\nSong,Valid,1:00,Vo,");
    Check(!malformedRequired.IsValid && malformedRequired.Rows[0].Disposition=="Invalid","assigned row still requires valid kind, title and duration");
    var invalid=await core.ReadEventImportPreviewAsync(Who("manager"),id,csv+"\nSong,Bad,Artist,invalid,,Vo");
    Check(!invalid.IsValid && invalid.Rows[^1].Error is not null && invalid.ProposedEnd is null,"one invalid row blocks complete prospective batch");
    var capacity="Kind,Title,Duration,Parts\n"+string.Join('\n',Enumerable.Range(1,301).Select(i=>$"Song,N{i},0:01,Vo"));
    Check(!(await core.ReadEventImportPreviewAsync(Who("manager"),id,capacity)).IsValid,"301 songs exceed existing application cap");
    var transitions="Kind,Title,Duration\n"+string.Join('\n',Enumerable.Range(1,301).Select(i=>$"Transition,N{i},0:00"));
    Check(!(await core.ReadEventImportPreviewAsync(Who("manager"),id,transitions)).IsValid,"301 transitions exceed existing application cap");
    await Change(db=>{ db.Songs.Add(new(){Id="existing-song",EventId=id,Title="Second",Artist="Artist",PerformanceSeconds=90}); db.TimeParts.Add(new(){Id="existing-time",EventId=id,Kind="Song",SongId="existing-song"}); return Task.CompletedTask; });
    var existing=await core.ReadEventImportPreviewAsync(Who("manager"),id,csv);
    Check(existing.IsValid && existing.NewSongs==1 && existing.SkippedRows==1,"existing song key skipped without update");
    Check(existing.Fingerprint!=p.Fingerprint,"comparison covers existing active plan even if version unchanged in synthetic fixture");
    before=await Snapshot(); await core.ReadEventImportPreviewAsync(Who("manager"),id,csv); Check(before==await Snapshot(),"existing-plan preview still read-only");
    await Change(async db=>db.TimeParts.Remove(await db.TimeParts.SingleAsync(x=>x.Id=="existing-time")));
    Check(!(await core.ReadEventImportPreviewAsync(Who("manager"),id,csv)).IsValid,"incomplete existing song/time-part mapping fails closed");
    await Change(db=>{ db.TimeParts.Add(new(){Id="existing-time",EventId=id,Kind="Song",SongId="existing-song"}); return Task.CompletedTask; });
    await Change(async db=>{ (await db.SessionEvents.SingleAsync(x=>x.Id==id)).ImportHistoryKnown=false; });
    await Deny<ValidationException>(()=>core.ReadEventImportPreviewAsync(Who("manager"),id,csv),"unknown legacy history fails closed even empty registrations");
    await Change(async db=>{ var e=await db.SessionEvents.SingleAsync(x=>x.Id==id); e.ImportHistoryKnown=true; e.HasEverRegistrationPhase=true; });
    await Deny<ValidationException>(()=>core.ReadEventImportPreviewAsync(Who("manager"),id,csv),"prior Custom remains blocked after Hidden");
    await Change(async db=>{ var e=await db.SessionEvents.SingleAsync(x=>x.Id==id); e.HasEverRegistrationPhase=false; db.EventSpectators.Add(new(){EventId=id,UserId="member",RegisteredBy="manager",RegisteredAt="2026-10-14T00:00:00Z"}); });
    await Deny<ValidationException>(()=>core.ReadEventImportPreviewAsync(Who("manager"),id,csv),"contradictory participant record refuses import");
    await Change(async db=>db.EventSpectators.Remove(await db.EventSpectators.SingleAsync(x=>x.EventId==id)));
    await Change(async db=>{ var d=await db.EventPartDefinitions.FirstAsync(x=>x.EventId==id&&x.Name=="Vo"); d.DeletedAt="2026-10-14T00:00:00Z"; });
    Check(!(await core.ReadEventImportPreviewAsync(Who("manager"),id,csv)).IsValid,"deleted local definition unavailable");
    await Change(db=>{ db.RevokedAuthSessions.Add(new(){TokenHash=SemEndpoints.Hash("manager-session"),UserId="manager",RevokedAt="2026-10-14T00:00:00Z"}); return Task.CompletedTask; });
    await Deny<UnauthorizedAccessException>(()=>core.ReadEventImportPreviewAsync(Who("manager"),id,csv),"revoked session cannot preview cached plan");
    await using(var db=await factory.CreateDbContextAsync()) { await db.Database.OpenConnectionAsync(); await using var cmd=db.Database.GetDbConnection().CreateCommand(); cmd.CommandText="PRAGMA integrity_check"; Check((string?)await cmd.ExecuteScalarAsync()=="ok","SQLite integrity okay"); cmd.CommandText="PRAGMA foreign_key_check"; await using var r=await cmd.ExecuteReaderAsync(); Check(!await r.ReadAsync(),"foreign keys valid"); }
    Console.WriteLine("TOTAL PASS: "+checks);
} finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
sealed class TestClock:TimeProvider { public override DateTimeOffset GetUtcNow()=>DateTimeOffset.Parse("2026-10-14T12:30:00Z"); }
