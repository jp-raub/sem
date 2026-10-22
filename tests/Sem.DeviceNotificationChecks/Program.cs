using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

const string Origin = "https://sem-notify-test.invalid";
var root = Path.Combine(Path.GetTempPath(), "sem-device-notification-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite("Data Source=" + Path.Combine(root, "test.db")));
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager();
builder.Services.AddDataProtection();
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Push:PublicKey"] = "synthetic-public-key",
    ["Push:PrivateKey"] = "synthetic-private-key"
});
builder.Services.AddSingleton(new DeploymentSettings { PublicUrl = Origin + "/" });
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<DeviceNotificationService>();
builder.Services.AddScoped<PasskeyAdditionService>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "test-csrf";
    options.Cookie.SecurePolicy = CookieSecurePolicy.None;
});
builder.Services.AddAuthentication("Synthetic")
    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Synthetic", _ => { })
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        options.Cookie.Name = "test-application";
        options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        options.Cookie.HttpOnly = true;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
        options.SlidingExpiration = false;
    });
var app = builder.Build();
app.UseAuthentication();
app.UseSemLiveAuthenticationSession();
app.UseAntiforgery();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/core/device-notifications"))
        context.Response.Headers["X-Test-Request-Content-Length"] = context.Request.ContentLength?.ToString() ?? "null";
    await next();
});
app.MapGet("/fixture/csrf", (HttpContext context, IAntiforgery csrf) =>
    Results.Json(new { token = csrf.GetAndStoreTokens(context).RequestToken }));
