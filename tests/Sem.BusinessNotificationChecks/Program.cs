using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;
var root=Path.Combine(Path.GetTempPath(),"sem-business-"+Guid.NewGuid());Directory.CreateDirectory(root);
var services=new ServiceCollection();services.AddLogging();services.AddDataProtection().SetApplicationName("SEM.BusinessChecks").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root,"keys")));
services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+Path.Combine(root,"data.db")),ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
var time=new Clock{Now=DateTimeOffset.Parse("2026-11-01T15:00:00.123Z")};services.AddSingleton<TimeProvider>(time);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();var store=provider.GetRequiredService<BusinessNotificationStore>();
int passed=0;void Check(bool b,string label){if(!b)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
async Task Deny<T>(Func<Task> f,string label)where T:Exception{bool b=false;try{await f();}catch(T){b=true;}Check(b,label);}
ClaimsPrincipal Who(string id)=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,id),new Claim("AspNet.Identity.SecurityStamp",id+"-stamp"),new Claim(PasskeyAdditionService.SessionClaim,id+"-session")],"Identity.Application"));
var admin=Who("admin");var one=Who("one");var two=Who("two");var manager=Who("manager");string eventId="";
async Task<ParticipationEdit> Draft(ClaimsPrincipal actor,string kind,string[] slots,string?subject=null,string reason=""){
 var p=await core.ReadParticipationAsync(actor,eventId,subjectId:subject);return new(Guid.NewGuid().ToString(),p.RowVersion,p.PhaseId,kind,slots.Select(id=>new SlotChoice(id,p.Slots.Single(x=>x.Id==id).RowVersion)).ToArray(),[],subject,false,reason,p.ParticipantVersion);
}
async Task<BusinessNotification[]> Queue(){await using var db=await factory.CreateDbContextAsync();return await db.BusinessNotifications.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync();}
try{
 await using(var db=await factory.CreateDbContextAsync()){
  await db.GetService<IMigrator>().MigrateAsync("20261013063027_AddShortening");
  foreach(var id in new[]{"admin","manager","one","two"})db.Users.Add(new(){Id=id,UserName=id+"@test.example",Email=id+"@test.example",DisplayName=id+" display",Enabled=true,EmailConfirmed=true,IsAdmin=id=="admin",SecurityStamp=id+"-stamp"});
  db.Deliveries.Add(new(){Id="legacy-delivery",UserId="one",ProtectedBody="retained-ciphertext",Subject="retained auth mail",Due=1});db.Messages.Add(new(){Id="legacy-message",UserId="one",Title="retained",Body="retained"});await db.SaveChangesAsync();
  await SqliteStorageInitializer.InitializeAsync(db);
  Check((await db.Deliveries.SingleAsync()).ProtectedBody=="retained-ciphertext"&&await db.Users.CountAsync()==4&&await db.Messages.CountAsync()==1,"Populated PR15 migration retains users existing auth outbox and messages");
  Check(!db.Database.HasPendingModelChanges(),"Generated notification migration matches EF model");
 }
 eventId=await core.SaveEventAsync(admin,null,new("Business Event","","",time.Now.AddDays(2),null,"","Hidden",["manager"],0));
 var phases=await core.ReadPhasePlanAsync(admin,eventId);var custom=new CustomPhaseEdit(Guid.NewGuid().ToString(),"Open",-1,null,null);await core.SavePhasePlanAsync(admin,eventId,new(phases.RowVersion,true,custom.Id,[custom]));
 var defs=await core.ReadDefinitionsAsync(admin,eventId);var v=Guid.NewGuid().ToString();var g=Guid.NewGuid().ToString();await core.SaveDefinitionsAsync(admin,eventId,new(defs.RowVersion,[new(v,"Voice","Sing"),new(g,"Guitar","Play")]));
 var songs=await core.ReadSongsAsync(admin,eventId);var s1=Guid.NewGuid().ToString();var s2=Guid.NewGuid().ToString();songs=await core.SaveSongsAsync(admin,eventId,new(songs.RowVersion,0,[new(s1,"Frozen Song A","",180,null,[v,g]),new(s2,"Song B","",180,null,[v,g])]));
 var firstSlot=songs.Songs[0].Parts[0].Id;var secondSlot=songs.Songs[0].Parts[1].Id;var thirdSlot=songs.Songs[1].Parts[0].Id;
 var first=await Draft(one,"Register",[firstSlot,secondSlot]);var result=await core.SubmitParticipationAsync(one,eventId,first);
 Check(result.Succeeded==2,"Business registration commits voice plus instrument");var queued=(await Queue()).Single();var originalCipher=queued.ProtectedPayload;var snapshot=store.Read(queued);
 Check(queued.Id==first.RequestId&&queued.UserId=="one"&&queued.EventId==eventId&&queued.Status=="Pending"&&queued.Attempts==0&&queued.Due==time.Now.ToUnixTimeSeconds(),"One immediate encrypted email per committed operation, not per slot");
 Check(snapshot.Slots.Length==2&&snapshot.Slots.All(x=>x.SongTitle=="Frozen Song A")&&snapshot.EventTitle=="Business Event"&&snapshot.Body.Contains("Voice")&&snapshot.Body.Contains("Guitar"),"Frozen details include only successful song and part names");
 Check(snapshot.Body.Contains("+09:00")&&snapshot.Body.Contains("2026-11-02")&&!queued.ProtectedPayload.Contains("Frozen Song A")&&!snapshot.Body.Contains("one@test.example"),"Body uses absolute Japanese date and encrypted storage, no private email list");
 result=await core.SubmitParticipationAsync(one,eventId,first);Check(result.Replayed&&(await Queue()).Length==1,"Replay never adds a second business email");
 await Deny<ValidationException>(async()=>await core.SubmitParticipationAsync(one,eventId,first with{Reason="changed"}),"Reused operation payload rejected");Check((await Queue()).Length==1,"Rejected reuse adds no notification");
 var partial=await Draft(two,"Register",[firstSlot,thirdSlot]);result=await core.SubmitParticipationAsync(two,eventId,partial);snapshot=store.Read((await Queue()).Single(x=>x.Id==partial.RequestId));
 Check(result.Succeeded==1&&snapshot.Slots.Length==1&&snapshot.Slots[0].SlotId==thirdSlot,"Partial success emails only the committed slot, not occupied selection");
 var failure=await Draft(two,"Register",[firstSlot]);result=await core.SubmitParticipationAsync(two,eventId,failure);Check(result.Succeeded==0&&(await Queue()).Length==2,"Zero-success operation generates no email");
 await Deny<UnauthorizedAccessException>(async()=>await core.SubmitParticipationAsync(one,eventId,await Draft(one,"Release",[firstSlot],reason:"forged")),"Member cannot trigger release mail");
 await Deny<ValidationException>(async()=>await core.SubmitParticipationAsync(manager,eventId,await Draft(manager,"Release",[firstSlot],subject:"one")),"Reasonless manager release rejected");Check((await Queue()).Length==2,"Rejected authorization or validation has no outbox effect");
 var release=await Draft(manager,"Release",[firstSlot],"one","Requested by performer");await core.SubmitParticipationAsync(manager,eventId,release);snapshot=store.Read((await Queue()).Single(x=>x.Id==release.RequestId));
 Check(snapshot.Slots.Length==1&&snapshot.Reason=="Requested by performer"&&snapshot.ActorName=="manager display"&&snapshot.Body.Contains("登録解除"),"Release snapshots actor and reason and targets affected performer");
 Check((await Queue()).Single(x=>x.Id==release.RequestId).UserId=="one","Proxy actor never becomes recipient by accident");
 var renamed=await core.ReadSongsAsync(admin,eventId);await core.SaveSongsAsync(admin,eventId,new(renamed.RowVersion,0,renamed.Songs.Select(s=>new SongEdit(s.Id,"Renamed",s.Artist,s.PerformanceSeconds,s.PrepSeconds,s.Parts.Select(p=>p.DefinitionId).ToArray())).ToArray()));
 Check(store.Read((await Queue()).Single(x=>x.Id==first.RequestId)).Slots.All(x=>x.SongTitle=="Frozen Song A"),"Later plan edits do not rewrite historical notification details");
 var stale=await Draft(one,"Register",[firstSlot]);await core.SubmitParticipationAsync(one,eventId,await Draft(one,"Answers",[]));await Deny<CoreConflictException>(async()=>await core.SubmitParticipationAsync(one,eventId,stale),"Stale participant draft rejected");Check((await Queue()).Length==3,"Answer-only success and stale registration send no email");
 // Inject a failure after the operation and outbox are staged: both must roll back.
 await using(var db=await factory.CreateDbContextAsync())await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_business BEFORE INSERT ON BusinessNotifications BEGIN SELECT RAISE(ABORT,'synthetic rollback'); END");
 var rolled=await Draft(one,"Register",[firstSlot]);await Deny<DbUpdateException>(async()=>await core.SubmitParticipationAsync(one,eventId,rolled),"Queue insert failure rolls back the entire registration");
 await using(var db=await factory.CreateDbContextAsync()){
  Check(!await db.RegistrationOperations.AnyAsync(x=>x.Id==rolled.RequestId)&&(await db.SongParts.SingleAsync(x=>x.Id==firstSlot)).AssignedUserId is null,"Rolled-back operation leaves neither assignment nor operation result");
  await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_business");
 }
 Check((await Queue()).Length==3,"Rollback leaves no future mail");
 var fake=new Transport();BusinessEmailDispatcher Dispatcher(IBusinessEmailTransport t)=>new(factory,store,t,time,NullLogger<BusinessEmailDispatcher>.Instance);
 fake.Fail=true;await Dispatcher(fake).DispatchOnce();queued=(await Queue()).Single(x=>x.Id==first.RequestId);
 Check(queued.Status=="Pending"&&queued.Attempts==1&&queued.Error==nameof(IOException)&&queued.Due==time.Now.ToUnixTimeSeconds()+30,"SMTP failure persists bounded retry independently of business operation");
 Check((await core.ReadParticipationAsync(one,eventId)).Slots.Single(x=>x.Id==secondSlot).AssignedUserId=="one","SMTP failure never undoes committed assignment");
 var calls=fake.Calls.Count;await Dispatcher(fake).DispatchOnce();Check(fake.Calls.Count==calls,"Future retries are not sent early");
 fake.Fail=false;time.Now=time.Now.AddSeconds(31);await Task.WhenAll(Dispatcher(fake).DispatchOnce(),Dispatcher(fake).DispatchOnce());
 Check(fake.Calls.Count==calls+3&&(await Queue()).All(x=>x.Status=="Sent"&&x.Attempts==2&&x.SentAt!=null&&x.Claim==null),"Two dispatchers claim atomically and each queued email is sent once");
 calls=fake.Calls.Count;await Dispatcher(fake).DispatchOnce();Check(fake.Calls.Count==calls,"Sent queue cannot be delivered again");
 // Recipient revoked between commit and worker; cancellation is not a successful SMTP send.
 var next=await Draft(one,"Register",[firstSlot]);await core.SubmitParticipationAsync(one,eventId,next);await using(var db=await factory.CreateDbContextAsync()){var u=await db.Users.SingleAsync(x=>x.Id=="one");u.Enabled=false;await db.SaveChangesAsync();}
 await Dispatcher(fake).DispatchOnce();queued=(await Queue()).Single(x=>x.Id==next.RequestId);Check(queued.Status=="Cancelled"&&queued.SentAt==null&&fake.Calls.Count==calls,"Disabled recipient cancels business mail without false SentAt");
 await using(var db=await factory.CreateDbContextAsync()){var u=await db.Users.SingleAsync(x=>x.Id=="one");u.Enabled=true;await db.SaveChangesAsync();}
 // Reclaim a persisted abandoned lease after restart and DP key reload.
 var nextRelease=await Draft(manager,"Release",[firstSlot],"one","Second release");await core.SubmitParticipationAsync(manager,eventId,nextRelease);
 await using(var db=await factory.CreateDbContextAsync()){var x=await db.BusinessNotifications.SingleAsync(x=>x.Id==nextRelease.RequestId);x.Status="Sending";x.Claim="abandoned";x.LeaseUntil=time.Now.ToUnixTimeSeconds()+120;x.Attempts=1;await db.SaveChangesAsync();}
 await Dispatcher(fake).DispatchOnce();Check(fake.Calls.Count==calls,"Live lease prevents a second worker from sending");
 time.Now=time.Now.AddSeconds(121);var restartedStore=new BusinessNotificationStore(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root,"keys")),b=>b.SetApplicationName("SEM.BusinessChecks")));
 Check(restartedStore.Read((await Queue()).Single(x=>x.Id==first.RequestId)).Body.Contains("Frozen Song A"),"Restored DP keys decrypt immutable notification snapshot");
 await new BusinessEmailDispatcher(factory,restartedStore,fake,time,NullLogger<BusinessEmailDispatcher>.Instance).DispatchOnce();queued=(await Queue()).Single(x=>x.Id==nextRelease.RequestId);Check(queued.Status=="Sent"&&queued.Attempts==2&&fake.Calls.Count==calls+1,"Expired claim is recovered after restart");
 // Retry exhaustion; only synthetic transport, no external SMTP.
 var retry=await Draft(two,"Release",[thirdSlot],"two","Retry test");await core.SubmitParticipationAsync(manager,eventId,retry);fake.Fail=true;
 for(int i=0;i<5;i++){await Dispatcher(fake).DispatchOnce();time.Now=time.Now.AddMinutes(20);}
 queued=(await Queue()).Single(x=>x.Id==retry.RequestId);Check(queued.Status=="Failed"&&queued.Attempts==5&&queued.SentAt==null,"Fifth SMTP failure is terminal and never called Sent");
 calls=fake.Calls.Count;await Dispatcher(fake).DispatchOnce();Check(fake.Calls.Count==calls,"Terminal failure does not retry forever");
 await using(var db=await factory.CreateDbContextAsync()){
  Check((await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").ToListAsync()).Single()=="ok"&&(await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").ToListAsync()).Single()==0,"Business migration and queue preserve SQLite integrity and all foreign keys");
  Check((await db.Deliveries.SingleAsync()).ProtectedBody=="retained-ciphertext","Business dispatcher never consumes authentication queue");
 }
 Console.WriteLine("Business notification checks passed: "+passed);
}finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
sealed class Clock:TimeProvider{public DateTimeOffset Now{get;set;}public override DateTimeOffset GetUtcNow()=>Now;}
sealed class Transport:IBusinessEmailTransport{
 public bool Fail;public List<string> Calls=[];readonly object gate=new();
 public Task Send(string id,ApplicationUser user,BusinessEmailPayload payload,CancellationToken ct){lock(gate){Calls.Add(id);}if(Fail)throw new IOException("synthetic SMTP failure containing no secrets");return Task.CompletedTask;}
}
