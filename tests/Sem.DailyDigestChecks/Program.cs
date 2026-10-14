using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;
var root=Path.Combine(Path.GetTempPath(),"sem-daily-"+Guid.NewGuid());Directory.CreateDirectory(root);
var services=new ServiceCollection();services.AddLogging();services.AddDataProtection().SetApplicationName("SEM.DailyChecks").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root,"keys")));
services.AddDbContextFactory<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+Path.Combine(root,"data.db")),ServiceLifetime.Scoped);
services.AddIdentityCore<ApplicationUser>(o=>o.Stores.SchemaVersion=IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
var clock=new Clock{Now=DateTimeOffset.Parse("2026-11-02T15:04:59.999Z")};services.AddSingleton<TimeProvider>(clock);services.AddSingleton<BusinessNotificationStore>();services.AddScoped<CoreFoundationService>();
using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();var core=scope.ServiceProvider.GetRequiredService<CoreFoundationService>();var store=provider.GetRequiredService<BusinessNotificationStore>();var fake=new Transport();int passed=0;
void Check(bool b,string label){if(!b)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
ClaimsPrincipal Who(string id)=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,id),new Claim("AspNet.Identity.SecurityStamp",id+"-stamp"),new Claim(PasskeyAdditionService.SessionClaim,id+"-session")],"Identity.Application"));
DailyDigestScheduler Scheduler()=>new(factory,clock);DailyDigestDispatcher Dispatcher()=>new(factory,store,fake,clock,NullLogger<DailyDigestDispatcher>.Instance);
async Task<DailyDigestMail[]> Queue(){await using var db=await factory.CreateDbContextAsync();return await db.DailyDigestMails.AsNoTracking().ToArrayAsync();}
async Task Change(string id,Action<ApplicationUser> change){await using var db=await factory.CreateDbContextAsync();change(await db.Users.SingleAsync(x=>x.Id==id));await db.SaveChangesAsync();}
string a="",b="";
async Task Seed(DateOnly day,int number=2){await using var db=await factory.CreateDbContextAsync();var actor=await db.Users.SingleAsync(x=>x.Id=="g1");for(int n=0;n<number;n++){var eventId=n%2==0?a:b;var e=await db.SessionEvents.SingleAsync(x=>x.Id==eventId);var p=await db.Phases.SingleAsync(x=>x.EventId==eventId&&x.IsActive);var id=Guid.NewGuid().ToString();var at=DigestPreviewBuilder.Bounds(day).Start.AddHours(1).AddMilliseconds(n);var kind=n%2==0?"Register":"Release";db.RegistrationOperations.Add(new(){Id=id,EventId=eventId,ActorId="g1",SubjectId="member",PhaseId=p.Id,Kind=kind,At=DigestPreviewBuilder.Format(at),Reason="reason",RequestHash="hash",ResultJson="{}"});store.Enqueue(db,id,e,actor,"member",kind,"reason",[new("slot-"+n,"song-"+n,"Frozen Song "+n,"Voice")],at);}await db.SaveChangesAsync();}
try{
 Check(DailyDigestPolicy.LastClosedDay(clock.Now)==new DateOnly(2026,11,1),"00:04:59.999 does not close the previous Japanese day early");clock.Now=clock.Now.AddMilliseconds(1);
 Check(DailyDigestPolicy.LastClosedDay(clock.Now)==new DateOnly(2026,11,2)&&DailyDigestPolicy.ScheduledAt(new(2026,11,2))==clock.Now,"00:05 exact boundary closes previous day");
 await using(var db=await factory.CreateDbContextAsync()){
  await db.GetService<IMigrator>().MigrateAsync("20261013080809_AddBusinessNotifications");
  foreach(var id in new[]{"g1","g2","ma","mb","multi","empty","disabled","unconfirmed","member"})db.Users.Add(new(){Id=id,UserName=id+"@digest.example.test",Email=id+"@digest.example.test",DisplayName=id+" current",Enabled=id!="disabled",EmailConfirmed=id!="unconfirmed",IsAdmin=id is "g1" or "g2" or "disabled" or "unconfirmed",SecurityStamp=id+"-stamp"});
  db.Deliveries.Add(new(){Id="auth",UserId="member",ProtectedBody="retained",Subject="auth",Due=1});await db.SaveChangesAsync();await SqliteStorageInitializer.InitializeAsync(db);
  Check(await db.Users.CountAsync()==9&&(await db.Deliveries.SingleAsync()).ProtectedBody=="retained","Populated PR17 migration retains identity and authentication mail");Check(!db.Database.HasPendingModelChanges(),"Daily digest migration matches EF model");
 }
 a=await core.SaveEventAsync(Who("g1"),null,new("Hidden A","","",clock.Now.AddDays(2),null,"","Hidden",["ma","multi"],0));b=await core.SaveEventAsync(Who("g1"),null,new("Hidden B","","",clock.Now.AddDays(2),null,"","Hidden",["mb","multi"],0));
 await Seed(new(2026,11,2));clock.Now=clock.Now.AddMilliseconds(-1);await Scheduler().Generate();Check((await Queue()).Length==0,"No queue before scheduled time");clock.Now=clock.Now.AddMilliseconds(1);
 await using(var db=await factory.CreateDbContextAsync())await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_daily BEFORE INSERT ON DailyDigestMails BEGIN SELECT RAISE(ABORT,'synthetic'); END");
 bool rejected=false;try{await Scheduler().Generate();}catch(DbUpdateException){rejected=true;}
 await using(var db=await factory.CreateDbContextAsync()){Check(rejected&&!await db.DailyDigestRuns.AnyAsync()&&!await db.DailyDigestMails.AnyAsync(),"Generation failure rolls back day marker and every recipient atomically");await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_daily");}
 await Scheduler().Generate();await Scheduler().Generate();var q=await Queue();Check(q.Length==5&&q.Select(x=>x.UserId).Order().SequenceEqual(new[]{"g1","g2","ma","mb","multi"}),"Exactly one daily mail per enabled verified authorized admin; no ordinary or zero-change recipients");Check(q.All(x=>x.Due==clock.Now.ToUnixTimeSeconds()&&x.Attempts==0),"Approved due time and immutable daily recipient uniqueness");
 await using(var db=await factory.CreateDbContextAsync()){db.EventAdmins.RemoveRange(await db.EventAdmins.Where(x=>x.UserId=="ma"||x.UserId=="multi"&&x.EventId==a).ToArrayAsync());db.EventAdmins.Add(new(){EventId=a,UserId="g2"});await db.SaveChangesAsync();}
 await Change("g2",u=>u.IsAdmin=false);await Change("g1",u=>u.Email="current@digest.example.test");
 await Task.WhenAll(Dispatcher().Dispatch(),Dispatcher().Dispatch());q=await Queue();Check(fake.Calls.Count==4&&q.Count(x=>x.Status=="Sent")==4&&q.Single(x=>x.UserId=="ma").Status=="Cancelled","Concurrent dispatchers claim once and revoked manager receives nothing");
 Check(fake.Calls.Single(x=>x.User=="g1").Email=="current@digest.example.test"&&fake.Calls.Single(x=>x.User=="g1").Body.Contains("Hidden A")&&fake.Calls.Single(x=>x.User=="g1").Body.Contains("Hidden B"),"Global digest combines all events and sends to current verified email");
 Check(fake.Calls.Single(x=>x.User=="g2").Body.Contains("Hidden A")&&!fake.Calls.Single(x=>x.User=="g2").Body.Contains("Hidden B"),"Demoted global admin sees only current assigned event");Check(fake.Calls.Where(x=>x.User is "multi" or "mb").All(x=>x.Body.Contains("Hidden B")&&!x.Body.Contains("Hidden A")),"Lost event permission removes hidden event details before send");Check(fake.Calls.All(x=>!x.Body.Contains("送信予約・実送信は行いません")&&x.Id.StartsWith("daily-digest:")),"Actual digest has distinct stable identity and no preview disclaimer");
 var calls=fake.Calls.Count;await Dispatcher().Dispatch();Check(fake.Calls.Count==calls,"Sent daily queue never replays");await using(var db=await factory.CreateDbContextAsync()){Check(await db.BusinessNotifications.AllAsync(x=>x.Status=="Pending"&&x.Attempts==0),"Daily dispatcher never consumes immediate business mail");Check((await db.Deliveries.SingleAsync()).ProtectedBody=="retained","Daily dispatcher never touches authentication outbox");}
 // Future day and retry scoping.
 await Seed(new(2026,11,3));clock.Now=DateTimeOffset.Parse("2026-11-03T15:05:00Z");await Scheduler().Generate();fake.Fail=true;await Dispatcher().Dispatch();q=(await Queue()).Where(x=>x.Day=="2026-11-03").ToArray();Check(q.All(x=>x.Status=="Pending"&&x.Attempts==1&&x.Due==clock.Now.ToUnixTimeSeconds()+30),"SMTP failure stores first retry at 30 seconds without losing day");calls=fake.Calls.Count;await Dispatcher().Dispatch();Check(fake.Calls.Count==calls,"Retry not sent before due time");
 await Change("g1",u=>u.Enabled=false);await Change("g2",u=>u.EmailConfirmed=false);fake.Fail=false;clock.Now=clock.Now.AddSeconds(31);await Dispatcher().Dispatch();q=(await Queue()).Where(x=>x.Day=="2026-11-03").ToArray();Check(q.Count(x=>x.Status=="Cancelled")==2&&q.Count(x=>x.Status=="Sent")==2,"Retry rechecks disabled and unconfirmed recipients");
 await Change("g1",u=>u.Enabled=true);await Change("g2",u=>u.EmailConfirmed=true);
 // Multiple missed recorded days: seven per scheduler tick, never silently truncated.
 for(int d=4;d<=12;d++)await Seed(new(2026,11,d));clock.Now=DateTimeOffset.Parse("2026-11-12T15:05:00Z");await Scheduler().Generate();await using(var db=await factory.CreateDbContextAsync())Check(await db.DailyDigestRuns.CountAsync()==9,"Recovery generates first seven missed recorded days");await Scheduler().Generate();await using(var db=await factory.CreateDbContextAsync())Check(await db.DailyDigestRuns.CountAsync()==11,"Subsequent tick catches all remaining days, not just most recent");
 // Work with one day to exercise restart/leases without backlog interference.
 await using(var db=await factory.CreateDbContextAsync()){await db.DailyDigestMails.Where(x=>x.Status=="Pending").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Cancelled"));var m=await db.DailyDigestMails.SingleAsync(x=>x.Day=="2026-11-12"&&x.UserId=="g1");m.Status="Sending";m.Attempts=1;m.Claim="abandoned";m.LeaseUntil=clock.Now.ToUnixTimeSeconds()+120;await db.SaveChangesAsync();}
 calls=fake.Calls.Count;await Dispatcher().Dispatch();Check(fake.Calls.Count==calls,"Unexpired lease prevents second send");clock.Now=clock.Now.AddSeconds(121);await Dispatcher().Dispatch();Check(fake.Calls.Count==calls+1&&(await Queue()).Single(x=>x.Day=="2026-11-12"&&x.UserId=="g1").Attempts==2,"Expired persisted lease recovered after restart");
 await using(var db=await factory.CreateDbContextAsync()){var m=await db.DailyDigestMails.SingleAsync(x=>x.Day=="2026-11-12"&&x.UserId=="g2");m.Status="Sending";m.Attempts=5;m.Claim="last";m.LeaseUntil=clock.Now.ToUnixTimeSeconds()-1;await db.SaveChangesAsync();}await Dispatcher().Dispatch();Check((await Queue()).Single(x=>x.Day=="2026-11-12"&&x.UserId=="g2").Status=="Failed","Abandoned fifth attempt terminates instead of being stuck forever");
 await Seed(new(2026,11,13));clock.Now=DateTimeOffset.Parse("2026-11-13T15:05:00Z");await Scheduler().Generate();fake.Fail=true;for(int n=0;n<5;n++){await Dispatcher().Dispatch();clock.Now=clock.Now.AddMinutes(10);}q=(await Queue()).Where(x=>x.Day=="2026-11-13").ToArray();Check(q.All(x=>x.Status=="Failed"&&x.Attempts==5&&x.SentAt==null),"Fifth SMTP failure is terminal, not false success");calls=fake.Calls.Count;await Dispatcher().Dispatch();Check(fake.Calls.Count==calls,"Terminal SMTP failures do not retry indefinitely");
 fake.Fail=false;await Seed(new(2026,11,14),1002);clock.Now=DateTimeOffset.Parse("2026-11-14T15:05:00Z");await Scheduler().Generate();await Dispatcher().Dispatch();var large=fake.Calls.Single(x=>x.User=="g1"&&x.Id=="daily-digest:"+DailyDigestPolicy.MailId("2026-11-14","g1"));Check(large.Body.Contains("Frozen Song 1001")&&large.Body.Contains("Frozen Song 0")&&(await Queue()).Count(x=>x.Day=="2026-11-14"&&x.Status=="Sent")==4,"Automatic one-message digest includes more than preview 1000-row cap without silent truncation");
 await using(var db=await factory.CreateDbContextAsync()){Check((await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").ToListAsync()).Single()=="ok"&&(await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").ToListAsync()).Single()==0,"Daily migration queue and history preserve SQLite integrity and foreign keys");Check(await db.DailyDigestMails.Select(x=>new{x.Day,x.UserId}).Distinct().CountAsync()==await db.DailyDigestMails.CountAsync(),"All catch-up days retain unique day-recipient identity");}
 // Two schedulers contend for the same new day; transient SQLite conflicts are retried by the worker.
 await Seed(new(2026,11,15));clock.Now=DateTimeOffset.Parse("2026-11-15T15:05:00Z");
 async Task ConcurrentGenerate(){try{await Scheduler().Generate();}catch(DbUpdateException){}catch(Microsoft.Data.Sqlite.SqliteException ex)when(ex.SqliteErrorCode is 5 or 6 or 19){}}
 await Task.WhenAll(ConcurrentGenerate(),ConcurrentGenerate());await Scheduler().Generate();
 Check((await Queue()).Count(x=>x.Day=="2026-11-15")==4,"Concurrent schedulers and retry produce one row per day-recipient");
 if(args.Contains("--smtp")){
  var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Smtp:Host","127.0.0.1"},{"Smtp:Port","2525"},{"Smtp:From","fixture@example.test"},{"Smtp:Tls","None"},{"Smtp:AllowInsecureLocalTest","true"}}).Build();
  var smtp=new BusinessEmailTransport(new NotificationService(cfg,provider.GetRequiredService<IDataProtectionProvider>()));
  await new DailyDigestDispatcher(factory,store,smtp,clock,NullLogger<DailyDigestDispatcher>.Instance).Dispatch();
  using var http=new HttpClient();using var doc=JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:18025/"));
  var messages=doc.RootElement.EnumerateArray().Where(x=>x.GetProperty("subject").GetString()!.Contains("2026-11-15")).ToArray();
  Check(messages.Length==4&&(await Queue()).Where(x=>x.Day=="2026-11-15").All(x=>x.Status=="Sent"),"Production transport delivers exactly four accepted scoped daily emails to isolated SMTP");
  Check(messages.All(x=>x.GetProperty("from").GetString()=="fixture@example.test"&&x.GetProperty("message_id").GetString()!.Contains("@notifications.sem.invalid")),"Real SMTP retains configured sender and stable Message-ID");
  var global=messages.Single(x=>x.GetProperty("to").GetString()!.Contains("current@digest.example.test"));var eventB=messages.Where(x=>x.GetProperty("to").GetString()!.Contains("mb@")||x.GetProperty("to").GetString()!.Contains("multi@"));
  Check(global.GetProperty("body").GetString()!.Contains("Hidden A")&&global.GetProperty("body").GetString()!.Contains("Hidden B")&&eventB.All(x=>!x.GetProperty("body").GetString()!.Contains("Hidden A")),"Real received MIME bodies combine global events but never leak unassigned event to scoped recipients");
  await new DailyDigestDispatcher(factory,store,smtp,clock,NullLogger<DailyDigestDispatcher>.Instance).Dispatch();using var again=JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:18025/"));
  Check(again.RootElement.EnumerateArray().Count(x=>x.GetProperty("subject").GetString()!.Contains("2026-11-15"))==4,"Real SMTP replay of sent queue produces no duplicate");
 }
 Console.WriteLine($"Daily digest checks passed: {passed}");
}finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
sealed class Clock:TimeProvider{public DateTimeOffset Now;public override DateTimeOffset GetUtcNow()=>Now;}
sealed class Transport:IBusinessEmailTransport{public bool Fail;public readonly System.Collections.Concurrent.ConcurrentBag<(string Id,string User,string Email,string Body)>Calls=[];public Task Send(string id,ApplicationUser user,BusinessEmailPayload payload,CancellationToken ct){Calls.Add((id,user.Id,user.Email!,payload.Body));if(Fail)throw new IOException("synthetic");return Task.CompletedTask;}}
