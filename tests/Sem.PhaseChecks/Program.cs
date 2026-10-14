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
var root=Path.Combine(Path.GetTempPath(),"sem-phase-"+Guid.NewGuid());Directory.CreateDirectory(root);
var clock=new Clock(new DateTimeOffset(2026,11,1,0,0,0,TimeSpan.Zero));var services=new ServiceCollection();services.AddLogging();
services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+Path.Combine(root,"phase.db")),ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
services.AddSingleton<TimeProvider>(clock);services.AddScoped<CoreFoundationService>();
using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();var passed=0;
void Check(bool v,string label){if(!v)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
async Task Deny<T>(Func<Task> f,string label)where T:Exception{var denied=false;try{await f();}catch(T){denied=true;}Check(denied,label);}
ClaimsPrincipal Who(string id)=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,id),new Claim("AspNet.Identity.SecurityStamp",id+"-stamp"),new Claim(PasskeyAdditionService.SessionClaim,id+"-session")],"Identity.Application"));
try{
 await using(var db=await factory.CreateDbContextAsync()){
  // Start at the last shipped foundation schema, seed formal data, then exercise the real upgrade.
  await db.GetService<IMigrator>().MigrateAsync("20261012162159_AddCoreFoundation");
  db.Users.AddRange(new ApplicationUser{Id="admin",UserName="admin@test",Email="admin@test",DisplayName="Admin",Enabled=true,EmailConfirmed=true,IsAdmin=true,SecurityStamp="admin-stamp"},new ApplicationUser{Id="manager",UserName="manager@test",Email="manager@test",DisplayName="Manager",Enabled=true,EmailConfirmed=true,SecurityStamp="manager-stamp"},new ApplicationUser{Id="member",UserName="member@test",Email="member@test",DisplayName="Member",Enabled=true,EmailConfirmed=true,SecurityStamp="member-stamp"});
  db.SiteStates.Add(new SiteState{Initialized=true,Name="Preserved"});await db.SaveChangesAsync();
  await db.Database.ExecuteSqlRawAsync("INSERT INTO SessionEvents(Id,Title,Description,Location,ScheduledDate,AnnouncementMessage,PhaseKind,CreatedAt,RowVersion) VALUES ('old','Retained','','','2026-10-01T00:00:00.000Z','','Hidden','2026-09-01T00:00:00.000Z',7);");
  await db.Database.ExecuteSqlRawAsync("INSERT INTO EventAdmins(EventId,UserId,GrantedAt) VALUES ('old','manager','2026-09-01T00:00:00.000Z');");
  await SqliteStorageInitializer.InitializeAsync(db);
 }
 var admin=Who("admin");var manager=Who("manager");var member=Who("member");
 var old=await core.ReadPhasePlanAsync(manager,"old");
 Check(old.Title=="Retained"&&old.RowVersion==7&&old.ManualOverride&&old.Phases.Length==2&&old.Phases.Single(p=>p.IsActive).Kind=="Hidden","Foundation upgrade retains formal event, managers and visibility with two default phases");
 Check(await core.EvaluateAutomaticPhasesAsync()==0,"Adopted past event stays explicitly hidden until auto is resumed");
 await using(var db=await factory.CreateDbContextAsync())Check(await db.Users.CountAsync()==3&&(await db.SiteStates.SingleAsync()).Name=="Preserved","Upgrade preserves account and site rows");
 await Deny<UnauthorizedAccessException>(()=>core.ReadPhasePlanAsync(member,"old"),"Non-manager cannot inspect management-only phase plan");
 var start=clock.Now.AddHours(10);EventEdit Event(string title,string[] managers)=>new(title,"","",start,null,"","Hidden",managers,0);
 var id=await core.SaveEventAsync(admin,null,Event("New event",["manager"]));var other=await core.SaveEventAsync(admin,null,Event("Other",[]));
 var plan=await core.ReadPhasePlanAsync(manager,id);Check(plan.Phases.Count(p=>p.IsActive)==1&&!plan.ManualOverride,"New event creates exactly one active default in automatic mode");
 PhasePlanEdit Draft(PhasePlanView p,CustomPhaseEdit[] rows,bool manual=true,string? active=null)=>new(p.RowVersion,manual,active??p.Phases.Single(x=>x.IsActive).Id,rows);
 CustomPhaseEdit Custom(string name,int max,DateTimeOffset? begin=null,DateTimeOffset? end=null,string? key=null)=>new(key??Guid.NewGuid().ToString(),name,max,begin,end);
 var a=Custom("First",2,clock.Now.AddHours(1),clock.Now.AddHours(5));var b=Custom("Second",-1,clock.Now.AddHours(3),clock.Now.AddHours(7));
 await Deny<UnauthorizedAccessException>(()=>core.SavePhasePlanAsync(member,id,Draft(plan,[a])),"Member cannot edit phases");
 await Deny<UnauthorizedAccessException>(()=>core.SavePhasePlanAsync(manager,other,Draft(plan,[a])),"Manager cannot edit another event");
 await Deny<ValidationException>(()=>core.SavePhasePlanAsync(manager,id,Draft(plan,[a with{MaxPicks=-2}])),"Maximum below unlimited sentinel is rejected");
 await Deny<ValidationException>(()=>core.SavePhasePlanAsync(manager,id,Draft(plan,[a with{DeactivateAt=a.ActivateAt}])),"Equal activation/deactivation is invalid");
 await Deny<ValidationException>(()=>core.SavePhasePlanAsync(manager,id,Draft(plan,[a,a])),"Duplicate phase identifiers are rejected atomically");
 await Deny<ValidationException>(()=>core.SavePhasePlanAsync(manager,id,Draft(plan,[a with{Id=plan.Phases[0].Id}])),"Default phase cannot be disguised as editable custom phase");
 plan=await core.SavePhasePlanAsync(manager,id,Draft(plan,[a,b],false));Check(plan.Phases.Length==4&&plan.Phases.Single(p=>p.Id==b.Id).MaxPicks==-1,"Custom phases store independent song limit and unlimited sentinel");
 await Deny<CoreConflictException>(()=>core.SavePhasePlanAsync(manager,id,new(plan.RowVersion-1,true,a.Id,[a,b])),"Stale whole-plan save cannot overwrite another manager");
 clock.Now=clock.Now.AddHours(1);Check(await core.EvaluateAutomaticPhasesAsync()==1,"Activation boundary is inclusive");plan=await core.ReadPhasePlanAsync(manager,id);Check(plan.Phases.Single(p=>p.IsActive).Id==a.Id,"First eligible custom phase becomes active");
 Check((await core.ReadAsync(member)).Events.Any(e=>e.Id==id&&e.ActivePhaseKind=="Custom"&&!e.CanManage),"Custom phase grants event visibility, never management or an implemented signup");
 Check(await core.EvaluateAutomaticPhasesAsync()==0,"Repeated evaluation is an audit/version no-op");
 clock.Now=clock.Now.AddHours(2);Check(await core.EvaluateAutomaticPhasesAsync()==1,"Overlapping schedules choose the latest activation");plan=await core.ReadPhasePlanAsync(manager,id);Check(plan.Phases.Single(p=>p.IsActive).Id==b.Id,"Newest-start overlap winner is deterministic");
 plan=await core.SavePhasePlanAsync(manager,id,Draft(plan,[a with{ActivateAt=b.ActivateAt},b],false));Check(plan.Phases.Single(p=>p.IsActive).Id==a.Id,"Equal activation times retain saved list-order precedence");
 plan=await core.SavePhasePlanAsync(manager,id,Draft(plan,[a,b],false));clock.Now=b.DeactivateAt!.Value;Check(await core.EvaluateAutomaticPhasesAsync()==1,"Deactivation boundary is exclusive");plan=await core.ReadPhasePlanAsync(manager,id);Check(plan.Phases.Single(p=>p.IsActive).Kind=="Hidden","Expired intervals do not remain auto-active");
 plan=await core.SavePhasePlanAsync(manager,id,Draft(plan,[a,b],true,a.Id));clock.Now=start.AddHours(-1);Check(await core.EvaluateAutomaticPhasesAsync()==0&&(await core.ReadPhasePlanAsync(manager,id)).Phases.Single(p=>p.IsActive).Id==a.Id,"Manual override persists beyond scheduled expiry and worker evaluation");
 await Deny<ValidationException>(()=>core.SavePhasePlanAsync(manager,id,Draft(plan,[b],true,a.Id)),"Selected active custom phase cannot be deleted");
 plan=await core.SavePhasePlanAsync(manager,id,Draft(plan,[a,b],false));Check(plan.Phases.Single(p=>p.IsActive).Kind=="Hidden","Resuming auto uses real clock and falls back hidden before event start");
 clock.Now=start;Check(await core.EvaluateAutomaticPhasesAsync()==2,"At event start all automatic events fall back to viewing");plan=await core.ReadPhasePlanAsync(manager,id);Check(plan.Phases.Single(p=>p.IsActive).Kind=="View","Start boundary viewing is inclusive");
 plan=await core.SavePhasePlanAsync(manager,id,Draft(plan,[b],true));await using(var db=await factory.CreateDbContextAsync())Check((await db.Phases.SingleAsync(p=>p.Id==a.Id)).DeletedAt is not null,"Removing inactive custom phase retains a historical tombstone");
 await Deny<ValidationException>(()=>core.SavePhasePlanAsync(manager,id,Draft(plan,[a,b])),"Deleted phase identity cannot be resurrected");
 var racePlan=plan;
 async Task<bool> Race(string name){try{await core.SavePhasePlanAsync(admin,id,Draft(racePlan,[b with{Name=name}]));return true;}catch(CoreConflictException){return false;}}
 var races=await Task.WhenAll(Task.Run(()=>Race("Race A")),Task.Run(()=>Race("Race B")));Check(races.Count(v=>v)==1,"Concurrent manager plans commit once without lost updates");plan=await core.ReadPhasePlanAsync(admin,id);
 var foreign=await core.ReadPhasePlanAsync(admin,other);var c=Custom("Foreign",0);foreign=await core.SavePhasePlanAsync(admin,other,Draft(foreign,[c],true,c.Id));
 await Deny<ValidationException>(()=>core.SavePhasePlanAsync(manager,id,Draft(plan,[b,c])),"Phase identifiers cannot be moved across events");
 await using(var db=await factory.CreateDbContextAsync()){
  await db.Database.ExecuteSqlRawAsync("INSERT INTO RevokedAuthSessions(TokenHash,UserId,RevokedAt) VALUES ({0},'manager','2026-11-01T00:00:00.000Z');",SemEndpoints.Hash("manager-session"));
 }
 await Deny<UnauthorizedAccessException>(()=>core.SavePhasePlanAsync(manager,id,Draft(plan,[b])),"Revoked connected-session identity cannot save a phase draft");
 plan=await core.ReadPhasePlanAsync(admin,id);await core.SaveEventAsync(admin,id,new("New title","","",start,null,"","Hidden",["manager"],plan.RowVersion));
 var changed=await core.ReadPhasePlanAsync(admin,id);Check(changed.ManualOverride&&changed.Phases.Single(p=>p.IsActive).Kind=="Hidden","Compatibility overview visibility switch updates actual default phase atomically");
 await using(var db=await factory.CreateDbContextAsync()){
  await SqliteStorageInitializer.InitializeAsync(db);Check(await db.Phases.CountAsync(p=>p.EventId==id&&p.IsActive)==1,"Restart retains exactly one active phase and all custom data");
  var bad=new EventPhase{EventId=id,Name="bad",IsActive=true};db.Phases.Add(bad);await Deny<DbUpdateException>(()=>db.SaveChangesAsync(),"Database unique index prevents a second active phase");
 }
 Console.WriteLine("Phase checks passed: "+passed);
}finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
sealed class Clock(DateTimeOffset now):TimeProvider{public DateTimeOffset Now=now;public override DateTimeOffset GetUtcNow()=>Now;}
