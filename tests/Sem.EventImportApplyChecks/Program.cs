using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
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

var resume = args.FirstOrDefault() == "--resume";
var root = resume ? args[1] : Path.Combine(Path.GetTempPath(), "sem-import-apply-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var services = new ServiceCollection(); services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + Path.Combine(root,"test.db")), ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddDataProtection(); services.AddSingleton<TimeProvider>(new TestClock());
services.AddSingleton<BusinessNotificationStore>(); services.AddScoped<CoreFoundationService>();
using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
var core = scope.ServiceProvider.GetRequiredService<CoreFoundationService>(); var checks = 0;
void Check(bool ok,string label) { if (!ok) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }
ClaimsPrincipal Who(string id,string? stamp=null) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier,id), new Claim("AspNet.Identity.SecurityStamp", stamp ?? id+"-stamp"),
    new Claim(PasskeyAdditionService.SessionClaim,id+"-session")],"Identity.Application"));
async Task Change(Func<ApplicationDbContext,Task> action) { await using var db = await factory.CreateDbContextAsync(); await action(db); await db.SaveChangesAsync(); }
async Task<string> Snapshot() {
    await using var db=await factory.CreateDbContextAsync(); await db.Database.OpenConnectionAsync();
    var connection=db.Database.GetDbConnection(); var names=new List<string>();
    await using(var cmd=connection.CreateCommand()) { cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"; await using var r=await cmd.ExecuteReaderAsync();while(await r.ReadAsync()) names.Add(r.GetString(0)); }
    var tables=new Dictionary<string,List<object?[]>>();
    foreach(var name in names) { var rows=new List<object?[]>(); await using var cmd=connection.CreateCommand();cmd.CommandText="SELECT * FROM \""+name.Replace("\"","\"\"")+"\" ORDER BY rowid";await using var r=await cmd.ExecuteReaderAsync();while(await r.ReadAsync()){var values=new object[r.FieldCount];r.GetValues(values);rows.Add(values.Select(x=>x is DBNull?null:x).ToArray());}tables[name]=rows; }
    return JsonSerializer.Serialize(tables);
}
async Task Deny<T>(Func<Task> action,string label) where T:Exception {
    var before=await Snapshot();try { await action(); } catch(T) { Check(before==await Snapshot(),label+"; no database changes"); return; } throw new Exception("FAIL " + label);
}
async Task<string> NewEvent() => await core.SaveEventAsync(Who("admin"),null,new("Import event","","",DateTimeOffset.Parse("2099-01-01T00:00:00Z"),null,"","Hidden",["manager"],0));
async Task<EventImportApply> Edit(string id,string csv) { var p=await core.ReadEventImportPreviewAsync(Who("manager"),id,csv);return new(Guid.NewGuid().ToString("D"),p.RowVersion,p.Fingerprint,csv,true,false); }
async Task ViewPhase(string id) { var p=await core.ReadPhasePlanAsync(Who("manager"),id);await core.SavePhasePlanAsync(Who("manager"),id,new(p.RowVersion,true,p.Phases.Single(x=>x.Kind=="View").Id,[])); }
const string csv="Kind,Title,Artist,Duration,Prep,Parts\nSong,New <img src=x>,Artist,4:30,,Vo|Gt\nTransition,Pause,,10:00,,\nSong,Second,Artist,1:02:03,0:00,Vo";
if(resume) {
    var edit=JsonSerializer.Deserialize<EventImportApply>(await File.ReadAllTextAsync(Path.Combine(root,"request.json")))!;
    var result=await core.ApplyEventImportAsync(Who("manager"),args[2],edit);
    if(!result.Replayed) throw new Exception("Expected durable replay");
    Console.WriteLine("Separate process replay recovery: PASS");return;
}
try {
    // Upgrade a data-bearing previous schema; never replace an existing database.
    await using(var db=await factory.CreateDbContextAsync()) {
        await db.GetService<IMigrator>().MigrateAsync("20261014124343_AddEventImportHistory");
        foreach(var uid in new[]{"admin","manager","member","other"}) db.Users.Add(new(){Id=uid,UserName=uid,Email=uid+"@PRIVATE.invalid",DisplayName=uid,Enabled=true,EmailConfirmed=true,SecurityStamp=uid+"-stamp",IsAdmin=uid=="admin"});
        await db.SaveChangesAsync();
    }
    var id=await NewEvent(); var original=await core.ReadTimetableAsync(Who("manager"),id);
    await Change(db=>{db.Messages.Add(new(){Id="existing-inbox",UserId="member",Title="old",Body="retained",Created=1});return Task.CompletedTask;});
    await using(var db=await factory.CreateDbContextAsync()) { await db.Database.MigrateAsync();Check(!db.Database.HasPendingModelChanges(),"migration snapshot matches model");Check(await db.Users.CountAsync()==4 && await db.Messages.CountAsync()==1 && await db.SessionEvents.CountAsync()==1,"data-bearing previous schema upgrade preserves users inbox and event");Check(await db.Set<EventImportOperation>().CountAsync()==0,"migration creates empty operation ledger without backfill"); }
    var edit=await Edit(id,csv);
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),id,edit with{Confirmed=false}),"explicit confirmation required");
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),id,edit with{RequestId=edit.RequestId.ToUpperInvariant()}),"canonical lowercase operation ID required");
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),id,edit with{PreviewFingerprint="bad"}),"malformed comparison rejected");
    await Deny<UnauthorizedAccessException>(()=>core.ApplyEventImportAsync(Who("member"),id,edit),"ordinary user cannot import");
    await Deny<UnauthorizedAccessException>(()=>core.ApplyEventImportAsync(Who("other"),id,edit),"foreign manager cannot import");
    await Deny<UnauthorizedAccessException>(()=>core.ApplyEventImportAsync(Who("manager","wrong"),id,edit),"current security stamp required");
    await Deny<UnauthorizedAccessException>(()=>core.ApplyEventImportAsync(Who("manager"),"missing",edit),"missing event refused");
    await Deny<CoreConflictException>(()=>core.ApplyEventImportAsync(Who("manager"),id,edit with{RowVersion=edit.RowVersion+1}),"stale version refused");
    await Deny<CoreConflictException>(()=>core.ApplyEventImportAsync(Who("manager"),id,edit with{PreviewFingerprint=new string('a',64)}),"wrong comparison refused");
    await Deny<CoreConflictException>(()=>core.ApplyEventImportAsync(Who("manager"),id,edit with{Csv=csv.Replace("Second","Changed")}),"changed CSV requires fresh preview");
    var result=await core.ApplyEventImportAsync(Who("manager"),id,edit);
    Check(!result.Replayed && result.NewSongs==2 && result.NewTransitions==1 && result.SkippedRows==0,"mixed batch accepted with original counts");
    Check(result.RowVersion==edit.RowVersion+1,"event version advances exactly once");
    var after=await core.ReadTimetableAsync(Who("manager"),id);
    Check(after.Parts.Select(x=>x.Kind).SequenceEqual(new[]{"Song","Transition","Song"}),"mixed rows retain CSV order");
    Check(after.Songs[0].PrepSeconds is null && after.Songs[1].PrepSeconds==0,"null inheritance and explicit zero retained");
    Check(after.Songs[0].Title=="New <img src=x>","title stored as literal source text");
    Check(after.Songs.All(x=>Guid.TryParseExact(x.Id,"D",out _)) && after.Parts.All(x=>Guid.TryParseExact(x.Id,"D",out _)),"row IDs generated server-side");
    await using(var db=await factory.CreateDbContextAsync()) {
        Check(await db.SongParts.CountAsync(x=>x.EventId==id)==3 && !await db.SongParts.AnyAsync(x=>x.EventId==id&&x.AssignedUserId!=null),"definition-linked parts created without assignments");
        Check(await db.Audits.CountAsync(x=>x.Action=="EventImportApplied")==1 && await db.Set<EventImportOperation>().CountAsync()==1,"one receipt and one audit saved");
        Check(await db.RegistrationOperations.CountAsync()==0 && await db.EventReceptions.CountAsync()==0 && await db.EventQuestionAnswers.CountAsync()==0 && await db.NotificationReservations.CountAsync()==0 && await db.Messages.CountAsync()==1 && await db.EventProgressStates.CountAsync()==0,"no registration reception answers notifications baseline or inbox mutation");
    }
    var saved=await Snapshot();var replay=await core.ApplyEventImportAsync(Who("manager"),id,edit);
    Check(replay==result with{Replayed=true} && saved==await Snapshot(),"identical retry returns original result with no writes");
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("admin"),id,edit),"another current admin cannot reuse author operation");
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),id,edit with{ConfirmOverrun=true}),"same ID with different confirmation cannot replay");
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),id,edit with{Csv=csv.Replace("Second","Different")}),"same ID with changed source cannot replay");
    var otherEvent=await NewEvent();await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),otherEvent,edit),"same ID cannot cross events");
    await File.WriteAllTextAsync(Path.Combine(root,"request.json"),JsonSerializer.Serialize(edit));
    using(var child=new Process()) {child.StartInfo=new ProcessStartInfo(Environment.ProcessPath!){RedirectStandardOutput=true,RedirectStandardError=true};child.StartInfo.ArgumentList.Add("--resume");child.StartInfo.ArgumentList.Add(root);child.StartInfo.ArgumentList.Add(id);child.Start();var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync();Check(child.ExitCode==0&&(await output).Contains("Separate process replay recovery: PASS"),"actual separate-process reopen recovers receipt");if(child.ExitCode!=0)throw new Exception(await error);}
    var nextCsv="Kind,Title,Duration,Prep,Parts\nSong,Second,1:00,,Vo\nTransition,Pause,1:00,,\nSong,Append,1:00,,Gt";
    // Existing Second has Artist=Artist, so use same pair for duplicate matching.
    nextCsv="Kind,Title,Artist,Duration,Prep,Parts\nSong,Second,Artist,1:00,,Vo\nTransition,Pause,,1:00,,\nSong,Append,,1:00,,Gt";
    var next=await Edit(id,nextCsv);var added=await core.ApplyEventImportAsync(Who("manager"),id,next);var appended=await core.ReadTimetableAsync(Who("manager"),id);
    Check(added.NewSongs==1 && added.SkippedRows==2,"existing song and transition keys skipped");
    Check(appended.Parts.Take(3).SequenceEqual(after.Parts) && appended.Songs.Take(2).Select(x=>JsonSerializer.Serialize(x)).SequenceEqual(after.Songs.Select(x=>JsonSerializer.Serialize(x))),"append preserves existing rows and mixed order without overwrite");
    var assignedCsv="Kind,Title,Duration,Parts,UserToken\nSong,Assigned,1:00,Unknown,SECRET-LOW-ENTROPY\nSong,Valid,1:00,Vo,";var assignedEdit=await Edit(otherEvent,assignedCsv);var assignedResult=await core.ApplyEventImportAsync(Who("manager"),otherEvent,assignedEdit);
    Check(assignedResult.NewSongs==1 && assignedResult.SkippedRows==1,"assigned CSV rows are skipped without assignments");
    var sanitizedReplay=await core.ApplyEventImportAsync(Who("manager"),otherEvent,assignedEdit with{Csv=assignedCsv.Replace("SECRET-LOW-ENTROPY","ANOTHER-ASSIGNMENT")});Check(sanitizedReplay.Replayed,"assignment plaintext is excluded from normalized replay identity");
    Check(!(await Snapshot()).Contains("SECRET-LOW-ENTROPY",StringComparison.Ordinal),"raw assignment contents never persisted");
    var concurrentId=await NewEvent();var concurrentEdit=await Edit(concurrentId,csv);var attempts=await Task.WhenAll(Enumerable.Range(0,3).Select(_=>Task.Run(()=>core.ApplyEventImportAsync(Who("manager"),concurrentId,concurrentEdit))));
    Check(attempts.Count(x=>!x.Replayed)==1 && attempts.Count(x=>x.Replayed)==2,"three concurrent same-ID submits create one batch and two replays");
    var conflictId=await NewEvent();var conflictEdit=await Edit(conflictId,csv);
    async Task<bool> Distinct(EventImportApply request) { try {await core.ApplyEventImportAsync(Who("manager"),conflictId,request);return true;}catch(CoreConflictException){return false;} }
    var distinct=await Task.WhenAll(Task.Run(()=>Distinct(conflictEdit)),Task.Run(()=>Distinct(conflictEdit with{RequestId=Guid.NewGuid().ToString("D")})));
    Check(distinct.Count(x=>x)==1,"different concurrent IDs on same preview do not duplicate plan");
    var faultId=await NewEvent();var faultEdit=await Edit(faultId,csv);
    await using(var db=await factory.CreateDbContextAsync())await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER import_audit_fault BEFORE INSERT ON Audits WHEN NEW.Action='EventImportApplied' BEGIN SELECT RAISE(ABORT,'injected audit failure'); END;");
    await Deny<DbUpdateException>(()=>core.ApplyEventImportAsync(Who("manager"),faultId,faultEdit),"injected audit failure rolls back plan receipt and version");
    await using(var db=await factory.CreateDbContextAsync())await db.Database.ExecuteSqlRawAsync("DROP TRIGGER import_audit_fault;");
    Check(!(await core.ApplyEventImportAsync(Who("manager"),faultId,faultEdit)).Replayed,"failed atomic attempt can safely retry same request");
    var gateId=await NewEvent();var gateEdit=await Edit(gateId,csv);
    await Change(async db=>{(await db.SessionEvents.SingleAsync(x=>x.Id==gateId)).ImportHistoryKnown=false;});
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),gateId,gateEdit),"unknown history refuses cached preview");
    await Change(async db=>{var e=await db.SessionEvents.SingleAsync(x=>x.Id==gateId);e.ImportHistoryKnown=true;e.HasEverRegistrationPhase=true;});
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),gateId,gateEdit),"ever registration marker refuses cached preview");
    await Change(async db=>{var e=await db.SessionEvents.SingleAsync(x=>x.Id==gateId);e.HasEverRegistrationPhase=false;db.EventSpectators.Add(new(){EventId=gateId,UserId="member",RegisteredBy="manager",RegisteredAt="2026-10-14T00:00:00Z"});});
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),gateId,gateEdit),"contrary registration evidence refuses cached preview");
    await Change(async db=>db.EventSpectators.Remove(await db.EventSpectators.SingleAsync(x=>x.EventId==gateId)));
    await Change(async db=>(await db.EventPartDefinitions.FirstAsync(x=>x.EventId==gateId&&x.Name=="Vo")).Name="Vocals");
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),gateId,gateEdit),"deleted or renamed local definition cannot use old preview");
    await Change(async db=>(await db.EventPartDefinitions.FirstAsync(x=>x.EventId==gateId&&x.Name=="Vocals")).Name="Vo");
    await Change(async db=>(await db.SessionEvents.SingleAsync(x=>x.Id==gateId)).DefaultPrepSeconds=42);
    await Deny<CoreConflictException>(()=>core.ApplyEventImportAsync(Who("manager"),gateId,gateEdit),"settings change without version in synthetic fixture changes fingerprint");
    var overrunId=await NewEvent();await Change(async db=>{var e=await db.SessionEvents.SingleAsync(x=>x.Id==overrunId);e.PlannedEndAt=e.ScheduledDate;});var overrunEdit=await Edit(overrunId,csv);
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),overrunId,overrunEdit),"overrun requires explicit acknowledgment");
    Check(!(await core.ApplyEventImportAsync(Who("manager"),overrunId,overrunEdit with{ConfirmOverrun=true})).Replayed,"acknowledged overrun accepted");
    var invalidId=await NewEvent();var invalidEdit=await Edit(invalidId,csv+"\nSong,Bad,Artist,invalid,,Vo");await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),invalidId,invalidEdit),"one invalid row blocks whole batch");
    var maxVersionId=await NewEvent();await Change(async db=>(await db.SessionEvents.SingleAsync(x=>x.Id==maxVersionId)).RowVersion=long.MaxValue);var maxVersionEdit=await Edit(maxVersionId,csv);
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),maxVersionId,maxVersionEdit),"event version overflow refused");
    var orderId=await NewEvent();await core.ApplyEventImportAsync(Who("manager"),orderId,await Edit(orderId,csv));
    await Change(async db=>(await db.TimeParts.FirstAsync(x=>x.EventId==orderId)).SortOrder=int.MaxValue);
    var extraCsv="Kind,Title,Duration,Parts\nSong,Additional,0:30,Vo";var orderPreview=await core.ReadEventImportPreviewAsync(Who("manager"),orderId,extraCsv);
    Check(!orderPreview.IsValid && orderPreview.ProposedEnd is null,"preview refuses sort overflow instead of wrapping timetable");
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),orderId,new(Guid.NewGuid().ToString("D"),orderPreview.RowVersion,orderPreview.Fingerprint,extraCsv,true,false)),"sort overflow cannot mutate plan");
    await Change(async db=>{(await db.TimeParts.FirstAsync(x=>x.EventId==orderId&&x.SortOrder==int.MaxValue)).SortOrder=0;(await db.Songs.FirstAsync(x=>x.EventId==orderId)).SortOrder=int.MaxValue;});
    var songOrderPreview=await core.ReadEventImportPreviewAsync(Who("manager"),orderId,extraCsv);Check(!songOrderPreview.IsValid && songOrderPreview.ProposedEnd is null,"song sort overflow independently refuses preview");
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),orderId,new(Guid.NewGuid().ToString("D"),songOrderPreview.RowVersion,songOrderPreview.Fingerprint,extraCsv,true,false)),"song sort overflow cannot mutate plan");
    var protectedId=await NewEvent();var protectedEdit=await Edit(protectedId,csv);
    await Change(db=>{db.EventProgressStates.Add(new(){EventId=protectedId,PlanJson="{}",CreatedAt="2026-10-14T00:00:00Z"});return Task.CompletedTask;});
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),protectedId,protectedEdit),"progress baseline refuses cached import");
    var deletedId=await NewEvent();await core.ApplyEventImportAsync(Who("manager"),deletedId,await Edit(deletedId,csv));
    string[] deletedSongs=[];await Change(async db=>{var songs=await db.Songs.Where(x=>x.EventId==deletedId).ToArrayAsync();deletedSongs=songs.Select(x=>x.Id).ToArray();foreach(var x in songs)x.DeletedAt="2026-10-14T00:00:00Z";foreach(var x in await db.TimeParts.Where(x=>x.EventId==deletedId).ToArrayAsync())x.DeletedAt="2026-10-14T00:00:00Z";});
    await core.ApplyEventImportAsync(Who("manager"),deletedId,await Edit(deletedId,csv));
    await using(var db=await factory.CreateDbContextAsync())Check(await db.Songs.CountAsync(x=>deletedSongs.Contains(x.Id)&&x.DeletedAt!=null)==2 && await db.Songs.CountAsync(x=>x.EventId==deletedId&&x.DeletedAt==null)==2,"same source creates new active rows without resurrecting deleted songs");
    var phaseId=await NewEvent();var phaseEdit=await Edit(phaseId,csv);await ViewPhase(phaseId);phaseEdit=phaseEdit with{RowVersion=(await core.ReadPhasePlanAsync(Who("manager"),phaseId)).RowVersion};
    await Deny<ValidationException>(()=>core.ApplyEventImportAsync(Who("manager"),phaseId,phaseEdit),"current non-Hidden phase blocks new apply");
    await ViewPhase(id);
    Check((await core.ApplyEventImportAsync(Who("manager"),id,edit)).Replayed,"current authorized replay returns old result after phase change without new import");
    await Change(async db=>db.EventAdmins.Remove(await db.EventAdmins.SingleAsync(x=>x.EventId==id&&x.UserId=="manager")));
    await Deny<UnauthorizedAccessException>(()=>core.ApplyEventImportAsync(Who("manager"),id,edit),"removed author manager cannot recover receipt");
    await Change(db=>{db.RevokedAuthSessions.Add(new(){TokenHash=SemEndpoints.Hash("manager-session"),UserId="manager",RevokedAt="2026-10-14T00:00:00Z"});return Task.CompletedTask;});
    await Deny<UnauthorizedAccessException>(()=>core.ApplyEventImportAsync(Who("manager"),concurrentId,concurrentEdit),"revoked session cannot replay receipt");
    await using(var db=await factory.CreateDbContextAsync()) {await db.Database.OpenConnectionAsync();await using var cmd=db.Database.GetDbConnection().CreateCommand();cmd.CommandText="PRAGMA integrity_check";Check((string?)await cmd.ExecuteScalarAsync()=="ok","SQLite integrity okay");cmd.CommandText="PRAGMA foreign_key_check";await using var r=await cmd.ExecuteReaderAsync();Check(!await r.ReadAsync(),"foreign keys valid");}
    Console.WriteLine("TOTAL PASS: "+checks);
} finally {SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
sealed class TestClock:TimeProvider { public override DateTimeOffset GetUtcNow()=>DateTimeOffset.Parse("2026-10-14T18:00:00Z"); }
