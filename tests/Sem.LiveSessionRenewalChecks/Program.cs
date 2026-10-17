using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root=Path.Combine(Path.GetTempPath(),"sem-session-renewal-"+Guid.NewGuid().ToString("N"));
var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Logging.ClearProviders();
builder.Configuration["Sem:PublicUrl"]="https://session-fixture.invalid/";builder.Configuration["Sem:DataDirectory"]=root;
builder.AddSemDeployment();
builder.Services.AddDbContext<ApplicationDbContext>(o=>o.UseSqlite("Data Source="+Path.Combine(root,"fixture.db")));
builder.Services.AddIdentityCore<ApplicationUser>(o=>{o.Stores.SchemaVersion=IdentitySchemaVersions.Version3;}).AddEntityFrameworkStores<ApplicationDbContext>().AddSignInManager();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
var app=builder.Build();app.UseAuthentication();app.UseSemLiveAuthenticationSession();
app.MapGet("/fixture/signin/{mode}",async(string mode,HttpContext c,ApplicationDbContext db,SignInManager<ApplicationUser> signin)=>{
 var u=await db.Users.SingleAsync();var claims=new List<Claim>{new("sem.fixture.untrusted-extra","must-not-survive")};
 if(mode!="missing")claims.Add(new(PasskeyAdditionService.SessionClaim,"fixture-session"));
 if(mode=="duplicate")claims.Add(new(PasskeyAdditionService.SessionClaim,"other-session"));
 await signin.SignInWithClaimsAsync(u,new AuthenticationProperties{IssuedUtc=DateTimeOffset.UtcNow.AddMinutes(-31),ExpiresUtc=DateTimeOffset.UtcNow.AddHours(1)},claims);
 return Results.Ok();
});
app.MapGet("/fixture/probe",(HttpContext c)=>c.User.Identity?.IsAuthenticated==true
 ? Results.Json(new{sessionCount=c.User.FindAll(PasskeyAdditionService.SessionClaim).Count(),sameSession=c.User.FindFirstValue(PasskeyAdditionService.SessionClaim)=="fixture-session",extra=c.User.HasClaim(x=>x.Type=="sem.fixture.untrusted-extra")})
 : Results.StatusCode(401));
