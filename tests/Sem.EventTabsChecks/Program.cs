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
var root=Path.Combine(Path.GetTempPath(),"sem-event-tabs-"+Guid.NewGuid());Directory.CreateDirectory(root);
var services=new ServiceCollection();services.AddLogging();services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+Path.Combine(root,"data.db")),ServiceLifetime.Scoped);services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();var clock=new FixedClock();services.AddSingleton<TimeProvider>(clock);Microsoft.Extensions.DependencyInjection.DataProtectionServiceCollectionExtensions.AddDataProtection(services);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();int passed=0;
void Check(bool v,string label){if(!v)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
async Task Deny<T>(Func<Task> f,string label)where T:Exception{bool denied=false;try{await f();}catch(T){denied=true;}Check(denied,label);}
ClaimsPrincipal Who(string id)=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,id),new Claim("AspNet.Identity.SecurityStamp",id+"-stamp"),new Claim(PasskeyAdditionService.SessionClaim,id+"-session"),new Claim(ClaimTypes.Role,"Admin")],"Identity.Application"));
var admin=Who("admin");var manager=Who("manager");var viewer=Who("viewer");
try {
 await using(var db=await factory.CreateDbContextAsync()) {
  await SqliteStorageInitializer.InitializeAsync(db);
  foreach(var id in new[]{"admin","manager","viewer"})db.Users.Add(new(){Id=id,Email=id+"@PRIVATE.invalid",UserName=id+"@PRIVATE.invalid",DisplayName=id,Enabled=true,EmailConfirmed=true,SecurityStamp=id+"-stamp",IsAdmin=id=="admin"});
  foreach(var id in new[]{"event","other"}) { var e=new SessionEvent{Id=id,Title="Event",Description="Details",Location="Hall",ScheduledDate="2026-10-13T00:00:00.000Z",CreatedAt="2026-10-01T00:00:00.000Z",ManualPhaseOverride=true};e.Phases.Add(new(){EventId=id,Kind="Hidden",Name="Hidden",IsActive=true});e.Phases.Add(new(){EventId=id,Kind="View",Name="View"});if(id=="event")e.Admins.Add(new(){EventId=id,UserId="manager",GrantedAt=e.CreatedAt});db.SessionEvents.Add(e); }
  await db.SaveChangesAsync();
 }
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventTabsAsync(new ClaimsPrincipal(),"event"),"Anonymous denied");
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventTabsAsync(viewer,"event"),"Forged admin claim cannot reveal hidden event tabs");
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventTabsAsync(manager,"other"),"Event assignment cannot cross event boundary");
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventTabsAsync(admin,"missing"),"Unknown event denied");
 var hidden=await core.ReadEventTabsAsync(manager,"event");Check(hidden.Available.SequenceEqual(new[]{"main","roster","checkin","notifications"}),"Off-day manager has approved four tabs, no progress tab");
 Check(hidden.UserId=="manager"&&hidden.EventId=="event"&&hidden.Description=="Details"&&hidden.Location=="Hall","Scope and public event overview values remain correct");
 Check(!JsonSerializer.Serialize(hidden).Contains("PRIVATE")&&!JsonSerializer.Serialize(hidden).Contains("SecurityStamp"),"Tab view does not expose account contact session or stamp");
 await using(var db=await factory.CreateDbContextAsync()) {var e=await db.SessionEvents.Include(e=>e.Phases).SingleAsync(e=>e.Id=="event");e.Phases.Single(p=>p.IsActive).IsActive=false;await db.SaveChangesAsync();e.Phases.Single(p=>p.Kind=="View").IsActive=true;await db.SaveChangesAsync();}
 Check((await core.ReadEventTabsAsync(viewer,"event")).Available.SequenceEqual(new[]{"main","roster"}),"Visible off-day ordinary viewer has main and public roster only");
 clock.Now=DateTimeOffset.Parse("2026-10-12T15:00:00Z");
 Check((await core.ReadEventTabsAsync(viewer,"event")).Available.SequenceEqual(new[]{"main","roster","progress"}),"Japan midnight enables participant progress tab");
 Check((await core.ReadEventTabsAsync(manager,"event")).Available.SequenceEqual(new[]{"main","roster","checkin","progress","notifications"}),"On-day assigned manager has approved five-tab order");
 Check((await core.ReadEventTabsAsync(admin,"event")).Available.SequenceEqual(new[]{"main","roster","checkin","progress","notifications"}),"Database global administrator has management tabs");
 clock.Now=DateTimeOffset.Parse("2026-10-13T15:00:00Z");Check(!(await core.ReadEventTabsAsync(manager,"event")).Available.Contains("progress"),"Next Japan day removes progress tab without removing standalone preview API");
 await using(var db=await factory.CreateDbContextAsync()) {db.EventAdmins.Remove(await db.EventAdmins.SingleAsync());await db.SaveChangesAsync();}
 Check((await core.ReadEventTabsAsync(manager,"event")).Available.SequenceEqual(new[]{"main","roster"}),"Removed manager loses management tabs on next read");
 await using(var db=await factory.CreateDbContextAsync()) {var u=await db.Users.SingleAsync(u=>u.Id=="viewer");u.Enabled=false;await db.SaveChangesAsync();}
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventTabsAsync(viewer,"event"),"Disabled account denied");
 await using(var db=await factory.CreateDbContextAsync()) {db.RevokedAuthSessions.Add(new(){TokenHash=SemEndpoints.Hash("admin-session"),UserId="admin",RevokedAt="2026-10-01T00:00:00.000Z"});await db.SaveChangesAsync();}
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventTabsAsync(admin,"event"),"Revoked global admin session denied");
 await using(var db=await factory.CreateDbContextAsync()) {Check(!db.Database.HasPendingModelChanges(),"No schema change required");Check(await db.RegistrationOperations.CountAsync()==0&&await db.CommentOperations.CountAsync()==0&&await db.EventReceptions.CountAsync()==0,"Overview and tab reads do not write business operations");}
 Console.WriteLine("Event tabs checks passed: "+passed);
}finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
sealed class FixedClock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.Parse("2026-10-12T14:59:59Z");public override DateTimeOffset GetUtcNow()=>Now;}
