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
var root=Path.Combine(Path.GetTempPath(),"sem-manual-policy-"+Guid.NewGuid());Directory.CreateDirectory(root);
var path=Path.Combine(root,"data.db");var clock=new Clock(DateTimeOffset.Parse("2026-10-14T08:00:00Z"));
var services=new ServiceCollection();services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+path),ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddSingleton<TimeProvider>(clock);DataProtectionServiceCollectionExtensions.AddDataProtection(services);
services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();
var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();
var policy=new ManualNotificationPushPolicy(factory,clock);int passed=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
async Task Change(Func<ApplicationDbContext,Task> action){await using var db=await factory.CreateDbContextAsync();await action(db);await db.SaveChangesAsync();}
var actor=new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,"admin"),new("AspNet.Identity.SecurityStamp","admin-stamp"),new(PasskeyAdditionService.SessionClaim,"admin-session")],"Identity.Application"));
async Task<(string Id,ManualNotificationSubmit Submit)> Accept(){var p=await core.PreviewEventManualNotificationAsync(actor,"event","PRIVATE-SUBJECT","PRIVATE-BODY");var s=new ManualNotificationSubmit(Guid.NewGuid().ToString("D"),p.Fingerprint,p.Subject,p.Body,true);await core.AcceptEventManualNotificationAsync(actor,"event",s);return(s.RequestId,s);}
async Task<string> Recipient(string op){await using var db=await factory.CreateDbContextAsync();return await db.Set<ManualNotificationRecipient>().Where(x=>x.OperationId==op).Select(x=>x.Id).SingleAsync();}
string fp="";
async Task<ManualNotificationPushPolicyResult> Read(string op,string? fingerprint=null,string device="device")=>await policy.ReadAsync(await Recipient(op),device,fingerprint??fp);
try{
 await using(var db=await factory.CreateDbContextAsync()){
  // Build a real pre-upgrade database, with retained account + inbox + accepted history.
  await db.GetService<IMigrator>().MigrateAsync("20261014025716_AddManualNotificationAcceptance");
  foreach(var id in new[]{"admin","member","late"})db.Users.Add(new(){Id=id,UserName=id,Email=id+"@PRIVATE.invalid",DisplayName=id,Enabled=true,EmailConfirmed=true,SecurityStamp=id+"-stamp",IsAdmin=id=="admin"});
  var e=new SessionEvent{Id="event",Title="PRIVATE-EVENT",ScheduledDate="2099-01-01T00:00:00Z",CreatedAt="2026-10-01T00:00:00Z",ManualPhaseOverride=true};
  e.Phases.Add(new(){Id="view",EventId="event",Kind="View",Name="View",IsActive=true});e.Phases.Add(new(){Id="hidden",EventId="event",Kind="Hidden",Name="Hidden"});await LegacySessionEventSeed.AddAsync(db,e);
  db.EventSpectators.Add(new(){EventId="event",UserId="member",RegisteredBy="admin",RegisteredAt="2026-10-01T00:00:00Z"});
  db.PushDevices.Add(new(){Id="device",UserId="member",SecurityStamp="member-stamp",SessionHash="member-session",Endpoint="https://fcm.googleapis.com/PRIVATE",P256dh="PRIVATE-KEY",Auth="PRIVATE-AUTH"});
  db.Add(new ManualNotificationOperation{Id="legacy",EventId="event",ActorId="admin",RequestHash=new('A',64),PreviewFingerprint=new('B',64),Subject="old",Body="keep",RecipientCount=1,AcceptedAt=clock.Now.ToUnixTimeSeconds()});
  db.Messages.Add(new(){Id=new('C',64),UserId="member",Title="old",Body="keep",Created=clock.Now.ToUnixTimeSeconds(),ReadAt=123});
  db.Add(new ManualNotificationRecipient{Id=new('C',64),OperationId="legacy",EventId="event",UserId="member"});
  await db.SaveChangesAsync();
  await db.Database.MigrateAsync();
  Check(!db.Database.HasPendingModelChanges(),"migration matches EF model");
  Check(await db.Set<ManualNotificationPushActivation>().CountAsync()==0&&await db.Set<ManualNotificationPushAdmission>().CountAsync()==0,"upgrade creates no activation or backfilled admissions");
  Check(await db.Messages.AnyAsync(x=>x.Id==new string('C',64)&&x.ReadAt==123&&x.Body=="keep")&&await db.Users.AnyAsync(x=>x.Id=="member"&&x.SecurityStamp=="member-stamp"),"data-bearing old schema retains inbox read flag content and account stamp");
  fp=PushBinding.Fingerprint(await db.PushDevices.SingleAsync());
 }
 Check((await Read("legacy")).State=="NotAdmitted","pre-upgrade acceptance is never retrospectively admitted");
 var off=await Accept();Check((await Read(off.Id)).State=="NotAdmitted","missing activation defaults OFF without preventing inbox save");
 await Change(db=>{db.Add(new ManualNotificationPushActivation{Enabled=false,Epoch=1});return Task.CompletedTask;});
 var disabled=await Accept();Check((await Read(disabled.Id)).State=="NotAdmitted","explicit disabled activation records no admission");
 // Fixture-only activation. There is deliberately no production activation writer yet.
 await Change(async db=>{var a=await db.Set<ManualNotificationPushActivation>().SingleAsync();a.Enabled=true;a.Epoch=2;});
 Check((await Read(off.Id)).State=="NotAdmitted"&&(await Read(disabled.Id)).State=="NotAdmitted","same-second activation does not backfill old accepts");
 await core.AcceptEventManualNotificationAsync(actor,"event",off.Submit);
 Check((await Read(off.Id)).State=="NotAdmitted","replaying old request after activation never admits it");
 var admitted=await Accept();var rid=await Recipient(admitted.Id);var first=await Read(admitted.Id);
 Check(first.State=="EligibleForStaging"&&!first.DispatchConnected&&first.ExpiresAt==clock.Now.ToUnixTimeSeconds()+600,"new acceptance stores approved ten-minute deadline without dispatch");
 await using(var db=await factory.CreateDbContextAsync())Check(await db.Set<ManualNotificationPushAdmission>().CountAsync()==1,"only originally enabled accept has one durable admission");
 await core.AcceptEventManualNotificationAsync(actor,"event",admitted.Submit);
 await using(var db=await factory.CreateDbContextAsync())Check(await db.Set<ManualNotificationPushAdmission>().CountAsync()==1&&await db.Messages.CountAsync()==4,"exact replay adds neither admission nor inbox message");
 var start=clock.Now;clock.Now=start.AddSeconds(599.9);Check((await Read(admitted.Id)).State=="EligibleForStaging","deadline minus fraction remains eligible");
 clock.Now=start.AddSeconds(600);Check((await Read(admitted.Id)).State=="Expired","exact acceptance plus ten minutes is expired");
 clock.Now=start.AddSeconds(900);Check((await Read(admitted.Id)).State=="Expired","overdue request remains expired");
 clock.Now=start.AddSeconds(-1);Check((await Read(admitted.Id)).State=="ClockBeforeAcceptance","clock rollback before acceptance fails closed");clock.Now=start;
 await Change(async db=>(await db.Set<ManualNotificationPushActivation>().SingleAsync()).Enabled=false);
 Check((await Read(admitted.Id)).State=="Disabled","runtime disable blocks previously admitted request");
 await Change(async db=>{var a=await db.Set<ManualNotificationPushActivation>().SingleAsync();a.Enabled=true;a.Epoch=3;});
 Check((await Read(admitted.Id)).State=="ActivationChanged","new activation generation cannot revive old admitted work");
 var current=await Accept();Check((await Read(current.Id)).State=="EligibleForStaging","fresh generation accepts only new work");
 await Change(async db=>db.EventSpectators.Remove(await db.EventSpectators.SingleAsync()));
 Check((await Read(current.Id)).State=="NoLongerRegistered","approved removal policy blocks Push for unregistered recipient");
 await using(var db=await factory.CreateDbContextAsync())Check(await db.Messages.CountAsync()==5&&await db.Messages.AnyAsync(x=>x.Id==rid&&x.Body=="PRIVATE-BODY"),"registration removal keeps all saved inbox messages");
 await Change(db=>{db.EventSpectators.Add(new(){EventId="event",UserId="member",RegisteredBy="admin",RegisteredAt="2026-10-14T08:00:00Z"});db.EventSpectators.Add(new(){EventId="event",UserId="late",RegisteredBy="admin",RegisteredAt="2026-10-14T08:00:00Z"});return Task.CompletedTask;});
 await using(var db=await factory.CreateDbContextAsync())Check(await db.Set<ManualNotificationRecipient>().CountAsync(x=>x.OperationId==current.Id)==1,"late join cannot expand accepted audience");
 Check((await Read(current.Id,fingerprint:new string('A',64))).State=="BindingChanged","expected device binding enforced under same policy read transaction");
 Check((await Read(current.Id,device:"missing")).State=="DeviceUnavailable","missing device fails closed");
 Check((await policy.ReadAsync("", "device", fp)).State=="InvalidInput","invalid inputs rejected");
 Check((await policy.ReadAsync(new string('D',64),"device",fp)).State=="MissingAcceptedRecipient","unknown accepted recipient rejected");
 await Change(async db=>(await db.Users.SingleAsync(x=>x.Id=="member")).Enabled=false);
 Check((await Read(current.Id)).State=="AccountUnavailable","current disabled account checked");
 await Change(async db=>{var u=await db.Users.SingleAsync(x=>x.Id=="member");u.Enabled=true;u.SecurityStamp="reset";});
 Check((await Read(current.Id)).State=="BindingInvalid","current stamp reset checked");
 await Change(async db=>(await db.Users.SingleAsync(x=>x.Id=="member")).SecurityStamp="member-stamp");
 await Change(db=>{db.RevokedAuthSessions.Add(new(){TokenHash="member-session",UserId="member",RevokedAt="2026-10-14T08:00:00Z"});return Task.CompletedTask;});
 Check((await Read(current.Id)).State=="BindingInvalid","current revoked session checked");
 await Change(async db=>db.RevokedAuthSessions.Remove(await db.RevokedAuthSessions.SingleAsync()));
 await Change(async db=>{await using var tx=await db.Database.BeginTransactionAsync();await db.Phases.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.IsActive,false));await db.Phases.Where(x=>x.Id=="hidden").ExecuteUpdateAsync(x=>x.SetProperty(p=>p.IsActive,true));await tx.CommitAsync();});
 Check((await Read(current.Id)).State=="ViewingDenied","current event viewing denial checked");
 await Change(async db=>{await using var tx=await db.Database.BeginTransactionAsync();await db.Phases.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.IsActive,false));await db.Phases.Where(x=>x.Id=="view").ExecuteUpdateAsync(x=>x.SetProperty(p=>p.IsActive,true));await tx.CommitAsync();db.EventSpectators.Remove(await db.EventSpectators.SingleAsync(x=>x.UserId=="late"));});
 var result=await Read(current.Id);Check(!JsonSerializer.Serialize(result).Contains("PRIVATE"),"policy result contains no message or endpoint secrets");
 // Actual read-only connection and independent provider prove durable recovery, not in-memory state.
 var options=new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source="+path+";Mode=ReadOnly").Options;
 var reopened=new ManualNotificationPushPolicy(new Factory(options),clock);
 Check((await reopened.ReadAsync(await Recipient(current.Id),"device",fp)).State=="EligibleForStaging","fresh context via actual read-only connection recovers durable admission");
 await using(var db=await factory.CreateDbContextAsync()){
  Check(await db.Set<ManualNotificationRecipient>().AllAsync(x=>x.PushState=="Pending")&&await db.Set<EventPushDelivery>().CountAsync()==0&&await db.Deliveries.CountAsync()==0,"policy reads never mutate dormant recipients or create transport work");
  await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectManualAdmission BEFORE INSERT ON ManualNotificationPushAdmissions BEGIN SELECT RAISE(ABORT, 'synthetic-admission-failure'); END");
 }
 int before;await using(var db=await factory.CreateDbContextAsync())before=await db.Messages.CountAsync();
 bool rolled=false;try{await Accept();}catch(DbUpdateException){rolled=true;}Check(rolled,"injected admission failure propagates");
 await using(var db=await factory.CreateDbContextAsync()){
  Check(await db.Messages.CountAsync()==before&&await db.Set<ManualNotificationOperation>().CountAsync()==before&&await db.Set<ManualNotificationRecipient>().CountAsync()==before&&await db.Audits.CountAsync(x=>x.Action=="ManualNotificationAccepted")==before-1,"admission failure rolls back operation inbox recipients and audit atomically");
  await db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectManualAdmission");
  bool constrained=false;try{await db.Database.ExecuteSqlRawAsync("UPDATE ManualNotificationPushAdmissions SET ExpiresAt=AcceptedAt+601");}catch(Microsoft.Data.Sqlite.SqliteException){constrained=true;}Check(constrained,"database enforces exact ten-minute deadline");
  bool epochConstrained=false;try{await db.Database.ExecuteSqlRawAsync("UPDATE ManualNotificationPushActivations SET Epoch=0");}catch(Microsoft.Data.Sqlite.SqliteException){epochConstrained=true;}Check(epochConstrained,"database rejects invalid activation generation");
  await db.Database.OpenConnectionAsync();await using var cmd=db.Database.GetDbConnection().CreateCommand();cmd.CommandText="PRAGMA integrity_check";Check((string?)await cmd.ExecuteScalarAsync()=="ok","SQLite integrity ok");cmd.CommandText="PRAGMA foreign_key_check";await using var reader=await cmd.ExecuteReaderAsync();Check(!await reader.ReadAsync(),"no foreign key violations");
 }
 using(var stop=new CancellationTokenSource()){stop.Cancel();bool cancelled=false;try{await policy.ReadAsync(rid,"device",fp,stop.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"cancellation propagated without write");}
 Console.WriteLine($"Manual notification Push policy: {passed} checks passed.");
}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(root,true);}catch{}}
sealed class Factory(DbContextOptions<ApplicationDbContext> options):IDbContextFactory<ApplicationDbContext>{public ApplicationDbContext CreateDbContext()=>new(options);}
sealed class Clock(DateTimeOffset now):TimeProvider{public DateTimeOffset Now=now;public override DateTimeOffset GetUtcNow()=>Now;}
