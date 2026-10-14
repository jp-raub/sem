using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;
var root=Path.Combine(Path.GetTempPath(),"sem-manual-composition-"+Guid.NewGuid());Directory.CreateDirectory(root);
var path=Path.Combine(root,"data.db");var clock=new Clock(DateTimeOffset.Parse("2026-10-14T09:00:00Z"));
var services=new ServiceCollection();services.AddLogging();services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+path),ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();services.AddSingleton<TimeProvider>(clock);DataProtectionServiceCollectionExtensions.AddDataProtection(services);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
var spy=new Spy();services.AddSingleton<IEventPushTransport>(spy);services.AddManualNotificationPushPipeline();services.AddManualNotificationPushPipeline();
using var provider=services.BuildServiceProvider(new ServiceProviderOptions{ValidateScopes=true});using var scope=provider.CreateScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();var queue=scope.ServiceProvider.GetRequiredService<ManualNotificationPushQueue>();int passed=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
async Task Change(Func<ApplicationDbContext,Task> action){await using var db=await factory.CreateDbContextAsync();await action(db);await db.SaveChangesAsync();}
ClaimsPrincipal Who()=>new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,"admin"),new("AspNet.Identity.SecurityStamp","admin-stamp"),new(PasskeyAdditionService.SessionClaim,"admin-session")],"Identity.Application"));
async Task<string> Accept(){var p=await core.PreviewEventManualNotificationAsync(Who(),"event","Engine subject","PRIVATE-BODY");var id=Guid.NewGuid().ToString("D");await core.AcceptEventManualNotificationAsync(Who(),"event",new(id,p.Fingerprint,p.Subject,p.Body,true));await using var db=await factory.CreateDbContextAsync();return await db.Set<ManualNotificationRecipient>().Where(x=>x.OperationId==id).Select(x=>x.Id).SingleAsync();}
async Task<ManualNotificationPushDelivery[]> Rows(string recipient){await using var db=await factory.CreateDbContextAsync();return await db.Set<ManualNotificationPushDelivery>().AsNoTracking().Where(x=>x.RecipientId==recipient).ToArrayAsync();}

