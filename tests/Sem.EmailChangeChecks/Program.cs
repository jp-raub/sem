using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-email-change-" + Guid.NewGuid()); Directory.CreateDirectory(root);
var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); count++; }
async Task Deny(Func<Task> work, string name) { try { await work(); throw new Exception("FAIL accepted " + name); } catch (Exception e) when (e is ValidationException or UnauthorizedAccessException or EmailChangeRateLimitException) { Check(true, name); } }
try
{
    await Run(async f => {
        var id = await f.Begin(); await f.Verify(id);
        var row = await f.Db.EmailChangeOperations.SingleAsync(); var mail = await f.Mail("Verification");
        Check(row.TokenHash == SemEndpoints.Hash(mail.Token) && !row.ProtectedTargetEmail.Contains("next@"), "only hash and protected target stored");
        Check(f.User.Email == "old@example.test" && row.ExpiresAt == f.Clock.Now + 900, "new email not applied until confirmation; first issue15min");
        Check(!JsonSerializer.Serialize(await f.Db.Audits.ToListAsync()).Contains(mail.Token), "audit excludes bearer token");
        await f.Service.Confirm(id, mail.Token);
        Check(f.User.Email == "next@example.test" && f.User.UserName == "next@example.test" && f.User.EmailConfirmed, "anonymous purpose token changes email and username only");
        Check(await f.Db.RevokedAuthSessions.AnyAsync(x => x.TokenHash == SemEndpoints.Hash("session")), "initiating session revoked; no new session dependency");
        var notice = await f.Mail("OldAddressNotice"); Check(notice.Payload.Recipient == "old@example.test", "notice recipient snapshots old address atomically");
        await Deny(() => f.Service.Confirm(id, mail.Token), "confirmation is one use");
    });
    await Run(async f => { var id = await f.Begin(); await f.Verify(id); var old = await f.Mail("Verification"); var expiry = (await f.Db.EmailChangeOperations.SingleAsync()).ExpiresAt;
        f.Clock.Advance(60); await f.Service.Options(f.Principal, id); await f.Verify(id); var current = await f.Mail("Verification");
        Check(old.Token != current.Token && (await f.Db.EmailChangeOperations.SingleAsync()).ExpiresAt == expiry, "resend rotates token without extending original deadline");
        await Deny(() => f.Service.Confirm(id, old.Token), "superseded token rejected"); await f.Service.Confirm(id, current.Token);
    });
    await Run(async f => { var id = await f.Begin(); await f.Verify(id); var mail = await f.Mail("Verification"); f.Clock.Advance(900); await Deny(() => f.Service.Confirm(id, mail.Token), "expiry exclusive exactly15min"); Check(f.User.Email == "old@example.test", "expired proof leaves email unchanged"); });
    await Run(async f => { await f.Begin(); await Deny(() => f.Service.Begin(f.Principal, "other@example.test"), "pending target cannot be replaced"); });
    await Run(async f => { var id = await f.Begin(); await f.Service.Cancel(f.Principal,id); await Deny(() => f.Verify(id), "cancel clears proof capability"); var next = await f.Begin(); Check(next != id, "cancel permits new independent operation id"); });
    await Run(async f => { var id = await f.Begin(); f.Clock.Advance(300); await Deny(() => f.Verify(id), "initial challenge expires at5min"); });
    await Run(async f => { var id = await f.Begin(); await f.Verify(id); await Deny(() => f.Verify(id), "used assertion challenge cannot be replayed"); });
    await Run(async f => { var id = await f.Begin(); f.Proof.Succeeds=false;
        for(var i=0;i<5;i++){if(i>0)await f.Service.Options(f.Principal,id);await Deny(()=>f.Verify(id),"passkey failure counted"+i);}
        await Deny(()=>f.Service.Options(f.Principal,id),"five failures throttle account purpose");
        await f.Service.Cancel(f.Principal,id); await Deny(()=>f.Begin(),"operation recreation cannot clear passkey failure window");
        Check(f.User.AccessFailedCount==0 && f.User.LockoutEnd is null,"flow failures never lock normal login");
        f.Clock.Advance(900); await f.Begin(); Check(true,"failure window ages out byrolling15min"); });
    await Run(async f => { var id=await f.Begin();await f.Verify(id);for(var i=0;i<5;i++)await Deny(()=>f.Service.Confirm(id,"wrong-token"),"verification failure"+i);
        Check((await f.Db.EmailChangeOperations.SingleAsync()).Status=="Invalidated","fifth verification failure invalidates operation");
        await Deny(()=>f.Begin(),"verification throttle survives recreation"); });
    await Run(async f => { var id=await f.Begin();await f.Verify(id);f.User.SecurityStamp="changed";await f.Db.SaveChangesAsync();var mail=await f.Mail("Verification");await Deny(()=>f.Service.Confirm(id,mail.Token),"changed security stamp invalidates confirmation"); });
    await Run(async f => { var id=await f.Begin();await f.Verify(id);f.User.Enabled=false;await f.Db.SaveChangesAsync();var mail=await f.Mail("Verification");await Deny(()=>f.Service.Confirm(id,mail.Token),"disabled user cannot confirm"); });
    await Run(async f => { var id=await f.Begin();await f.Verify(id);await f.Service.Options(f.Principal,id);await Deny(()=>f.Verify(id),"resend minimum60seconds");
        f.Clock.Advance(60);await f.Service.Options(f.Principal,id);await f.Verify(id);f.Clock.Advance(60);await f.Service.Options(f.Principal,id);await f.Verify(id);
        f.Clock.Advance(60);await f.Service.Options(f.Principal,id);await Deny(()=>f.Verify(id),"only3 accepted user sends perrollinghour");
        Check(await f.Db.EmailChangeMails.CountAsync(x=>x.Kind=="Verification")==3,"delivery retries not counted as new user sends"); });
    var rate=new EmailChangeRateState();EmailChangePolicy.AdmitSend(rate,3600);EmailChangePolicy.AdmitSend(rate,3660);EmailChangePolicy.AdmitSend(rate,3720);
    await Deny(()=>{EmailChangePolicy.AdmitSend(rate,7199);return Task.CompletedTask;},"rolling window does not reset atcalendarhour");
    EmailChangePolicy.AdmitSend(rate,7200);Check(true,"exact60min oldest send ages out");
    var rollback = new EmailChangeRateState { SendTimes = "[3600]" };
    await Deny(()=>{EmailChangePolicy.AdmitSend(rollback,3500);return Task.CompletedTask;},"clock rollback cannot erase accepted send");
    Check(EmailChangePolicy.Recent("[4000,4001,4002,4003,4004]",3500,900).Count==5,"clock rollback retains failure windows");
    await Run(async f => { Check(!f.Db.Database.HasPendingModelChanges(),"runtime model matches migration snapshot");
        var id=await f.Begin();await f.Verify(id);var transport=new RecordingMail();
        var dispatcher=new EmailChangeMailDispatcher(new DbFactory(f.Provider.GetRequiredService<DbContextOptions<ApplicationDbContext>>()),f.Protection,transport,f.Clock,Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailChangeMailDispatcher>.Instance);
        await dispatcher.DispatchOnce();Check(transport.Messages.Count==1 && transport.Messages[0].Recipient=="next@example.test","dispatcher uses explicit protected new recipient");
        Check((await f.Db.EmailChangeMails.AsNoTracking().SingleAsync()).ProtectedPayload=="","dispatcher clears sent encrypted payload");
        Check((await f.Db.EmailChangeRateStates.AsNoTracking().SingleAsync()).SendTimes=="["+f.Clock.Now+"]","SMTP delivery does not increment user-send count"); });
    await Run(async f => { var id=await f.Begin();await f.Verify(id);f.User.SecurityStamp="revoked";await f.Db.SaveChangesAsync();var transport=new RecordingMail();
        var dispatcher=new EmailChangeMailDispatcher(new DbFactory(f.Provider.GetRequiredService<DbContextOptions<ApplicationDbContext>>()),f.Protection,transport,f.Clock,Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailChangeMailDispatcher>.Instance);
        await dispatcher.DispatchOnce();Check(transport.Messages.Count==0 && (await f.Db.EmailChangeMails.AsNoTracking().SingleAsync()).Status=="Cancelled","dispatcher cancels stamp-revoked verification"); });
    await Run(async f => { f.Db.Users.Add(new ApplicationUser{Id="other",UserName="other@example.test",Email="other@example.test",EmailConfirmed=true,Enabled=true,SecurityStamp="other-stamp"});
        f.Db.EmailChangeOperations.Add(new EmailChangeOperation{UserId="other",OriginalEmailHash=SemEndpoints.Hash("OLDOTHER@EXAMPLE.TEST"),TargetEmailHash=SemEndpoints.Hash("NEXT@EXAMPLE.TEST"),ProtectedTargetEmail="expired",SecurityStamp="other-stamp",SessionHash=SemEndpoints.Hash("other-session"),CreatedAt=f.Clock.Now-600,ExpiresAt=f.Clock.Now-1,ChallengeExpiresAt=f.Clock.Now-1});await f.Db.SaveChangesAsync();
        await f.Begin();var expired=await f.Db.EmailChangeOperations.AsNoTracking().SingleAsync(x=>x.UserId=="other");Check(expired.TargetEmailHash is null && expired.Status=="Invalidated","expired foreign reservation releases target atomically"); });
    await Run(async f => { f.Db.Users.Add(new ApplicationUser{Id="registered-target",UserName="next@example.test",NormalizedUserName="NEXT@EXAMPLE.TEST",Email="next@example.test",NormalizedEmail="NEXT@EXAMPLE.TEST",EmailConfirmed=true,Enabled=true,SecurityStamp="target-stamp"});await f.Db.SaveChangesAsync();
        var id=await f.Begin();Check(Guid.TryParse(id,out _),"begin does not reveal registered target membership");await f.Verify(id);var mail=await f.Mail("Verification");
        await Deny(()=>f.Service.Confirm(id,mail.Token),"registered target cannot be claimed after mail proof");Check(f.User.Email=="old@example.test","conflicting target preserves original account"); });
    Console.WriteLine("Email change checks passed: "+count);
}
finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
async Task Run(Func<Fixture,Task> test) { await using var f=await Fixture.Create(Path.Combine(root,Guid.NewGuid()+".db"));await test(f); }

