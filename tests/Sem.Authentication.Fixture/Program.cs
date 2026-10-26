using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sem.Authentication;
using Sem.Web.Core;
using Sem.Web.Data;
// この実行projectだけに合成登録・変更用APIを置く。製品ホストは参照しない。
var directory=Environment.GetEnvironmentVariable("SEM_AUTH_FIXTURE_DIRECTORY") ?? throw new InvalidOperationException("合成領域を指定してください");
var resume=Environment.GetEnvironmentVariable("SEM_AUTH_FIXTURE_RESUME")=="1";
var marker=Path.Combine(directory,"synthetic-fixture.marker");
if(!Path.IsPathFullyQualified(directory) || (!resume && Directory.Exists(directory)) ||
    (resume && (!File.Exists(marker) || File.ReadAllText(marker)!="sem-auth-synthetic-v1")))
    throw new InvalidOperationException("新規または明示作成済みの合成領域だけを指定してください");
Directory.CreateDirectory(directory);
if(!resume)File.WriteAllText(marker,"sem-auth-synthetic-v1");
var port=int.Parse(Environment.GetEnvironmentVariable("SEM_AUTH_FIXTURE_PORT") ?? "18766");
if(port<1024||port>65534)throw new InvalidOperationException("隔離portを指定してください");
using var rsa=RSA.Create(2048);
var request=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(san.Build());
using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(2));
var builder=WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k=>{k.Listen(IPAddress.Loopback,port,l=>l.UseHttps(certificate));k.Listen(IPAddress.Loopback,port+1);});
builder.Services.AddDbContext<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+Path.Combine(directory,"synthetic.db")));
builder.Services.AddSemNativeAuthentication(new AuthenticationSettings($"https://localhost:{port}",Path.Combine(directory,"keys"),Environment.GetEnvironmentVariable("SEM_AUTH_FIXTURE_TRUST_LOOPBACK")=="1"?[IPAddress.Loopback]:[]));
// stamp再生成を毎要求で発生させ、session claimの継承を回帰する。
builder.Services.Configure<SecurityStampValidatorOptions>(o=>o.ValidationInterval=TimeSpan.Zero);
var app=builder.Build();
using(var scope=app.Services.CreateScope()){
 var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();await SqliteStorageInitializer.InitializeAsync(db);
 var users=scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
 if(!resume){
 var created=await users.CreateAsync(new ApplicationUser{Id="synthetic-user",UserName="synthetic@example.invalid",Email="synthetic@example.invalid",EmailConfirmed=true,Enabled=true,IsAdmin=true,DisplayName="合成管理者"});
 if(!created.Succeeded)throw new InvalidOperationException("合成ユーザー作成失敗");
 if(Environment.GetEnvironmentVariable("SEM_EVENT_LIST_FIXTURE")=="1"){var user=(await users.FindByIdAsync("synthetic-user"))!;user.IsAdmin=false;await users.UpdateAsync(user);await EventListFixture.Seed(db);}
 }else if((await users.FindByIdAsync("synthetic-user"))?.Email!="synthetic@example.invalid")throw new InvalidOperationException("合成人物が一致しません");
}
app.UseSemNativeAuthentication();
app.MapSemNativeAuthentication();
app.MapGet("/",()=>Results.Content("<!doctype html><html lang=ja><title>隔離認証検証</title><body>合成認証器の検証専用ホスト</body></html>","text/html"));
app.MapPost("/fixture/create-options",async(HttpContext c,IAntiforgery csrf,UserManager<ApplicationUser> users,SignInManager<ApplicationUser> signin)=>{
 await csrf.ValidateRequestAsync(c);
 return Results.Text(await signin.MakePasskeyCreationOptionsAsync(new(){Id="synthetic-user",Name="synthetic@example.invalid",DisplayName="合成管理者"}),"application/json");
});
app.MapPost("/fixture/create",async(HttpContext c,IAntiforgery csrf,UserManager<ApplicationUser> users,SignInManager<ApplicationUser> signin)=>{
 await csrf.ValidateRequestAsync(c);
 using var reader=new StreamReader(c.Request.Body);var json=await reader.ReadToEndAsync();
 var result=await signin.PerformPasskeyAttestationAsync(json);
 if(!result.Succeeded)return Results.BadRequest();
 var added=await users.AddOrUpdatePasskeyAsync((await users.FindByIdAsync("synthetic-user"))!,result.Passkey);
 return added.Succeeded?Results.Ok():Results.BadRequest();
});
app.MapPost("/fixture/change/{action}",async(string action,HttpContext c,IAntiforgery csrf,ApplicationDbContext db,UserManager<ApplicationUser> users)=>{
 await csrf.ValidateRequestAsync(c);var user=(await users.FindByIdAsync("synthetic-user"))!;
 if(action=="disable")user.Enabled=false;
 else if(action=="enable")user.Enabled=true;
 else if(action=="unconfirm")user.EmailConfirmed=false;
 else if(action=="confirm")user.EmailConfirmed=true;
 else if(action=="stamp")await users.UpdateSecurityStampAsync(user);
 else if(action=="revoke"){
  var session=AuthenticationSession.SingleClaim(c.User,AuthenticationSession.SessionClaim);
  if(session is null)return Results.Unauthorized();
  db.RevokedAuthSessions.Add(new RevokedAuthSession{TokenHash=AuthenticationSession.Hash(session),UserId=user.Id,RevokedAt=DateTimeOffset.UtcNow.ToString("O")});
 }else return Results.BadRequest();
 await db.SaveChangesAsync();return Results.Ok();
});
await app.RunAsync();