app.MapSem(); // Include the production catch-all to prove exact legacy routes win.
app.MapDeviceNotifications();
var started = false;
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}
try
{
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
        db.Users.AddRange(User("member", "member@example.test", "member-stamp"),
            User("other", "other@example.test", "other-stamp"));
        db.RevokedAuthSessions.Add(new RevokedAuthSession
        {
            UserId = "member", TokenHash = SemEndpoints.Hash("revoked-session"), RevokedAt = DateTimeOffset.UtcNow.ToString("O")
        });
        await db.SaveChangesAsync();
    }
    await app.StartAsync();
    started = true;
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(address) };

    async Task<(string Token, string Cookie)> Csrf(string user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/fixture/csrf");
        request.Headers.Add("Cookie", "test-auth=" + user);
        using var response = await client.SendAsync(request);
        var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        var cookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("test-csrf=", StringComparison.Ordinal)).Split(';', 2)[0];
        return (token, cookie);
    }
    async Task<HttpResponseMessage> Post(string path, string body, string user, string csrfToken, string csrfCookie,
        string origin = Origin, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Cookie", "test-auth=" + user + "; " + csrfCookie);
        if (!string.IsNullOrEmpty(csrfToken)) request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        return await client.SendAsync(request);
    }
    var memberCsrf = await Csrf("member");
    var pkey = P256PublicKey();
    var offCurvePkey = B64Url([0x04, .. Enumerable.Repeat((byte)0x00, 64)]);
    var auth = B64Url(Enumerable.Repeat((byte)0x22, 16).ToArray());
    var differentAuth = B64Url(Enumerable.Repeat((byte)0x23, 16).ToArray());
    const string endpoint = "https://fcm.googleapis.com/send/test-subscription";

    using (var response = await client.GetAsync("/api/core/device-notifications?unexpected=1"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "GET rejects query parameters");
    using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/core/device-notifications"))
    {
        request.Headers.Add("Cookie", "test-auth=member");
        using var response = await client.SendAsync(request);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Check(response.IsSuccessStatusCode && json.GetProperty("actorId").GetString() == "member" &&
              json.GetProperty("pushConfigured").GetBoolean(), "GET returns actor and VAPID configuration");
    }
    foreach (var cookie in new[] { "member-stale", "member-revoked" })
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/core/device-notifications");
        request.Headers.Add("Cookie", "test-auth=" + cookie);
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.Unauthorized, cookie + " is rejected by live stamp/session validation");
    }
    using (var response = await client.GetAsync("/api/core/device-notifications/register"))
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed && response.Content.Headers.Allow.Contains("POST"),
            "subaction rejects unsupported methods with Allow");
    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/core/device-notifications"))
    using (var response = await client.SendAsync(request))
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed && response.Content.Headers.Allow.Contains("GET"),
            "root rejects unsupported methods with Allow");
    using (var response = await client.GetAsync("/api/core/device-notifications/unknown"))
        Check(response.StatusCode == HttpStatusCode.NotFound, "unknown sub-action is not routed as a core action");
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=other",
               "{", "member", "", memberCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "expectedActor mismatch rejected before CSRF and body parsing");
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=member",
               "{}", "member", "", memberCsrf.Cookie, origin: "https://foreign.invalid"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "foreign Origin rejected");
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=member",
               "{}", "member", "", memberCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "missing CSRF header rejected");
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=member",
               "{}", "member", memberCsrf.Token, memberCsrf.Cookie, contentType: "application/x-www-form-urlencoded"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "form content rejected before antiforgery fallback");
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=member",
               "{\"endpoint\":1,\"p256dh\":\"x\",\"auth\":\"x\"}", "member", memberCsrf.Token, memberCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "wrong JSON field type rejected");
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=member",
               "{\"endpoint\":\"a\",\"endpoint\":\"b\",\"p256dh\":\"x\",\"auth\":\"x\"}", "member", memberCsrf.Token, memberCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "duplicate JSON property rejected");
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=member",
               "{\"endpoint\":\"a\",\"p256dh\":\"x\",\"auth\":\"x\",\"extra\":1}", "member", memberCsrf.Token, memberCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "unknown JSON property rejected");
    using (var content = new ByteArrayContent([0xC3, 0x28]))
    {
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/core/device-notifications/register?expectedActor=member") { Content = content };
        request.Headers.Add("Origin", Origin); request.Headers.Add("Cookie", "test-auth=member; " + memberCsrf.Cookie);
        request.Headers.Add("X-CSRF-TOKEN", memberCsrf.Token);
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid UTF-8 rejected");
    }
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=member",
               new string('x', 17 * 1024), "member", memberCsrf.Token, memberCsrf.Cookie))
        Check((int)response.StatusCode == 413, "request content length capped at 16 KiB");
    using (var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 17 * 1024)))))
    {
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/core/device-notifications/register?expectedActor=member") { Content = content };
        request.Headers.Add("Origin", Origin); request.Headers.Add("Cookie", "test-auth=member; " + memberCsrf.Cookie);
        request.Headers.Add("X-CSRF-TOKEN", memberCsrf.Token); request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        var observedLength = response.Headers.TryGetValues("X-Test-Request-Content-Length", out var values)
            ? values.Single() : "missing";
        Check(observedLength == "null", "fixture received the chunked request without Content-Length");
        Check((int)response.StatusCode == 413, "unknown-length chunked request stream capped at 16 KiB");
    }

    using (var response = await Post("/api/core/device-notifications/register?expectedActor=member",
               JsonSerializer.Serialize(new { endpoint, p256dh = "x", auth }), "member", memberCsrf.Token, memberCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "malformed P-256 key rejected");
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=member",
               JsonSerializer.Serialize(new { endpoint, p256dh = offCurvePkey, auth }), "member", memberCsrf.Token, memberCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "off-curve 65-byte P-256 point rejected");
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=member",
               JsonSerializer.Serialize(new { endpoint = "https://attacker.invalid/subscribe", p256dh = pkey, auth }),
               "member", memberCsrf.Token, memberCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "endpoint outside push allowlist rejected");

    var registerBody = JsonSerializer.Serialize(new { endpoint, p256dh = pkey, auth });
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=member",
               registerBody, "member", memberCsrf.Token, memberCsrf.Cookie))
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Check(response.IsSuccessStatusCode && json.GetProperty("actorId").GetString() == "member" &&
              json.GetProperty("registered").GetBoolean(), "register binds approved endpoint and key material");
    }
    using (var response = await Post("/api/core/device-notifications/status?expectedActor=member",
               JsonSerializer.Serialize(new { endpoint }), "member", memberCsrf.Token, memberCsrf.Cookie))
        Check(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("registered").GetBoolean(),
            "status confirms endpoint for current actor, stamp, and session");
    var otherSessionCsrf = await Csrf("member-other-session");
    using (var response = await Post("/api/core/device-notifications/unregister?expectedActor=member",
               JsonSerializer.Serialize(new { endpoint }), "member-other-session", otherSessionCsrf.Token, otherSessionCsrf.Cookie))
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Check(response.IsSuccessStatusCode && !json.GetProperty("registered").GetBoolean(),
            "same-user different-session unregister is a no-op");
    }
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.PushDevices.SingleAsync(x => x.Endpoint == endpoint);
        Check(row.UserId == "member" && row.SessionHash == SemEndpoints.Hash("member-session"),
            "different-session unregister preserves the original binding");
    }
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=member",
               JsonSerializer.Serialize(new { endpoint, p256dh = pkey, auth }), "member-other-session",
               otherSessionCsrf.Token, otherSessionCsrf.Cookie))
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Check(response.IsSuccessStatusCode && json.GetProperty("registered").GetBoolean(),
            "exact stored subscription keys allow an explicit same-user session rebind");
    }
    using (var response = await Post("/api/core/device-notifications/status?expectedActor=member",
               JsonSerializer.Serialize(new { endpoint }), "member-other-session", otherSessionCsrf.Token, otherSessionCsrf.Cookie))
        Check(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("registered").GetBoolean(),
            "rebound endpoint is current-session status");
    using (var response = await Post("/api/core/device-notifications/status?expectedActor=member",
               JsonSerializer.Serialize(new { endpoint }), "member", memberCsrf.Token, memberCsrf.Cookie))
        Check(!JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("registered").GetBoolean(),
            "previous session no longer has an active binding");

    var otherCsrf = await Csrf("other");
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=other",
               JsonSerializer.Serialize(new { endpoint }), "other", otherCsrf.Token, otherCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "endpoint-only cross-user transfer is rejected");
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=other",
               JsonSerializer.Serialize(new { endpoint, p256dh = pkey, auth = differentAuth }),
               "other", otherCsrf.Token, otherCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "cross-user transfer with different auth secret is rejected");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.PushDevices.SingleAsync(x => x.Endpoint == endpoint);
        Check(row.UserId == "member" && row.SessionHash == SemEndpoints.Hash("other-session") &&
              row.P256dh == pkey && row.Auth == auth,
            "rejected transfer attempts leave original owner and keys unchanged");
    }
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=other",
               JsonSerializer.Serialize(new { endpoint, p256dh = pkey, auth }), "other", otherCsrf.Token, otherCsrf.Cookie))
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Check(response.IsSuccessStatusCode && json.GetProperty("registered").GetBoolean(),
            "matching stored public and auth keys permit an explicit cross-user rebind");
    }
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.PushDevices.SingleAsync(x => x.Endpoint == endpoint);
        Check(row.UserId == "other" && row.SessionHash == SemEndpoints.Hash("other-session") &&
              row.P256dh == pkey && row.Auth == auth,
            "successful rebind atomically stores the new owner and session binding");
    }
    using (var response = await Post("/api/core/device-notifications/unregister?expectedActor=member",
               JsonSerializer.Serialize(new { endpoint }), "member-other-session", otherSessionCsrf.Token, otherSessionCsrf.Cookie))
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Check(response.IsSuccessStatusCode && !json.GetProperty("registered").GetBoolean(),
            "previous owner cannot unregister after a valid transfer");
    }
    using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Check(await db.PushDevices.AnyAsync(x => x.Endpoint == endpoint && x.UserId == "other"),
            "former owner unregister attempt preserves the new owner's row");
    }
    using (var response = await Post("/api/core/device-notifications/unregister?expectedActor=other",
               JsonSerializer.Serialize(new { endpoint }), "other", otherCsrf.Token, otherCsrf.Cookie))
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Check(response.IsSuccessStatusCode && !json.GetProperty("registered").GetBoolean(),
            "current binding owner can unregister its endpoint");
    }
    using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Check(!await db.PushDevices.AnyAsync(x => x.Endpoint == endpoint), "unregister removes only the caller's current binding");
    }
    // Exercise actual compatibility routes, not just the shared service. No
    // outbound Push client is registered or invoked by these HTTP operations.
    const string legacyEndpoint = "https://fcm.googleapis.com/send/legacy-fixture";
    var legacyBody = JsonSerializer.Serialize(new { endpoint = legacyEndpoint, p256dh = pkey, auth });
    var legacyRemove = JsonSerializer.Serialize(new { endpoint = legacyEndpoint });
    foreach (var route in new[] { "/api/subscribe", "/api/unsubscribe" })
    {
        using (var response = await client.GetAsync(route))
            Check(response.StatusCode == HttpStatusCode.MethodNotAllowed, route + " rejects GET");
        using (var response = await Post(route, legacyBody, "member", memberCsrf.Token, memberCsrf.Cookie,
                   origin: "https://foreign.invalid"))
            Check(response.StatusCode == HttpStatusCode.Unauthorized, route + " rejects foreign Origin");
        using (var response = await Post(route, legacyBody, "member", "", memberCsrf.Cookie))
            Check(response.StatusCode == HttpStatusCode.BadRequest, route + " requires CSRF");
        using (var response = await Post(route + "?unexpected=1", legacyBody, "member", memberCsrf.Token, memberCsrf.Cookie))
            Check(response.StatusCode == HttpStatusCode.BadRequest, route + " rejects ambiguous query");
        foreach (var actor in new[] { "member-stale", "member-revoked", "unknown" })
        {
            using var response = await Post(route, legacyBody, actor, memberCsrf.Token, memberCsrf.Cookie);
            Check(response.StatusCode == HttpStatusCode.Unauthorized, route + " rejects " + actor);
        }
        using (var response = await Post(route, new string('x', 17 * 1024), "member", memberCsrf.Token, memberCsrf.Cookie))
            Check((int)response.StatusCode == 413, route + " limits body to 16 KiB");
    }
    using (var response = await Post("/api/subscribe",
               JsonSerializer.Serialize(new { endpoint = legacyEndpoint, p256dh = offCurvePkey, auth }),
               "member", memberCsrf.Token, memberCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "legacy register validates P-256 point");
    using (var response = await Post("/api/subscribe", legacyBody, "member", memberCsrf.Token, memberCsrf.Cookie))
    {
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Check(response.IsSuccessStatusCode && result.GetProperty("registered").GetBoolean() &&
              result.GetProperty("message").GetString() == "端末を登録しました。", "legacy register keeps sem.js success message");
    }
    using (var response = await Post("/api/subscribe",
               JsonSerializer.Serialize(new { endpoint = legacyEndpoint, p256dh = pkey, auth = differentAuth }),
               "other", otherCsrf.Token, otherCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "legacy cross-user replacement without both keys rejected");
    using (var response = await Post("/api/subscribe", legacyRemove, "other", otherCsrf.Token, otherCsrf.Cookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "legacy endpoint-only takeover rejected");
    using (var response = await Post("/api/unsubscribe", legacyRemove, "member-other-session", otherSessionCsrf.Token, otherSessionCsrf.Cookie))
        Check(response.IsSuccessStatusCode, "legacy different-session removal is idempotent no-op");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PushDevices.SingleAsync(x => x.Endpoint == legacyEndpoint);
        Check(row.UserId == "member" && row.SessionHash == SemEndpoints.Hash("member-session") && row.Auth == auth,
            "legacy rejected transfer and other-session removal preserve original binding");
    }
    using (var response = await Post("/api/core/device-notifications/register?expectedActor=other", legacyBody,
               "other", otherCsrf.Token, otherCsrf.Cookie))
        Check(response.IsSuccessStatusCode, "modern route can rebind a legacy binding with exact key proof");
    using (var response = await Post("/api/unsubscribe", legacyRemove, "member", memberCsrf.Token, memberCsrf.Cookie))
        Check(response.IsSuccessStatusCode, "legacy former owner removal is harmless after modern transfer");
    await using (var scope = app.Services.CreateAsyncScope())
        Check(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PushDevices.AnyAsync(x =>
            x.Endpoint == legacyEndpoint && x.UserId == "other"), "modern owner preserved across legacy removal attempt");
    using (var response = await Post("/api/subscribe", legacyBody, "member", memberCsrf.Token, memberCsrf.Cookie))
        Check(response.IsSuccessStatusCode, "legacy route can explicitly rebind modern binding with exact proof");
    using (var response = await Post("/api/unsubscribe", legacyRemove, "member", memberCsrf.Token, memberCsrf.Cookie))
    {
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Check(response.IsSuccessStatusCode && result.GetProperty("message").GetString() == "端末の通知を解除しました。",
            "legacy current-session removal keeps sem.js success message");
    }
    await using (var scope = app.Services.CreateAsyncScope())
        Check(!await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PushDevices.AnyAsync(x => x.Endpoint == legacyEndpoint),
            "legacy current binding is removed");
    const string racedEndpoint = "https://fcm.googleapis.com/send/raced-legacy-fixture";
    var raceBodies = new[] {
        JsonSerializer.Serialize(new { endpoint = racedEndpoint, p256dh = pkey, auth }),
        JsonSerializer.Serialize(new { endpoint = racedEndpoint, p256dh = P256PublicKey(), auth = differentAuth })
    };
    var races = await Task.WhenAll(
        Post("/api/subscribe", raceBodies[0], "member", memberCsrf.Token, memberCsrf.Cookie),
        Post("/api/core/device-notifications/register?expectedActor=other", raceBodies[1], "other", otherCsrf.Token, otherCsrf.Cookie));
    try
    {
        Check(races.Count(x => x.IsSuccessStatusCode) == 1 && races.All(x => x.IsSuccessStatusCode ||
              x.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict),
            "concurrent legacy and modern registration admits only one owner without shared key proof");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.PushDevices.SingleAsync(x => x.Endpoint == racedEndpoint);
        var winner = races[0].IsSuccessStatusCode ? 0 : 1;
        var submitted = JsonDocument.Parse(raceBodies[winner]).RootElement;
        Check(row.UserId == (winner == 0 ? "member" : "other") &&
              row.P256dh == submitted.GetProperty("p256dh").GetString() && row.Auth == submitted.GetProperty("auth").GetString(),
            "concurrent registration leaves one complete winner binding, not mixed key material");
        Check(!await db.Messages.AnyAsync() && !await db.Deliveries.AnyAsync(),
            "compatibility subscription operations enqueue no notifications");
    }
    finally { foreach (var response in races) response.Dispose(); }
    Console.WriteLine($"PASS {checks} device-notification checks");
}
finally
{
    if (started) await app.StopAsync();
    await app.DisposeAsync();
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}

static ApplicationUser User(string id, string email, string stamp) => new()
{
    Id = id, UserName = email, NormalizedUserName = email.ToUpperInvariant(), Email = email,
    NormalizedEmail = email.ToUpperInvariant(), DisplayName = id, EmailConfirmed = true, Enabled = true,
    SecurityStamp = stamp
};
static string B64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
static string P256PublicKey()
{
    using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    var point = key.ExportParameters(false).Q;
    var bytes = new byte[65];
    bytes[0] = 0x04;
    point.X!.CopyTo(bytes, 1);
    point.Y!.CopyTo(bytes, 33);
    return B64Url(bytes);
}

sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue("test-auth", out var kind)) return Task.FromResult(AuthenticateResult.NoResult());
        var (id, stamp, session) = kind switch
        {
            "member" => ("member", "member-stamp", "member-session"),
            "member-other-session" => ("member", "member-stamp", "other-session"),
            "member-stale" => ("member", "old-stamp", "member-session"),
            "member-revoked" => ("member", "member-stamp", "revoked-session"),
            "other" => ("other", "other-stamp", "other-session"),
            _ => ("unknown", "unknown-stamp", "unknown-session")
        };
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, id), new Claim("AspNet.Identity.SecurityStamp", stamp),
            new Claim(PasskeyAdditionService.SessionClaim, session) };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
