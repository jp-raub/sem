using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;
var root=Path.Combine(Path.GetTempPath(),"sem-manual-engine-"+Guid.NewGuid());Directory.CreateDirectory(root);
var path=Path.Combine(root,"data.db");var clock=new Clock(DateTimeOffset.Parse("2026-10-14T09:00:00Z"));
var services=new ServiceCollection();services.AddLogging();services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+path),ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();services.AddSingleton<TimeProvider>(clock);DataProtectionServiceCollectionExtensions.AddDataProtection(services);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();var queue=new ManualNotificationPushQueue(factory,clock);var spy=new Spy();var dispatcher=new ManualNotificationPushDispatcher(factory,queue,spy,clock);var engine=new ManualNotificationPushEngine(factory,queue,dispatcher,clock);int passed=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
async Task Change(Func<ApplicationDbContext,Task> action){await using var db=await factory.CreateDbContextAsync();await action(db);await db.SaveChangesAsync();}
ClaimsPrincipal Who()=>new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,"admin"),new("AspNet.Identity.SecurityStamp","admin-stamp"),new(PasskeyAdditionService.SessionClaim,"admin-session")],"Identity.Application"));
async Task<string> Accept(){var p=await core.PreviewEventManualNotificationAsync(Who(),"event","Engine subject","PRIVATE-BODY");var id=Guid.NewGuid().ToString("D");await core.AcceptEventManualNotificationAsync(Who(),"event",new(id,p.Fingerprint,p.Subject,p.Body,true));await using var db=await factory.CreateDbContextAsync();return await db.Set<ManualNotificationRecipient>().Where(x=>x.OperationId==id).Select(x=>x.Id).SingleAsync();}
async Task<ManualNotificationPushDelivery[]> Rows(string recipient){await using var db=await factory.CreateDbContextAsync();return await db.Set<ManualNotificationPushDelivery>().AsNoTracking().Where(x=>x.RecipientId==recipient).ToArrayAsync();}
try{
 await using(var db=await factory.CreateDbContextAsync()){
  await SqliteStorageInitializer.InitializeAsync(db);foreach(var uid in new[]{"admin","member"})db.Users.Add(new(){Id=uid,UserName=uid,Email=uid+"@PRIVATE.invalid",DisplayName=uid,Enabled=true,EmailConfirmed=true,SecurityStamp=uid+"-stamp",IsAdmin=uid=="admin"});var e=new SessionEvent{Id="event",Title="Event",ScheduledDate="2099-01-01T00:00:00Z",CreatedAt="2026-10-01T00:00:00Z",ManualPhaseOverride=true};e.Phases.Add(new(){Id="view",EventId="event",Kind="View",Name="View",IsActive=true});e.Phases.Add(new(){Id="hidden",EventId="event",Kind="Hidden",Name="Hidden"});db.SessionEvents.Add(e);db.EventSpectators.Add(new(){EventId="event",UserId="member",RegisteredBy="admin",RegisteredAt="2026-10-01T00:00:00Z"});db.PushDevices.Add(new(){Id="device",UserId="member",SecurityStamp="member-stamp",SessionHash="member-session",Endpoint="https://fcm.googleapis.com/PRIVATE",P256dh="PRIVATE-P256DH",Auth="PRIVATE-AUTH"});await db.SaveChangesAsync();
 }
 var old=await Accept();var off=await engine.RunOnceAsync();Check(off.PreparedRecipients==0&&off.ProcessedDeliveries==0&&spy.Calls==0,"default OFF scans no accepted audience or external work");
 await core.SetManualPushActivationAsync(Who(),new(Guid.NewGuid().ToString("D"),true,0,true));
 for(var n=0;n<25;n++)await Accept();
 var first=await engine.RunOnceAsync();Check(first.PreparedRecipients==20&&first.ProcessedDeliveries==20&&first.FailedItems==0&&spy.Calls==20,"one batch bounds both preparation and dispatch to twenty");
 var second=await engine.RunOnceAsync();Check(second.PreparedRecipients==5&&second.ProcessedDeliveries==5&&spy.Calls==25,"next batch progresses beyond first twenty without cursor duplication");
 var third=await engine.RunOnceAsync();Check(third.PreparedRecipients==0&&third.ProcessedDeliveries==0&&spy.Calls==25,"idle batch never resends completed work");
 await using(var db=await factory.CreateDbContextAsync())Check(!await db.Set<ManualNotificationPushPreparation>().AnyAsync(x=>x.RecipientId==old),"pre-activation request remains excluded without backfill");
 spy.Available=false;var unavailable=new List<string>();for(var n=0;n<25;n++)unavailable.Add(await Accept());
 var absent=await engine.RunOnceAsync();Check(absent.PreparedRecipients==20&&absent.ProcessedDeliveries==20&&spy.Calls==25,"unconfigured transport still permits bounded durable preparation without invoking");await engine.RunOnceAsync();
 await using(var db=await factory.CreateDbContextAsync())Check(await db.Set<ManualNotificationPushDelivery>().CountAsync(x=>x.State=="Pending"&&x.Attempts==0)==25,"unconfigured transport neither claims nor increments attempts");
 clock.Now=clock.Now.AddSeconds(600);var sweep=await engine.RunOnceAsync();Check(sweep.SweptDeliveries==20&&sweep.ProcessedDeliveries==0,"expiry housekeeping is bounded and works without configured transport");
 Check((await engine.RunOnceAsync()).SweptDeliveries==5,"subsequent housekeeping settles remaining expired rows");
 await using(var db=await factory.CreateDbContextAsync())Check(!await db.Set<ManualNotificationPushDelivery>().AnyAsync(x=>x.State=="Pending"),"all expired pending work becomes terminal without send");
 var missed=await Accept();clock.Now=clock.Now.AddSeconds(600);Check((await engine.RunOnceAsync()).PreparedRecipients==0&&(await Rows(missed)).Length==0,"already expired admission is never staged for late dispatch");
 spy.Available=true;await Change(async db=>(await db.PushDevices.SingleAsync()).Auth="");var empty=await Accept();Check((await engine.RunOnceAsync()).PreparedRecipients==1&&(await Rows(empty)).Length==0,"no-valid-device outcome consumes preparation once without sending");
 await Change(async db=>(await db.PushDevices.SingleAsync()).Auth="PRIVATE-AUTH");Check((await engine.RunOnceAsync()).PreparedRecipients==0&&(await Rows(empty)).Length==0,"later binding validity does not revive empty preparation");
 // One preparation failure must not stop unrelated recipients in the same pass.
 var ids=new List<string>();for(var n=0;n<3;n++)ids.Add(await Accept());var target=ids[1];
 await Change(db=>db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectOneRecipient BEFORE INSERT ON ManualNotificationPushDeliveries WHEN NEW.RecipientId='"+target+"' BEGIN SELECT RAISE(ABORT,'synthetic-one-recipient'); END"));
 var partial=await engine.RunOnceAsync();Check(partial.FailedItems==1&&partial.PreparedRecipients==2&&partial.ProcessedDeliveries==2,"one failed preparation does not abandon two independent recipients");
 await Change(db=>db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectOneRecipient"));var recovered=await engine.RunOnceAsync();Check(recovered.PreparedRecipients==1&&recovered.ProcessedDeliveries==1&&(await Rows(target)).Length==1,"rolled-back recipient recovers on next pass without duplicating others");
 // Crash leases are fenced even when transport is disabled again.
 spy.Available=false;var crash=await Accept();await queue.PrepareAsync(crash);var crashId=(await Rows(crash)).Single().Id;var claim=await queue.ClaimAsync(crashId);clock.Now=clock.Now.AddSeconds(30);
 var crashSweep=await engine.RunOnceAsync();Check(crashSweep.SweptDeliveries==1&&(await Rows(crash)).Single().State=="Unknown","elapsed claimed lease becomes Unknown independent of transport configuration");
 spy.Available=true;var beforeCalls=spy.Calls;await engine.RunOnceAsync();Check(spy.Calls==beforeCalls,"scanner never automatically redispatches Unknown");
 // Current authority is still validated in the dispatcher, not trusted from the scan.
 var removed=await Accept();spy.BeforeConfigured=()=>Change(async db=>db.EventSpectators.Remove(await db.EventSpectators.SingleAsync())).GetAwaiter().GetResult();
 var changed=await engine.RunOnceAsync();Check(changed.PreparedRecipients==1&&changed.ProcessedDeliveries==1&&(await Rows(removed)).Single().State=="Suppressed"&&spy.Calls==beforeCalls,"registration removed after scanning/preparation is rechecked before I/O");spy.BeforeConfigured=null;
 await Change(db=>{db.EventSpectators.Add(new(){EventId="event",UserId="member",RegisteredBy="admin",RegisteredAt="2026-10-14T09:00:00Z"});return Task.CompletedTask;});
 await engine.RunOnceAsync();Check(spy.Calls==beforeCalls,"rejoin does not revive already suppressed scan result");
 // Overlapping full passes may observe the same candidates; queue ownership still fences I/O.
 var concurrentA=await Accept();var concurrentB=await Accept();var parallelBefore=spy.Calls;
 var overlaps=await Task.WhenAll(Enumerable.Range(0,3).Select(_=>Task.Run(()=>engine.RunOnceAsync())));
 Check(spy.Calls-parallelBefore==2&&(await Rows(concurrentA)).Length==1&&(await Rows(concurrentB)).Length==1,"overlapping complete batches never duplicate devices or external calls");
 Check(overlaps.All(x=>x.FailedItems==0),"concurrent scans tolerate replay/claim races without abandoning independent work");
 // Explicit TEST composition of the actual BackgroundService. Production Program never registers it.
 var workerId=await Accept();var workerBefore=spy.Calls;
 var workerServices=new ServiceCollection();workerServices.AddScoped<ManualNotificationPushEngine>(_=>engine);
 using(var workerProvider=workerServices.BuildServiceProvider())
 using(var worker=new ManualNotificationPushWorker(workerProvider.GetRequiredService<IServiceScopeFactory>(),Microsoft.Extensions.Logging.Abstractions.NullLogger<ManualNotificationPushWorker>.Instance)){
  await worker.StartAsync(CancellationToken.None);
  for(var n=0;n<80&&spy.Calls==workerBefore;n++)await Task.Delay(100);
  Check(spy.Calls==workerBefore+1&&(await Rows(workerId)).Single().State=="Accepted","explicitly test-composed real worker processes a bounded synthetic pass");
  using var stopWorker=new CancellationTokenSource(TimeSpan.FromSeconds(5));await worker.StopAsync(stopWorker.Token);
  var afterStop=spy.Calls;await Task.Delay(200);Check(spy.Calls==afterStop,"worker cancellation stops timer and leaves no further background I/O");
 }
 using(var stop=new CancellationTokenSource()){stop.Cancel();bool cancelled=false;try{await engine.RunOnceAsync(stop.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"batch cancellation propagates before housekeeping or I/O");}
 await using(var db=await factory.CreateDbContextAsync()){
  Check(await db.Messages.CountAsync()==await db.Set<ManualNotificationOperation>().CountAsync()&&await db.Messages.AllAsync(x=>x.Body=="PRIVATE-BODY"&&x.ReadAt==null),"orchestration never duplicates or changes inbox content/read state");
  Check(await db.Set<EventPushDelivery>().CountAsync()==0&&await db.Deliveries.CountAsync()==0,"manual orchestration leaves legacy and automatic queues untouched");
  Check(!db.Database.HasPendingModelChanges(),"engine adds no migration/model difference");await db.Database.OpenConnectionAsync();await using var cmd=db.Database.GetDbConnection().CreateCommand();cmd.CommandText="PRAGMA integrity_check";Check((string?)await cmd.ExecuteScalarAsync()=="ok","SQLite integrity ok");cmd.CommandText="PRAGMA foreign_key_check";await using var reader=await cmd.ExecuteReaderAsync();Check(!await reader.ReadAsync(),"foreign keys valid");
 }
 Console.WriteLine($"Manual notification Push engine: {passed} checks passed.");
}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(root,true);}catch{}}
sealed class Clock(DateTimeOffset now):TimeProvider{public DateTimeOffset Now=now;public override DateTimeOffset GetUtcNow()=>Now;}
sealed class Spy:IEventPushTransport{
 public bool Available=true;public int Calls;public Action? BeforeConfigured;
 public bool Configured{get{var action=BeforeConfigured;BeforeConfigured=null;action?.Invoke();return Available;}}
 public Task Send(PushDevice d,EventPushPayload p,int ttl,CancellationToken ct){ct.ThrowIfCancellationRequested();Calls++;return Task.CompletedTask;}
}
