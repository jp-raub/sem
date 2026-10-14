using System.Net;
using System.Security.Claims;
using System.Text;
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
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

const string Origin = "https://sem-test.raub.jp";
const string Csv = "Kind,Title,Artist,Duration,Prep,Parts\nSong,HTTP boundary,Artist,1:00,,Vo";
var root = Path.Combine(Path.GetTempPath(), "sem-import-http-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var database = Path.Combine(root, "test.db");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = typeof(CoreEndpoints).Assembly.GetName().Name });
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + database), ServiceLifetime.Scoped);
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
    await using (var init = app.Services.CreateAsyncScope())
    {
        var factory = init.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        foreach (var uid in new[] { "admin", "manager", "member" })
            db.Users.Add(new ApplicationUser { Id = uid, UserName = uid, Email = uid + "@PRIVATE.invalid", DisplayName = uid,
                Enabled = true, EmailConfirmed = true, SecurityStamp = uid + "-stamp", IsAdmin = uid == "admin" });
        await db.SaveChangesAsync();
    }
    await using (var seed = app.Services.CreateAsyncScope())
    {
        var core = seed.ServiceProvider.GetRequiredService<CoreFoundationService>();
        await core.SaveEventAsync(Who("admin"), null,
            new("HTTP import test", "", "", DateTimeOffset.Parse("2099-01-01T00:00:00Z"), null, "", "Hidden", ["manager"], 0));
    }

    await app.StartAsync(); started = true;
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(address) };
    using var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/__csrf");
    csrfRequest.Headers.TryAddWithoutValidation("Cookie", "test-auth=manager");
    using var csrfResponse = await client.SendAsync(csrfRequest);
    Check(csrfResponse.IsSuccessStatusCode, "test host issues antiforgery token");
    var csrfJson = JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync());
    var csrfToken = csrfJson.RootElement.GetProperty("token").GetString()!;
    var csrfCookie = csrfResponse.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("test-csrf=", StringComparison.Ordinal));
    var cookieValue = csrfCookie.Split(';', 2)[0];
    var cookieHeader = "test-auth=manager; " + cookieValue;
    async Task<(string Token, string Cookie)> CsrfFor(string identity)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__csrf");
        request.Headers.TryAddWithoutValidation("Cookie", "test-auth=" + identity);
        using var response = await client.SendAsync(request);
        var tokenDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("test-csrf=", StringComparison.Ordinal)).Split(';', 2)[0];
        return (tokenDoc.RootElement.GetProperty("token").GetString()!, cookie);
    }
    string eventId = "";

    async Task<HttpResponseMessage> Send(string action, string json, string query = "id=", bool origin = true, bool token = true,
        string? cookie = null, bool chunked = false, string? contentType = "application/json", string? csrfTokenOverride = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/core/" + action + (query.Length == 0 ? "" : "?" + query));
        request.Content = chunked
            ? new StreamContent(new NonSeekableReadStream(Encoding.UTF8.GetBytes(json)))
            : new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        if (contentType is not null) request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        if (origin) request.Headers.TryAddWithoutValidation("Origin", Origin);
        if (token) request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrfTokenOverride ?? csrfToken);
        cookie ??= cookieHeader;
        if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        var response = await client.SendAsync(request);
        if (response.Headers.CacheControl?.NoStore != true)
        {
            var details = await response.Content.ReadAsStringAsync();
            response.Dispose();
            throw new Exception("Core response omitted no-store (" + action + ": " + (int)response.StatusCode + "): " + details);
        }
        return response;
    }
    async Task<HttpResponseMessage> Preview(string csv, string? query = null)
    {
        var json = JsonSerializer.Serialize(new { csv });
        return await Send("event-import-preview", json, query ?? "id=" + eventId);
    }

    // Obtain the seeded event id via the database, without exposing or relying on client state.
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        eventId = await db.SessionEvents.Select(x => x.Id).SingleAsync();
    }
    var oversizedBody = JsonSerializer.Serialize(new { csv = new string('x', 7 * 1024 * 1024) });

    using (var get = await client.GetAsync("/api/core/event-import-preview?id=" + Uri.EscapeDataString(eventId)))
        Check(get.StatusCode == HttpStatusCode.MethodNotAllowed && get.Headers.CacheControl?.NoStore == true, "preview GET is 405 and no-store");
    using (var getApply = await client.GetAsync("/api/core/event-import-apply?id=" + Uri.EscapeDataString(eventId)))
        Check(getApply.StatusCode == HttpStatusCode.MethodNotAllowed && getApply.Headers.CacheControl?.NoStore == true, "apply GET is 405 and no-store");

    using (var noOrigin = await Send("event-import-preview", JsonSerializer.Serialize(new { csv = Csv }), "id=" + eventId, origin: false))
        Check(noOrigin.StatusCode == HttpStatusCode.Forbidden && noOrigin.Headers.CacheControl?.NoStore == true, "missing Origin is rejected 403 no-store");
    using (var noCsrf = await Send("event-import-preview", JsonSerializer.Serialize(new { csv = Csv }), "id=" + eventId, token: false))
        Check(noCsrf.StatusCode == HttpStatusCode.BadRequest && noCsrf.Headers.CacheControl?.NoStore == true, "missing CSRF token is rejected 400 no-store");
    using (var repeatedId = await Preview(Csv, "id=" + eventId + "&id=" + eventId))
        Check(repeatedId.StatusCode == HttpStatusCode.BadRequest, "id must occur exactly once");
    using (var emptyId = await Preview(Csv, "id="))
        Check(emptyId.StatusCode == HttpStatusCode.BadRequest, "id must be nonempty");

    // Endpoint input is sourced from cookie identity; no actor query/body override is accepted.
    using (var noActor = await Send("event-import-preview", JsonSerializer.Serialize(new { csv = Csv, actor = "manager" }), "id=" + eventId))
        Check(noActor.StatusCode == HttpStatusCode.BadRequest, "actor body override rejected as unknown JSON field");
    var memberCsrf = await CsrfFor("member");
    using (var anonymousNoCsrf = await Send("event-import-preview", "{", "id=" + eventId, token: false, cookie: ""))
        Check(anonymousNoCsrf.StatusCode == HttpStatusCode.Forbidden, "anonymous request is authorized before CSRF or malformed-body checks");
    using (var anonymousCsrf = await Send("event-import-preview", "{", "id=" + eventId, cookie: cookieValue))
        Check(anonymousCsrf.StatusCode == HttpStatusCode.Forbidden, "anonymous request with a CSRF token is denied before body parsing");
    using (var anonymousOversize = await Send("event-import-preview", oversizedBody, "id=" + eventId, token: false, cookie: ""))
        Check(anonymousOversize.StatusCode == HttpStatusCode.Forbidden, "anonymous oversized JSON is denied before 413");
    using (var anonymousApply = await Send("event-import-apply", "{", "id=" + eventId, token: false, cookie: ""))
        Check(anonymousApply.StatusCode == HttpStatusCode.Forbidden, "anonymous apply request is denied before CSRF/body checks");
    using (var memberMalformed = await Send("event-import-preview", "{", "id=" + eventId,
        cookie: "test-auth=member; " + memberCsrf.Cookie, csrfTokenOverride: memberCsrf.Token))
        Check(memberMalformed.StatusCode == HttpStatusCode.Forbidden, "non-manager is denied before malformed JSON parsing");
    using (var memberOversize = await Send("event-import-preview", oversizedBody, "id=" + eventId,
        cookie: "test-auth=member; " + memberCsrf.Cookie, csrfTokenOverride: memberCsrf.Token))
        Check(memberOversize.StatusCode == HttpStatusCode.Forbidden, "non-manager is denied before declared-length 413");
    using (var memberApplyOversize = await Send("event-import-apply", oversizedBody, "id=" + eventId,
        cookie: "test-auth=member; " + memberCsrf.Cookie, csrfTokenOverride: memberCsrf.Token))
        Check(memberApplyOversize.StatusCode == HttpStatusCode.Forbidden, "non-manager apply is denied before declared-length 413");
    using (var noSession = await Send("event-import-preview", "{", "id=" + eventId,
        cookie: "test-auth=manager-nosession; " + cookieValue))
        Check(noSession.StatusCode == HttpStatusCode.Forbidden, "missing current session claim is rejected before body parsing");
    using (var forgedQuery = await Send("event-import-preview", JsonSerializer.Serialize(new { csv = Csv }), "id=" + eventId + "&actor=manager",
        cookie: "test-auth=member; " + memberCsrf.Cookie, csrfTokenOverride: memberCsrf.Token))
    {
        var forgedBody = await forgedQuery.Content.ReadAsStringAsync();
        Check(forgedQuery.StatusCode == HttpStatusCode.Forbidden, "query actor cannot override cookie identity (" + (int)forgedQuery.StatusCode + ": " + forgedBody + ")");
    }

    using (var missing = await Send("event-import-preview", "{}", "id=" + eventId))
        Check(missing.StatusCode == HttpStatusCode.BadRequest, "missing required JSON field rejected");
    using (var unknown = await Send("event-import-preview", "{\"csv\":\"" + Csv.Replace("\n", "\\n") + "\",\"extra\":true}", "id=" + eventId))
        Check(unknown.StatusCode == HttpStatusCode.BadRequest, "unknown JSON field rejected");
    using (var malformed = await Send("event-import-preview", "{\"csv\":", "id=" + eventId))
        Check(malformed.StatusCode == HttpStatusCode.BadRequest, "malformed JSON rejected");
    using (var wrongContentType = await Send("event-import-preview", JsonSerializer.Serialize(new { csv = Csv }), "id=" + eventId, contentType: "text/plain"))
        Check(wrongContentType.StatusCode == HttpStatusCode.BadRequest && wrongContentType.Headers.CacheControl?.NoStore == true,
            "unsupported Content-Type returns 400 no-store");

    var snapshotBeforePreview = await Snapshot(app.Services);
    using var validPreview = await Preview(Csv);
    Check(validPreview.StatusCode == HttpStatusCode.OK && validPreview.Headers.CacheControl?.NoStore == true,
        "cookie-authenticated valid preview succeeds no-store");
    var previewDocument = JsonDocument.Parse(await validPreview.Content.ReadAsStringAsync());
    Check(await Snapshot(app.Services) == snapshotBeforePreview, "valid preview is read-only");
    var version = previewDocument.RootElement.GetProperty("rowVersion").GetInt64();
    var fingerprint = previewDocument.RootElement.GetProperty("fingerprint").GetString()!;

    var largeCsv = "Kind,Title,Artist,Duration,Prep,Parts\n" + string.Concat(Enumerable.Repeat("Song,Duplicate,Artist,0:01,,Vo\n", 2500));
    Check(Encoding.UTF8.GetByteCount(largeCsv) > 65_536 && Encoding.UTF8.GetByteCount(largeCsv) <= 1_048_576, "large-boundary fixture exceeds 65 KiB but stays under 1 MiB");
    using (var largePreview = await Preview(largeCsv))
        Check(largePreview.StatusCode == HttpStatusCode.OK && largePreview.Headers.CacheControl?.NoStore == true,
            "CSV over 65 KiB and within 1 MiB accepted by HTTP boundary");

    const string controlPrefix = "Kind,Title,Duration,UserToken\nSong,Assigned,0:01,";
    var escapedCsv = controlPrefix + new string('\u0001', 1_048_576 - Encoding.UTF8.GetByteCount(controlPrefix));
    var escapedJson = JsonSerializer.Serialize(new { csv = escapedCsv });
    Check(Encoding.UTF8.GetByteCount(escapedCsv) == 1_048_576 && Encoding.UTF8.GetByteCount(escapedJson) > 6_000_000
        && Encoding.UTF8.GetByteCount(escapedJson) <= 7 * 1024 * 1024,
        "1 MiB UTF-8 CSV with sixfold JSON escaping fits the bounded envelope");
    using (var escapedPreview = await Send("event-import-preview", escapedJson, "id=" + eventId))
        Check(escapedPreview.StatusCode == HttpStatusCode.OK && escapedPreview.Headers.CacheControl?.NoStore == true,
            "escaped JSON envelope for maximum UTF-8 CSV is accepted");

    var apply = new EventImportApply(Guid.NewGuid().ToString("D"), version, fingerprint, Csv, true, false);
    var applyJson = JsonSerializer.Serialize(apply, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    using (var applyMissing = await Send("event-import-apply", "{}", "id=" + eventId))
        Check(applyMissing.StatusCode == HttpStatusCode.BadRequest, "apply missing required fields rejected");
    using (var applyUnknown = await Send("event-import-apply", JsonSerializer.Serialize(new
        { requestId = apply.RequestId, rowVersion = apply.RowVersion, previewFingerprint = apply.PreviewFingerprint,
            csv = apply.Csv, confirmed = apply.Confirmed, confirmOverrun = apply.ConfirmOverrun, actor = "member" }), "id=" + eventId))
        Check(applyUnknown.StatusCode == HttpStatusCode.BadRequest, "apply unknown actor field rejected");
    using (var applyMalformed = await Send("event-import-apply", "{\"requestId\":", "id=" + eventId))
        Check(applyMalformed.StatusCode == HttpStatusCode.BadRequest, "apply malformed JSON rejected");
    using var accepted = await Send("event-import-apply", applyJson, "id=" + eventId);
    Check(accepted.StatusCode == HttpStatusCode.OK && accepted.Headers.CacheControl?.NoStore == true, "apply accepted and no-store");
    var acceptance = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
    Check(!acceptance.RootElement.GetProperty("replayed").GetBoolean(), "first apply returns a new receipt");
    using (var replay = await Send("event-import-apply", applyJson, "id=" + eventId))
    {
        var replayDoc = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Check(replay.StatusCode == HttpStatusCode.OK && replayDoc.RootElement.GetProperty("replayed").GetBoolean(), "identical apply retry replays receipt");
    }
    var stale = apply with { RequestId = Guid.NewGuid().ToString("D") };
    using (var staleResponse = await Send("event-import-apply", JsonSerializer.Serialize(stale, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "id=" + eventId))
        Check(staleResponse.StatusCode == HttpStatusCode.Conflict && staleResponse.Headers.CacheControl?.NoStore == true, "stale apply returns 409 no-store");
    const string expandedPrefix = "Kind,Title,Duration,UserToken,Parts\nSong,Assigned,0:01,";
    const string expandedSuffix = ",Vo\nSong,Expanded JSON,0:01,,Vo";
    var expandedCsv = expandedPrefix + new string('\u0001', 1_048_576 - Encoding.UTF8.GetByteCount(expandedPrefix + expandedSuffix)) + expandedSuffix;
    var expandedPreviewJson = JsonSerializer.Serialize(new { csv = expandedCsv });
    using var expandedPreview = await Send("event-import-preview", expandedPreviewJson, "id=" + eventId);
    Check(expandedPreview.StatusCode == HttpStatusCode.OK, "max-byte expanded CSV preview succeeds");
    var expandedPreviewDoc = JsonDocument.Parse(await expandedPreview.Content.ReadAsStringAsync());
    var expandedEdit = new EventImportApply(Guid.NewGuid().ToString("D"),
        expandedPreviewDoc.RootElement.GetProperty("rowVersion").GetInt64(),
        expandedPreviewDoc.RootElement.GetProperty("fingerprint").GetString()!, expandedCsv, true, false);
    var expandedApplyJson = JsonSerializer.Serialize(expandedEdit, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(Encoding.UTF8.GetByteCount(expandedApplyJson) > 6_000_000 && Encoding.UTF8.GetByteCount(expandedApplyJson) <= 7 * 1024 * 1024,
        "apply envelope with maximum UTF-8 CSV stays within 7 MiB despite JSON escaping");
    using (var expandedApply = await Send("event-import-apply", expandedApplyJson, "id=" + eventId))
        Check(expandedApply.StatusCode == HttpStatusCode.OK && expandedApply.Headers.CacheControl?.NoStore == true,
            "apply accepts 1 MiB UTF-8 CSV with sixfold JSON escaping");
    await using (var phaseScope = app.Services.CreateAsyncScope())
    {
        var core = phaseScope.ServiceProvider.GetRequiredService<CoreFoundationService>();
        var phasePlan = await core.ReadPhasePlanAsync(Who("manager"), eventId);
        await core.SavePhasePlanAsync(Who("manager"), eventId,
            new(phasePlan.RowVersion, true, phasePlan.Phases.Single(x => x.Kind == "View").Id, []));
    }
    using (var replayAfterPhase = await Send("event-import-apply", applyJson, "id=" + eventId))
    {
        var result = JsonDocument.Parse(await replayAfterPhase.Content.ReadAsStringAsync());
        Check(replayAfterPhase.StatusCode == HttpStatusCode.OK && result.RootElement.GetProperty("replayed").GetBoolean(),
            "preauthorization preserves receipt replay after event phase change");
    }
    using (var forbidden = await Send("event-import-preview", JsonSerializer.Serialize(new { csv = Csv }), "id=" + eventId,
        cookie: "test-auth=member; " + memberCsrf.Cookie, csrfTokenOverride: memberCsrf.Token))
        Check(forbidden.StatusCode == HttpStatusCode.Forbidden && forbidden.Headers.CacheControl?.NoStore == true, "unauthorized cookie actor returns 403 no-store");
    using (var badStamp = await Send("event-import-preview", JsonSerializer.Serialize(new { csv = Csv }), "id=" + eventId,
        cookie: "test-auth=manager-stale; " + cookieValue))
        Check(badStamp.StatusCode == HttpStatusCode.Forbidden, "stale security-stamp cookie returns 403");

    var tooLargeCsv = new string('x', 1_048_577);
    using (var overCsv = await Preview(tooLargeCsv))
        Check(overCsv.StatusCode == HttpStatusCode.BadRequest, "CSV above 1 MiB rejected by parser");

    using (var declared = await Send("event-import-preview", oversizedBody, "id=" + eventId))
        Check(declared.StatusCode == HttpStatusCode.RequestEntityTooLarge && declared.Headers.CacheControl?.NoStore == true,
            "declared oversized request returns 413 no-store");
    using (var streamed = await Send("event-import-preview", oversizedBody, "id=" + eventId, chunked: true))
        Check(streamed.StatusCode == HttpStatusCode.RequestEntityTooLarge && streamed.Headers.CacheControl?.NoStore == true,
            "chunked streamed oversized request returns 413 no-store");
    using (var applyDeclared = await Send("event-import-apply", oversizedBody, "id=" + eventId))
        Check(applyDeclared.StatusCode == HttpStatusCode.RequestEntityTooLarge, "apply declared oversized request returns 413 no-store");
    using (var applyStreamed = await Send("event-import-apply", oversizedBody, "id=" + eventId, chunked: true))
        Check(applyStreamed.StatusCode == HttpStatusCode.RequestEntityTooLarge, "apply chunked oversized request returns 413 no-store");

    Console.WriteLine($"PASS total={checks}");
}
finally
{
    if (started) await app.StopAsync();
    await app.DisposeAsync();
}

async Task<string> Snapshot(IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    return JsonSerializer.Serialize(new
    {
        Events = await db.SessionEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Songs = await db.Songs.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Parts = await db.TimeParts.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        Operations = await db.Set<EventImportOperation>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()
    });
}

ClaimsPrincipal Who(string id) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier, id), new Claim("AspNet.Identity.SecurityStamp", id + "-stamp"),
    new Claim(PasskeyAdditionService.SessionClaim, id + "-session")], "SyntheticCookie"));

sealed class SyntheticCookieHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var value = Request.Cookies["test-auth"];
        if (string.IsNullOrEmpty(value)) return Task.FromResult(AuthenticateResult.NoResult());
        var stale = value.EndsWith("-stale", StringComparison.Ordinal);
        var noSession = value.EndsWith("-nosession", StringComparison.Ordinal);
        var suffixLength = stale ? 6 : noSession ? 10 : 0;
        var id = suffixLength == 0 ? value : value[..^suffixLength];
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id),
            new("AspNet.Identity.SecurityStamp", stale ? "invalid-stamp" : id + "-stamp")
        };
        if (!noSession) claims.Add(new(PasskeyAdditionService.SessionClaim, id + "-session"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

sealed class NoopPushTransport : IEventPushTransport
{
    public bool Configured => false;
    public Task Send(PushDevice device, EventPushPayload payload, int ttl, CancellationToken ct) => Task.CompletedTask;
}

sealed class NonSeekableReadStream(byte[] bytes) : MemoryStream(bytes)
{
    public override bool CanSeek => false;
    public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
}
