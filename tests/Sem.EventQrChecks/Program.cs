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
var root=Path.Combine(Path.GetTempPath(),"sem-event-qr-"+Guid.NewGuid());Directory.CreateDirectory(root);
var services=new ServiceCollection();services.AddLogging();services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+Path.Combine(root,"data.db")),ServiceLifetime.Scoped);services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();services.AddSingleton(TimeProvider.System);Microsoft.Extensions.DependencyInjection.DataProtectionServiceCollectionExtensions.AddDataProtection(services);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();int passed=0;
void Check(bool v,string label){if(!v)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
async Task Deny<T>(Func<Task> f,string label)where T:Exception{bool denied=false;try{await f();}catch(T){denied=true;}Check(denied,label);}
ClaimsPrincipal Who(string id)=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,id),new Claim("AspNet.Identity.SecurityStamp",id+"-stamp"),new Claim(PasskeyAdditionService.SessionClaim,id+"-session"),new Claim(ClaimTypes.Role,"Admin")],"Identity.Application"));
var admin=Who("admin");var manager=Who("manager");var viewer=Who("viewer");
try{
 await using(var db=await factory.CreateDbContextAsync()){
  await SqliteStorageInitializer.InitializeAsync(db);
  foreach(var id in new[]{"admin","manager","viewer","performer","spectator"})db.Users.Add(new(){Id=id,Email=id+"@PRIVATE.invalid",UserName=id+"@PRIVATE.invalid",DisplayName=id=="performer"?"ＡＢＣ ﾈｲﾃｨﾌﾞ":id,Enabled=true,EmailConfirmed=true,SecurityStamp=id+"-stamp",IsAdmin=id=="admin"});
  foreach(var id in new[]{"event","other"}){var e=new SessionEvent{Id=id,Title=id,ScheduledDate="2099-01-01T00:00:00.000Z",CreatedAt="2026-10-01T00:00:00.000Z",ManualPhaseOverride=true};e.Phases.Add(new(){EventId=id,Kind="Hidden",Name="Hidden",IsActive=true});e.Phases.Add(new(){EventId=id,Kind="View",Name="View"});e.Phases.Add(new(){Id=id+"-custom",EventId=id,Kind="Custom",Name="Signup",MaxPicks=-1});if(id=="event")e.Admins.Add(new(){EventId=id,UserId="manager",GrantedAt=e.CreatedAt});db.SessionEvents.Add(e);}
  db.UserProfiles.Add(new(){UserId="performer",Reading="ねいてぃぶ",CallName="ねい",SocialJson="[{\"Service\":\"PRIVATE SOCIAL\",\"Url\":\"https://private.invalid/\"}]"});
  db.EventPartDefinitions.AddRange(new EventPartDefinition{Id="vo",EventId="event",Name="Voice",NormalizedName="VOICE",Category="Sing"},new EventPartDefinition{Id="gt",EventId="event",Name="Guitar",NormalizedName="GUITAR",Category="Play"});db.Songs.Add(new(){Id="song",EventId="event",Title="Current song",PerformanceSeconds=60});db.SongParts.AddRange(new SongPart{Id="voice",EventId="event",SongId="song",PartDefinitionId="vo",AssignedUserId="performer",PhaseId="event-custom"},new SongPart{Id="guitar",EventId="event",SongId="song",PartDefinitionId="gt",AssignedUserId="performer",PhaseId="event-custom"});db.EventSpectators.Add(new(){EventId="event",UserId="spectator",RegisteredAt="2026-10-01T00:00:00.000Z",RegisteredBy="admin"});db.EventQuestions.Add(new(){Id="private",EventId="event",Text="PRIVATE QUESTION",Type="Text"});db.EventQuestionAnswers.Add(new(){QuestionId="private",UserId="viewer",Value="PRIVATE ANSWER",UpdatedAt="2026-10-01T00:00:00.000Z"});await db.SaveChangesAsync();
 }

 var performer=Who("performer");var spectator=Who("spectator");
 EventQrEdit Issue(string? user=null,long version=0,bool reissue=false,string? request=null)=>new(request??Guid.NewGuid().ToString(),user,version,reissue);
 EventQrReadEdit Read(string token,string? request=null)=>new(request??Guid.NewGuid().ToString(),token);
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventQrAsync(performer,"event"),"Ordinary user cannot retrieve own token in Hidden event");
 await Deny<UnauthorizedAccessException>(()=>core.IssueEventQrAsync(viewer,"event",Issue("performer")),"Forged Admin claim cannot issue another participant token");
 await Deny<UnauthorizedAccessException>(()=>core.RecordEventQrReadAsync(viewer,"event",Read(new string('A',64))),"Member cannot submit manager read operation");
 Check((await core.ReadEventQrAsync(manager,"event","performer")).Token is null,"Authorized initial read creates no token");
 await using(var db=await factory.CreateDbContextAsync()){await using var tx=await db.Database.BeginTransactionAsync();var phases=await db.Set<EventPhase>().Where(x=>x.EventId=="event").ToArrayAsync();foreach(var phase in phases)phase.IsActive=false;await db.SaveChangesAsync();phases.Single(x=>x.Kind=="View").IsActive=true;await db.SaveChangesAsync();await tx.CommitAsync();}
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventQrAsync(performer,"event","spectator"),"Participant cannot read someone else token");
 await Deny<ValidationException>(()=>core.IssueEventQrAsync(viewer,"event",Issue()),"Historical answer without current registration cannot issue token");
 var first=Issue();var result=await core.IssueEventQrAsync(performer,"event",first);var view=await core.ReadEventQrAsync(performer,"event");Check(result.TokenVersion==1&&view.TokenVersion==1&&view.Token?.Length==64,"Registered performer receives random 256-bit token");
 Check((await core.ReadEventQrAsync(manager,"event","performer")).Token==view.Token,"Current assigned manager may retrieve participant token");
 Check((await core.IssueEventQrAsync(performer,"event",first)).Replayed,"Exact issuance retry is once-only");
 await Deny<ValidationException>(()=>core.IssueEventQrAsync(performer,"event",first with{Reissue=true}),"Changed issuance cannot reuse request ID");
 await Deny<ValidationException>(()=>core.IssueEventQrAsync(performer,"event",Issue(version:1)),"Existing token must not silently rotate on first-issue request");
 var read=Read(view.Token!);var readResult=await core.RecordEventQrReadAsync(manager,"event",read);Check(readResult.UserId=="performer"&&readResult.TokenVersion==1,"Valid code read stores actor time participant and token version");
 Check((await core.RecordEventQrReadAsync(manager,"event",read)).Replayed,"Same scan operation replay does not add another read");
 await core.RecordEventQrReadAsync(manager,"event",Read(view.Token!));Check((await core.ReadEventQrHistoryAsync(manager,"event")).Reads.Length==2,"Intentional repeated reads with new IDs are distinct retained records");
 await Deny<ValidationException>(()=>core.RecordEventQrReadAsync(manager,"event",Read("invalid")),"Malformed token rejected without successful read");
 await Deny<ValidationException>(()=>core.RecordEventQrReadAsync(manager,"event",Read(new string('A',64))),"Unknown correctly shaped token rejected");
 await Deny<ValidationException>(()=>core.RecordEventQrReadAsync(admin,"other",Read(view.Token!)),"Token cannot cross event boundaries");
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventQrHistoryAsync(performer,"event"),"Ordinary participant cannot see management read history");
 var reissue=Issue(version:1,reissue:true);await core.IssueEventQrAsync(performer,"event",reissue);var current=await core.ReadEventQrAsync(performer,"event");Check(current.TokenVersion==2&&current.Token!=view.Token,"Explicit reissue atomically rotates current token");
 await Deny<ValidationException>(()=>core.RecordEventQrReadAsync(manager,"event",Read(view.Token!)),"Old code is invalid for new read after reissue");
 Check((await core.RecordEventQrReadAsync(manager,"event",read)).Replayed&&(await core.ReadEventQrHistoryAsync(manager,"event")).Reads.Length==2,"Historical read replay reports commit without new scan after reissue");
 Check((await core.IssueEventQrAsync(performer,"event",first)).TokenVersion==1&&(await core.ReadEventQrAsync(performer,"event")).Token==current.Token,"Historical issuance retry does not roll current token back");
 await Deny<CoreConflictException>(()=>core.IssueEventQrAsync(performer,"event",Issue(version:1,reissue:true)),"Stale reissue version rejected");
 await core.IssueEventQrAsync(spectator,"event",Issue());Check((await core.ReadEventQrAsync(spectator,"event")).Token is not null,"Spectator-only registration can issue own code");
 var before=await core.ReadReceptionTargetAsync(manager,"event","performer");await core.RecordEventQrReadAsync(manager,"event",Read(current.Token!));Check((await core.ReadReceptionTargetAsync(manager,"event","performer")).Current is null,"Valid code scan does not complete reception");
 var receipt=new ManualReceptionEdit(Guid.NewGuid().ToString(),"performer",before.EventVersion,before.ParticipantVersion,before.ReceptionVersion,true,"");await core.CompleteManualReceptionAsync(manager,"event",receipt);var completed=await core.ReadReceptionTargetAsync(manager,"event","performer");await core.CancelReceptionAsync(manager,"event",new(Guid.NewGuid().ToString(),"performer",completed.Current!.Id,completed.ReceptionVersion,true));Check((await core.ReadEventQrHistoryAsync(manager,"event")).Reads.Length==3,"Manual receipt cancellation preserves all read records");
 await using(var db=await factory.CreateDbContextAsync()){var row=await db.EventParticipantQrs.SingleAsync(x=>x.UserId=="performer");Check(row.ProtectedToken!=current.Token&&!row.ProtectedToken.Contains(current.Token!)&&row.TokenHash!=current.Token,"Raw current token is not stored in readable plaintext");Check(!(await db.EventQrOperations.Select(x=>x.ResultJson).ToArrayAsync()).Any(x=>x.Contains(current.Token!)||x.Contains(view.Token!)),"Replay results never retain secret token body");Check(!db.Database.HasPendingModelChanges(),"QR model matches additive migration");await SqliteStorageInitializer.InitializeAsync(db);}
 Check((await core.ReadEventQrAsync(performer,"event")).Token==current.Token&&(await core.ReadEventQrHistoryAsync(manager,"event")).Reads.Length==3,"Restart with protection keys retains token and scan history");
 await using(var db=await factory.CreateDbContextAsync()){foreach(var p in await db.SongParts.Where(x=>x.AssignedUserId=="performer").ToArrayAsync()){p.AssignedUserId=p.PhaseId=null;}await db.SaveChangesAsync();}
 await Deny<ValidationException>(()=>core.RecordEventQrReadAsync(manager,"event",Read(current.Token!)),"Removed final registration invalidates new scan");Check((await core.ReadEventQrHistoryAsync(manager,"event")).Reads.Length==3,"Registration removal preserves historical reads");
 await using(var db=await factory.CreateDbContextAsync()){db.EventAdmins.Remove(await db.EventAdmins.SingleAsync());await db.SaveChangesAsync();}
 await Deny<UnauthorizedAccessException>(()=>core.RecordEventQrReadAsync(manager,"event",read),"Removed management role denies even historical read retry");
 await using(var db=await factory.CreateDbContextAsync()){db.RevokedAuthSessions.Add(new(){TokenHash=SemEndpoints.Hash("spectator-session"),UserId="spectator",RevokedAt="2026-10-01T00:00:00.000Z"});await db.SaveChangesAsync();}
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventQrAsync(spectator,"event"),"Revoked own session cannot retrieve token");
 var history=JsonSerializer.Serialize(await core.ReadEventQrHistoryAsync(admin,"event"));Check(!history.Contains("PRIVATE.invalid")&&!history.Contains(current.Token!)&&!history.Contains("TokenHash")&&!history.Contains("PRIVATE ANSWER"),"History contains no emails secret tokens hashes or private answers");

 await using(var db=await factory.CreateDbContextAsync()){for(var i=0;i<105;i++)db.EventQrReads.Add(new(){Id=Guid.NewGuid().ToString(),EventId="event",UserId="performer",ActorId="admin",TokenVersion=1,At="2099-01-01T00:00:00.000Z"});await db.SaveChangesAsync();}
 var page=await core.ReadEventQrHistoryAsync(admin,"event");Check(page.Reads.Length==100&&page.Next is not null,"History is bounded with explicit continuation");var tail=await core.ReadEventQrHistoryAsync(admin,"event",page.Next);Check(tail.Reads.Length==8&&tail.Next is null&&page.Reads.Select(x=>x.Id).Intersect(tail.Reads.Select(x=>x.Id)).Count()==0,"Timestamp ID cursor preserves all 108 records including tied times without overlap");
 await Deny<ValidationException>(()=>core.ReadEventQrHistoryAsync(admin,"event",new("bad","bad")),"Malformed history cursor rejected");
 await using(var db=await factory.CreateDbContextAsync()){db.Users.Add(new(){Id="qr-race",DisplayName="QR race",Enabled=true,EmailConfirmed=true});db.EventSpectators.Add(new(){EventId="event",UserId="qr-race",RegisteredAt="2026-10-01T00:00:00.000Z",RegisteredBy="admin"});await db.SaveChangesAsync();}
 async Task<bool> Attempt(EventQrEdit input){try{await core.IssueEventQrAsync(admin,"event",input);return true;}catch(CoreConflictException){return false;}catch(SqliteException ex)when(ex.SqliteErrorCode is 5 or 6){return false;}catch(DbUpdateException ex)when(ex.InnerException is SqliteException sql&&sql.SqliteErrorCode is 5 or 6 or 19){return false;}}
 var outcomes=await Task.WhenAll(Task.Run(()=>Attempt(Issue("qr-race"))),Task.Run(()=>Attempt(Issue("qr-race"))));Check(outcomes.Count(x=>x)==1,"Concurrent first issuance commits at most one current token");
 Check((await core.ReadEventQrAsync(admin,"event","qr-race")).TokenVersion==1,"Concurrent loser does not consume token version");
 Console.WriteLine("Event QR checks passed: "+passed);
}finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
