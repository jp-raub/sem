using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;
var root=Path.Combine(Path.GetTempPath(),"sem-manual-activation-"+Guid.NewGuid());Directory.CreateDirectory(root);
var path=Path.Combine(root,"data.db");var clock=new Clock(DateTimeOffset.Parse("2026-10-14T08:30:00Z"));
var services=new ServiceCollection();services.AddLogging();services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+path),ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddSingleton<TimeProvider>(clock);DataProtectionServiceCollectionExtensions.AddDataProtection(services);
services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();
var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();int passed=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
async Task Deny<T>(Func<Task> action,string label)where T:Exception{bool denied=false;try{await action();}catch(T){denied=true;}Check(denied,label);}
async Task Change(Func<ApplicationDbContext,Task> action){await using var db=await factory.CreateDbContextAsync();await action(db);await db.SaveChangesAsync();}
ClaimsPrincipal Who(string id)=>new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,id),new("AspNet.Identity.SecurityStamp",id+"-stamp"),new(PasskeyAdditionService.SessionClaim,id+"-session"),new(ClaimTypes.Role,"Admin")],"Identity.Application"));
ManualNotificationPushActivationEdit Edit(bool enabled,long epoch)=>new(Guid.NewGuid().ToString("D"),enabled,epoch,true);
Task<ManualNotificationPushActivationResult> Set(ManualNotificationPushActivationEdit edit,string actor="admin")=>core.SetManualPushActivationAsync(Who(actor),edit);
async Task<string> Accept(){var p=await core.PreviewEventManualNotificationAsync(Who("admin"),"event","private-subject","private-body");var id=Guid.NewGuid().ToString("D");await core.AcceptEventManualNotificationAsync(Who("admin"),"event",new(id,p.Fingerprint,p.Subject,p.Body,true));return id;}
try{
 await using(var db=await factory.CreateDbContextAsync()){
  await db.GetService<IMigrator>().MigrateAsync("20261014080345_AddManualNotificationPushQueue");
  foreach(var id in new[]{"admin","admin2","manager","member"})db.Users.Add(new(){Id=id,UserName=id,Email=id+"@PRIVATE.invalid",DisplayName=id,Enabled=true,EmailConfirmed=true,SecurityStamp=id+"-stamp",IsAdmin=id.StartsWith("admin")});
  var e=new SessionEvent{Id="event",Title="Event",ScheduledDate="2099-01-01T00:00:00Z",CreatedAt="2026-10-01T00:00:00Z",ManualPhaseOverride=true};e.Phases.Add(new(){Id="view",EventId="event",Kind="View",Name="View",IsActive=true});e.Phases.Add(new(){Id="hidden",EventId="event",Kind="Hidden",Name="Hidden"});e.Admins.Add(new(){EventId="event",UserId="manager",GrantedAt="2026-10-01T00:00:00Z"});await LegacySessionEventSeed.AddAsync(db,e);
  db.EventSpectators.Add(new(){EventId="event",UserId="member",RegisteredBy="admin",RegisteredAt="2026-10-01T00:00:00Z"});db.Messages.Add(new(){Id="prior",UserId="member",Title="keep",Body="keep",Created=123,ReadAt=124});
  await db.SaveChangesAsync();await db.Database.MigrateAsync();
  Check(!db.Database.HasPendingModelChanges()&&await db.Messages.AnyAsync(x=>x.Id=="prior"&&x.ReadAt==124),"additive control migration preserves old inbox and model");
  Check(await db.Set<ManualNotificationPushActivation>().CountAsync()==0&&await db.Set<ManualNotificationPushActivationOperation>().CountAsync()==0,"migration never activates or inserts control history");
 }
 var initial=await core.ReadManualPushActivationAsync(Who("admin"));Check(!initial.Enabled&&initial.Epoch==0,"missing state reads OFF epoch zero without writes");
 await Deny<UnauthorizedAccessException>(()=>core.ReadManualPushActivationAsync(Who("manager")),"event manager and forged role cannot read global activation");
 await Deny<UnauthorizedAccessException>(()=>Set(Edit(true,0),"member"),"ordinary member cannot enable");
 await Deny<UnauthorizedAccessException>(()=>core.SetManualPushActivationAsync(new ClaimsPrincipal(),Edit(true,0)),"anonymous cannot enable");
 var enable=Edit(true,0);
 await Deny<ValidationException>(()=>Set(enable with{Confirmed=false}),"explicit confirmation required");
 await Deny<ValidationException>(()=>Set(enable with{RequestId="bad"}),"malformed operation ID rejected");
 await Deny<ValidationException>(()=>Set(enable with{RequestId=enable.RequestId.Replace("-","")}),"alternate GUID form rejected");
 await Deny<ValidationException>(()=>Set(enable with{ExpectedEpoch=-1}),"negative expected version rejected");
 await Deny<CoreConflictException>(()=>Set(enable with{ExpectedEpoch=1}),"stale first expected version rejected");
 var noop=await Set(Edit(false,0));Check(!noop.Changed&&!noop.Enabled&&noop.Epoch==0,"confirming default OFF is audited no-op without arming");
 var before=await Accept();var enabled=await Set(enable);Check(enabled.Changed&&enabled.Enabled&&enabled.Epoch==1&&!enabled.Replayed,"confirmed global administrator enables fresh generation once");
 var after=await Accept();await using(var db=await factory.CreateDbContextAsync())Check(!await db.Set<ManualNotificationPushAdmission>().AnyAsync(x=>x.OperationId==before)&&await db.Set<ManualNotificationPushAdmission>().AnyAsync(x=>x.OperationId==after&&x.Epoch==1),"same-second enable admits only subsequent original acceptance");
 Check((await Set(enable)).Replayed,"same operation returns original result without another epoch");
 await Deny<ValidationException>(()=>Set(enable with{Enabled=false}),"operation ID cannot bind opposite decision");
 await Deny<ValidationException>(()=>Set(enable with{ExpectedEpoch=1}),"operation ID cannot bind different expected generation");
 await Deny<ValidationException>(()=>Set(enable,"admin2"),"operation ID cannot bind another current administrator");
 var same=await Set(Edit(true,1));Check(!same.Changed&&same.Epoch==1,"new confirmation of already enabled does not rotate epoch");
 var disable=Edit(false,1);Check((await Set(disable)).Epoch==2,"disabling advances version and blocks original generation");
 var replay=await Set(enable);Check(replay.Replayed&&replay.Enabled&&replay.Epoch==1&&!(await core.ReadManualPushActivationAsync(Who("admin"))).Enabled,"replay returns original result but never re-enables current OFF state");
 await Deny<CoreConflictException>(()=>Set(Edit(true,1)),"stale decision cannot re-enable after disable");
 Check((await Set(Edit(true,2))).Epoch==3,"re-enable uses monotonically new epoch");
 await using(var db=await factory.CreateDbContextAsync())Check(await db.Set<ManualNotificationPushAdmission>().AllAsync(x=>x.Epoch==1)&&await db.Messages.CountAsync()==3,"enable changes never backfill admissions or rewrite inboxes");
 // Current authentication must be repeated for reading old accepted control outcomes.
 await Change(async db=>(await db.Users.SingleAsync(x=>x.Id=="admin")).Enabled=false);
 await Deny<UnauthorizedAccessException>(()=>Set(enable),"disabled actor cannot recover old control result");
 await Change(async db=>{var u=await db.Users.SingleAsync(x=>x.Id=="admin");u.Enabled=true;u.EmailConfirmed=false;});
 await Deny<UnauthorizedAccessException>(()=>Set(enable),"unconfirmed actor cannot replay");
 await Change(async db=>{var u=await db.Users.SingleAsync(x=>x.Id=="admin");u.EmailConfirmed=true;u.SecurityStamp="changed";});
 await Deny<UnauthorizedAccessException>(()=>Set(enable),"stale stamp cannot replay");
 await Change(async db=>{var u=await db.Users.SingleAsync(x=>x.Id=="admin");u.SecurityStamp="admin-stamp";u.IsAdmin=false;});
 await Deny<UnauthorizedAccessException>(()=>Set(enable),"removed global administrator cannot replay even with role claim");
 await Change(async db=>{(await db.Users.SingleAsync(x=>x.Id=="admin")).IsAdmin=true;db.RevokedAuthSessions.Add(new(){TokenHash=SemEndpoints.Hash("admin-session"),UserId="admin",RevokedAt="2026-10-14T08:30:00Z"});});
 await Deny<UnauthorizedAccessException>(()=>Set(enable),"revoked session cannot replay");
 await Change(async db=>db.RevokedAuthSessions.Remove(await db.RevokedAuthSessions.SingleAsync()));
 var fault=Edit(false,3);await Change(db=>db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectControlAudit BEFORE INSERT ON Audits WHEN NEW.Action='ManualPushActivationChanged' BEGIN SELECT RAISE(ABORT, 'synthetic-control-audit'); END"));
 await Deny<DbUpdateException>(()=>Set(fault),"audit failure propagates");
 await using(var db=await factory.CreateDbContextAsync())Check((await db.Set<ManualNotificationPushActivation>().SingleAsync()).Epoch==3&&!await db.Set<ManualNotificationPushActivationOperation>().AnyAsync(x=>x.Id==fault.RequestId),"failed audit rolls back state and operation ledger");
 await Change(db=>db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectControlAudit"));
 var duplicates=await Task.WhenAll(Enumerable.Range(0,3).Select(_=>Task.Run(()=>Set(fault))));Check(duplicates.Count(x=>!x.Replayed)==1&&duplicates.All(x=>x.Epoch==4&&!x.Enabled),"three concurrent exact controls cause one transition and two replays");
 var requests=new[]{Edit(true,4),Edit(true,4)};var decisions=await Task.WhenAll(requests.Select(edit=>Task.Run(async()=>{try{await Set(edit);return true;}catch(CoreConflictException){return false;}})));
 Check(decisions.Count(x=>x)==1&&(await core.ReadManualPushActivationAsync(Who("admin"))).Epoch==5,"distinct stale concurrent controls cannot both apply");
 // Prove acceptance waits behind an uncommitted state change in the same SQLite DB.
 var turnOff=await Set(Edit(false,5));Check(turnOff.Epoch==6,"prepare serialized acceptance fixture OFF");
 var preview=await core.PreviewEventManualNotificationAsync(Who("admin"),"event","serialized","body");var requestId=Guid.NewGuid().ToString("D");Task<ManualNotificationAcceptance> waiting;
 await using(var db=await factory.CreateDbContextAsync()){
  await using var tx=await db.Database.BeginTransactionAsync();var state=await db.Set<ManualNotificationPushActivation>().SingleAsync();state.Enabled=true;state.Epoch=7;await db.SaveChangesAsync();
  waiting=Task.Run(()=>core.AcceptEventManualNotificationAsync(Who("admin"),"event",new(requestId,preview.Fingerprint,preview.Subject,preview.Body,true)));
  await Task.Delay(150);Check(!waiting.IsCompleted,"original acceptance waits for held activation transaction");await tx.CommitAsync();
 }
 await waiting;await using(var db=await factory.CreateDbContextAsync())Check(await db.Set<ManualNotificationPushAdmission>().AnyAsync(x=>x.OperationId==requestId&&x.Epoch==7),"waiting acceptance observes committed epoch rather than stale activation snapshot");
 await Change(async db=>{var s=await db.Set<ManualNotificationPushActivation>().SingleAsync();s.Epoch=long.MaxValue;});
 await Deny<ValidationException>(()=>Set(Edit(false,long.MaxValue)),"generation overflow fails closed without wrapping");
 await using(var db=await factory.CreateDbContextAsync()){
  Check(await db.Set<ManualNotificationPushDelivery>().CountAsync()==0&&await db.Set<EventPushDelivery>().CountAsync()==0&&await db.Deliveries.CountAsync()==0,"control never prepares or sends any notification");
  var operations=await db.Set<ManualNotificationPushActivationOperation>().CountAsync();var audits=await db.Audits.CountAsync(x=>x.Action=="ManualPushActivationChanged"||x.Action=="ManualPushActivationConfirmed");Check(operations==audits,"one audit per accepted distinct control including no-op and none for replay");
  await db.Database.OpenConnectionAsync();await using var cmd=db.Database.GetDbConnection().CreateCommand();cmd.CommandText="PRAGMA integrity_check";Check((string?)await cmd.ExecuteScalarAsync()=="ok","SQLite integrity ok");cmd.CommandText="PRAGMA foreign_key_check";await using var reader=await cmd.ExecuteReaderAsync();Check(!await reader.ReadAsync(),"foreign keys valid");
 }
 using(var stop=new CancellationTokenSource()){stop.Cancel();await Deny<OperationCanceledException>(()=>core.SetManualPushActivationAsync(Who("admin"),Edit(false,7),stop.Token),"cancellation propagates without control side effect");}
 Console.WriteLine($"Manual notification Push activation: {passed} checks passed.");
}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(root,true);}catch{}}
sealed class Clock(DateTimeOffset now):TimeProvider{public override DateTimeOffset GetUtcNow()=>now;}
