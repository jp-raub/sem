using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ImageMagick;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

const string Origin = "https://sem-image-test.invalid";
var root = Path.Combine(Path.GetTempPath(), "sem-profile-image-http-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "image.db");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = typeof(CoreEndpoints).Assembly.GetName().Name });
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + dbPath));
builder.Services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<BusinessNotificationStore>();
builder.Services.AddScoped<CoreFoundationService>();
builder.Services.AddSingleton(new DeploymentSettings { PublicUrl = Origin + "/", DataDirectory = root });
builder.Services.AddSingleton(new ManualPushRuntime(false));
builder.Services.AddSingleton<IEventPushTransport, NoopPushTransport>();
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "test-csrf"; o.Cookie.SecurePolicy = CookieSecurePolicy.None; });
builder.Services.AddAuthentication("SyntheticCookie").AddScheme<AuthenticationSchemeOptions, SyntheticCookieHandler>("SyntheticCookie", _ => { });
var app = builder.Build();
app.UseAuthentication();
app.UseAntiforgery();
app.MapGet("/__csrf", (HttpContext ctx, IAntiforgery anti) => Results.Json(new { token = anti.GetAndStoreTokens(ctx).RequestToken }));
app.MapCoreFoundation();
var checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }
var started = false;
try
{
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        db.Users.Add(new ApplicationUser { Id = "member", UserName = "member", Email = "member@invalid.test", EmailConfirmed = true, Enabled = true, SecurityStamp = "member-stamp", DisplayName = "Member" });
        await db.SaveChangesAsync();
    }
    await app.StartAsync(); started = true;
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(address) };
    async Task<(string Token, string Cookie)> Csrf()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/__csrf"); req.Headers.TryAddWithoutValidation("Cookie", "test-auth=member");
        using var res = await client.SendAsync(req); var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("token").GetString()!, res.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("test-csrf=", StringComparison.Ordinal)).Split(';', 2)[0]);
    }
    var (csrf, csrfCookie) = await Csrf(); var cookie = "test-auth=member; " + csrfCookie;
    async Task<HttpResponseMessage> Post(string path, byte[] body, string mime, bool token = true, string origin = Origin, string actor = "member")
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("Origin", origin);
        if (token) req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(req);
    }
    byte[] input;
    using (var source = new MagickImage(MagickColors.CornflowerBlue, 1200, 600)) input = source.ToByteArray(MagickFormat.Png);
    using (var denied = await Post("/api/core/profile-image?expectedActor=other&rowVersion=0", input, "image/png"))
        Check(denied.StatusCode == HttpStatusCode.Forbidden, "actor mismatch denied before image body processing");
    using (var noCsrf = await Post("/api/core/profile-image?expectedActor=member&rowVersion=0", input, "image/png", token: false))
        Check(noCsrf.StatusCode == HttpStatusCode.BadRequest, "CSRF failure denied before image body processing");
    using (var badOrigin = await Post("/api/core/profile-image?expectedActor=member&rowVersion=0", input, "image/png", origin: "https://evil.invalid"))
        Check(badOrigin.StatusCode == HttpStatusCode.Forbidden, "foreign Origin denied");
    using var saved = await Post("/api/core/profile-image?expectedActor=member&rowVersion=0", input, "image/png");
    Check(saved.StatusCode == HttpStatusCode.OK, "owner upload accepted");
    var save = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement;
    Check(save.GetProperty("rowVersion").GetInt64() == 1 && save.GetProperty("width").GetInt32() == 512 && save.GetProperty("height").GetInt32() == 256, "upload returns new image version and normalized dimensions");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var core = scope.ServiceProvider.GetRequiredService<CoreFoundationService>();
        var qr = await core.IssueProfileQrAsync(Who("member"), new ProfileQrIssueEdit(0, false));
        var guest = await core.ResolveProfileQrShareAsync(qr.Token);
        Check(guest.Profile.ImageRowVersion == 1 && guest.Profile.ImageDataUrl!.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "existing guest QR projection includes image under existing bearer authorization");
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var credential = await db.ProfileQrCredentials.SingleAsync();
        credential.IssuedAt = "1999-01-01T00:00:00.000Z";
        credential.ExpiresAt = "2000-01-01T00:00:00.000Z";
        await db.SaveChangesAsync();
        var expired = false;
        try { await core.ResolveProfileQrShareAsync(qr.Token); } catch (UnauthorizedAccessException) { expired = true; }
        Check(expired, "expired guest QR cannot resolve the image projection");
    }
    long auditsBeforeStale;
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        auditsBeforeStale = await db.Audits.LongCountAsync();
    }
    using (var stale = await Post("/api/core/profile-image?expectedActor=member&rowVersion=0", input, "image/png"))
        Check(stale.StatusCode == HttpStatusCode.Conflict, "stale image version rejected");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Check(await db.Audits.LongCountAsync() == auditsBeforeStale, "version conflict leaves image audit unchanged");
    }
    using (var profileRequest = new HttpRequestMessage(HttpMethod.Get, "/api/core/profile?id=member"))
    {
        profileRequest.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var profileResponse = await client.SendAsync(profileRequest);
        var profile = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync()).RootElement;
        Check(profile.GetProperty("imageRowVersion").GetInt64() == 1 && profile.GetProperty("imageDataUrl").GetString()!.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "authorized profile projection includes bounded PNG data URL");
    }
    using (var del = await Post("/api/core/profile-image-delete?expectedActor=member", System.Text.Encoding.UTF8.GetBytes("{\"rowVersion\":1}"), "application/json"))
    {
        Check(del.StatusCode == HttpStatusCode.OK, "owner delete accepted");
        var result = JsonDocument.Parse(await del.Content.ReadAsStringAsync()).RootElement;
        Check(result.GetProperty("rowVersion").GetInt64() == 2 && result.GetProperty("width").GetInt32() == 0, "delete returns bumped tombstone version");
    }
    using (var staleDelete = await Post("/api/core/profile-image-delete?expectedActor=member", System.Text.Encoding.UTF8.GetBytes("{\"rowVersion\":1}"), "application/json"))
        Check(staleDelete.StatusCode == HttpStatusCode.Conflict, "stale delete rejected");
    using (var badDelete = await Post("/api/core/profile-image-delete?expectedActor=member", System.Text.Encoding.UTF8.GetBytes("{\"rowVersion\":2,\"extra\":true}"), "application/json"))
        Check(badDelete.StatusCode == HttpStatusCode.BadRequest, "delete body rejects unknown fields");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var tombstone = await db.ProfileImages.SingleAsync();
        Check(tombstone.Data.Length == 0 && tombstone.Width == 0 && tombstone.Height == 0 && tombstone.RowVersion == 2, "delete persists empty anti-ABA tombstone");
    }
    await app.StopAsync(); started = false;
    var reopenedOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=" + dbPath).Options;
    await using (var reopened = new ApplicationDbContext(reopenedOptions))
    {
        var persisted = await reopened.ProfileImages.AsNoTracking().SingleAsync();
        Check(persisted.RowVersion == 2 && persisted.Data.Length == 0, "image tombstone/version survive database reopen");
    }
    Console.WriteLine($"PASS total={checks}");
}
finally { if (started) await app.StopAsync(); await app.DisposeAsync(); Directory.Delete(root, recursive: true); }

ClaimsPrincipal Who(string id) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier, id), new Claim("AspNet.Identity.SecurityStamp", id + "-stamp"),
    new Claim(PasskeyAdditionService.SessionClaim, id + "-session")], "SyntheticCookie"));

sealed class SyntheticCookieHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = Request.Cookies["test-auth"];
        if (string.IsNullOrEmpty(id)) return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, id), new Claim("AspNet.Identity.SecurityStamp", id + "-stamp"),
            new Claim(PasskeyAdditionService.SessionClaim, id + "-session")], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
sealed class NoopPushTransport : IEventPushTransport
{
    public bool Configured => false;
    public Task Send(PushDevice device, EventPushPayload payload, int ttl, CancellationToken ct) => Task.CompletedTask;
}
