using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-profile-extension-http-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var database = Path.Combine(root, "test.db");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = typeof(CoreEndpoints).Assembly.GetName().Name });
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite("Data Source=" + database), ServiceLifetime.Scoped);
builder.Services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<BusinessNotificationStore>();
builder.Services.AddScoped<CoreFoundationService>();
builder.Services.AddSingleton(new DeploymentSettings { PublicUrl = "https://profile-extension.test/", DataDirectory = root });
builder.Services.AddSingleton(new ManualPushRuntime(false));
builder.Services.AddSingleton<IEventPushTransport, NoopPushTransport>();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddAuthentication("SyntheticCookie").AddScheme<AuthenticationSchemeOptions, SyntheticCookieHandler>("SyntheticCookie", _ => { });
var app = builder.Build();
app.UseAuthentication();
app.MapGet("/api/csrf", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
app.MapCoreFoundation();
var passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
var started = false;
try
{
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        db.Users.AddRange(
            new ApplicationUser { Id = "admin", UserName = "admin@test.invalid", Email = "admin@test.invalid", DisplayName = "Admin", IsAdmin = true, Enabled = true, EmailConfirmed = true, SecurityStamp = "admin-stamp" },
            new ApplicationUser { Id = "member", UserName = "member@test.invalid", Email = "secret@test.invalid", DisplayName = "Member", Enabled = true, EmailConfirmed = true, SecurityStamp = "member-stamp" });
        await db.SaveChangesAsync();
    }
    await app.StartAsync(); started = true;
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(address) };
    const string origin = "https://profile-extension.test";
    async Task<(string Token, string Cookie)> Csrf(string authCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/csrf");
        if (authCookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", authCookie);
        using var response = await client.SendAsync(request);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var token = json.GetProperty("token").GetString()!;
        var setCookie = response.Headers.GetValues("Set-Cookie").FirstOrDefault()!;
        var cookie = setCookie.Split(';', 2)[0];
        return (token, cookie);
    }
    async Task<HttpResponseMessage> Get(string path, string authCookie = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (authCookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", authCookie);
        return await client.SendAsync(request);
    }
    async Task<HttpResponseMessage> Post(string path, object body, (string Token, string Cookie) csrf, string authCookie = "", string? originOverride = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Origin", originOverride ?? origin);
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf.Token);
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", new[] { authCookie, csrf.Cookie }.Where(x => x.Length > 0)));
        return await client.SendAsync(request);
    }
    async Task<HttpResponseMessage> PostRaw(string path, string body, (string Token, string Cookie) csrf,
        string authCookie = "", string? originOverride = null, bool includeCsrf = true, string mediaType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        request.Headers.TryAddWithoutValidation("Origin", originOverride ?? origin);
        if (includeCsrf) request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf.Token);
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", new[] { authCookie, csrf.Cookie }.Where(x => x.Length > 0)));
        return await client.SendAsync(request);
    }

    using (var editor = await Get("/api/core/profile-editor", "test-auth=member"))
    {
        Check(editor.StatusCode == HttpStatusCode.OK && editor.Headers.CacheControl?.NoStore == true, "editor GET uses current cookie and no-store");
        var json = JsonDocument.Parse(await editor.Content.ReadAsStringAsync()).RootElement;
        Check(json.GetProperty("actorId").GetString() == "member" && !json.TryGetProperty("email", out _), "editor response is actor-bound and excludes email");
    }
    var adminCsrf = await Csrf("test-auth=admin");
    using (var adminAccounts = await Get("/api/core/admin-accounts", "test-auth=admin"))
    {
        Check(adminAccounts.StatusCode == HttpStatusCode.OK && adminAccounts.Headers.CacheControl?.NoStore == true,
            "administrator-only account API is no-store");
        var json = JsonDocument.Parse(await adminAccounts.Content.ReadAsStringAsync()).RootElement;
        Check(json.GetProperty("actorId").GetString() == "admin" &&
            json.GetProperty("accounts").EnumerateArray().Any(x => x.GetProperty("email").GetString() == "secret@test.invalid"),
            "separate admin DTO returns current actor and email only through admin route");
    }
    using (var deniedAccounts = await Get("/api/core/admin-accounts", "test-auth=member"))
        Check(deniedAccounts.StatusCode == HttpStatusCode.Forbidden, "ordinary member cannot read the admin account API");
    using (var actorMismatch = await Post("/api/core/admin-accounts?expectedActor=member", new { changes = Array.Empty<object>() }, adminCsrf, "test-auth=admin"))
        Check(actorMismatch.StatusCode == HttpStatusCode.Forbidden, "admin write rejects a mismatched expectedActor before body processing");
    using (var badOrigin = await Post("/api/core/admin-accounts?expectedActor=admin", new { changes = Array.Empty<object>() }, adminCsrf, "test-auth=admin", "https://attacker.invalid"))
        Check(badOrigin.StatusCode == HttpStatusCode.Forbidden, "admin write rejects a foreign Origin");
    using (var noCsrf = await PostRaw("/api/core/admin-accounts?expectedActor=admin", "{\"changes\":[]}", adminCsrf, "test-auth=admin", includeCsrf: false))
        Check(noCsrf.StatusCode == HttpStatusCode.BadRequest, "admin write requires an explicit CSRF header");
    using (var strictJson = await PostRaw("/api/core/admin-accounts?expectedActor=admin", "{\"changes\":[],\"extra\":true}", adminCsrf, "test-auth=admin"))
        Check(strictJson.StatusCode == HttpStatusCode.BadRequest, "admin role API rejects unknown JSON properties");
    using (var duplicateJson = await PostRaw("/api/core/admin-accounts?expectedActor=admin", "{\"changes\":[],\"changes\":[]}", adminCsrf, "test-auth=admin"))
        Check(duplicateJson.StatusCode == HttpStatusCode.BadRequest, "admin role API rejects duplicate JSON properties");
    using (var invalidUtf8Request = new HttpRequestMessage(HttpMethod.Post, "/api/core/admin-accounts?expectedActor=admin")
    {
        Content = new ByteArrayContent([0x7b, 0xff, 0x7d])
    })
    {
        invalidUtf8Request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        invalidUtf8Request.Headers.TryAddWithoutValidation("Origin", origin);
        invalidUtf8Request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", adminCsrf.Token);
        invalidUtf8Request.Headers.TryAddWithoutValidation("Cookie", $"test-auth=admin; {adminCsrf.Cookie}");
        using var invalidUtf8 = await client.SendAsync(invalidUtf8Request);
        Check(invalidUtf8.StatusCode == HttpStatusCode.BadRequest, "admin role API rejects invalid UTF-8");
    }
    using (var overLimit = await PostRaw("/api/core/admin-accounts?expectedActor=admin",
        "{\"changes\":[],\"padding\":\"" + new string('x', 65_537) + "\"}", adminCsrf, "test-auth=admin"))
        Check(overLimit.StatusCode == HttpStatusCode.RequestEntityTooLarge, "admin role API bounds JSON request size");
    using (var noOpSave = await Post("/api/core/admin-accounts?expectedActor=admin", new { changes = Array.Empty<object>() }, adminCsrf, "test-auth=admin"))
        Check(noOpSave.StatusCode == HttpStatusCode.OK && noOpSave.Headers.CacheControl?.NoStore == true,
            "admin role API accepts an actor-bound, Origin/CSRF-protected no-op save without cache");
    using var forbiddenQuestion = await Post("/api/core/profile-questions?expectedActor=member", new { operation = "Add", text = "Should deny", type = "Text", rowVersion = 0 }, adminCsrf, "test-auth=admin");
    Check(forbiddenQuestion.StatusCode == HttpStatusCode.Forbidden, "expectedActor mismatch is rejected before write");
    using var add = await Post("/api/core/profile-questions?expectedActor=admin", new { operation = "Add", text = "公開プロフィールの質問", type = "Text", rowVersion = 0 }, adminCsrf, "test-auth=admin");
    Check(add.StatusCode == HttpStatusCode.OK, "global administrator can add a profile question through HTTP");
    var questionId = JsonDocument.Parse(await add.Content.ReadAsStringAsync()).RootElement.GetProperty("questions")[0].GetProperty("id").GetString()!;
    var memberCsrf = await Csrf("test-auth=member");
    using var nullId = await Post("/api/core/profile-editor?expectedActor=member", new { displayName="Member",reading="",callName="",icon="",rowVersion=0,social=Array.Empty<object>(),answers=new object[]{new {questionId=(string?)null,value="value"}} },memberCsrf,"test-auth=member");
    Check(nullId.StatusCode==HttpStatusCode.BadRequest,"null question ID returns400 not500");
    using var nullOrder = await Post("/api/core/profile-questions?expectedActor=admin",new{operation="Reorder",id=(string?)null,text=(string?)null,type=(string?)null,rowVersion=0,order=new object?[]{null}},adminCsrf,"test-auth=admin");
    Check(nullOrder.StatusCode==HttpStatusCode.BadRequest,"null reorder item returns400 not500");
    using var quotedVersion = await Post("/api/core/profile-qr?expectedActor=member",new{rowVersion="0",reissue=false},memberCsrf,"test-auth=member");
    Check(quotedVersion.StatusCode==HttpStatusCode.BadRequest,"quoted rowVersion returns400 under strict numeric JSON");
    using var save = await Post("/api/core/profile-editor?expectedActor=member", new
    {
        displayName = "Member", reading = "めんばー", callName = "", icon = "🎸", rowVersion = 0,
        social = Array.Empty<object>(), answers = new[] { new { questionId, value = "favorite" } }
    }, memberCsrf, "test-auth=member");
    Check(save.StatusCode == HttpStatusCode.OK, "self profile and answer save with expected actor, Origin, and CSRF");
    using (var memberRead = await Get("/api/core/profile?id=member", "test-auth=member"))
    {
        var json = JsonDocument.Parse(await memberRead.Content.ReadAsStringAsync()).RootElement;
        Check(json.GetProperty("answers")[0].GetProperty("value").GetString() == "favorite", "existing profile read route now projects current answers");
        Check(json.GetProperty("icon").GetString() == "🎸", "existing profile read route projects emoji icon");
    }
    var qrCsrf = await Csrf("test-auth=member");
    using var initial = await Get("/api/core/profile-qr", "test-auth=member");
    Check(initial.StatusCode == HttpStatusCode.OK, "QR status is owner-scoped read-only GET");
    using var issuedResponse = await Post("/api/core/profile-qr?expectedActor=member", new { rowVersion = 0, reissue = false }, qrCsrf, "test-auth=member");
    Check(issuedResponse.StatusCode == HttpStatusCode.OK && issuedResponse.Headers.CacheControl?.NoStore == true, "QR issuance is a no-store POST");
    var issued = JsonDocument.Parse(await issuedResponse.Content.ReadAsStringAsync()).RootElement;
    var rawToken = issued.GetProperty("token").GetString()!;
    var version = issued.GetProperty("rowVersion").GetInt64();
    using (var status = await Get("/api/core/profile-qr", "test-auth=member"))
    {
        var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement;
        Check(json.GetProperty("active").GetBoolean() && !json.TryGetProperty("token", out _), "QR status exposes no bearer token");
    }
    using (var svg = new HttpRequestMessage(HttpMethod.Get, $"/api/core/profile-qr-svg?expectedActor=member&version={version}"))
    {
        svg.Headers.TryAddWithoutValidation("Cookie", "test-auth=member");
        svg.Headers.TryAddWithoutValidation("X-Profile-QR-Token", rawToken);
        using var response = await client.SendAsync(svg);
        var content = await response.Content.ReadAsStringAsync();
        Check(response.StatusCode == HttpStatusCode.OK && content.StartsWith("<svg", StringComparison.Ordinal) &&
            response.Content.Headers.ContentType?.MediaType == "image/svg+xml", "authenticated owner token-header GET returns generated SVG");
        Check(!response.RequestMessage!.RequestUri!.OriginalString.Contains(rawToken, StringComparison.Ordinal), "bearer never appears in QR SVG request URL");
    }
    var anonymousCsrf = await Csrf("");
    using (var guest = await Post("/api/core/profile-qr-view", new { token = rawToken }, anonymousCsrf))
    {
        Check(guest.StatusCode == HttpStatusCode.OK, "fragment-held bearer resolves through anonymous same-origin POST body");
        var json = JsonDocument.Parse(await guest.Content.ReadAsStringAsync()).RootElement;
        Check(json.GetProperty("displayName").GetString() == "Member" && !json.TryGetProperty("email", out _) &&
            !json.TryGetProperty("events", out _), "guest response contains profile only, never email or history");
        Check(guest.RequestMessage!.RequestUri!.Query.Length == 0, "guest bearer is not in request query");
    }
    using (var guestHistory = await Get("/api/core/profile-registration-history?id=member"))
        Check(guestHistory.StatusCode == HttpStatusCode.Forbidden, "anonymous QR access does not grant registration-history access");
    using (var wrongOrigin = await Post("/api/core/profile-qr-view", new { token = rawToken }, anonymousCsrf, "", "https://attacker.invalid"))
        Check(wrongOrigin.StatusCode == HttpStatusCode.Forbidden, "anonymous QR resolver rejects cross-origin POST");
    await using (var scaleScope = app.Services.CreateAsyncScope())
    {
        var factory = scaleScope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        for(var i=0;i<99;i++) db.ProfileQuestions.Add(new ProfileQuestion{Text="追加設問"+i,Type="Text",Enabled=true,SortOrder=i+1});
        await db.SaveChangesAsync();
        var ids=await db.ProfileQuestions.Select(q=>q.Id).ToArrayAsync();
        var largeDraft=new{displayName="Member",reading="",callName="",icon="",rowVersion=1,social=Array.Empty<object>(),answers=ids.Select(id=>new{questionId=id,value=new string('好',1000)}).ToArray()};
        Check(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(largeDraft))>65536,"100 valid full-length answers exceed legacy64KiB cap");
        using var largeSaved=await Post("/api/core/profile-editor?expectedActor=member",largeDraft,memberCsrf,"test-auth=member");
        Check(largeSaved.StatusCode==HttpStatusCode.OK,"100 valid1000-character answers fit bounded1MiB profile write");
    }
    using var declaredLarge=await Post("/api/core/profile-editor?expectedActor=member",new{padding=new string('x',1024*1024)},memberCsrf,"test-auth=member");
    Check(declaredLarge.StatusCode==HttpStatusCode.RequestEntityTooLarge,"profile editor declared body beyond1MiB returns413");
    using var unauthorizedLarge=await Post("/api/core/profile-questions?expectedActor=member",new{padding=new string('x',70000)},memberCsrf,"test-auth=member");
    Check(unauthorizedLarge.StatusCode==HttpStatusCode.Forbidden,"nonadmin questions denied before oversize body processing");
    Console.WriteLine($"Profile extension HTTP checks passed: {passed}");
}
finally
{
    if (started) await app.StopAsync();
    await app.DisposeAsync();
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, true);
}

sealed class SyntheticCookieHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = Request.Cookies["test-auth"];
        if (string.IsNullOrEmpty(id)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, id),
            new Claim("AspNet.Identity.SecurityStamp", id + "-stamp"),
            new Claim(PasskeyAdditionService.SessionClaim, id + "-session")
        };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
sealed class NoopPushTransport : IEventPushTransport
{
    public bool Configured => false;
    public Task Send(PushDevice device, EventPushPayload payload, int ttl, CancellationToken ct) => Task.CompletedTask;
}
