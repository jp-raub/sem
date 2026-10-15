using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Data;
using Sem.Web.Runtime;

const string Origin="https://sem-email-test.invalid";
var root=Path.Combine(Path.GetTempPath(),"sem-email-http-"+Guid.NewGuid());Directory.CreateDirectory(root);
var b=WebApplication.CreateBuilder();b.WebHost.UseUrls("http://127.0.0.1:0");b.Logging.ClearProviders();
b.Services.AddDbContextFactory<ApplicationDbContext>(x=>x.UseSqlite("Data Source="+Path.Combine(root,"test.db")));
b.Services.AddIdentityCore<ApplicationUser>(o=>{o.Stores.SchemaVersion=IdentitySchemaVersions.Version3;o.User.RequireUniqueEmail=true;}).AddEntityFrameworkStores<ApplicationDbContext>().AddSignInManager();
b.Services.AddDataProtection();b.Services.AddSingleton<TimeProvider>(TimeProvider.System);b.Services.AddSingleton(new DeploymentSettings{PublicUrl=Origin+"/"});
b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{{"Smtp:Host","synthetic.invalid"},{"Smtp:From","noreply@example.test"}});
b.Services.AddSingleton<NotificationService>();b.Services.AddScoped<PasskeyAdditionService>();b.Services.AddScoped<EmailChangeService>();b.Services.AddSingleton<IEmailChangePasskeyProof,Proof>();
b.Services.AddAntiforgery(o=>{o.HeaderName="X-CSRF-TOKEN";o.Cookie.Name="test-csrf";o.Cookie.SecurePolicy=CookieSecurePolicy.None;});
b.Services.AddAuthentication("Synthetic").AddScheme<AuthenticationSchemeOptions,Auth>("Synthetic",_=>{});
var app=b.Build();app.UseAuthentication();app.UseAntiforgery();app.MapSem();var count=0;bool started=false;
void Check(bool ok,string name){if(!ok)throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name);count++;}
try{
 await using(var scope=app.Services.CreateAsyncScope()){var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();await db.Database.MigrateAsync();db.Users.Add(new(){Id="member",UserName="old@example.test",NormalizedUserName="OLD@EXAMPLE.TEST",Email="old@example.test",NormalizedEmail="OLD@EXAMPLE.TEST",EmailConfirmed=true,Enabled=true,SecurityStamp="stamp"});await db.SaveChangesAsync();}
 await app.StartAsync();started=true;var addr=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();using var client=new HttpClient(new HttpClientHandler{UseCookies=false}){BaseAddress=new Uri(addr)};
 async Task<(string Token,string Cookie)> Csrf(bool authenticated){using var req=new HttpRequestMessage(HttpMethod.Get,"/api/csrf");if(authenticated)req.Headers.Add("Cookie","test-auth=member");using var res=await client.SendAsync(req);var json=JsonDocument.Parse(await res.Content.ReadAsStringAsync());return(json.RootElement.GetProperty("token").GetString()!,res.Headers.GetValues("Set-Cookie").Single(x=>x.StartsWith("test-csrf=")).Split(';')[0]);}
 var anti=await Csrf(true);var cookie="test-auth=member; "+anti.Cookie;
 async Task<HttpResponseMessage> Post(string action,string body,string? authCookie=null,bool csrf=true,string origin=Origin){var req=new HttpRequestMessage(HttpMethod.Post,"/api/"+action+"?expectedActor=member"){Content=new StringContent(body,Encoding.UTF8,"application/json")};req.Headers.Add("Origin",origin);if(!string.IsNullOrEmpty(authCookie??cookie))req.Headers.Add("Cookie",authCookie??cookie);if(csrf)req.Headers.Add("X-CSRF-TOKEN",anti.Token);return await client.SendAsync(req);}
 using(var r=await Post("email-change-begin","malformed",authCookie:""))Check(r.StatusCode==HttpStatusCode.Forbidden,"anonymous rejected before malformedbody");
 using(var r=await Post("email-change-begin","{}",csrf:false))Check(r.StatusCode==HttpStatusCode.BadRequest,"CSRF required");
 using(var r=await Post("email-change-begin","{}",origin:"https://evil.invalid"))Check(r.StatusCode==HttpStatusCode.Forbidden,"foreignOrigin rejected");
 using(var r=await Post("email-change-begin","{\"newEmail\":null}"))Check(r.StatusCode==HttpStatusCode.BadRequest,"null email400");
 using(var r=await Post("email-change-begin","{\"newEmail\":\"next@example.test\",\"newEmail\":\"other@example.test\"}"))Check(r.StatusCode==HttpStatusCode.BadRequest,"duplicateJSON member rejected");
 using(var r=await Post("email-change-begin","{\"newEmail\":\"next@example.test\",\"userId\":\"other\"}"))Check(r.StatusCode==HttpStatusCode.BadRequest,"userselector rejected");
 using(var r=await Post("email-change-begin",new string('x',4097)))Check((int)r.StatusCode==413,"bounded request413");
 using(var r=await client.GetAsync("/api/email-change-begin"))Check((int)r.StatusCode==405&&r.Headers.CacheControl?.NoStore==true,"GET begin405 no-store");
 foreach(var suffix in new[]{"?expectedActor=other","?expectedActor=member&expectedActor=member","?expectedActor="}){
  using var req=new HttpRequestMessage(HttpMethod.Post,"/api/email-change-begin"+suffix){Content=new StringContent("malformed")};req.Headers.Add("Origin",Origin);req.Headers.Add("Cookie",cookie);using var r=await client.SendAsync(req);Check((int)r.StatusCode==(suffix.Contains("other")?403:400),"actor query rejected before body/CSRF "+suffix);}
 using(var req=new HttpRequestMessage(HttpMethod.Post,"/api/email-change-begin?expectedActor=member"){Content=new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x',4097))))}){req.Headers.Add("Origin",Origin);req.Headers.Add("Cookie",cookie);req.Headers.Add("X-CSRF-TOKEN",anti.Token);req.Headers.TransferEncodingChunked=true;req.Content.Headers.ContentType=new("application/json");using var r=await client.SendAsync(req);Check((int)r.StatusCode==413&&r.Headers.CacheControl?.NoStore==true,"chunked overflow413 no-store");}
 using var begin=await Post("email-change-begin","{\"newEmail\":\"next@example.test\"}");Check(begin.IsSuccessStatusCode,"authenticatedbegin succeeds");var id=JsonDocument.Parse(await begin.Content.ReadAsStringAsync()).RootElement.GetProperty("operationId").GetString()!;
 using(var r=await Post("email-change-begin","{\"newEmail\":\"other@example.test\"}"))Check((int)r.StatusCode==400,"pending secondtarget rejected");
 var credential=JsonSerializer.Serialize(new{response=new{clientDataJSON=Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"challenge\":\"synthetic-challenge\"}"))}});
 using(var r=await Post("email-change-reauth",JsonSerializer.Serialize(new{operationId=id,credential})))Check(r.IsSuccessStatusCode,"purposeproof queues confirmation syntheticverifier");
 string token;await using(var scope=app.Services.CreateAsyncScope()){var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();var mail=await db.EmailChangeMails.SingleAsync();var dp=scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("SEM.EmailChange.Mail.v1");var payload=JsonSerializer.Deserialize<EmailChangeMailPayload>(dp.Unprotect(mail.ProtectedPayload))!;token=payload.Body.Split("&token=")[1];Check((await db.Users.SingleAsync()).Email=="old@example.test","emailunchanged before confirmation");}
 var anon=await Csrf(false);using(var req=new HttpRequestMessage(HttpMethod.Post,"/api/email-change-confirm"){Content=JsonContent.Create(new{operationId=id,token})}){req.Headers.Add("Origin",Origin);req.Headers.Add("Cookie",anon.Cookie);req.Headers.Add("X-CSRF-TOKEN",anon.Token);using var r=await client.SendAsync(req);Check(r.IsSuccessStatusCode,"anonymous token-onlyconfirm succeeds");Check(!r.Headers.TryGetValues("Set-Cookie",out var values)||!values.Any(x=>x.Contains("Identity.Application")||x.Contains("SEM-Auth")),"confirmation grants no normal sessioncookie");}
 await using(var scope=app.Services.CreateAsyncScope()){var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();Check((await db.Users.SingleAsync()).Email=="next@example.test","confirmed address updated");Check(await db.EmailChangeMails.CountAsync(x=>x.Kind=="OldAddressNotice")==1,"old-addressnotice queued once");}
 using(var req=new HttpRequestMessage(HttpMethod.Get,"/api/email-change-status")){req.Headers.Add("Cookie",cookie);using var r=await client.SendAsync(req);Check((int)r.StatusCode==403,"oldstamp andinitiating session deniedafterchange");}
 Console.WriteLine("Email change HTTP checks passed: "+count);
}finally{if(started)await app.StopAsync();await app.DisposeAsync();SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
sealed class Proof:IEmailChangePasskeyProof{public Task<string> Options(ApplicationUser u)=>Task.FromResult("{\"challenge\":\"synthetic-challenge\"}");public Task<bool> Verify(ApplicationUser u,string c)=>Task.FromResult(true);}
sealed class Auth(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder):AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder){protected override Task<AuthenticateResult> HandleAuthenticateAsync(){if(!Request.Cookies.ContainsKey("test-auth"))return Task.FromResult(AuthenticateResult.NoResult());var p=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,"member"),new Claim("AspNet.Identity.SecurityStamp","stamp"),new Claim(PasskeyAdditionService.SessionClaim,"session")],"Synthetic"));return Task.FromResult(AuthenticateResult.Success(new(p,Scheme.Name)));}}
