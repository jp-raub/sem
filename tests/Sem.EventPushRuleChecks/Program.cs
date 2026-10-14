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
var root=Path.Combine(Path.GetTempPath(),"sem-event-rule-"+Guid.NewGuid());Directory.CreateDirectory(root);
var clock=new Clock();var services=new ServiceCollection();services.AddLogging();services.AddDataProtection();services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+Path.Combine(root,"data.db")),ServiceLifetime.Scoped);services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();services.AddSingleton<TimeProvider>(clock);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();int passed=0;
void Check(bool b,string name){if(!b)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
async Task Deny<T>(Func<Task> f,string name)where T:Exception{bool denied=false;try{await f();}catch(T){denied=true;}Check(denied,name);}
ClaimsPrincipal Who(string id)=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,id),new Claim("AspNet.Identity.SecurityStamp",id+"-stamp"),new Claim(PasskeyAdditionService.SessionClaim,id+"-session")],"Identity.Application"));
async Task<int> AuditCount(){await using var db=await factory.CreateDbContextAsync();return await db.Audits.CountAsync();}
try{
 var now=clock.Now;var at=now.AddHours(1);var start=now.AddHours(3);
 var entry=new NotificationPlanEntry("opening","OpeningReminder",null,"Opening","EventStartMinus120Minutes",null,start,at,"Planned");
 EventPushRule Rule(string kind="OpeningReminder")=>new(){Kind=kind,Enabled=true,Version=1,ArmedAt=now.ToUnixTimeMilliseconds(),ArmedProgressVersion=0};
 RuleEligibility Eval(NotificationPlanEntry item,EventPushRule? rule,DateTimeOffset? time=null,long progress=0,ProgressRow[]?rows=null)=>EventPushRulePolicy.Evaluate(item,rule,progress,rows??[],time??now);
 Check(Eval(entry,null).State=="Disabled","Absent rule defaults disabled, never activates a dormant plan");
 Check(Eval(entry,Rule()).State=="Planned"&&Eval(entry,Rule()).ExpiresAt==start,"Opening expiry is reference time, not a fixed arrival promise");
 Check(Eval(entry,Rule(),at.AddMilliseconds(-1)).State=="Planned"&&Eval(entry,Rule(),at).State=="Eligible","Due threshold keeps millisecond half-open boundary");
 Check(Eval(entry,Rule(),start).State=="Expired","Opening never eligible at/after opening or event reference");
 Check(Eval(entry with{DueAt=now},Rule()).State=="BeforeActivation","Exact activation-time due is suppressed, not historical send");
 Check(Eval(entry with{DueAt=now.AddMilliseconds(-1)},Rule()).State=="BeforeActivation","Existing dormant Due before activation excluded");
 Check(Eval(entry with{State="Obsolete"},Rule()).State=="TargetStarted","Already-started target suppressed even with valid due/expiry");
 Check(Eval(entry with{DueAt=null,ReferenceAt=null,State="WaitingActual"},Rule()).State=="WaitingActual","No previous actual yields no guessed scheduling instant");
 Check(Eval(entry,new EventPushRule{Kind="OpeningReminder",Enabled=true,Version=1}).State=="InvalidRule","Enabled rule without persisted activation bound fails closed");
 Check(Eval(entry with{State="invented"},Rule()).State=="InvalidSource","Unknown source state cannot become send eligible");
 Check(Eval(entry with{ReferenceAt=null},Rule()).State=="InvalidExpiry","Missing expiry basis fails closed");
 var scheduled=new TimedPart("song","Song","Song","","🎵","song",now.AddMinutes(3),now.AddMinutes(4),now.AddMinutes(6),120,null);
 var row=new ProgressRow(scheduled,scheduled,[],"未開始",null);
 var prep=new NotificationPlanEntry("song:song","SongPreparation","song","Song","PreviousPartActualStart","talk",now.AddSeconds(1),now.AddSeconds(1),"Due");
 Check(Eval(prep,Rule("SongPreparation"),now.AddSeconds(2),1,[row]).ExpiresAt==scheduled.PrepStart,"Song expiry uses earlier latest preparation start when under10min");
 var later=row with{Latest=scheduled with{PrepStart=now.AddHours(1)}};
 Check(Eval(prep,Rule("SongPreparation"),now.AddSeconds(2),1,[later]).ExpiresAt==prep.DueAt!.Value.AddMinutes(10),"Song expiry never exceeds due+10min even for later preparation");
 Check(Eval(prep,Rule("SongPreparation"),now.AddSeconds(2),0,[row]).State=="BeforeActivation","Actual-version watermark rejects records already present when rule was enabled");
 Check(Eval(prep,Rule("SongPreparation"),now.AddSeconds(2),1,[row]).State=="Eligible","New previous actual after arming can be eligible before expiry");
 Check(Eval(prep,Rule("SongPreparation"),scheduled.PrepStart,1,[row]).State=="Expired","Preparation-start expiry boundary is exclusive");
 Check(Eval(prep,Rule("SongPreparation"),now.AddSeconds(2),1).State=="InvalidExpiry","Missing target row does not invent expiration");
 Check(Eval(prep,Rule(),now.AddSeconds(2),1,[row]).State=="InvalidRule","Rule kind cannot be reused for another notification kind");
 await Deny<JsonException>(()=>Task.FromResult(JsonSerializer.Deserialize<EventPushRuleEdit>("{\"kind\":\"OpeningReminder\",\"version\":0,\"eventVersion\":0}",new JsonSerializerOptions(JsonSerializerDefaults.Web))),"Enabled must be explicitly supplied");
 await using(var db=await factory.CreateDbContextAsync()){
  await db.GetService<IMigrator>().MigrateAsync("20261013130605_AddPartStartPreferences");
  foreach(var uid in new[]{"admin","manager","member","other"})db.Users.Add(new(){Id=uid,Email=uid+"@example.test",UserName=uid+"@example.test",DisplayName=uid,Enabled=true,EmailConfirmed=true,IsAdmin=uid=="admin",SecurityStamp=uid+"-stamp"});
  db.Deliveries.Add(new(){Id="auth-old",UserId="member",Subject="auth",ProtectedBody="protected",Due=1});
  var seed=new SessionEvent{Id=Guid.NewGuid().ToString(),Title="Old preference owner",ScheduledDate=start.ToString("O"),PhaseKind="View",ManualPhaseOverride=true};seed.Phases.Add(new(){EventId=seed.Id,Kind="Hidden",Name="Hidden",SortOrder=0});seed.Phases.Add(new(){EventId=seed.Id,Kind="View",Name="View",SortOrder=1,IsActive=true});await LegacySessionEventSeed.AddAsync(db,seed);await db.SaveChangesAsync();await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO PartStartPreferences(EventId,UserId,PartId,Enabled,Version,ChangedAt) VALUES ({seed.Id},{"member"},{"opening"},1,1,{now.ToUnixTimeMilliseconds()})");await db.Database.MigrateAsync();
  Check(await db.Users.CountAsync()==4&&(await db.Deliveries.SingleAsync()).ProtectedBody=="protected"&&(await db.Set<PartStartPreference>().SingleAsync()).Enabled,"Populated PR21 upgrade retains identities, auth outbox and personal selection");
  Check(!db.Database.HasPendingModelChanges(),"Rule migration snapshot matches live EF model");
 }
 var id=await core.SaveEventAsync(Who("admin"),null,new("Arming target","","",start,null,"","Hidden",["manager"],0));
 var audit=await AuditCount();var view=await core.ReadEventPushRulesAsync(Who("manager"),id);
 Check(view.Rules.Length==2&&view.Rules.All(x=>!x.Enabled&&x.Version==0)&&view.DeliveryConnected&&!view.PushConfigured,"Manager sees exactly approved two rules OFF; delivery connected but keys unset");
 Check(await AuditCount()==audit,"Reading default rule decisions never writes audit");
 await core.SaveEventPushRuleAsync(Who("manager"),id,new("OpeningReminder",false,0,view.EventVersion));Check(await AuditCount()==audit,"Absent OFF save is no-op");
 await core.SaveEventPushRuleAsync(Who("manager"),id,new("OpeningReminder",true,0,view.EventVersion));view=await core.ReadEventPushRulesAsync(Who("manager"),id);var enabled=view.Rules.Single(x=>x.Kind=="OpeningReminder");
 Check(enabled.Enabled&&enabled.Version==1&&enabled.ArmedAt==now&&enabled.ArmedProgressVersion==0&&await AuditCount()==audit+1,"Arming stores one exact clock/progress watermark and atomic audit");
 clock.Now=now.AddMinutes(1);await core.SaveEventPushRuleAsync(Who("manager"),id,new("OpeningReminder",true,1,view.EventVersion));Check((await core.ReadEventPushRulesAsync(Who("manager"),id)).Rules[0].ArmedAt==now&&await AuditCount()==audit+1,"Repeated ON does not advance activation watermark or duplicate audit");
 await Deny<CoreConflictException>(()=>core.SaveEventPushRuleAsync(Who("manager"),id,new("OpeningReminder",false,0,view.EventVersion)),"Stale tab cannot override enabled rule");
 await Deny<ValidationException>(()=>core.SaveEventPushRuleAsync(Who("manager"),id,new("PartStart",true,0,view.EventVersion)),"Personal start notifications are not silently added to manager automatic rules");
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventPushRulesAsync(Who("member"),id),"Member cannot probe manager arming data");
 await Deny<UnauthorizedAccessException>(()=>core.SaveEventPushRuleAsync(Who("other"),id,new("OpeningReminder",true,1,view.EventVersion)),"Unassigned user cannot enable event notification rules");
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventPushRulesAsync(new ClaimsPrincipal(),id),"Anonymous rule read denied");
 await Deny<UnauthorizedAccessException>(()=>core.ReadEventPushRulesAsync(Who("admin"),"missing"),"Missing event rule configuration denied");
 clock.Now=at;Check((await core.ReadEventPushRulesAsync(Who("manager"),id)).Entries.Single().State=="Eligible","Manager read shows eligibility only, not sent status");
 clock.Now=at.AddMinutes(1);await core.SaveEventPushRuleAsync(Who("manager"),id,new("OpeningReminder",false,1,view.EventVersion));view=await core.ReadEventPushRulesAsync(Who("manager"),id);
 Check(!view.Rules[0].Enabled&&view.Rules[0].Version==2&&view.Rules[0].ArmedAt is null&&view.Entries.Single().State=="Disabled","OFF clears arming bounds but retains generation");
 await core.SaveEventPushRuleAsync(Who("manager"),id,new("OpeningReminder",true,2,view.EventVersion));view=await core.ReadEventPushRulesAsync(Who("manager"),id);Check(view.Rules[0].Version==3&&view.Entries.Single().State=="BeforeActivation","Re-enable excludes due occurrence from previous activation; no historical burst");
 using(var fresh=provider.CreateScope())Check((await fresh.ServiceProvider.GetRequiredService<CoreFoundationService>().ReadEventPushRulesAsync(Who("manager"),id)).Rules[0].Version==3,"Restart/new context keeps exact arming generation");
 await using(var db=await factory.CreateDbContextAsync())await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_rule_audit BEFORE INSERT ON Audits WHEN NEW.Action='EventPushRuleChanged' BEGIN SELECT RAISE(ABORT,'synthetic'); END");
 audit=await AuditCount();await Deny<DbUpdateException>(()=>core.SaveEventPushRuleAsync(Who("manager"),id,new("OpeningReminder",false,3,view.EventVersion)),"Injected audit failure aborts rule update");Check((await core.ReadEventPushRulesAsync(Who("manager"),id)).Rules[0].Version==3&&await AuditCount()==audit,"Failed arming update rolls back state and audit together");
 await using(var db=await factory.CreateDbContextAsync()){await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_rule_audit");db.RevokedAuthSessions.Add(new(){TokenHash=SemEndpoints.Hash("manager-session"),UserId="manager",RevokedAt=clock.Now.ToString("O")});await db.SaveChangesAsync();}
 await Deny<UnauthorizedAccessException>(()=>core.SaveEventPushRuleAsync(Who("manager"),id,new("OpeningReminder",false,3,view.EventVersion)),"Revoked manager session cannot change rules");
 await using(var db=await factory.CreateDbContextAsync()){
  Check(await db.PushDevices.CountAsync()==0&&await db.Messages.CountAsync()==0&&await db.BusinessNotifications.CountAsync()==0&&await db.DailyDigestMails.CountAsync()==0&&await db.Deliveries.CountAsync()==1,"Rule arming never writes recipients, device data, messages or transport queues");
  Check((await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").ToListAsync()).Single()=="ok"&&(await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").ToListAsync()).Single()==0,"Rule persistence retains SQLite and foreign-key integrity");
 }
 Console.WriteLine($"Event push rule checks passed: {passed}");
}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.Parse("2026-11-02T03:00:00.123Z");public override DateTimeOffset GetUtcNow()=>Now;}