await using(var scope=app.Services.CreateAsyncScope()){
 var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();await db.Database.EnsureCreatedAsync();
 var result=await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(new ApplicationUser{UserName="renewal-fixture",Email="renewal@example.test",EmailConfirmed=true,Enabled=true,DisplayName="Synthetic",SecurityStamp=Guid.NewGuid().ToString("N")});
 if(!result.Succeeded)throw new Exception("Synthetic user seed failed");
}
var checks=0;void Check(bool value,string label){if(!value)throw new Exception("FAIL "+label);Console.WriteLine("PASS "+label);checks++;}
await app.StartAsync();
try{
 var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
 using var client=new HttpClient(new HttpClientHandler{UseCookies=false}){BaseAddress=new Uri(address)};
 string? Cookie(HttpResponseMessage r)=>r.Headers.TryGetValues("Set-Cookie",out var values)?values.FirstOrDefault(x=>x.StartsWith("__Host-SEM-Auth="))?.Split(';')[0]:null;
 async Task<string> Signin(string mode="valid"){using var r=await client.GetAsync("/fixture/signin/"+mode);r.EnsureSuccessStatusCode();return Cookie(r)??throw new Exception("Missing fixture cookie");}
 async Task<HttpResponseMessage> Probe(string cookie){var q=new HttpRequestMessage(HttpMethod.Get,"/fixture/probe");q.Headers.Add("Cookie",cookie);return await client.SendAsync(q);}
 var aged=await Signin();using(var r=await Probe(aged)){
  Check(r.StatusCode==HttpStatusCode.OK,"Aged real Identity cookie remains authenticated through default stamp renewal");
  var body=JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
  Check(body.GetProperty("sessionCount").GetInt32()==1&&body.GetProperty("sameSession").GetBoolean(),"Renewal preserves exactly the same session binding");
  Check(!body.GetProperty("extra").GetBoolean(),"Renewal does not copy arbitrary old claims");
  aged=Cookie(r)??throw new Exception("Expected refreshed Identity cookie");
 }
 using(var r=await Probe(aged))Check(r.StatusCode==HttpStatusCode.OK,"Refreshed cookie works on the next request");
 await using(var scope=app.Services.CreateAsyncScope()){
  var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();var u=await db.Users.SingleAsync();
  db.RevokedAuthSessions.Add(new Sem.Web.Core.RevokedAuthSession{TokenHash=SemEndpoints.Hash("fixture-session"),UserId=u.Id,RevokedAt=DateTimeOffset.UtcNow.ToString("O")});await db.SaveChangesAsync();
 }
 using(var r=await Probe(aged))Check(r.StatusCode==HttpStatusCode.Unauthorized,"Revocation of the original session rejects its renewed cookie");
 await using(var scope=app.Services.CreateAsyncScope()){var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();await db.RevokedAuthSessions.ExecuteDeleteAsync();}
 foreach(var mode in new[]{"missing","duplicate"}){var bad=await Signin(mode);using var r=await Probe(bad);Check(r.StatusCode==HttpStatusCode.Unauthorized,"Renewal does not invent a binding for "+mode+" session claims");}
 var stale=await Signin();await using(var scope=app.Services.CreateAsyncScope()){var users=scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();var u=await users.Users.SingleAsync();await users.UpdateSecurityStampAsync(u);}
 using(var r=await Probe(stale))Check(r.StatusCode==HttpStatusCode.Unauthorized,"Stamp rotation still rejects an aged cookie");
 foreach(var field in new[]{"Enabled","EmailConfirmed"}){
  var before=await Signin();await using(var scope=app.Services.CreateAsyncScope()){var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();var u=await db.Users.SingleAsync();if(field=="Enabled")u.Enabled=false;else u.EmailConfirmed=false;await db.SaveChangesAsync();}
  using(var r=await Probe(before))Check(r.StatusCode==HttpStatusCode.Unauthorized,"Renewal cannot bypass "+field);
  await using(var scope=app.Services.CreateAsyncScope()){var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();var u=await db.Users.SingleAsync();u.Enabled=u.EmailConfirmed=true;await db.SaveChangesAsync();}
 }
 var renewal=app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityStampValidatorOptions>>().Value.OnRefreshingPrincipal!;
 ClaimsPrincipal Principal(string actor,string stamp,string? session)=>new(new ClaimsIdentity(new[]{new Claim(ClaimTypes.NameIdentifier,actor),new Claim("AspNet.Identity.SecurityStamp",stamp)}.Concat(session is null?Array.Empty<Claim>():new[]{new Claim(PasskeyAdditionService.SessionClaim,session)}),"fixture"));
 foreach(var mismatch in new[]{"actor","stamp"}){
  var next=Principal(mismatch=="actor"?"different":"same",mismatch=="stamp"?"different":"stamp",null);
  await renewal(new SecurityStampRefreshingPrincipalContext{CurrentPrincipal=Principal("same","stamp","fixture-session"),NewPrincipal=next});
  Check(!next.HasClaim(x=>x.Type==PasskeyAdditionService.SessionClaim),"Callback never transfers a binding across "+mismatch+" changes");
 }
 await renewal(new SecurityStampRefreshingPrincipalContext{CurrentPrincipal=Principal("same","stamp","fixture-session"),NewPrincipal=null});
 Check(true,"Missing replacement principal is handled without adding authority");
 await renewal(new SecurityStampRefreshingPrincipalContext{CurrentPrincipal=null!,NewPrincipal=Principal("same","stamp",null)});
 Check(true,"Missing original principal is handled without adding authority");
 Console.WriteLine($"Session renewal checks: {checks} passed");
}finally{await app.StopAsync();await app.DisposeAsync();Directory.Delete(root,true);}
