using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using Sem.Web.Runtime;
using Sem.Hosting;
using Sem.Authentication;
using Sem.Web.Data;
using Microsoft.EntityFrameworkCore;

if (args.Length == 1 && args[0] == "--build-info") {
    Console.WriteLine(JsonSerializer.Serialize(RuntimeBuildInfo.Current.Payload(null))); return;
}
ProductSettings? product;
ProductStorage? storage;
try {
    product = ProductSettings.Load(Environment.GetEnvironmentVariable);
    storage = product is null ? null : await ProductStorage.OpenAsync(product);
} catch (Exception) {
    Console.Error.WriteLine("SEM_PRODUCT_STARTUP_REJECTED"); Environment.ExitCode = 1; return;
}
using var storageLifetime = storage;
var builder = WebApplication.CreateBuilder(args);
if (product is not null) {
    // 配置URL・DB／鍵パス・Cookieを起動ログや例外へ出さない。
    builder.Logging.ClearProviders();
    builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(ProductStorage.ConnectionString(product.Database)));
    builder.Services.AddSemNativeAuthentication(product.Authentication);
}
var app = builder.Build();
if (product is not null) {
    app.UseSemNativeAuthentication();
    app.MapSemNativeAuthentication();
    app.MapGet("/assets/authentication.js", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "ProductUI", "authentication.js"), "text/javascript; charset=utf-8"));
    app.Use(async (c, next) => {
        c.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        // 実認証モードでは別HTMLやdemo scriptを開いて模擬成功する迂回入口を出さない。
        var p = c.Request.Path.Value ?? "";
        if ((p.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && p != "/index.html") ||
            (p.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && p != "/assets/authentication.js")) { c.Response.StatusCode = 404; return; }
        await next();
    });
}
var root = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var manifest = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(app.Environment.ContentRootPath,"mock-manifest.json")))!;
bool ready = manifest.Count > 0 && manifest.All(e => {
    var path = Path.Combine(root,e.Key);
    return File.Exists(path) && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == e.Value;
});
if (product is not null && !File.Exists(Path.Combine(app.Environment.ContentRootPath, "ProductUI", "authentication.js"))) ready = false;
var info = RuntimeBuildInfo.Current;
app.Use(async (context,next) => {
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});
bool Probe(HttpContext c, out string? probe) {
    probe=null;
    if(c.Request.Query.Keys.Any(k=>k!="probe")) return false;
    if(!c.Request.Query.TryGetValue("probe",out var values)) return true;
    if(values.Count!=1 || values[0] is not string value || !Regex.IsMatch(value,"\\A[0-9a-f]{32}\\z")) return false;
    probe=value;return true;
}
app.MapGet("/api/version",(HttpContext c)=>Probe(c,out var p)?Results.Json(info.Payload(p)):Results.BadRequest());
app.MapGet("/health/live",()=>Results.Json(new {status="live"}));
app.MapGet("/health/ready",(HttpContext c)=>!Probe(c,out var p)?Results.BadRequest():ready?Results.Json(new {status="mock-ui-ready",probe=p}):Results.StatusCode(503));
string Footer() => "<footer class=\"sem-runtime-footer\" style=\"text-align:center;padding:24px;overflow-wrap:anywhere\"><a href=\"/about\">SEM 稼働版："+WebUtility.HtmlEncode(info.Display)+"</a></footer>";
string Index() {
    var html = File.ReadAllText(Path.Combine(root, "index.html"));
    if (product is not null) {
        html = Regex.Replace(html, "<script\\b[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<div class=\"demo-bar\">.*?</div>", "", RegexOptions.Singleline);
        html = html.Replace("</body>", "<script src=\"/assets/authentication.js\"></script></body>");
        html = html.Replace("Session Room — UI Mock", "Session Room");
    }
    return html.Replace("</body>", Footer() + "</body>");
}
app.MapGet("/",()=> !ready?Results.StatusCode(503):Results.Content(Index(),"text/html; charset=utf-8"));
app.MapGet("/index.html",()=> !ready?Results.StatusCode(503):Results.Content(Index(),"text/html; charset=utf-8"));
app.MapGet("/about",()=>Results.Content(("""
<!doctype html><html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>このアプリについて — SEM</title>
<style>body{font:16px/1.7 system-ui,sans-serif;margin:0;background:#f4f7f3;color:#193a34}main{max-width:48rem;margin:2rem auto;padding:1rem}section{padding:1.25rem;background:#fff;border:1px solid #cad9d2;border-radius:12px}h1{font-size:1.8rem}h2{font-size:1.3rem}a{color:#176859}dd{margin:0 0 1rem;overflow-wrap:anywhere}dt{font-weight:bold}@media(prefers-color-scheme:dark){body{background:#13221e;color:#e4ede8}section{background:#20322a}a{color:#a3d8c9}}</style></head><body><main><h1>このアプリについて</h1><section><h2>応答しているアプリの稼働版</h2>
<p>このページを表示したアプリ自身のビルド情報です。「最新版」や全機能の動作確認済みを意味しません。</p>
<p><strong>現在は採用モックを移行したUIデモ版です。</strong>認証・登録・保存・QR・通知等は模擬動作です。実データの閲覧・保存や実通知は行いません。デモに個人情報や秘密情報を入力しないでください。</p><dl><dt>コミット</dt><dd>
""" + (info.Published ? "<a href=\""+info.SourceUrl+"\">"+info.Commit+"</a>" : "開発版（識別情報なし）") + "</dd><dt>ビルドID</dt><dd>"+WebUtility.HtmlEncode(info.BuildId??"未設定")+"</dd></dl><p>同じコミットでもビルドIDが異なれば別のビルドです。これはビルドの識別情報であり、デプロイ完了日時ではありません。</p><p>準備状態はモック資産の整合性を確認したものです。DB・認証・全業務機能の準備完了を意味しません。</p><p><a href=\"/api/version\">自動照合用のバージョン情報（JSON）</a></p><p><a href=\"/\">トップページへ戻る</a></p></section></main></body></html>").Replace("<strong>現在は採用モックを移行したUIデモ版です。</strong>認証・登録・保存・QR・通知等は模擬動作です。実データの閲覧・保存や実通知は行いません。デモに個人情報や秘密情報を入力しないでください。", product is null ? "<strong>現在は採用モックを移行したUIデモ版です。</strong>認証・登録・保存・QR・通知等は模擬動作です。実データの閲覧・保存や実通知は行いません。デモに個人情報や秘密情報を入力しないでください。" : "<strong>既存アカウントのパスキーログインを接続した段階版です。</strong>イベント・プロフィール・登録・保存・QR・通知・新規登録／復旧はまだ接続していません。ログインだけで全機能が利用可能になったことを意味しません。"), "text/html; charset=utf-8"));
app.UseStaticFiles();
app.Run();