sealed class Clock:TimeProvider { public long Now=1_800_000_000;public override DateTimeOffset GetUtcNow()=>DateTimeOffset.FromUnixTimeSeconds(Now);public void Advance(long seconds)=>Now+=seconds; }
sealed class Proof:IEmailChangePasskeyProof { public bool Succeeds=true;public Task<string> Options(ApplicationUser u)=>Task.FromResult("{\"challenge\":\"synthetic-challenge\",\"rpId\":\"localhost\"}");public Task<bool> Verify(ApplicationUser u,string c)=>Task.FromResult(Succeeds); }
sealed class Fixture:IAsyncDisposable
{
    public ServiceProvider Provider=null!;public ApplicationDbContext Db=null!;public EmailChangeService Service=null!;public ApplicationUser User=null!;public Clock Clock=new();public Proof Proof=new();public ClaimsPrincipal Principal=null!;public readonly EphemeralDataProtectionProvider Protection=new();
    public static async Task<Fixture> Create(string path){var f=new Fixture();var services=new ServiceCollection();services.AddLogging();services.AddDbContext<ApplicationDbContext>(x=>x.UseSqlite("Data Source="+path));services.AddIdentityCore<ApplicationUser>(o=>{o.Stores.SchemaVersion=IdentitySchemaVersions.Version3;o.User.RequireUniqueEmail=true;}).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton<TimeProvider>(f.Clock);services.AddSingleton<IEmailChangePasskeyProof>(f.Proof);services.AddSingleton<IDataProtectionProvider>(f.Protection);services.AddSingleton(new DeploymentSettings{PublicUrl="https://localhost/"});services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Smtp:Host","synthetic.invalid"},{"Smtp:From","noreply@example.test"}}).Build());services.AddSingleton<NotificationService>();services.AddScoped<EmailChangeService>();f.Provider=services.BuildServiceProvider();f.Db=f.Provider.GetRequiredService<ApplicationDbContext>();await f.Db.Database.MigrateAsync();
        f.User=new ApplicationUser{Id="member",UserName="old@example.test",NormalizedUserName="OLD@EXAMPLE.TEST",Email="old@example.test",NormalizedEmail="OLD@EXAMPLE.TEST",EmailConfirmed=true,Enabled=true,SecurityStamp="stamp",DisplayName="Member"};f.Db.Users.Add(f.User);await f.Db.SaveChangesAsync();
        f.Principal=new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,"member"),new Claim(PasskeyAdditionService.SessionClaim,"session"),new Claim("AspNet.Identity.SecurityStamp","stamp")],"Synthetic"));f.Service=f.Provider.GetRequiredService<EmailChangeService>();return f;}
    public async Task<string> Begin(){var result=JsonSerializer.SerializeToElement(await Service.Begin(Principal,"next@example.test"));return result.GetProperty("operationId").GetString()!;}
    public Task Verify(string id)=>Service.Reauthenticate(Principal,id,JsonSerializer.Serialize(new{response=new{clientDataJSON=Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"challenge\":\"synthetic-challenge\"}"))}}));
    public async Task<(EmailChangeMailPayload Payload,string Token)> Mail(string kind){var mail=await Db.EmailChangeMails.OrderByDescending(x=>x.Due).ThenByDescending(x=>x.Id).FirstAsync(x=>x.Kind==kind&&x.Status=="Pending");var p=JsonSerializer.Deserialize<EmailChangeMailPayload>(Protection.CreateProtector("SEM.EmailChange.Mail.v1").Unprotect(mail.ProtectedPayload))!;var fragment=p.Body.Contains("#op=")?p.Body.Split("#op=")[1]:"";var token=fragment.Contains("&token=")?fragment.Split("&token=")[1]:"";return(p,token);}
    public async ValueTask DisposeAsync()=>await Provider.DisposeAsync();
}

sealed class RecordingMail:IEmailChangeMailTransport { public List<EmailChangeMailPayload> Messages=new();public Task Send(string id,EmailChangeMailPayload payload,CancellationToken ct){Messages.Add(payload);return Task.CompletedTask;} }
sealed class DbFactory(DbContextOptions<ApplicationDbContext> options):IDbContextFactory<ApplicationDbContext> {public ApplicationDbContext CreateDbContext()=>new(options);}