async Task<bool> Prepared(string recipient){await using var db=await factory.CreateDbContextAsync();return await db.Set<ManualNotificationPushPreparation>().AnyAsync(x=>x.RecipientId==recipient);}
async Task Until(Func<Task<bool>> predicate){for(var n=0;n<140;n++){if(await predicate())return;await Task.Delay(100);}throw new Exception("Synthetic worker condition timed out");}
try{
 await using(var db=await factory.CreateDbContextAsync()){
  await SqliteStorageInitializer.InitializeAsync(db);foreach(var uid in new[]{"admin","member"})db.Users.Add(new(){Id=uid,UserName=uid,Email=uid+"@PRIVATE.invalid",DisplayName=uid,Enabled=true,EmailConfirmed=true,SecurityStamp=uid+"-stamp",IsAdmin=uid=="admin"});var e=new SessionEvent{Id="event",Title="Event",ScheduledDate="2099-01-01T00:00:00Z",CreatedAt="2026-10-01T00:00:00Z",ManualPhaseOverride=true};e.Phases.Add(new(){Id="view",EventId="event",Kind="View",Name="View",IsActive=true});e.Phases.Add(new(){Id="hidden",EventId="event",Kind="Hidden",Name="Hidden"});db.SessionEvents.Add(e);db.EventSpectators.Add(new(){EventId="event",UserId="member",RegisteredBy="admin",RegisteredAt="2026-10-01T00:00:00Z"});db.PushDevices.Add(new(){Id="device",UserId="member",SecurityStamp="member-stamp",SessionHash="member-session",Endpoint="https://fcm.googleapis.com/PRIVATE",P256dh="PRIVATE-P256DH",Auth="PRIVATE-AUTH"});await db.SaveChangesAsync();
 }

 var workers=provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<ManualNotificationPushWorker>().ToArray();
 Check(workers.Length==1,"repeated actual composition registers exactly one hosted manual worker");
 Check(provider.GetRequiredService<ManualPushRuntime>().DeliveryConnected,"runtime status reports infrastructure registration, not activation or delivery");
 Check(ReferenceEquals(scope.ServiceProvider.GetRequiredService<TimeProvider>(),clock),"composition preserves supplied clock instead of overwriting it");
 Check(ReferenceEquals(queue,scope.ServiceProvider.GetRequiredService<ManualNotificationPushQueue>()),"queue lifetime is scoped");
 using(var other=provider.CreateScope())Check(!ReferenceEquals(queue,other.ServiceProvider.GetRequiredService<ManualNotificationPushQueue>()),"each worker pass resolves an independent scoped queue");
 _=scope.ServiceProvider.GetRequiredService<ManualNotificationPushEngine>();Check(true,"actual scoped engine and dispatcher graph resolves with scope validation");
 var worker=workers.Single();var old=await Accept();
 await worker.StartAsync(CancellationToken.None);await Task.Delay(3400);
 await using(var db=await factory.CreateDbContextAsync()){
  Check(!await db.Set<ManualNotificationPushActivation>().AnyAsync(),"worker startup never inserts or enables activation");
  Check(!await db.Set<ManualNotificationPushPreparation>().AnyAsync()&&spy.Calls==0,"default OFF worker performs no preparation or external call");
 }
 await core.SetManualPushActivationAsync(Who(),new(Guid.NewGuid().ToString("D"),true,0,true));
 var good=await Accept();await Until(async()=> (await Rows(good)).Any(x=>x.State=="Accepted"));
 Check(spy.Calls==1&&(await Rows(good)).Single().Attempts==1,"actual registered worker dispatches eligible new acceptance exactly once");
 Check((await Rows(old)).Length==0,"ON never backfills acceptance made while OFF");
 Check(spy.Payloads.All(x=>x.Body=="新しいメッセージがあります"&&x.Url=="/?messages=1"&&!x.Body.Contains("PRIVATE")),"actual composition transports fixed generic body and inbox route, not private multiline text");
 spy.Available=false;var noKeys=await Accept();await Until(async()=> (await Rows(noKeys)).Length==1);
 Check((await Rows(noKeys)).Single().Attempts==0&&spy.Calls==1,"unconfigured transport prepares durably but does not claim or send");
 clock.Now=clock.Now.AddSeconds(600);await Until(async()=> (await Rows(noKeys)).Single().State=="Expired");
 Check(spy.Calls==1,"unconfigured queued work expires at original ten-minute deadline without sending");
 spy.Available=true;spy.Fail=true;var unknown=await Accept();await Until(async()=> (await Rows(unknown)).Any(x=>x.State=="Unknown"));var before=spy.Calls;
 await Task.Delay(3400);Check(spy.Calls==before&&(await Rows(unknown)).Single().Attempts==1,"registered background loop never auto-retries network AcceptanceUnknown");spy.Fail=false;
 spy.Available=false;var stopped=await Accept();await Until(async()=> (await Rows(stopped)).Length==1);
 await core.SetManualPushActivationAsync(Who(),new(Guid.NewGuid().ToString("D"),false,1,true));spy.Available=true;
 await Until(async()=> (await Rows(stopped)).Single().State is "Suppressed" or "Expired");
 Check(spy.Calls==before,"OFF after preparation suppresses original queued work before external I/O");
 await core.SetManualPushActivationAsync(Who(),new(Guid.NewGuid().ToString("D"),true,2,true));await Task.Delay(3400);
 Check(spy.Calls==before,"new activation epoch does not resurrect previously stopped work");
 var removed=await Accept();spy.BeforeConfigured=()=>Change(async db=>db.EventSpectators.Remove(await db.EventSpectators.SingleAsync())).GetAwaiter().GetResult();
 await Until(async()=> (await Rows(removed)).Any(x=>x.State=="Suppressed"));Check(spy.Calls==before,"current registration removed after preparation is rechecked before worker I/O");
 await Change(db=>{db.EventSpectators.Add(new(){EventId="event",UserId="member",RegisteredBy="admin",RegisteredAt="2026-10-14T09:00:00Z"});return Task.CompletedTask;});
 using(var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5)))await worker.StopAsync(stop.Token);
 var afterStop=await Accept();await Task.Delay(3400);Check(spy.Calls==before&&!await Prepared(afterStop),"stopped hosted worker leaves no subsequent background scan or I/O");
 // Restart through the SAME extension with a fresh service provider, not a manually constructed engine.
 using(var restarted=services.BuildServiceProvider(new ServiceProviderOptions{ValidateScopes=true})){
  var restartWorker=restarted.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<ManualNotificationPushWorker>().Single();
  await restartWorker.StartAsync(CancellationToken.None);await Until(async()=> (await Rows(afterStop)).Any(x=>x.State=="Accepted"));
  Check(spy.Calls==before+1,"fresh DI provider/worker restart resumes unexpired current-epoch pending acceptance");
  Check((await Rows(old)).Length==0&&(await Rows(unknown)).Single().State=="Unknown","restart neither backfills old OFF acceptance nor revives Unknown");
  using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));await restartWorker.StopAsync(stop.Token);
 }
 await core.SetManualPushActivationAsync(Who(),new(Guid.NewGuid().ToString("D"),false,3,true));
 await using(var db=await factory.CreateDbContextAsync()){
  Check(!(await db.Set<ManualNotificationPushActivation>().SingleAsync()).Enabled,"synthetic final activation returned OFF explicitly");
  Check(await db.Messages.CountAsync()==await db.Set<ManualNotificationOperation>().CountAsync()&&await db.Messages.AllAsync(x=>x.Body=="PRIVATE-BODY"&&x.ReadAt==null),"composed pipeline preserves inbox content and unread state");
  Check(await db.Set<EventPushDelivery>().CountAsync()==0&&await db.Deliveries.CountAsync()==0,"manual worker does not populate legacy/automatic queues");
  Check(!db.Database.HasPendingModelChanges(),"composition requires no migration/model change");await db.Database.OpenConnectionAsync();await using var cmd=db.Database.GetDbConnection().CreateCommand();cmd.CommandText="PRAGMA integrity_check";Check((string?)await cmd.ExecuteScalarAsync()=="ok","SQLite integrity ok");cmd.CommandText="PRAGMA foreign_key_check";await using var reader=await cmd.ExecuteReaderAsync();Check(!await reader.ReadAsync(),"foreign keys valid");
 }
 Console.WriteLine($"Manual Push composition: {passed} checks passed.");

}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(root,true);}catch{}}
sealed class Clock(DateTimeOffset now):TimeProvider{private DateTimeOffset value=now;private readonly object gate=new();public DateTimeOffset Now{get{lock(gate)return value;}set{lock(gate)this.value=value;}}public override DateTimeOffset GetUtcNow()=>Now;}
sealed class Spy:IEventPushTransport{
 public volatile bool Available=true,Fail;private int calls;public int Calls=>Volatile.Read(ref calls);public Action? BeforeConfigured;public System.Collections.Concurrent.ConcurrentBag<EventPushPayload> Payloads=new();
 public bool Configured{get{Interlocked.Exchange(ref BeforeConfigured,null)?.Invoke();return Available;}}
 public Task Send(PushDevice d,EventPushPayload p,int ttl,CancellationToken ct){ct.ThrowIfCancellationRequested();Interlocked.Increment(ref calls);Payloads.Add(p);return Fail?Task.FromException(new HttpRequestException("synthetic response lost")):Task.CompletedTask;}
}
