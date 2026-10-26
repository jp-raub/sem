using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sem.Authentication;
using Sem.Web.Data;
using Sem.Web.Core;
int checks=0;
void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
var dir=Path.Combine(Path.GetTempPath(),"sem-auth-checks-"+Guid.NewGuid().ToString("N"));
var settings=new AuthenticationSettings("https://auth.example.invalid:8443/",dir);
Check(settings.PublicOrigin=="https://auth.example.invalid:8443","Origin正規化");
Check(settings.RelyingPartyId=="auth.example.invalid","RPにportを含めない");
foreach(var origin in new[]{"", "http://auth.example.invalid", "https://user:secret@auth.example.invalid", "https://auth.example.invalid/path", "https://auth.example.invalid?", "https://auth.example.invalid#", "https://auth.example.invalid\n", "https://auth.example.invalid:99999", "https://auth.example.invalid\\evil"}){
 bool rejected=false;try{new AuthenticationSettings(origin,dir);}catch(ArgumentException e){rejected=e.Message=="SEM_AUTH_INVALID_ORIGIN";}
 Check(rejected,"不正Origin拒否・値を含まない診断");
}
bool relativeRejected=false;try{new AuthenticationSettings(settings.PublicOrigin,"relative");}catch(ArgumentException){relativeRejected=true;}Check(relativeRejected,"相対鍵領域拒否");
using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
var services=new ServiceCollection();services.AddLogging(o=>o.SetMinimumLevel(LogLevel.None));
services.AddDbContext<ApplicationDbContext>(o=>o.UseSqlite(connection));
services.AddSemNativeAuthentication(settings);
await using var provider=services.BuildServiceProvider();
var forwarding=provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
Check(forwarding.ForwardedHeaders==ForwardedHeaders.None,"指定なしはプロキシを一切信頼しない");
var proxies=new ServiceCollection();proxies.AddLogging();proxies.AddSemNativeAuthentication(new AuthenticationSettings(settings.PublicOrigin,dir,[IPAddress.Loopback]));
using(var proxyProvider=proxies.BuildServiceProvider()){
 var p=proxyProvider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
 Check(p.KnownProxies.SequenceEqual(new[]{IPAddress.Loopback}) && p.KnownIPNetworks.Count==0 && p.ForwardLimit==1,"信頼プロキシ限定・一段");
}
var cookie=provider.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
Check(cookie.Cookie.Name=="__Host-SEM-Auth" && cookie.Cookie.HttpOnly && cookie.Cookie.SecurePolicy==Microsoft.AspNetCore.Http.CookieSecurePolicy.Always && cookie.Cookie.Path=="/","旧Cookie名・安全属性互換");
Check(provider.GetRequiredService<IOptions<Microsoft.AspNetCore.DataProtection.DataProtectionOptions>>().Value.ApplicationDiscriminator=="SEM.ExternalTest","旧Data Protection用途分離名互換");
var identity=provider.GetRequiredService<IOptions<IdentityOptions>>().Value;
Check(identity.Stores.SchemaVersion==IdentitySchemaVersions.Version3,"Identity schema3互換");
Check(provider.GetRequiredService<IOptions<IdentityPasskeyOptions>>().Value.UserVerificationRequirement=="required","ユーザー検証必須");
Check(AuthenticationSession.Hash("abc")=="BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD","旧大文字ハッシュ互換");
Check(AuthenticationSession.NewSession().Length==64 && AuthenticationSession.NewSession()!=AuthenticationSession.NewSession(),"ランダムなセッション");
ClaimsPrincipal Principal(string id="actor", string stamp="stamp",string? session="session"){
 var claims=new List<Claim>{new(identity.ClaimsIdentity.UserIdClaimType,id),new(identity.ClaimsIdentity.SecurityStampClaimType,stamp)};
 if(session is not null)claims.Add(new(AuthenticationSession.SessionClaim,session));
 return new(new ClaimsIdentity(claims,"synthetic"));
}
var original=Principal();var refresh=Principal(session:null);
AuthenticationSession.PreserveBinding(original,refresh,identity);Check(AuthenticationSession.SingleClaim(refresh,AuthenticationSession.SessionClaim)=="session","stamp更新後も一意session保持");
foreach(var next in new[]{Principal(id:"other",session:null),Principal(stamp:"changed",session:null),new ClaimsPrincipal(new ClaimsIdentity())}){
 AuthenticationSession.PreserveBinding(original,next,identity);Check(!next.HasClaim(c=>c.Type==AuthenticationSession.SessionClaim),"別ID・stamp・未認証へ転用しない");
}
var duplicated=Principal();((ClaimsIdentity)duplicated.Identity!).AddClaim(new(AuthenticationSession.SessionClaim,"other"));
refresh=Principal(session:null);AuthenticationSession.PreserveBinding(duplicated,refresh,identity);Check(!refresh.HasClaim(c=>c.Type==AuthenticationSession.SessionClaim),"重複sessionを継承しない");
await using var scope=provider.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
await SqliteStorageInitializer.InitializeAsync(db);
var user=new ApplicationUser{Id="actor",UserName="synthetic@example.invalid",Email="synthetic@example.invalid",Enabled=true,EmailConfirmed=true,SecurityStamp="stamp",DisplayName="合成人物"};db.Users.Add(user);await db.SaveChangesAsync();
Check(await AuthenticationSession.ValidAsync(db,original,identity),"現在の有効セッション");
foreach(var principal in new[]{Principal(id:"missing"),Principal(stamp:"old"),Principal(session:null),duplicated,new ClaimsPrincipal(new ClaimsIdentity())}) Check(!await AuthenticationSession.ValidAsync(db,principal,identity),"無効なbinding拒否");
user.Enabled=false;await db.SaveChangesAsync();Check(!await AuthenticationSession.ValidAsync(db,original,identity),"無効化直後拒否");
user.Enabled=true;user.EmailConfirmed=false;await db.SaveChangesAsync();Check(!await AuthenticationSession.ValidAsync(db,original,identity),"メール未確認拒否");
user.EmailConfirmed=true;await db.SaveChangesAsync();
db.RevokedAuthSessions.Add(new RevokedAuthSession{TokenHash=AuthenticationSession.Hash("session"),UserId="actor",RevokedAt="2026-01-01T00:00:00Z"});await db.SaveChangesAsync();
Check(!await AuthenticationSession.ValidAsync(db,original,identity),"失効テーブルの旧形式hashで即時拒否");
Check(await AuthenticationSession.ValidAsync(db,Principal(session:"another"),identity),"他sessionは巻き添えにしない");
var clock=new TestClock();var attempts=new LoginAttemptStore(clock);
var attempt=attempts.Issue("challenge")!;
Check(attempts.Consume(attempt,"challenge"),"一回限りのchallenge消費");
Check(!attempts.Consume(attempt,"challenge"),"同じCookieと署名の再送拒否");
attempt=attempts.Issue("original")!;Check(!attempts.Consume(attempt,"different"),"別challengeへの転用拒否");
Check(!attempts.Consume(attempt,"original"),"失敗した試行も再使用不可");
attempt=attempts.Issue("expired")!;clock.Now+=LoginAttemptStore.Lifetime;
Check(!attempts.Consume(attempt,"expired"),"期限ちょうどで拒否");
attempt=attempts.Issue("parallel")!;
var consumed=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>attempts.Consume(attempt,"parallel"))));
Check(consumed.Count(x=>x)==1,"8並行要求で消費1件だけ");
Check(Enumerable.Range(0,4096).All(_=>attempts.Issue("capacity") is not null),"有界store投入");
Check(attempts.Issue("overflow") is null,"上限時は拒否・無制限メモリ増加なし");
clock.Now+=LoginAttemptStore.Lifetime;Check(attempts.Issue("after-expiration") is not null,"期限切れ回収");
Console.WriteLine($"認証基盤: {checks}検査成功。合成メモリDBのみ・実配置の有効化を示す検査ではありません。");
sealed class TestClock : TimeProvider {
 public DateTimeOffset Now {get;set;}=DateTimeOffset.Parse("2026-01-01T00:00:00Z");
 public override DateTimeOffset GetUtcNow()=>Now;
}
