using System.ComponentModel.DataAnnotations;
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
var root=Path.Combine(Path.GetTempPath(),"sem-start-preference-"+Guid.NewGuid());Directory.CreateDirectory(root);
var clock=new Clock();var services=new ServiceCollection();services.AddLogging();services.AddDataProtection();services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+Path.Combine(root,"data.db")),ServiceLifetime.Scoped);services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();services.AddSingleton<TimeProvider>(clock);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();int passed=0;
void Check(bool b,string name){if(!b)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
async Task Deny<T>(Func<Task> f,string name)where T:Exception{bool denied=false;try{await f();}catch(T){denied=true;}Check(denied,name);}
ClaimsPrincipal Who(string id)=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,id),new Claim("AspNet.Identity.SecurityStamp",id+"-stamp"),new Claim(PasskeyAdditionService.SessionClaim,id+"-session")],"Identity.Application"));
async Task<int> AuditCount(){await using var db=await factory.CreateDbContextAsync();return await db.Audits.CountAsync();}
try{
 await Deny<JsonException>(()=>Task.FromResult(JsonSerializer.Deserialize<PartStartPreferenceEdit>("{\"partId\":\"x\",\"version\":0,\"eventVersion\":0}",new JsonSerializerOptions(JsonSerializerDefaults.Web))),"Missing Enabled is not silently treated as OFF");
 await Deny<JsonException>(()=>Task.FromResult(JsonSerializer.Deserialize<PartStartPreferenceEdit>("{\"partId\":\"x\",\"enabled\":true,\"version\":0,\"eventVersion\":0,\"userId\":\"other\"}",new JsonSerializerOptions(JsonSerializerDefaults.Web))),"Subject injection forbidden; server derives actor from current session");
 await using(var db=await factory.CreateDbContextAsync()){
  await db.GetService<IMigrator>().MigrateAsync("20261013115902_AddNotificationReservations");
  foreach(var uid in new[]{"admin","member","other"})db.Users.Add(new(){Id=uid,Email=uid+"@example.test",UserName=uid+"@example.test",DisplayName=uid,Enabled=true,EmailConfirmed=true,IsAdmin=uid=="admin",SecurityStamp=uid+"-stamp"});
  db.Deliveries.Add(new(){Id="preserved-mail",UserId="member",Subject="auth",ProtectedBody="protected",Due=1});await db.SaveChangesAsync();await db.Database.MigrateAsync();
  Check(await db.Users.CountAsync()==3&&(await db.Deliveries.SingleAsync()).ProtectedBody=="protected","Populated PR20 migration preserves identities and protected auth outbox");
  Check(!db.Database.HasPendingModelChanges(),"Migration and model snapshot match");
 }
 var id=await core.SaveEventAsync(Who("admin"),null,new("Personal preference event","","",clock.Now.AddHours(3),clock.Now.AddHours(2),"","View",[],0));
 var defs=await core.ReadDefinitionsAsync(Who("admin"),id);var voice=Guid.NewGuid().ToString();await core.SaveDefinitionsAsync(Who("admin"),id,new(defs.RowVersion,[new(voice,"Voice","Sing")]));
 var songs=await core.ReadSongsAsync(Who("admin"),id);var song=Guid.NewGuid().ToString();await core.SaveSongsAsync(Who("admin"),id,new(songs.RowVersion,30,[new(song,"Optional song","",120,null,[voice])]));
 var table=await core.ReadTimetableAsync(Who("admin"),id);var talk=Guid.NewGuid().ToString();await core.SaveTimetableAsync(Who("admin"),id,new(table.RowVersion,table.RoundStarts,table.ClockFormat,table.DurationFormat,table.SongIcon,null,[..table.Parts,new(talk,"Transition",null,"Talk",30)]));
 var audit=await AuditCount();var view=await core.ReadPartStartPreferencesAsync(Who("member"),id);
 Check(view.Parts.Length==3&&view.Parts.Select(x=>x.Kind).ToHashSet().SetEquals(["Opening","Song","Transition"]),"Member can select any opening/song/transition without a performance assignment");
 Check(view.Parts.All(x=>!x.Enabled&&x.Version==0)&&!view.DeliveryConnected,"All choices default OFF and truthfully show disconnected delivery");
 await using(var db=await factory.CreateDbContextAsync())Check(await db.Set<PartStartPreference>().CountAsync()==0&&await AuditCount()==audit,"Read of defaults writes no preference or audit");
 await core.SavePartStartPreferenceAsync(Who("member"),id,new(talk,false,0,view.EventVersion));Check(await AuditCount()==audit,"Absent OFF no-op does not create audit or generation");
 await core.SavePartStartPreferenceAsync(Who("member"),id,new(talk,true,0,view.EventVersion));var enabled=(await core.ReadPartStartPreferencesAsync(Who("member"),id)).Parts.Single(x=>x.PartId==talk);
 Check(enabled.Enabled&&enabled.Version==1&&await AuditCount()==audit+1,"Own ON persists with one atomic audit and version");
 Check(!(await core.ReadPartStartPreferencesAsync(Who("other"),id)).Parts.Single(x=>x.PartId==talk).Enabled&&!(await core.ReadPartStartPreferencesAsync(Who("admin"),id)).Parts.Single(x=>x.PartId==talk).Enabled,"Other member and global admin read only their own independent settings");
 await core.SavePartStartPreferenceAsync(Who("member"),id,new(talk,true,1,view.EventVersion));Check(await AuditCount()==audit+1,"Identical ON is no-op");
 await Deny<CoreConflictException>(()=>core.SavePartStartPreferenceAsync(Who("member"),id,new(talk,false,0,view.EventVersion)),"Stale second tab cannot overwrite ON");
 await Deny<ValidationException>(()=>core.SavePartStartPreferenceAsync(Who("member"),id,new("missing",true,0,view.EventVersion)),"Unknown part refused without fabricated preference");
 await Deny<ValidationException>(()=>core.SavePartStartPreferenceAsync(Who("member"),id,new(talk,false,-1,view.EventVersion)),"Negative version refused");
 await Deny<UnauthorizedAccessException>(()=>core.ReadPartStartPreferencesAsync(new ClaimsPrincipal(),id),"Anonymous settings read denied");
 await Deny<UnauthorizedAccessException>(()=>core.SavePartStartPreferenceAsync(new ClaimsPrincipal(),id,new(talk,true,0,view.EventVersion)),"Anonymous settings write denied");
 await Deny<UnauthorizedAccessException>(()=>core.ReadPartStartPreferencesAsync(Who("member"),"missing"),"Missing event denied without disclosure");
 // Persistence survives a new service/context, and OFF retains its version against ABA writes.
 using(var next=provider.CreateScope())Check((await next.ServiceProvider.GetRequiredService<CoreFoundationService>().ReadPartStartPreferencesAsync(Who("member"),id)).Parts.Single(x=>x.PartId==talk).Enabled,"Restart/new scoped service keeps personal selection");
 await core.SavePartStartPreferenceAsync(Who("member"),id,new(talk,false,1,view.EventVersion));var off=(await core.ReadPartStartPreferencesAsync(Who("member"),id)).Parts.Single(x=>x.PartId==talk);
 Check(!off.Enabled&&off.Version==2,"OFF retained as versioned row rather than deletion/ABA reset");
 await Deny<CoreConflictException>(()=>core.SavePartStartPreferenceAsync(Who("member"),id,new(talk,true,0,view.EventVersion)),"Old absent-row editor cannot revive OFF preference");
 var source=(await core.ReadAsync(Who("admin"))).Events.Single(x=>x.Id==id);await core.SaveEventAsync(Who("admin"),id,new(source.Title,source.Description,source.Location,TimetableCalculator.Instant(source.ScheduledDate),TimetableCalculator.Instant(source.OpensAt!),"Edited","View",[],source.RowVersion));
 await Deny<CoreConflictException>(()=>core.SavePartStartPreferenceAsync(Who("member"),id,new(talk,true,2,view.EventVersion)),"Changed event version requires refreshed part list");
 view=await core.ReadPartStartPreferencesAsync(Who("member"),id);await core.SavePartStartPreferenceAsync(Who("member"),id,new(talk,true,2,view.EventVersion));
 // A failed audit insert must roll back the preference change too.
 await using(var db=await factory.CreateDbContextAsync())await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_preference_audit BEFORE INSERT ON Audits WHEN NEW.Action='PartStartPreferenceChanged' BEGIN SELECT RAISE(ABORT,'synthetic'); END");
 audit=await AuditCount();await Deny<DbUpdateException>(()=>core.SavePartStartPreferenceAsync(Who("member"),id,new(talk,false,3,view.EventVersion)),"Injected audit failure aborts preference save");
 Check((await core.ReadPartStartPreferencesAsync(Who("member"),id)).Parts.Single(x=>x.PartId==talk).Version==3&&await AuditCount()==audit,"Preference and audit rolled back together");
 await using(var db=await factory.CreateDbContextAsync())await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_preference_audit");
 table=await core.ReadTimetableAsync(Who("admin"),id);await core.SaveTimetableAsync(Who("admin"),id,new(table.RowVersion,table.RoundStarts,table.ClockFormat,table.DurationFormat,table.SongIcon,null,table.Parts.Where(x=>x.Id!=talk).ToArray()));
 view=await core.ReadPartStartPreferencesAsync(Who("member"),id);Check(!view.Parts.Any(x=>x.PartId==talk),"Removed part omitted even when saved preference is ON");
 await Deny<ValidationException>(()=>core.SavePartStartPreferenceAsync(Who("member"),id,new(talk,false,3,view.EventVersion)),"Deleted part preference cannot be reselected or resurrect part");
 source=(await core.ReadAsync(Who("admin"))).Events.Single(x=>x.Id==id);await core.SaveEventAsync(Who("admin"),id,new(source.Title,source.Description,source.Location,TimetableCalculator.Instant(source.ScheduledDate),TimetableCalculator.Instant(source.OpensAt!),"","Hidden",[],source.RowVersion));
 await Deny<UnauthorizedAccessException>(()=>core.ReadPartStartPreferencesAsync(Who("member"),id),"Hidden event revokes personal settings read despite saved opt-in");
 await Deny<UnauthorizedAccessException>(()=>core.SavePartStartPreferenceAsync(Who("member"),id,new(song,true,0,view.EventVersion)),"Hidden event refuses write before using stale event version");
 source=(await core.ReadAsync(Who("admin"))).Events.Single(x=>x.Id==id);await core.SaveEventAsync(Who("admin"),id,new(source.Title,source.Description,source.Location,TimetableCalculator.Instant(source.ScheduledDate),TimetableCalculator.Instant(source.OpensAt!),"","View",[],source.RowVersion));
 await using(var db=await factory.CreateDbContextAsync()){db.RevokedAuthSessions.Add(new(){TokenHash=SemEndpoints.Hash("member-session"),UserId="member",RevokedAt=clock.Now.ToString("O")});await db.SaveChangesAsync();}
 await Deny<UnauthorizedAccessException>(()=>core.ReadPartStartPreferencesAsync(Who("member"),id),"Revoked authenticated session cannot read opt-ins");
 await Deny<UnauthorizedAccessException>(()=>core.SavePartStartPreferenceAsync(Who("member"),id,new(song,true,0,0)),"Revoked session cannot edit opt-ins");
 await using(var db=await factory.CreateDbContextAsync()){
  Check(await db.PushDevices.CountAsync()==0&&await db.Messages.CountAsync()==0&&await db.BusinessNotifications.CountAsync()==0&&await db.DailyDigestMails.CountAsync()==0&&await db.Deliveries.CountAsync()==1,"Personal settings never send or modify transport/permission/user assignment data");
  Check((await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").ToListAsync()).Single()=="ok"&&(await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").ToListAsync()).Single()==0,"SQLite and foreign-key integrity preserved");
 }
 Console.WriteLine($"Part start preference checks passed: {passed}");
}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.Parse("2026-11-02T03:00:00.123Z");public override DateTimeOffset GetUtcNow()=>Now;}
