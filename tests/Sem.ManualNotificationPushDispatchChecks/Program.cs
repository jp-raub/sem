using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;
using WebPush;
var root=Path.Combine(Path.GetTempPath(),"sem-manual-dispatch-"+Guid.NewGuid());Directory.CreateDirectory(root);
var path=Path.Combine(root,"data.db");var clock=new Clock(DateTimeOffset.Parse("2026-10-14T08:45:00Z"));
var services=new ServiceCollection();services.AddLogging();services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+path),ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();services.AddSingleton<TimeProvider>(clock);DataProtectionServiceCollectionExtensions.AddDataProtection(services);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();var queue=new ManualNotificationPushQueue(factory,clock);var spy=new Spy();var dispatcher=new ManualNotificationPushDispatcher(factory,queue,spy,clock);int passed=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
async Task Change(Func<ApplicationDbContext,Task> action){await using var db=await factory.CreateDbContextAsync();await action(db);await db.SaveChangesAsync();}
ClaimsPrincipal Who()=>new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,"admin"),new("AspNet.Identity.SecurityStamp","admin-stamp"),new(PasskeyAdditionService.SessionClaim,"admin-session")],"Identity.Application"));
async Task<string> NewJob(){spy.Reset();var p=await core.PreviewEventManualNotificationAsync(Who(),"event","Subject <img src=x>","PRIVATE-MULTILINE-BODY");var id=Guid.NewGuid().ToString("D");await core.AcceptEventManualNotificationAsync(Who(),"event",new(id,p.Fingerprint,p.Subject,p.Body,true));await using var db=await factory.CreateDbContextAsync();var r=await db.Set<ManualNotificationRecipient>().Where(x=>x.OperationId==id).Select(x=>x.Id).SingleAsync();await queue.PrepareAsync(r);return await db.Set<ManualNotificationPushDelivery>().Where(x=>x.RecipientId==r).Select(x=>x.Id).SingleAsync();}
async Task<ManualNotificationPushDelivery> Row(string id){await using var db=await factory.CreateDbContextAsync();return await db.Set<ManualNotificationPushDelivery>().AsNoTracking().SingleAsync(x=>x.Id==id);}
async Task Device(){await Change(async db=>{var d=await db.PushDevices.SingleOrDefaultAsync();if(d==null)db.PushDevices.Add(new(){Id="device",UserId="member",SecurityStamp="member-stamp",SessionHash="member-session",Endpoint="https://fcm.googleapis.com/PRIVATE",P256dh="PRIVATE-P256DH",Auth="PRIVATE-AUTH"});else{d.Auth="PRIVATE-AUTH";d.SecurityStamp="member-stamp";d.SessionHash="member-session";}});}
try{
 await using(var db=await factory.CreateDbContextAsync()){
  await SqliteStorageInitializer.InitializeAsync(db);foreach(var userId in new[]{"admin","member"})db.Users.Add(new(){Id=userId,UserName=userId,Email=userId+"@PRIVATE.invalid",DisplayName=userId,Enabled=true,EmailConfirmed=true,SecurityStamp=userId+"-stamp",IsAdmin=userId=="admin"});var e=new SessionEvent{Id="event",Title="Event",ScheduledDate="2099-01-01T00:00:00Z",CreatedAt="2026-10-01T00:00:00Z",ManualPhaseOverride=true};e.Phases.Add(new(){Id="view",EventId="event",Kind="View",Name="View",IsActive=true});e.Phases.Add(new(){Id="hidden",EventId="event",Kind="Hidden",Name="Hidden"});db.SessionEvents.Add(e);db.EventSpectators.Add(new(){EventId="event",UserId="member",RegisteredBy="admin",RegisteredAt="2026-10-01T00:00:00Z"});await db.SaveChangesAsync();
 }
 await Device();await core.SetManualPushActivationAsync(Who(),new(Guid.NewGuid().ToString("D"),true,0,true));
 var id=await NewJob();spy.Available=false;Check((await dispatcher.DispatchOneAsync(id)).State=="TransportUnavailable"&&spy.Calls==0&&(await Row(id)).Attempts==0,"unconfigured transport does not claim or invoke anything");spy.Available=true;
 var result=await dispatcher.DispatchOneAsync(id);Check(result.State=="ProviderAccepted"&&spy.Calls==1&&(await Row(id)).State=="Accepted","synthetic successful transport records one provider acceptance");
 Check(spy.Payload?.Title=="Subject <img src=x>"&&spy.Payload.Body=="新しいメッセージがあります"&&!spy.Payload.Body.Contains("PRIVATE"),"Push uses reviewed subject and generic body, never saved multiline body");
 Check(spy.Payload?.Url=="/?messages=1"&&spy.Payload.Id.Length==64&&spy.Payload.ExpiresAt==(await Row(id)).ExpiresAt*1000,"payload uses authenticated inbox URL stable recipient tag and millisecond expiry");
 Check(spy.Ttl is >0 and <=600,"TTL is positive and bounded by approved remaining lifetime");
 Check((await dispatcher.DispatchOneAsync(id)).State=="NotClaimed"&&spy.Calls==1,"repeated dispatch cannot resend provider-accepted job");
 var parallel=await NewJob();spy.Delay=40;var concurrent=await Task.WhenAll(Enumerable.Range(0,3).Select(_=>Task.Run(()=>dispatcher.DispatchOneAsync(parallel))));Check(concurrent.Count(x=>x.State=="ProviderAccepted")==1&&spy.Calls==1,"three concurrent dispatchers make one external invocation");
 foreach(var status in new[]{400,401,403,413}){var job=await NewJob();spy.Status=status;var r=await dispatcher.DispatchOneAsync(job);Check(r.State=="PermanentFailure"&&(await Row(job)).State=="Failed"&&spy.Calls==1,"explicit permanent provider response "+status+" is terminal");}
 foreach(var status in new[]{404,410}){await Device();var job=await NewJob();spy.Status=status;Check((await dispatcher.DispatchOneAsync(job)).State=="SubscriptionGone"&&(await Row(job)).State=="Suppressed","gone provider response "+status+" terminally suppresses");await using var db=await factory.CreateDbContextAsync();Check(!await db.PushDevices.AnyAsync(),"unchanged gone subscription removed for "+status);}
 await Device();var replacement=await NewJob();spy.BeforeSend=()=>Change(async db=>(await db.PushDevices.SingleAsync()).Auth="REPLACED-AUTH");spy.Status=410;await dispatcher.DispatchOneAsync(replacement);await using(var db=await factory.CreateDbContextAsync())Check(await db.PushDevices.AnyAsync(x=>x.Auth=="REPLACED-AUTH"),"in-flight gone response cannot delete replacement subscription");await Device();
 foreach(var status in new[]{408,500,503}){var job=await NewJob();spy.Status=status;Check((await dispatcher.DispatchOneAsync(job)).State=="AcceptanceUnknown"&&(await Row(job)).State=="Unknown","ambiguous provider response "+status+" cannot prove nonacceptance");Check((await dispatcher.DispatchOneAsync(job)).State=="NotClaimed"&&spy.Calls==1,"unknown "+status+" never automatically retries");}
 // Explicit429 is confirmed nonacceptance; use provider wait, local backoff and original lifetime together.
 var rate=await NewJob();spy.Status=429;spy.RetryAfter="90";var rateAt=clock.Now.ToUnixTimeSeconds();
 Check((await dispatcher.DispatchOneAsync(rate)).State=="RateLimited"&&(await Row(rate)).State=="Pending"&&(await Row(rate)).DueAt==rateAt+90&&(await Row(rate)).Attempts==1,"429 delta-seconds persists provider minimum wait without claiming acceptance");
 Check((await dispatcher.DispatchOneAsync(rate)).State=="NotClaimed"&&spy.Calls==1,"429 cannot retry before Retry-After");clock.Now=clock.Now.AddSeconds(90);spy.Status=0;
 Check((await dispatcher.DispatchOneAsync(rate)).State=="ProviderAccepted"&&spy.Calls==2&&(await Row(rate)).Attempts==2,"429 retries once due using same durable delivery and fresh claim");
 var dateRate=await NewJob();spy.Status=429;spy.RetryAfter=clock.Now.AddSeconds(77).ToString("r");await dispatcher.DispatchOneAsync(dateRate);
 Check((await Row(dateRate)).DueAt==clock.Now.ToUnixTimeSeconds()+77,"429 HTTP-date enforces absolute Retry-After");
 foreach(var header in new string?[]{null,"invalid-private-header","-5","0","Mon, 01 Jan 1900 00:00:00 GMT",clock.Now.AddSeconds(-60).ToString("r"),"1, 2"}){var j=await NewJob();spy.Status=429;spy.RetryAfter=header;await dispatcher.DispatchOneAsync(j);Check((await Row(j)).State=="Pending"&&(await Row(j)).DueAt==clock.Now.ToUnixTimeSeconds()+15,"missing invalid past or short429 header uses safe local backoff");}
 var farRate=await NewJob();spy.Status=429;spy.RetryAfter="86400";await dispatcher.DispatchOneAsync(farRate);Check((await Row(farRate)).State=="Expired"&&(await Row(farRate)).Reason=="RetryPastDeadline","provider wait beyond original acceptance deadline expires rather than truncating wait");
 var boundaryRate=await NewJob();spy.Status=429;spy.RetryAfter="600";await dispatcher.DispatchOneAsync(boundaryRate);Check((await Row(boundaryRate)).State=="Expired","Retry-After exactly at original deadline cannot be retried");
 var fractional=await NewJob();clock.Now=clock.Now.AddMilliseconds(500);spy.Status=429;spy.RetryAfter="20";await dispatcher.DispatchOneAsync(fractional);Check((await Row(fractional)).DueAt==clock.Now.ToUnixTimeSeconds()+21,"fractional response clock rounds provider delay up, never retries early");clock.Now=clock.Now.AddMilliseconds(-500);
 var limit=await NewJob();spy.Status=429;spy.RetryAfter="0";for(var n=0;n<5;n++){await dispatcher.DispatchOneAsync(limit);var row=await Row(limit);if(row.State=="Pending")clock.Now=DateTimeOffset.FromUnixTimeSeconds(row.DueAt);}
 Check((await Row(limit)).State=="Failed"&&(await Row(limit)).Attempts==5&&spy.Calls==5,"repeated429 stops at five attempts without deadline extension");
 var revokedRate=await NewJob();spy.Status=429;spy.RetryAfter="20";await dispatcher.DispatchOneAsync(revokedRate);clock.Now=DateTimeOffset.FromUnixTimeSeconds((await Row(revokedRate)).DueAt);await Change(async db=>db.EventSpectators.Remove(await db.EventSpectators.SingleAsync()));
 Check((await dispatcher.DispatchOneAsync(revokedRate)).State=="NotClaimed"&&spy.Calls==1&&(await Row(revokedRate)).State=="Suppressed","429 retry rechecks current registration before any second I/O");
 await Change(db=>{db.EventSpectators.Add(new(){EventId="event",UserId="member",RegisteredBy="admin",RegisteredAt="2026-10-14T08:45:00Z"});return Task.CompletedTask;});
 var concurrentRate=await NewJob();spy.Status=429;spy.RetryAfter="0";await dispatcher.DispatchOneAsync(concurrentRate);clock.Now=DateTimeOffset.FromUnixTimeSeconds((await Row(concurrentRate)).DueAt);spy.Status=0;spy.Delay=40;var rateRace=await Task.WhenAll(Enumerable.Range(0,3).Select(_=>Task.Run(()=>dispatcher.DispatchOneAsync(concurrentRate))));
 Check(rateRace.Count(x=>x.State=="ProviderAccepted")==1&&spy.Calls==2,"concurrent due429 retries make only one additional provider invocation");
 var network=await NewJob();spy.NetworkFailure=true;Check((await dispatcher.DispatchOneAsync(network)).State=="AcceptanceUnknown"&&(await Row(network)).State=="Unknown","network exception after invocation becomes unknown");
 var secretFailure=await NewJob();spy.OtherFailure=true;await dispatcher.DispatchOneAsync(secretFailure);Check(!(await Row(secretFailure)).Reason.Contains("SECRET"),"raw exception content is never persisted as delivery reason");
 // Configured getter callback runs after the snapshot and before FINAL current-policy recheck.
 var removed=await NewJob();spy.BeforeFinal=()=>Change(async db=>db.EventSpectators.Remove(await db.EventSpectators.SingleAsync())).GetAwaiter().GetResult();
 Check((await dispatcher.DispatchOneAsync(removed)).State=="FinalGateRejected"&&spy.Calls==0&&(await Row(removed)).Reason=="NoLongerRegistered","registration loss after snapshot is caught by final gate before transport");
 await Change(db=>{db.EventSpectators.Add(new(){EventId="event",UserId="member",RegisteredBy="admin",RegisteredAt="2026-10-14T08:45:00Z"});return Task.CompletedTask;});spy.BeforeFinal=null;
 Check((await dispatcher.DispatchOneAsync(removed)).State=="NotClaimed"&&spy.Calls==0,"rejoin cannot revive final-gate suppressed delivery");
 var changed=await NewJob();spy.BeforeFinal=()=>Change(async db=>(await db.PushDevices.SingleAsync()).Auth="NEW").GetAwaiter().GetResult();Check((await dispatcher.DispatchOneAsync(changed)).State=="FinalGateRejected"&&spy.Calls==0,"binding replacement after snapshot is caught before transport");await Device();
 var disabled=await NewJob();spy.BeforeFinal=()=>Change(async db=>(await db.Set<ManualNotificationPushActivation>().SingleAsync()).Enabled=false).GetAwaiter().GetResult();Check((await dispatcher.DispatchOneAsync(disabled)).State=="FinalGateRejected"&&spy.Calls==0,"activation disabled after snapshot is caught before transport");await Change(async db=>(await db.Set<ManualNotificationPushActivation>().SingleAsync()).Enabled=true);
 var stamp=await NewJob();spy.BeforeFinal=()=>Change(async db=>(await db.Users.SingleAsync(x=>x.Id=="member")).SecurityStamp="RESET").GetAwaiter().GetResult();Check((await dispatcher.DispatchOneAsync(stamp)).State=="FinalGateRejected"&&spy.Calls==0,"stamp reset after snapshot blocks transport");await Change(async db=>(await db.Users.SingleAsync(x=>x.Id=="member")).SecurityStamp="member-stamp");
 var deferred=await NewJob();spy.DisableAtFinal=true;Check((await dispatcher.DispatchOneAsync(deferred)).State=="DeferredBeforeSend"&&spy.Calls==0&&(await Row(deferred)).State=="Pending","definite no-I/O transport unavailability may schedule bounded retry");
 var late=await NewJob();clock.Now=DateTimeOffset.FromUnixTimeSeconds((await Row(late)).ExpiresAt).AddMilliseconds(-500);Check((await dispatcher.DispatchOneAsync(late)).State=="TooLateToSend"&&spy.Calls==0&&(await Row(late)).State=="Expired","subsecond remaining lifetime cannot send zero-TTL Push");
 var timeout=await NewJob();clock.Now=DateTimeOffset.FromUnixTimeSeconds((await Row(timeout)).ExpiresAt).AddMilliseconds(-1100);spy.WaitForCancellation=true;Check((await dispatcher.DispatchOneAsync(timeout)).State=="AcceptanceUnknown"&&spy.Calls==1&&(await Row(timeout)).State=="Unknown","deadline-capped transport timeout is unknown without auto retry");
 var cancelled=await NewJob();using(var stop=new CancellationTokenSource()){spy.BeforeSend=()=>{stop.Cancel();return Task.CompletedTask;};bool thrown=false;try{await dispatcher.DispatchOneAsync(cancelled,stop.Token);}catch(OperationCanceledException){thrown=true;}Check(thrown&&(await Row(cancelled)).State=="Unknown","parent cancellation after I/O invocation persists unknown via independent cleanup token");}
 var beforeIo=await NewJob();using(var stop=new CancellationTokenSource()){spy.BeforeFinal=()=>stop.Cancel();bool thrown=false;try{await dispatcher.DispatchOneAsync(beforeIo,stop.Token);}catch(OperationCanceledException){thrown=true;}Check(thrown&&spy.Calls==0&&(await Row(beforeIo)).State=="Pending","cancellation before transport preserves definite-not-sent retry rather than claiming delivery");}
 var preCancelled=await NewJob();using(var stop=new CancellationTokenSource()){stop.Cancel();bool thrown=false;try{await dispatcher.DispatchOneAsync(preCancelled,stop.Token);}catch(OperationCanceledException){thrown=true;}Check(thrown&&(await Row(preCancelled)).Attempts==0,"pre-cancelled call never claims");}
 var expired=await NewJob();clock.Now=DateTimeOffset.FromUnixTimeSeconds((await Row(expired)).ExpiresAt);Check((await dispatcher.DispatchOneAsync(expired)).State=="NotClaimed"&&spy.Calls==0&&(await Row(expired)).State=="Expired","exact deadline prevents claim and transport");
 // Real production encryption adapter, injected in-memory HttpMessageHandler only.
 var vapid=VapidHelper.GenerateVapidKeys();var subscription=VapidHelper.GenerateVapidKeys();
 await Change(async db=>{var d=await db.PushDevices.SingleAsync();d.P256dh=subscription.PublicKey;d.Auth=Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+','-').Replace('/','_');});
 var encrypted=await NewJob();var handler=new Capture();
 using(var http=new HttpClient(handler)){
  var cfg=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Push:PublicKey",vapid.PublicKey},{"Push:PrivateKey",vapid.PrivateKey},{"Push:Subject","mailto:test@example.test"}}).Build();
  using var real=new EventWebPushTransport(http,cfg);var production=new ManualNotificationPushDispatcher(factory,queue,real,clock);
  Check((await production.DispatchOneAsync(encrypted)).State=="ProviderAccepted"&&handler.Calls==1,"production WebPush adapter executes against in-memory HTTP handler only");
  Check(handler.Ttl is >0 and <=600&&handler.Topic?.Length==32&&handler.Encrypted.Length>0&&!System.Text.Encoding.UTF8.GetString(handler.Encrypted).Contains("新しいメッセージ")&&!System.Text.Encoding.UTF8.GetString(handler.Encrypted).Contains("Subject"),"real encrypted request has bounded TTL and stable Topic without plaintext subject or body");
 }
 await using(var db=await factory.CreateDbContextAsync()){
  Check(await db.Messages.CountAsync()==await db.Set<ManualNotificationOperation>().CountAsync()&&await db.Messages.AllAsync(x=>x.Body=="PRIVATE-MULTILINE-BODY"&&x.ReadAt==null),"dispatch never adds duplicate inboxes or changes saved content/read state");
  Check(await db.Set<EventPushDelivery>().CountAsync()==0&&await db.Deliveries.CountAsync()==0,"manual dispatcher does not invoke or overload automatic and legacy queues");
  Check(!db.Database.HasPendingModelChanges(),"dispatcher adds no migration/model difference");await db.Database.OpenConnectionAsync();await using var cmd=db.Database.GetDbConnection().CreateCommand();cmd.CommandText="PRAGMA integrity_check";Check((string?)await cmd.ExecuteScalarAsync()=="ok","SQLite integrity ok");cmd.CommandText="PRAGMA foreign_key_check";await using var reader=await cmd.ExecuteReaderAsync();Check(!await reader.ReadAsync(),"foreign keys valid");
 }
 Console.WriteLine($"Manual notification Push dispatcher: {passed} checks passed.");
}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(root,true);}catch{}}
sealed class Clock(DateTimeOffset now):TimeProvider{public DateTimeOffset Now=now;public override DateTimeOffset GetUtcNow()=>Now;}
sealed class Spy:IEventPushTransport{
 public bool Available=true,DisableAtFinal,NetworkFailure,OtherFailure,WaitForCancellation;public int Status,Delay,Calls,ConfiguredReads;public int Ttl;public EventPushPayload? Payload;public string? RetryAfter;public Func<Task>? BeforeSend;public Action? BeforeFinal;
 public bool Configured{get{ConfiguredReads++;if(ConfiguredReads==2){BeforeFinal?.Invoke();if(DisableAtFinal)return false;}return Available;}}
 public void Reset(){Available=true;DisableAtFinal=NetworkFailure=OtherFailure=WaitForCancellation=false;Status=Delay=Calls=ConfiguredReads=Ttl=0;Payload=null;RetryAfter=null;BeforeSend=null;BeforeFinal=null;}
 public async Task Send(PushDevice d,EventPushPayload p,int ttl,CancellationToken ct){Interlocked.Increment(ref Calls);Payload=p;Ttl=ttl;if(BeforeSend!=null)await BeforeSend();ct.ThrowIfCancellationRequested();if(Delay>0)await Task.Delay(Delay,ct);if(WaitForCancellation)await Task.Delay(Timeout.Infinite,ct);if(Status>0){var response=new HttpResponseMessage((HttpStatusCode)Status);if(RetryAfter is not null)response.Headers.TryAddWithoutValidation("Retry-After",RetryAfter);throw new WebPushException("SECRET-provider-response",new PushSubscription(d.Endpoint,d.P256dh,d.Auth),response);}if(NetworkFailure)throw new HttpRequestException("SECRET-network");if(OtherFailure)throw new Exception("SECRET-private-error");}
}

sealed class Capture:HttpMessageHandler{
 public int Calls,Ttl;public string? Topic;public byte[] Encrypted=[];
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Calls++;Ttl=int.Parse(r.Headers.GetValues("TTL").Single());Topic=r.Headers.GetValues("Topic").Single();Encrypted=await r.Content!.ReadAsByteArrayAsync(ct);return new(HttpStatusCode.Created);}
}
