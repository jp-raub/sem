using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;
var day=new DateOnly(2026,11,2);var root=Path.Combine(Path.GetTempPath(),"sem-digest-"+Guid.NewGuid());Directory.CreateDirectory(root);
var services=new ServiceCollection();services.AddLogging();services.AddDataProtection().SetApplicationName("SEM.DigestChecks").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root,"keys")));
services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+Path.Combine(root,"data.db")),ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();var clock=new Clock{Now=DateTimeOffset.Parse("2026-11-01T15:00:00Z")};services.AddSingleton<TimeProvider>(clock);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();var store=provider.GetRequiredService<BusinessNotificationStore>();int passed=0;
void Check(bool b,string name){if(!b)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
async Task Deny<T>(Func<Task> f,string name)where T:Exception{bool b=false;try{await f();}catch(T){b=true;}Check(b,name);}
ClaimsPrincipal Who(string id,string?stamp=null)=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,id),new Claim("AspNet.Identity.SecurityStamp",stamp??id+"-stamp"),new Claim(PasskeyAdditionService.SessionClaim,id+"-session")],"Identity.Application"));
var admin=Who("admin");var manager=Who("manager");var member=Who("member");var slot=new BusinessSlot("s1","song1","Frozen Song","Voice");
DigestEntry Entry(string id,string at,string kind="Register",BusinessSlot[]?slots=null)=>new(id,"event","Frozen Event",kind,at,"member","Current display","Frozen actor",kind=="Release"?"Frozen reason":"",slots??[slot]);
async Task<string> Snapshot(){await using var db=await factory.CreateDbContextAsync();return JsonSerializer.Serialize(new{notifications=await db.BusinessNotifications.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),operations=await db.RegistrationOperations.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),audit=await db.Audits.CountAsync(),deliveries=await db.Deliveries.CountAsync(),messages=await db.Messages.CountAsync(),users=await db.Users.AsNoTracking().OrderBy(x=>x.Id).Select(x=>new{x.Id,x.DisplayName,x.Enabled,x.IsAdmin}).ToArrayAsync(),events=await db.SessionEvents.AsNoTracking().OrderBy(x=>x.Id).Select(x=>new{x.Id,x.RowVersion}).ToArrayAsync()});}
try{
 var(start,end)=DigestPreviewBuilder.Bounds(day);Check(start==DateTimeOffset.Parse("2026-11-01T15:00:00Z")&&end==DateTimeOffset.Parse("2026-11-02T15:00:00Z"),"Japanese calendar day uses half-open UTC15-to15 bounds");
 var seed=new[]{Entry("before","2026-11-01T14:59:59.999Z"),Entry("lower","2026-11-01T15:00:00.000Z"),Entry("upper-before","2026-11-02T14:59:59.999Z","Release"),Entry("upper","2026-11-02T15:00:00.000Z")};var serialized=JsonSerializer.Serialize(seed);var result=DigestPreviewBuilder.Build(day,"SEM",seed);
 Check(result.OperationCount==2&&result.Entries[0].OperationId=="lower"&&result.Entries[1].OperationId=="upper-before","Midnight lower included and next midnight excluded exactly");
 Check(result.RegistrationSlots==1&&result.ReleaseSlots==1,"Registration and release of same slot are not netted away");Check(JsonSerializer.Serialize(seed)==serialized,"Pure builder leaves historical input unchanged");
 Check(result.Body.Contains("Asia/Tokyo")&&result.Body.Contains("+09:00")&&result.Subject.Contains("2026-11-02"),"Preview explicitly names absolute day and Japan offset");
 Check(result.Body.Contains("Frozen Event")&&result.Body.Contains("Frozen Song")&&result.Body.Contains("Voice")&&result.Body.Contains("Frozen actor")&&result.Body.Contains("Frozen reason"),"Frozen details and reason appear in preview");
 Check(result.Body.Contains("現在の表示名")&&result.Body.Contains("送信予約・実送信は行いません"),"Current subject labels and no-send status are explicit");
 Check(DigestPreviewBuilder.Build(day,"SEM",[]).OperationCount==0&&DigestPreviewBuilder.Build(day,"SEM",[]).Body.Contains("ありません"),"Empty day explicitly reports no recorded operations");
 Check(!DigestPreviewBuilder.Build(day,"name\r\nInjected",[]).Subject.Contains('\n'),"Preview subject removes service-name line breaks");
 var ties=DigestPreviewBuilder.Build(day,"SEM",[Entry("z","2026-11-01T16:00:00.000Z"),Entry("a","2026-11-01T16:00:00.000Z")]);Check(ties.Entries.Select(x=>x.OperationId).SequenceEqual(new[]{"a","z"}),"Equal timestamp ordering deterministic by operation ID");
 var offsets=DigestPreviewBuilder.Build(day,"SEM",[Entry("late","2026-11-02T01:00:00+09:00"),Entry("early","2026-11-01T15:30:00Z")]);Check(offsets.Entries[0].OperationId=="early","Offset timestamps sort by instant not raw string");
 await Deny<ValidationException>(()=>Task.FromResult(DigestPreviewBuilder.Build(day,"SEM",[Entry("bad","invalid")])),"Malformed timestamp refuses partial preview");
 await Deny<ValidationException>(()=>Task.FromResult(DigestPreviewBuilder.Build(day,"SEM",[Entry("x","2026-11-01T16:00:00Z"),Entry("x","2026-11-01T16:00:00Z")])),"Duplicate operation refuses inflated counts");
 await Deny<ValidationException>(()=>Task.FromResult(DigestPreviewBuilder.Build(day,"SEM",[Entry("x","2026-11-01T16:00:00Z","Answers")])),"Unsupported operation kind rejected");
 await Deny<ValidationException>(()=>Task.FromResult(DigestPreviewBuilder.Build(day,"SEM",[Entry("x","2026-11-01T16:00:00Z",slots:[])])),"Empty successful-slot snapshot rejected");
 await Deny<ValidationException>(()=>Task.FromResult(DigestPreviewBuilder.Build(day,"SEM",[Entry("x","2026-11-01T16:00:00Z",slots:[slot,slot])])),"Duplicate slot snapshot cannot inflate totals");
 await Deny<ValidationException>(()=>Task.FromResult(DigestPreviewBuilder.Bounds(DateOnly.MinValue)),"Minimum calendar boundary refused without overflow");await Deny<ValidationException>(()=>Task.FromResult(DigestPreviewBuilder.Bounds(DateOnly.MaxValue)),"Maximum calendar boundary refused without overflow");
 await using(var db=await factory.CreateDbContextAsync()){await SqliteStorageInitializer.InitializeAsync(db);foreach(var id in new[]{"admin","manager","member"})db.Users.Add(new(){Id=id,UserName=id+"@test.example",Email=id+"@test.example",DisplayName=id+" current",Enabled=true,EmailConfirmed=true,IsAdmin=id=="admin",SecurityStamp=id+"-stamp"});await db.SaveChangesAsync();}
 var eventId=await core.SaveEventAsync(admin,null,new("Frozen Event","","",clock.Now.AddDays(2),null,"","Hidden",["manager"],0));
 await using(var db=await factory.CreateDbContextAsync()){
  var e=await db.SessionEvents.SingleAsync(x=>x.Id==eventId);var a=await db.Users.SingleAsync(x=>x.Id=="admin");var phase=await db.Phases.SingleAsync(x=>x.EventId==eventId&&x.IsActive);
  var states=new[]{"Pending","Sending","Sent","Failed","Cancelled"};
  for(int n=0;n<5;n++){var id=Guid.NewGuid().ToString();var at=clock.Now.AddMinutes(n);var kind=n==4?"Release":"Register";
   db.RegistrationOperations.Add(new(){Id=id,EventId=eventId,ActorId="admin",SubjectId="member",PhaseId=phase.Id,Kind=kind,At=DigestPreviewBuilder.Format(at),Reason=kind=="Release"?"Reason":"",RequestHash="hash",ResultJson="{}"});store.Enqueue(db,id,e,a,"member",kind,"Reason",[slot],at);
   var notification=db.ChangeTracker.Entries<BusinessNotification>().Single(x=>x.Entity.Id==id).Entity;notification.Status=states[n];
   if(states[n]=="Sending"){notification.Claim="owner";notification.LeaseUntil=at.ToUnixTimeSeconds()+120;}if(states[n]=="Sent")notification.SentAt=at.ToUnixTimeSeconds();if(states[n]=="Failed")notification.Attempts=5;
  }
  await db.SaveChangesAsync();e.Title="Changed event title";a.DisplayName="Changed actor name";(await db.Users.SingleAsync(x=>x.Id=="member")).DisplayName="New subject name";await db.SaveChangesAsync();
 }

 await using(var db=await factory.CreateDbContextAsync()){
  var damaged=await db.BusinessNotifications.FirstAsync();var original=damaged.ProtectedPayload;damaged.ProtectedPayload="invalid-base64";await db.SaveChangesAsync();
  await Deny<ValidationException>(()=>core.ReadDigestPreviewAsync(admin,"2026-11-02"),"Unreadable protected history refuses partial preview with safe diagnostic");damaged.ProtectedPayload=original;await db.SaveChangesAsync();
 }
 var before=await Snapshot();var view=await core.ReadDigestPreviewAsync(admin,null);Check(view.Day=="2026-11-02","Server default day uses Japanese clock not UTC calendar");
 Check(view.OperationCount==5&&view.RegistrationSlots==4&&view.ReleaseSlots==1,"All committed history counted independently of five email statuses");
 Check(view.Entries.All(x=>x.EventTitle=="Frozen Event"&&x.ActorName=="admin current")&&!view.Body.Contains("Changed event title"),"Live event and actor edits never rewrite frozen historical details");
 Check(view.Entries.All(x=>x.CurrentSubjectName=="New subject name"),"Recipient display name explicitly current, not falsely historical");
 Check(!JsonSerializer.Serialize(view).Contains("@test.example")&&!view.Body.Contains("ProtectedPayload"),"No email addresses ciphertext or questionnaire answers exposed in digest DTO");
 Check(await Snapshot()==before,"Preview reads do not modify operations queue versions audit or authentication data");
 Check((await core.ReadDigestPreviewAsync(admin,"2026-11-01")).OperationCount==0,"Explicit previous date is independent of current day");
 await Deny<UnauthorizedAccessException>(()=>core.ReadDigestPreviewAsync(manager,"2026-11-02"),"Assigned event manager still denied global mail preview");
 await Deny<UnauthorizedAccessException>(()=>core.ReadDigestPreviewAsync(member,"2026-11-02"),"Ordinary member denied hidden-event digest");
 await Deny<UnauthorizedAccessException>(()=>core.ReadDigestPreviewAsync(new ClaimsPrincipal(),null),"Anonymous preview denied");
 await Deny<UnauthorizedAccessException>(()=>core.ReadDigestPreviewAsync(Who("admin","wrong"),null),"Security-stamp change denied on every preview read");
 await Deny<UnauthorizedAccessException>(()=>core.ReadDigestPreviewAsync(member,"invalid"),"Authorization precedes day validation, no event probing");
 foreach(var bad in new[]{"","2026-11-02T00:00:00Z","2026-02-30","2026/11/02","2026-11-02,2026-11-03"})await Deny<ValidationException>(()=>core.ReadDigestPreviewAsync(admin,bad),"Strict date-only input rejected: "+bad);
 await using(var db=await factory.CreateDbContextAsync()){var a=await db.Users.SingleAsync(x=>x.Id=="admin");a.IsAdmin=false;await db.SaveChangesAsync();}
 await Deny<UnauthorizedAccessException>(()=>core.ReadDigestPreviewAsync(admin,null),"Lost global role denied despite existing authenticated session");
 await using(var db=await factory.CreateDbContextAsync()){var a=await db.Users.SingleAsync(x=>x.Id=="admin");a.IsAdmin=true;db.RevokedAuthSessions.Add(new(){TokenHash=SemEndpoints.Hash("admin-session"),UserId="admin",RevokedAt=DigestPreviewBuilder.Format(clock.Now)});await db.SaveChangesAsync();}
 await Deny<UnauthorizedAccessException>(()=>core.ReadDigestPreviewAsync(admin,null),"Revoked connected session denied on next read");
 await using(var db=await factory.CreateDbContextAsync()){
  Check(!db.Database.HasPendingModelChanges(),"No additional schema or migration required for read-only preview");Check((await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").ToListAsync()).Single()=="ok"&&(await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").ToListAsync()).Single()==0,"Preview preserves SQLite integrity and foreign keys");
 }

 await using(var db=await factory.CreateDbContextAsync()){
  db.RevokedAuthSessions.Remove(await db.RevokedAuthSessions.SingleAsync());
  var template=await db.BusinessNotifications.FirstAsync();var phase=await db.Phases.SingleAsync(x=>x.EventId==eventId&&x.IsActive);
  for(int n=0;n<996;n++){var id=Guid.NewGuid().ToString();db.RegistrationOperations.Add(new(){Id=id,EventId=eventId,ActorId="admin",SubjectId="member",PhaseId=phase.Id,Kind="Register",At=template.CreatedAt,RequestHash="hash",ResultJson="{}"});db.BusinessNotifications.Add(new(){Id=id,EventId=eventId,UserId="member",Kind="Register",CreatedAt=template.CreatedAt,ProtectedPayload=template.ProtectedPayload,Due=0});}
  await db.SaveChangesAsync();
 }
 await Deny<ValidationException>(()=>core.ReadDigestPreviewAsync(admin,"2026-11-02"),"Preview overflow refuses instead of silently omitting operations");
 Console.WriteLine("Digest checks passed: "+passed);
}finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
sealed class Clock:TimeProvider{public DateTimeOffset Now{get;set;}public override DateTimeOffset GetUtcNow()=>Now;}
