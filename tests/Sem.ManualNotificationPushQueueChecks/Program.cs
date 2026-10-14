using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;
if (args.Length == 4 && args[0] == "--recover") {
 var childClock = new Clock(DateTimeOffset.FromUnixTimeSeconds(long.Parse(args[3])));
 var childFactory = new Factory(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source="+args[1]).Options);
 var childQueue = new ManualNotificationPushQueue(childFactory, childClock);
 var owned = await childQueue.ClaimAsync(args[2]);
 if (owned is null || !await childQueue.RecheckAsync(owned.DeliveryId, owned.Token) ||
     !await childQueue.CompleteAsync(owned.DeliveryId, owned.Token, ManualNotificationPushCompletion.ProviderAccepted))
     throw new Exception("Separate-process synthetic queue recovery failed");
 Console.WriteLine("Separate-process synthetic queue recovery PASS");return;
}
var root=Path.Combine(Path.GetTempPath(),"sem-manual-queue-"+Guid.NewGuid());Directory.CreateDirectory(root);
var path=Path.Combine(root,"data.db");var clock=new Clock(DateTimeOffset.Parse("2026-10-14T08:15:00Z"));
var services=new ServiceCollection();services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+path),ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddSingleton<TimeProvider>(clock);DataProtectionServiceCollectionExtensions.AddDataProtection(services);
services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();
var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();
var queue=new ManualNotificationPushQueue(factory,clock);int passed=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
async Task Change(Func<ApplicationDbContext,Task> action){await using var db=await factory.CreateDbContextAsync();await action(db);await db.SaveChangesAsync();}
var actor=new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,"admin"),new("AspNet.Identity.SecurityStamp","admin-stamp"),new(PasskeyAdditionService.SessionClaim,"admin-session")],"Identity.Application"));
async Task<string> Accept(){var p=await core.PreviewEventManualNotificationAsync(actor,"event","PRIVATE-SUBJECT","PRIVATE-BODY");var id=Guid.NewGuid().ToString("D");await core.AcceptEventManualNotificationAsync(actor,"event",new(id,p.Fingerprint,p.Subject,p.Body,true));await using var db=await factory.CreateDbContextAsync();return await db.Set<ManualNotificationRecipient>().Where(x=>x.OperationId==id).Select(x=>x.Id).SingleAsync();}
async Task<ManualNotificationPushDelivery[]> Rows(string recipient){await using var db=await factory.CreateDbContextAsync();return await db.Set<ManualNotificationPushDelivery>().AsNoTracking().Where(x=>x.RecipientId==recipient).OrderBy(x=>x.DeviceId).ToArrayAsync();}
async Task<string> PrepareOne(){var id=await Accept();var p=await queue.PrepareAsync(id);Check(p.State=="Prepared"&&p.DeviceCount==2,"fresh accepted audience prepares current valid devices");return (await Rows(id))[0].Id;}
async Task<ManualNotificationPushDelivery> Row(string id){await using var db=await factory.CreateDbContextAsync();return await db.Set<ManualNotificationPushDelivery>().AsNoTracking().SingleAsync(x=>x.Id==id);}
async Task<bool> Finish(ManualNotificationPushClaim c,ManualNotificationPushCompletion result)=>await queue.CompleteAsync(c.DeliveryId,c.Token,result);
try{
 await using(var db=await factory.CreateDbContextAsync()){
  await db.GetService<IMigrator>().MigrateAsync("20261014074520_AddManualNotificationPushAdmission");
  foreach(var id in new[]{"admin","member","late"})db.Users.Add(new(){Id=id,UserName=id,Email=id+"@PRIVATE.invalid",DisplayName=id,Enabled=true,EmailConfirmed=true,SecurityStamp=id+"-stamp",IsAdmin=id=="admin"});
  var e=new SessionEvent{Id="event",Title="PRIVATE-EVENT",ScheduledDate="2099-01-01T00:00:00Z",CreatedAt="2026-10-01T00:00:00Z",ManualPhaseOverride=true};e.Phases.Add(new(){Id="view",EventId="event",Kind="View",Name="View",IsActive=true});e.Phases.Add(new(){Id="hidden",EventId="event",Kind="Hidden",Name="Hidden"});db.SessionEvents.Add(e);
  db.EventSpectators.Add(new(){EventId="event",UserId="member",RegisteredBy="admin",RegisteredAt="2026-10-01T00:00:00Z"});
  foreach(var id in new[]{"a","b"})db.PushDevices.Add(new(){Id=id,UserId="member",SecurityStamp="member-stamp",SessionHash="member-session",Endpoint="https://fcm.googleapis.com/PRIVATE-"+id,P256dh="PRIVATE-KEY",Auth="PRIVATE-AUTH"});
  db.Messages.Add(new(){Id="legacy-message",UserId="member",Title="keep",Body="legacy",Created=123,ReadAt=124});
  db.Add(new ManualNotificationOperation{Id="prior-admission",EventId="event",ActorId="admin",RequestHash=new('A',64),PreviewFingerprint=new('B',64),Subject="prior",Body="keep",RecipientCount=1,AcceptedAt=clock.Now.ToUnixTimeSeconds()});
  db.Messages.Add(new(){Id=new('E',64),UserId="member",Title="prior",Body="keep",Created=clock.Now.ToUnixTimeSeconds()});
  db.Add(new ManualNotificationRecipient{Id=new('E',64),OperationId="prior-admission",EventId="event",UserId="member"});
  db.Add(new ManualNotificationPushAdmission{OperationId="prior-admission",Epoch=1,AcceptedAt=clock.Now.ToUnixTimeSeconds(),ExpiresAt=clock.Now.ToUnixTimeSeconds()+600});
  await db.SaveChangesAsync();await db.Database.MigrateAsync();
  Check(await db.Set<ManualNotificationPushAdmission>().AnyAsync(x=>x.OperationId=="prior-admission"&&x.ExpiresAt==x.AcceptedAt+600)&&await db.Set<ManualNotificationRecipient>().AnyAsync(x=>x.Id==new string('E',64)&&x.PushState=="Pending"),"queue upgrade preserves existing admission and dormant recipient history");
  Check(!db.Database.HasPendingModelChanges()&&await db.Messages.AnyAsync(x=>x.Id=="legacy-message"&&x.ReadAt==124),"additive queue migration preserves populated old schema and matches model");
  Check(await db.Set<ManualNotificationPushDelivery>().CountAsync()==0&&await db.Set<ManualNotificationPushActivation>().CountAsync()==0,"upgrade neither arms Push nor creates delivery jobs");
 }
 Check((await queue.PrepareAsync("")).State=="InvalidInput"&&await queue.ClaimAsync("bad")==null,"invalid inputs rejected");
 Check((await queue.PrepareAsync(new string('D',64))).State=="MissingAcceptedRecipient","unknown audience cannot prepare");
 var off=await Accept();Check((await queue.PrepareAsync(off)).State=="NotAdmitted","default OFF permanently prepares no Push work");
 await Change(db=>{db.Add(new ManualNotificationPushActivation{Enabled=true,Epoch=1});return Task.CompletedTask;});
 Check((await queue.PrepareAsync(off)).Replayed&&(await Rows(off)).Length==0,"later activation cannot revive nonadmitted preparation");
 var accepted=await Accept();var first=await queue.PrepareAsync(accepted);var rows=await Rows(accepted);
 Check(first.State=="Prepared"&&first.DeviceCount==2&&!first.Replayed&&rows.Length==2,"one row per current bound device staged atomically");
 Check(rows.All(x=>x.State=="Pending"&&x.Attempts==0&&x.Claim==null&&x.LeaseUntil==null&&x.ExpiresAt==clock.Now.ToUnixTimeSeconds()+600),"staged rows start dormant with acceptance-based deadline");
 Check(!JsonSerializer.Serialize(rows).Contains("PRIVATE"),"queue stores no endpoint keys stamp session or message content");
 await Change(db=>{db.PushDevices.Add(new(){Id="late-device",UserId="member",SecurityStamp="member-stamp",SessionHash="member-session",Endpoint="https://fcm.googleapis.com/PRIVATE-late",P256dh="PRIVATE-KEY",Auth="PRIVATE-AUTH"});return Task.CompletedTask;});
 Check((await queue.PrepareAsync(accepted)).Replayed&&(await Rows(accepted)).Length==2,"repeat preparation never appends a newly registered device");
 await Change(async db=>db.PushDevices.Remove(await db.PushDevices.SingleAsync(x=>x.Id=="late-device")));
 var claims=await Task.WhenAll(Enumerable.Range(0,3).Select(_=>Task.Run(()=>queue.ClaimAsync(rows[0].Id))));
 Check(claims.Count(x=>x!=null)==1,"concurrent claim requests produce one owner");var claim=claims.Single(x=>x!=null)!;
 Check(claim.Attempt==1&&claim.LeaseUntil==clock.Now.ToUnixTimeSeconds()+30&&await queue.RecheckAsync(claim.DeliveryId,claim.Token),"active owner rechecks current policy and bounded lease");
 Check(!await queue.RecheckAsync(claim.DeliveryId,Guid.NewGuid().ToString("N"))&&!await queue.CompleteAsync(claim.DeliveryId,Guid.NewGuid().ToString("N"),ManualNotificationPushCompletion.ProviderAccepted),"wrong owner cannot recheck or finish");
 Check(await Finish(claim,ManualNotificationPushCompletion.ProviderAccepted),"provider acceptance finalizes current claim");
 Check((await Row(claim.DeliveryId)).State=="Accepted"&&(await Row(claim.DeliveryId)).ProviderAcceptedAt==clock.Now.ToUnixTimeSeconds(),"provider acceptance is recorded without claiming handset receipt");
 Check(!await Finish(claim,ManualNotificationPushCompletion.RetryableNotAccepted)&&await queue.ClaimAsync(claim.DeliveryId)==null,"completed claim cannot replay mutate or resend");
 var second=await queue.ClaimAsync(rows[1].Id);clock.Now=clock.Now.AddSeconds(30);
 Check(!await queue.RecheckAsync(second!.DeliveryId,second.Token)&&(await Row(second.DeliveryId)).State=="Unknown","elapsed lease becomes unknown rather than automatic retry");
 Check(await queue.ClaimAsync(second.DeliveryId)==null&&!await Finish(second,ManualNotificationPushCompletion.ProviderAccepted),"unknown claim is not reclaimed or overwritten by stale completion");
 var expiredLeaseId=await PrepareOne();var expiredLease=await queue.ClaimAsync(expiredLeaseId);clock.Now=clock.Now.AddSeconds(30);
 Check(await queue.ClaimAsync(expiredLeaseId)==null&&(await Row(expiredLeaseId)).State=="Unknown","claim scan itself fences crashed owner without resend after lease elapsed");
 var unknownId=await PrepareOne();var unknown=await queue.ClaimAsync(unknownId);await Finish(unknown!,ManualNotificationPushCompletion.AcceptanceUnknown);
 Check((await Row(unknownId)).State=="Unknown"&&await queue.ClaimAsync(unknownId)==null,"ambiguous external outcome is never automatically retried");
 var retryId=await PrepareOne();var retry=await queue.ClaimAsync(retryId);var at=clock.Now;
 Check(await Finish(retry!,ManualNotificationPushCompletion.RetryableNotAccepted)&&(await Row(retryId)).DueAt==at.ToUnixTimeSeconds()+15,"definite nonacceptance schedules bounded backoff");
 Check(await queue.ClaimAsync(retryId)==null,"retry cannot run before due time");clock.Now=at.AddSeconds(15);
 var retry2=await queue.ClaimAsync(retryId);Check(retry2?.Attempt==2&&retry2.Token!=retry!.Token,"due retry has a fresh fenced token and incremented attempt");
 Check(!await Finish(retry!,ManualNotificationPushCompletion.ProviderAccepted),"old attempt token cannot complete newer claim");
 await Finish(retry2!,ManualNotificationPushCompletion.RetryableNotAccepted);
 for(var attempt=3;attempt<=5;attempt++){var row=await Row(retryId);clock.Now=DateTimeOffset.FromUnixTimeSeconds(row.DueAt);var c=await queue.ClaimAsync(retryId);Check(c?.Attempt==attempt,"bounded retry attempt "+attempt);await Finish(c!,ManualNotificationPushCompletion.RetryableNotAccepted);}
 Check((await Row(retryId)).State=="Failed"&&await queue.ClaimAsync(retryId)==null,"fifth definite failure exhausts retries");
 var removalId=await PrepareOne();var removal=await queue.ClaimAsync(removalId);
 await Change(async db=>db.EventSpectators.Remove(await db.EventSpectators.SingleAsync()));
 Check(!await queue.RecheckAsync(removalId,removal!.Token)&&(await Row(removalId)).Reason=="NoLongerRegistered","registration removal between claim and send gate permanently suppresses work");
 await Change(db=>{db.EventSpectators.Add(new(){EventId="event",UserId="member",RegisteredBy="admin",RegisteredAt="2026-10-14T08:15:00Z"});return Task.CompletedTask;});
 Check(await queue.ClaimAsync(removalId)==null,"rejoining cannot revive terminally suppressed device job");
 await using(var db=await factory.CreateDbContextAsync())Check(await db.Messages.AnyAsync(x=>x.Id=="legacy-message"&&x.ReadAt==124)&&await db.Messages.CountAsync()>1,"suppression preserves saved inboxes and read state");
 var bindingId=await PrepareOne();await Change(async db=>(await db.PushDevices.SingleAsync(x=>x.Id=="a")).Auth="changed");
 Check(await queue.ClaimAsync(bindingId)==null&&(await Row(bindingId)).Reason=="BindingChanged","subscription replacement suppresses pinned original binding");
 await Change(async db=>(await db.PushDevices.SingleAsync(x=>x.Id=="a")).Auth="PRIVATE-AUTH");
 var revokedId=await PrepareOne();await Change(db=>{db.RevokedAuthSessions.Add(new(){TokenHash="member-session",UserId="member",RevokedAt="2026-10-14T08:15:00Z"});return Task.CompletedTask;});
 Check(await queue.ClaimAsync(revokedId)==null&&(await Row(revokedId)).Reason=="BindingInvalid","revoked current session blocks pending claim");
 await Change(async db=>db.RevokedAuthSessions.Remove(await db.RevokedAuthSessions.SingleAsync()));
 var hiddenId=await PrepareOne();await Change(async db=>{await using var tx=await db.Database.BeginTransactionAsync();await db.Phases.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.IsActive,false));await db.Phases.Where(x=>x.Id=="hidden").ExecuteUpdateAsync(x=>x.SetProperty(p=>p.IsActive,true));await tx.CommitAsync();});
 Check(await queue.ClaimAsync(hiddenId)==null&&(await Row(hiddenId)).Reason=="ViewingDenied","current effective event viewing checked at claim");
 await Change(async db=>{await using var tx=await db.Database.BeginTransactionAsync();await db.Phases.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.IsActive,false));await db.Phases.Where(x=>x.Id=="view").ExecuteUpdateAsync(x=>x.SetProperty(p=>p.IsActive,true));await tx.CommitAsync();});
 var epochId=await PrepareOne();await Change(async db=>(await db.Set<ManualNotificationPushActivation>().SingleAsync()).Epoch++);
 Check(await queue.ClaimAsync(epochId)==null&&(await Row(epochId)).Reason=="ActivationChanged","activation generation checked at claim");
 var expiresId=await PrepareOne();var expiry=(await Row(expiresId)).ExpiresAt;clock.Now=DateTimeOffset.FromUnixTimeSeconds(expiry);
 Check(await queue.ClaimAsync(expiresId)==null&&(await Row(expiresId)).State=="Expired","exact deadline expires pending device row");
 var leaseCapId=await PrepareOne();var end=(await Row(leaseCapId)).ExpiresAt;clock.Now=DateTimeOffset.FromUnixTimeSeconds(end-5);var cap=await queue.ClaimAsync(leaseCapId);
 Check(cap?.LeaseUntil==end,"lease never exceeds accepted deadline");await Finish(cap!,ManualNotificationPushCompletion.RetryableNotAccepted);
 Check((await Row(leaseCapId)).State=="Expired","retry whose backoff exceeds deadline is not scheduled");
 var goneId=await PrepareOne();var gone=await queue.ClaimAsync(goneId);await Finish(gone!,ManualNotificationPushCompletion.SubscriptionGone);
 Check((await Row(goneId)).Reason=="SubscriptionGone","gone subscription outcome terminally suppresses original queue row");
 await using(var db=await factory.CreateDbContextAsync())Check(await db.PushDevices.CountAsync()==2,"queue-only outcome never deletes live subscription or credentials");
 // No valid devices gets one durable empty preparation, never later expansion.
 await Change(async db=>{await db.PushDevices.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Auth,""));});var empty=await Accept();Check((await queue.PrepareAsync(empty)).State=="NoEligibleDevices","invalid device bindings produce durable empty preparation");
 await Change(async db=>{await db.PushDevices.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Auth,"PRIVATE-AUTH"));});Check((await queue.PrepareAsync(empty)).Replayed&&(await Rows(empty)).Length==0,"later valid bindings do not resurrect empty preparation");
 // Prepare transaction fault: no partial ledger or per-device rows may survive.
 var fail=await Accept();await Change(db=>db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectQueue BEFORE INSERT ON ManualNotificationPushDeliveries WHEN NEW.DeviceId='b' BEGIN SELECT RAISE(ABORT, 'synthetic-device-failure'); END"));
 bool rolled=false;try{await queue.PrepareAsync(fail);}catch(DbUpdateException){rolled=true;}Check(rolled,"injected second-device insert failure propagates");
 await using(var db=await factory.CreateDbContextAsync())Check(!await db.Set<ManualNotificationPushPreparation>().AnyAsync(x=>x.RecipientId==fail)&&!await db.Set<ManualNotificationPushDelivery>().AnyAsync(x=>x.RecipientId==fail),"failed preparation rolls back ledger and every device row");
 await Change(db=>db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectQueue"));
 var preparations=await Task.WhenAll(Enumerable.Range(0,3).Select(_=>Task.Run(()=>queue.PrepareAsync(fail))));Check(preparations.Count(x=>!x.Replayed)==1&&(await Rows(fail)).Length==2,"concurrent preparation creates one snapshot and two replays");
 // Fresh connection factory reopens durable work, with no in-memory queue ownership.
 var reopened=new ManualNotificationPushQueue(new Factory(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source="+path).Options),clock);
 var reopenedId=(await Rows(fail))[0].Id;var reopenedClaim=await reopened.ClaimAsync(reopenedId);
 Check(reopenedClaim!=null&&await reopened.RecheckAsync(reopenedId,reopenedClaim.Token),"independent context factory recovers queue and owner lease");
 await Finish(reopenedClaim!,ManualNotificationPushCompletion.PermanentFailure);Check((await Row(reopenedId)).State=="Failed","permanent failure finishes without retry");
 var childId=(await Rows(fail))[1].Id;
 using(var child=new System.Diagnostics.Process()){
  child.StartInfo=new(Environment.ProcessPath!){RedirectStandardOutput=true,RedirectStandardError=true};
  foreach(var arg in new[]{"--recover",path,childId,clock.Now.ToUnixTimeSeconds().ToString()})child.StartInfo.ArgumentList.Add(arg);
  child.Start();var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync();
  Check(child.ExitCode==0&&(await output).Contains("Separate-process synthetic queue recovery PASS")&&(await Row(childId)).State=="Accepted","actual separate process recovers durable queue and completes simulated result");
  if(child.ExitCode!=0)throw new Exception(await error);
 }
 Check(!await queue.CompleteAsync(reopenedId,"bad",ManualNotificationPushCompletion.ProviderAccepted)&&!await queue.CompleteAsync(reopenedId,Guid.NewGuid().ToString("N"),(ManualNotificationPushCompletion)999),"malformed token and undefined result rejected");
 await using(var db=await factory.CreateDbContextAsync()){
  Check(await db.Set<ManualNotificationRecipient>().AllAsync(x=>x.PushState=="Pending")&&await db.Set<EventPushDelivery>().CountAsync()==0&&await db.Deliveries.CountAsync()==0,"new queue never consumes old recipient state or automatic/legacy queues");
  bool constrained=false;try{await db.Database.ExecuteSqlRawAsync("UPDATE ManualNotificationPushDeliveries SET Attempts=6");}catch(Microsoft.Data.Sqlite.SqliteException){constrained=true;}Check(constrained,"database rejects excess attempts");
  bool invalidClaim=false;try{await db.Database.ExecuteSqlRawAsync("UPDATE ManualNotificationPushDeliveries SET State='Claimed', Claim=NULL");}catch(Microsoft.Data.Sqlite.SqliteException){invalidClaim=true;}Check(invalidClaim,"database enforces claim-state consistency");
  await db.Database.OpenConnectionAsync();await using var cmd=db.Database.GetDbConnection().CreateCommand();cmd.CommandText="PRAGMA integrity_check";Check((string?)await cmd.ExecuteScalarAsync()=="ok","SQLite integrity ok");cmd.CommandText="PRAGMA foreign_key_check";await using var reader=await cmd.ExecuteReaderAsync();Check(!await reader.ReadAsync(),"foreign keys valid");
 }
 using(var stop=new CancellationTokenSource()){stop.Cancel();bool cancelled=false;try{await queue.PrepareAsync(fail,stop.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"cancellation propagates");}
 Console.WriteLine($"Manual notification Push queue: {passed} checks passed.");
}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(root,true);}catch{}}
sealed class Factory(DbContextOptions<ApplicationDbContext> options):IDbContextFactory<ApplicationDbContext>{public ApplicationDbContext CreateDbContext()=>new(options);}
sealed class Clock(DateTimeOffset now):TimeProvider{public DateTimeOffset Now=now;public override DateTimeOffset GetUtcNow()=>Now;}
