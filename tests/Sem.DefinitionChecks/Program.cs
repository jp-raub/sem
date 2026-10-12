using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;
var root=Path.Combine(Path.GetTempPath(),"sem-definitions-"+Guid.NewGuid());Directory.CreateDirectory(root);
var services=new ServiceCollection();services.AddLogging();services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+Path.Combine(root,"data.db")),ServiceLifetime.Scoped);services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();services.AddSingleton(TimeProvider.System);services.AddScoped<CoreFoundationService>();using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();int passed=0;
void Check(bool v,string label){if(!v)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
async Task Deny<T>(Func<Task> f,string label)where T:Exception{bool denied=false;try{await f();}catch(T){denied=true;}Check(denied,label);}
ClaimsPrincipal Who(string id)=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,id),new Claim("AspNet.Identity.SecurityStamp",id+"-stamp"),new Claim(PasskeyAdditionService.SessionClaim,id+"-session")],"Identity.Application"));
PartDefinitionPlanEdit Edit(PartDefinitionPlan p,params PartDefinitionEdit[] rows)=>new(p.RowVersion,rows);
PartDefinitionEdit Row(PartDefinitionView p)=>new(p.Id,p.Name,p.Category);
try{
 await using(var db=await factory.CreateDbContextAsync()){
  await db.GetService<IMigrator>().MigrateAsync("20261012180718_AddEventPhases");
  foreach(var userId in new[]{"admin","manager","member"})db.Users.Add(new ApplicationUser{Id=userId,Email=userId+"@test",UserName=userId+"@test",DisplayName=userId,Enabled=true,EmailConfirmed=true,SecurityStamp=userId+"-stamp",IsAdmin=userId=="admin"});
  var e=new SessionEvent{Id="old",Title="Retained",ScheduledDate="2026-11-01T00:00:00.000Z",CreatedAt="2026-10-01T00:00:00.000Z",RowVersion=7};e.Admins.Add(new(){EventId=e.Id,UserId="manager",GrantedAt=e.CreatedAt});e.Phases.Add(new(){EventId=e.Id,Kind="Hidden",Name="非表示",IsActive=true});e.Phases.Add(new(){EventId=e.Id,Kind="View",Name="閲覧",SortOrder=1});db.SessionEvents.Add(e);await db.SaveChangesAsync();await SqliteStorageInitializer.InitializeAsync(db);
 }
 var admin=Who("admin");var manager=Who("manager");var member=Who("member");var preset=await core.ReadDefinitionsAsync(admin,null);var old=await core.ReadDefinitionsAsync(manager,"old");
 Check(preset.Definitions.Select(x=>x.Name).SequenceEqual(new[]{"Vo","Cho","Gt","Ba","Dr","Key"}),"Migration seeds ordered six service presets");
 Check(old.Definitions.Length==6&&old.RowVersion==7&&old.Title=="Retained","Populated phase DB receives independent definitions without changing event version");
 Check(old.Definitions.All(x=>!preset.Definitions.Any(p=>p.Id==x.Id))&&old.Definitions.All(x=>x.SourcePresetId!=null),"Migrated definitions have independent identities and retained provenance");
 await Deny<UnauthorizedAccessException>(()=>core.ReadDefinitionsAsync(member,"old"),"Member cannot read management definitions");await Deny<UnauthorizedAccessException>(()=>core.ReadDefinitionsAsync(manager,null),"Event manager cannot inspect service preset management");
 var drafts=preset.Definitions.Select(Row).ToArray();await Deny<UnauthorizedAccessException>(()=>core.SaveDefinitionsAsync(manager,null,Edit(preset,drafts)),"Manager cannot change service preset");
 await Deny<ValidationException>(()=>core.SaveDefinitionsAsync(admin,null,Edit(preset,[drafts[0] with{Name="<script>"}])),"Unsafe name rejected");
 await Deny<ValidationException>(()=>core.SaveDefinitionsAsync(admin,null,Edit(preset,[drafts[0],drafts[1] with{Name="vo"}])),"Case-insensitive duplicate names rejected");
 await Deny<ValidationException>(()=>core.SaveDefinitionsAsync(admin,null,Edit(preset,[drafts[0] with{Category="Admin"}])),"Invalid category rejected");
 await Deny<ValidationException>(()=>core.SaveDefinitionsAsync(admin,null,Edit(preset,[drafts[0],drafts[0]])),"Duplicate identifiers rejected");
 var saved=await core.SaveDefinitionsAsync(admin,null,Edit(preset,[drafts[1] with{Name="Voice"},drafts[0] with{Name="Lead"}]));Check(saved.Definitions.Select(x=>x.Name).SequenceEqual(new[]{"Voice","Lead"})&&saved.RowVersion==1,"Atomic preset reorder rename removal commits with version");
 Check((await core.ReadDefinitionsAsync(manager,"old")).Definitions.Select(x=>x.Name).SequenceEqual(new[]{"Vo","Cho","Gt","Ba","Dr","Key"}),"Preset edits never mutate existing event snapshots");
 await Deny<CoreConflictException>(()=>core.SaveDefinitionsAsync(admin,null,Edit(preset,drafts)),"Stale preset draft rejected");
 await Deny<ValidationException>(()=>core.SaveDefinitionsAsync(admin,null,Edit(saved,drafts)),"Deleted preset identifier cannot be resurrected");
 var id=await core.SaveEventAsync(admin,null,new("New","","",DateTimeOffset.UtcNow.AddDays(2),null,"","Hidden",["manager"],0));var fresh=await core.ReadDefinitionsAsync(manager,id);
 Check(fresh.Definitions.Select(x=>x.Name).SequenceEqual(new[]{"Voice","Lead"})&&fresh.Definitions.All(x=>saved.Definitions.All(p=>p.Id!=x.Id)),"New event copies current preset with new identities atomically");
 var other=await core.SaveEventAsync(admin,null,new("Other","","",DateTimeOffset.UtcNow.AddDays(2),null,"","Hidden",[],0));await Deny<UnauthorizedAccessException>(()=>core.SaveDefinitionsAsync(manager,other,Edit(fresh,fresh.Definitions.Select(Row).ToArray())),"Manager cannot change another event");
 await Deny<ValidationException>(()=>core.SaveDefinitionsAsync(manager,id,Edit(fresh,[Row(old.Definitions[0])])),"Cross-event identifier reuse rejected");
 await Deny<ValidationException>(()=>core.SaveDefinitionsAsync(manager,id,Edit(fresh,[Row(saved.Definitions[0])])),"Service preset identity cannot be edited as event identity");
 var swap=fresh.Definitions.Select(Row).ToArray();(swap[0],swap[1])=(swap[0] with{Name=swap[1].Name},swap[1] with{Name=swap[0].Name});var renamed=await core.SaveDefinitionsAsync(manager,id,Edit(fresh,swap));Check(renamed.Definitions[0].Id==fresh.Definitions[0].Id&&renamed.Definitions[0].Name==fresh.Definitions[1].Name,"Atomic name swap retains event identities under unique indexes");
 var phase=await core.ReadPhasePlanAsync(manager,id);await core.SavePhasePlanAsync(manager,id,new(phase.RowVersion,true,phase.Phases.Single(x=>x.IsActive).Id,[]));await Deny<CoreConflictException>(()=>core.SaveDefinitionsAsync(manager,id,Edit(renamed,swap)),"Phase update conflicts with stale event-definition draft");
 renamed=await core.ReadDefinitionsAsync(manager,id);var target=renamed.Definitions[0];var deleted=await core.SaveDefinitionsAsync(manager,id,Edit(renamed,[Row(renamed.Definitions[1])]));Check(deleted.Definitions.Length==1,"Unused definition may be removed");await Deny<ValidationException>(()=>core.SaveDefinitionsAsync(manager,id,Edit(deleted,[Row(target)])),"Deleted event definition cannot be resurrected");
 var race=await core.ReadDefinitionsAsync(admin,id);async Task<bool>Race(string name){try{await core.SaveDefinitionsAsync(admin,id,Edit(race,[Row(race.Definitions[0]) with{Name=name}]));return true;}catch(CoreConflictException){return false;}}var results=await Task.WhenAll(Task.Run(()=>Race("RaceA")),Task.Run(()=>Race("RaceB")));Check(results.Count(x=>x)==1,"Concurrent definition drafts commit once");
 await using(var db=await factory.CreateDbContextAsync()){db.RevokedAuthSessions.Add(new(){TokenHash=SemEndpoints.Hash("manager-session"),UserId="manager",RevokedAt="2026-10-01T00:00:00.000Z"});await db.SaveChangesAsync();}
 var latest=await core.ReadDefinitionsAsync(admin,id);await Deny<UnauthorizedAccessException>(()=>core.SaveDefinitionsAsync(manager,id,Edit(latest,latest.Definitions.Select(Row).ToArray())),"Revoked connected-session manager cannot save");
 await using(var db=await factory.CreateDbContextAsync()){await SqliteStorageInitializer.InitializeAsync(db);Check(await db.Users.CountAsync()==3&&await db.Phases.CountAsync(x=>x.EventId=="old")==2&&await db.EventAdmins.AnyAsync(x=>x.EventId=="old"),"Restart preserves accounts phases managers and definitions");Check(!db.Database.HasPendingModelChanges(),"EF model matches latest migration");}
 Console.WriteLine("Definition checks passed: "+passed);
}finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
