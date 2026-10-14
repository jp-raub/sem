using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;
var root=Path.Combine(Path.GetTempPath(),"sem-manual-device-"+Guid.NewGuid());Directory.CreateDirectory(root);
var dbPath=Path.Combine(root,"data.db");
var services=new ServiceCollection();services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+dbPath),ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddSingleton(TimeProvider.System);DataProtectionServiceCollectionExtensions.AddDataProtection(services);
services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();
var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();
var gate=new ManualNotificationDevicePreflight(factory,TimeProvider.System);int passed=0;
void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
async Task Change(Func<ApplicationDbContext,Task> action){await using var db=await factory.CreateDbContextAsync();await action(db);await db.SaveChangesAsync();}
try {
 await using(var db=await factory.CreateDbContextAsync()) {
  await SqliteStorageInitializer.InitializeAsync(db);
  foreach(var id in new[]{"admin","spectator","other","late"})db.Users.Add(new(){Id=id,Email=id+"@PRIVATE.invalid",UserName=id+"@PRIVATE.invalid",DisplayName="PRIVATE-NAME",Enabled=true,EmailConfirmed=true,SecurityStamp=id+"-stamp",IsAdmin=id=="admin"});
  var e=new SessionEvent{Id="event",Title="PRIVATE-TITLE",ScheduledDate="2099-01-01T00:00:00Z",CreatedAt="2026-10-01T00:00:00Z",ManualPhaseOverride=true};
  e.Phases.Add(new(){Id="view",EventId=e.Id,Kind="View",Name="View",IsActive=true});e.Phases.Add(new(){Id="hidden",EventId=e.Id,Kind="Hidden",Name="Hidden"});db.SessionEvents.Add(e);
  db.EventSpectators.Add(new(){EventId=e.Id,UserId="spectator",RegisteredBy="admin",RegisteredAt="2026-10-01T00:00:00Z"});
  db.PushDevices.Add(new(){Id="device",UserId="spectator",SecurityStamp="spectator-stamp",SessionHash="session",Endpoint="https://fcm.googleapis.com/PRIVATE-ENDPOINT",P256dh="PRIVATE-P256DH",Auth="PRIVATE-AUTH"});
  db.PushDevices.Add(new(){Id="other-device",UserId="other",SecurityStamp="other-stamp",SessionHash="other-session",Endpoint="https://fcm.googleapis.com/OTHER-PRIVATE",P256dh="PRIVATE-P256DH",Auth="PRIVATE-AUTH"});
  await db.SaveChangesAsync();Check(!db.Database.HasPendingModelChanges(),"preflight adds no schema changes");
 }
 var who=new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,"admin"),new("AspNet.Identity.SecurityStamp","admin-stamp"),new(PasskeyAdditionService.SessionClaim,"admin-session")],"Identity.Application"));
 var preview=await core.PreviewEventManualNotificationAsync(who,"event","PRIVATE-SUBJECT","PRIVATE-BODY");
 await core.AcceptEventManualNotificationAsync(who,"event",new(Guid.NewGuid().ToString("D"),preview.Fingerprint,preview.Subject,preview.Body,true));
 string recipient,fp;
 await using(var db=await factory.CreateDbContextAsync()){recipient=await db.Set<ManualNotificationRecipient>().Select(x=>x.Id).SingleAsync();fp=PushBinding.Fingerprint(await db.PushDevices.SingleAsync(x=>x.Id=="device"));}
 async Task<ManualNotificationDevicePreflightResult> Read(string device="device",string? fingerprint=null)=>await gate.ReadAsync(recipient,device,fingerprint??fp);
 var valid=await Read();Check(valid.State=="PolicyPending"&&valid.CurrentlyRegistered==true&&!valid.DispatchConnected,"valid snapshot and binding remain explicitly disconnected pending policy");
 var serialized=JsonSerializer.Serialize(valid);Check(!new[]{"PRIVATE","stamp","session","Endpoint","P256dh","Auth","spectator"}.Any(serialized.Contains),"result exposes no identities message content or subscription secrets");
 Check((await gate.ReadAsync("", "device", fp)).State=="InvalidInput","empty recipient rejected");
 Check((await Read(fingerprint:"invalid")).State=="InvalidInput","malformed expected fingerprint rejected");
 Check((await gate.ReadAsync(new string('A',64),"device",fp)).State=="MissingAcceptedRecipient","cannot discover unaccepted recipients");
 Check((await Read("missing-device")).State=="DeviceUnavailable","missing device rejected");
 Check((await Read("other-device")).State=="DeviceUnavailable","device bound to another user rejected");
 Check((await Read(fingerprint:new string('A',64))).State=="BindingChanged","different expected binding rejected");
 await Change(async db=>(await db.Users.SingleAsync(x=>x.Id=="spectator")).Enabled=false);
 Check((await Read()).State=="AccountUnavailable","current disabled account rejected");
 await Change(async db=>{var u=await db.Users.SingleAsync(x=>x.Id=="spectator");u.Enabled=true;u.EmailConfirmed=false;});
 Check((await Read()).State=="AccountUnavailable","current unconfirmed account rejected");
 await Change(async db=>{var u=await db.Users.SingleAsync(x=>x.Id=="spectator");u.EmailConfirmed=true;u.SecurityStamp="changed";});
 Check((await Read()).State=="BindingInvalid","account security-stamp reset invalidates old device");
 await Change(async db=>(await db.Users.SingleAsync(x=>x.Id=="spectator")).SecurityStamp="spectator-stamp");
 await Change(async db=>(await db.PushDevices.SingleAsync(x=>x.Id=="device")).SessionHash=null);
 Check((await Read()).State=="BindingInvalid","legacy binding without session rejected");
 await Change(async db=>(await db.PushDevices.SingleAsync(x=>x.Id=="device")).SessionHash="session");
 await Change(db=>{db.RevokedAuthSessions.Add(new(){TokenHash="session",UserId="spectator",RevokedAt="2026-10-01T00:00:00Z"});return Task.CompletedTask;});
 Check((await Read()).State=="BindingInvalid","revoked device session rejected");
 await Change(async db=>db.RevokedAuthSessions.Remove(await db.RevokedAuthSessions.SingleAsync()));
 await Change(async db=>(await db.PushDevices.SingleAsync(x=>x.Id=="device")).Endpoint="https://example.test/PRIVATE-ENDPOINT");
 Check((await Read()).State=="BindingInvalid","non-allowlisted Push endpoint rejected");
 await Change(async db=>(await db.PushDevices.SingleAsync(x=>x.Id=="device")).Endpoint="https://fcm.googleapis.com/PRIVATE-ENDPOINT");
 await Change(async db=>(await db.PushDevices.SingleAsync(x=>x.Id=="device")).Auth="NEW-PRIVATE-AUTH");
 Check((await Read()).State=="BindingChanged","re-registration binding change never uses old fingerprint");
 await Change(async db=>(await db.PushDevices.SingleAsync(x=>x.Id=="device")).Auth="PRIVATE-AUTH");
 await Change(async db=>{await using var tx=await db.Database.BeginTransactionAsync();await db.Phases.Where(x=>x.EventId=="event").ExecuteUpdateAsync(x=>x.SetProperty(p=>p.IsActive,false));await db.Phases.Where(x=>x.Id=="hidden").ExecuteUpdateAsync(x=>x.SetProperty(p=>p.IsActive,true));await tx.CommitAsync();});
 Check((await Read()).State=="ViewingDenied","current hidden event blocks ordinary accepted recipient");
 await Change(db=>{db.EventAdmins.Add(new(){EventId="event",UserId="spectator",GrantedAt="2026-10-01T00:00:00Z"});return Task.CompletedTask;});
 Check((await Read()).State=="PolicyPending","current database manager can view hidden event but still cannot dispatch");
 await Change(async db=>{db.EventAdmins.Remove(await db.EventAdmins.SingleAsync());await using var tx=await db.Database.BeginTransactionAsync();await db.Phases.Where(x=>x.EventId=="event").ExecuteUpdateAsync(x=>x.SetProperty(p=>p.IsActive,false));await db.Phases.Where(x=>x.Id=="view").ExecuteUpdateAsync(x=>x.SetProperty(p=>p.IsActive,true));await tx.CommitAsync();});
 await Change(async db=>db.EventSpectators.Remove(await db.EventSpectators.SingleAsync()));
 var removed=await Read();Check(removed.State=="PolicyPending"&&removed.CurrentlyRegistered==false&&!removed.DispatchConnected,"registration removal reported as fact without silently selecting delivery retention policy");
 await Change(db=>{
  db.Phases.Add(new(){Id="old-custom",EventId="event",Kind="Custom",Name="Historical signup phase",MaxPicks=-1});
  db.EventPartDefinitions.Add(new(){Id="voice",EventId="event",Name="Voice",NormalizedName="VOICE",Category="Sing"});
  db.Songs.Add(new(){Id="song",EventId="event",Title="PRIVATE-SONG",PerformanceSeconds=60});
  db.SongParts.Add(new(){Id="slot",EventId="event",SongId="song",PartDefinitionId="voice",AssignedUserId="spectator",PhaseId="old-custom"});
  return Task.CompletedTask;
 });
 Check((await Read()).CurrentlyRegistered==true,"current nondeleted performance assignment counts even without spectator registration");
 await Change(async db=>(await db.Songs.SingleAsync()).DeletedAt="2026-10-01T00:00:00Z");
 Check((await Read()).CurrentlyRegistered==false,"soft-deleted song does not resurrect registration");
 await Change(async db=>{(await db.Songs.SingleAsync()).DeletedAt=null;(await db.EventPartDefinitions.SingleAsync()).DeletedAt="2026-10-01T00:00:00Z";});
 Check((await Read()).CurrentlyRegistered==false,"soft-deleted definition does not resurrect registration");
 await Change(async db=>(await db.EventPartDefinitions.SingleAsync()).DeletedAt=null);
 Check((await Read()).CurrentlyRegistered==true,"restored current assignment is reported as fact only");
 await Change(db=>{db.EventSpectators.Add(new(){EventId="event",UserId="late",RegisteredBy="admin",RegisteredAt="2026-10-01T00:00:00Z"});return Task.CompletedTask;});
 await using(var db=await factory.CreateDbContextAsync()) {
  Check(await db.Set<ManualNotificationRecipient>().CountAsync()==1&&await db.Messages.CountAsync()==1,"late registration does not expand immutable accepted audience");
 }
 await Change(async db=>(await db.Messages.SingleAsync()).UserId="other");
 Check((await Read()).State=="MissingInbox","snapshot whose inbox owner no longer matches fails closed");
 await Change(async db=>(await db.Messages.SingleAsync()).UserId="spectator");
 await using(var db=await factory.CreateDbContextAsync()) {
  Check(await db.Set<ManualNotificationRecipient>().AllAsync(x=>x.PushState=="Pending")&&await db.Set<EventPushDelivery>().CountAsync()==0&&await db.Deliveries.CountAsync()==0,"preflight never consumes recipient state or creates any transport work");
  Check(await db.Audits.CountAsync(x=>x.Action=="ManualNotificationAccepted")==1,"preflight adds no business audit or acceptance");
 }
 // A genuinely read-only SQLite connection proves this helper needs no database write access.
 var readOnlyOptions=new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source="+dbPath+";Mode=ReadOnly").Options;
 var readOnlyGate=new ManualNotificationDevicePreflight(new Factory(readOnlyOptions),TimeProvider.System);
 Check((await readOnlyGate.ReadAsync(recipient,"device",fp)).State=="PolicyPending","same checker succeeds with an actual read-only database connection");
 using(var stop=new CancellationTokenSource()){stop.Cancel();bool cancelled=false;try{await gate.ReadAsync(recipient,"device",fp,stop.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"cancellation is propagated without side effects");}
 Console.WriteLine($"Manual notification device preflight: {passed} checks passed.");
}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(root,true);}catch{}}
sealed class Factory(DbContextOptions<ApplicationDbContext> options):IDbContextFactory<ApplicationDbContext>{public ApplicationDbContext CreateDbContext()=>new(options);}
