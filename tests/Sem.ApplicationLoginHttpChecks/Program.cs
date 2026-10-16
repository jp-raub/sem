using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;

const string Origin = "https://sem-login-test.invalid";
var root = Path.Combine(Path.GetTempPath(),
    "sem-application-login-http-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "synthetic.db");
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + dbPath));
builder.Services.AddIdentityCore<ApplicationUser>(o =>
    {
        o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        o.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager();
builder.Services.AddDataProtection();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new DeploymentSettings { PublicUrl = Origin + "/" });
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddScoped<PasskeyAdditionService>();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = "test-csrf";
    o.Cookie.SecurePolicy = CookieSecurePolicy.None;
});
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();

var app = builder.Build();
app.UseAuthentication();
app.UseAntiforgery();
app.MapSem();
var started = false;
var checks = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

try
{
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
        db.Users.AddRange(
            new ApplicationUser
            {
                Id = "unconfirmed", UserName = "unconfirmed@example.test",
                NormalizedUserName = "UNCONFIRMED@EXAMPLE.TEST",
                Email = "unconfirmed@example.test", NormalizedEmail = "UNCONFIRMED@EXAMPLE.TEST",
                Enabled = true, EmailConfirmed = false, SecurityStamp = "synthetic-stamp-1"
            },
            new ApplicationUser
            {
                Id = "disabled", UserName = "disabled@example.test",
                NormalizedUserName = "DISABLED@EXAMPLE.TEST",
                Email = "disabled@example.test", NormalizedEmail = "DISABLED@EXAMPLE.TEST",
                Enabled = false, EmailConfirmed = true, SecurityStamp = "synthetic-stamp-2"
            });
        await db.SaveChangesAsync();
    }

    await app.StartAsync();
    started = true;
    var address = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
    {
        BaseAddress = new Uri(address)
    };

    async Task<(string Token, string Cookie)> GetCsrf()
    {
        using var response = await client.GetAsync("/api/csrf");
        Check(response.IsSuccessStatusCode, "anonymous CSRF bootstrap remains available");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("token").GetString()!;
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(v => v.StartsWith("test-csrf=", StringComparison.Ordinal))
            .Split(';', 2)[0];
        return (token, cookie);
    }

    var csrf = await GetCsrf();
    var cookie = csrf.Cookie;

    async Task<HttpResponseMessage> Post(
        string path, HttpContent content, bool includeCsrf = true,
        bool includeCookie = true, string origin = Origin, string? csrfToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("Origin", origin);
        if (includeCookie) request.Headers.Add("Cookie", cookie);
        if (includeCsrf) request.Headers.Add("X-CSRF-TOKEN", csrfToken ?? csrf.Token);
        return await client.SendAsync(request);
    }

    async Task AssertRejected(string action, HttpContent content, HttpStatusCode expected,
        string name, bool includeCsrf = true, bool includeCookie = true, string origin = Origin)
    {
        using var response = await Post("/api/" + action, content, includeCsrf,
            includeCookie, origin);
        Check(response.StatusCode == expected, name);
        Check(!response.Headers.Contains("Set-Cookie"),
            name + " does not issue an authentication cookie");
    }

    await AssertRejected("login-options",
        new StringContent("{\"email\":\"unconfirmed@example.test\"}", Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "enabled but unconfirmed account is refused", includeCookie: false);
    await AssertRejected("login-options",
        new StringContent("{\"email\":\"disabled@example.test\"}", Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "disabled account is refused", includeCookie: false);

    await AssertRejected("login-options",
        new StringContent("{\"email\":12}", Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "number email is rejected before Identity passkey calls", includeCookie: false);
    await AssertRejected("login-options",
        new StringContent("{\"email\":null}", Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "null email is rejected", includeCookie: false);
    await AssertRejected("login-options",
        new StringContent("{\"email\":\"a@example.test\",\"email\":\"b@example.test\"}",
            Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "duplicate JSON member is rejected", includeCookie: false);
    await AssertRejected("login-options",
        new StringContent("{\"email\":\"a@example.test\",\"password\":\"secret\"}",
            Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "unknown JSON member is rejected", includeCookie: false);
    await AssertRejected("login-options",
        new StringContent("{\"email\":\"a\\u0001@example.test\"}",
            Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "control characters in email are rejected", includeCookie: false);
    await AssertRejected("login-options",
        new StringContent("{\"email\":{\"a\":{\"b\":{\"c\":{\"d\":\"x\"}}}}}",
            Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "excess JSON depth or wrong property type is rejected", includeCookie: false);

    await AssertRejected("login",
        new StringContent("{\"credential\":false}", Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "boolean credential is rejected before native passkey sign-in", includeCookie: false);
    await AssertRejected("login",
        new StringContent("{\"credential\":\"synthetic\",\"extra\":\"value\"}",
            Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "unknown login field is rejected", includeCookie: false);
    await AssertRejected("login",
        new StringContent("{\"credential\":\"bad\\u0000credential\"}",
            Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "control characters in credential are rejected", includeCookie: false);

    await AssertRejected("login-options",
        new StringContent("{\"email\":\"a@example.test\"}", Encoding.UTF8, "application/x-www-form-urlencoded"),
        HttpStatusCode.UnsupportedMediaType, "form content type is rejected before antiforgery", includeCsrf: false, includeCookie: false);
    await AssertRejected("login-options",
        new StringContent("{\"email\":\"a@example.test\"}", Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "CSRF header is mandatory", includeCsrf: false, includeCookie: false);
    await AssertRejected("login-options",
        new StringContent("{\"email\":\"a@example.test\"}", Encoding.UTF8, "application/json"),
        HttpStatusCode.Forbidden, "foreign Origin is rejected", includeCookie: false,
        origin: "https://attacker.invalid");

    await AssertRejected("login-options?email=a@example.test",
        new StringContent("{\"email\":\"a@example.test\"}", Encoding.UTF8, "application/json"),
        HttpStatusCode.BadRequest, "query string is rejected", includeCookie: false);
    using (var response = await client.GetAsync("/api/login-options"))
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed &&
            response.Headers.CacheControl?.NoStore == true,
            "login options requires POST and is no-store");

    await AssertRejected("login-options",
        new StringContent(new string('x', 4097), Encoding.UTF8, "application/json"),
        HttpStatusCode.RequestEntityTooLarge, "options body over 4 KiB is rejected before antiforgery",
        includeCsrf: false, includeCookie: false);
    await AssertRejected("login",
        new StringContent(new string('x', 65_537), Encoding.UTF8, "application/json"),
        HttpStatusCode.RequestEntityTooLarge, "login body over 64 KiB is rejected before antiforgery",
        includeCsrf: false, includeCookie: false);

    var invalidUtf8 = new ByteArrayContent([0x7B, 0x22, 0x65, 0x6D, 0x61, 0x69, 0x6C,
        0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D]);
    invalidUtf8.Headers.ContentType = new("application/json");
    await AssertRejected("login-options", invalidUtf8, HttpStatusCode.BadRequest,
        "invalid UTF-8 is rejected", includeCookie: false);

    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/login-options"))
    {
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-CSRF-TOKEN", csrf.Token);
        request.Headers.TransferEncodingChunked = true;
        request.Content = new StreamContent(new MemoryStream(
            Encoding.UTF8.GetBytes(new string('x', 4097))));
        request.Content.Headers.ContentType = new("application/json");
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge,
            "chunked options request is bounded by the streaming limit");
        Check(!response.Headers.Contains("Set-Cookie"),
            "chunked overflow does not issue an authentication cookie");
    }

    Console.WriteLine($"Application login HTTP checks passed: {checks}");
}
finally
{
    if (started) await app.StopAsync();
    await app.DisposeAsync();
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root)) Directory.Delete(root, true);
}