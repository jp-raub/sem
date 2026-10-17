using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

const string Origin = "https://sem-invitations-test.invalid";
var root = Path.Combine(Path.GetTempPath(), "sem-invitation-management-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var stamps = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["admin"] = "stamp-admin-current",
    ["owner-a"] = "stamp-owner-a-current",
    ["owner-b"] = "stamp-owner-b-current",
    ["issuer"] = "stamp-issuer-current",
    ["requester-a"] = "stamp-requester-a-current",
    ["requester-b"] = "stamp-requester-b-current",
    ["registered-user"] = "stamp-registered-current",
    ["disabled-user"] = "stamp-disabled-current",
    ["unconfirmed-user"] = "stamp-unconfirmed-current"
};
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
builder.Services.AddSingleton(new DeploymentSettings { PublicUrl = Origin + "/" });
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddScoped<InvitationManagementService>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "test-csrf";
    options.Cookie.SecurePolicy = CookieSecurePolicy.None;
    options.Cookie.HttpOnly = true;
    options.Cookie.Path = "/";
});
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "TestSelector";
        options.DefaultChallengeScheme = "TestSelector";
        options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
    })
    .AddPolicyScheme("TestSelector", "Synthetic-or-Identity.Application", options =>
    {
        options.ForwardDefaultSelector = context => context.Request.Cookies.ContainsKey("test-application")
            ? IdentityConstants.ApplicationScheme
            : "Synthetic";
    })
    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Synthetic", _ => { })
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        options.Cookie.Name = "test-application";
        options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        options.Cookie.HttpOnly = true;
        options.Cookie.Path = "/";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
        options.SlidingExpiration = false;
    });

var app = builder.Build();
app.Use(async (context, next) =>
{
    var original = context.Request.Body;
    var meter = new CountingReadStream(original);
    context.Request.Body = meter;
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Test-Body-Read"] = meter.BytesRead.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Test-Content-Length"] =
            context.Request.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? "null";
        return Task.CompletedTask;
    });
    try { await next(); }
    finally { context.Request.Body = original; }
});
app.UseAuthentication();
app.UseSemLiveAuthenticationSession();
app.UseAntiforgery();
app.MapGet("/fixture/csrf", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Json(new { token = tokens.RequestToken });
});
app.MapGet("/fixture/application-cookie/{userId}", async (HttpContext context, string userId,
    IOptions<IdentityOptions> identityOptions) =>
{
    if (!stamps.TryGetValue(userId, out var currentStamp)) return Results.NotFound();
    var stamp = context.Request.Query["stamp"].FirstOrDefault() ?? currentStamp;
    var session = context.Request.Query["session"].FirstOrDefault() ?? ("cookie-session-" + userId);
    var claimTypes = identityOptions.Value.ClaimsIdentity;
    var principal = new ClaimsPrincipal(new ClaimsIdentity(
    [
        new Claim(claimTypes.UserIdClaimType, userId),
        new Claim(claimTypes.SecurityStampClaimType, stamp),
        new Claim(PasskeyAdditionService.SessionClaim, session)
    ], IdentityConstants.ApplicationScheme));
    await context.SignInAsync(IdentityConstants.ApplicationScheme, principal,
        new AuthenticationProperties { IsPersistent = false });
    return Results.NoContent();
});
app.MapInvitationManagement();

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
        db.Users.AddRange(
            User("admin", "admin@example.test", "Admin", stamps["admin"], isAdmin: true),
            User("owner-a", "owner-a@example.test", "Owner A", stamps["owner-a"]),
            User("owner-b", "owner-b@example.test", "Owner B", stamps["owner-b"]),
            User("issuer", "issuer@example.test", "Issuer", stamps["issuer"]),
            User("requester-a", "requester-a@example.test", "Requester A", stamps["requester-a"]),
            User("requester-b", "requester-b@example.test", "Requester B", stamps["requester-b"]),
            User("registered-user", "registered@example.test", "Registered", stamps["registered-user"], invitationId: "used-registered"),
            User("disabled-user", "disabled@example.test", "Disabled", stamps["disabled-user"], enabled: false),
            User("unconfirmed-user", "unconfirmed@example.test", "Unconfirmed", stamps["unconfirmed-user"], emailConfirmed: false));
        db.Invitations.AddRange(SeedInvitations());
        db.InvitationRequests.AddRange(SeedRequests());
        db.RevokedAuthSessions.Add(new RevokedAuthSession
        {
            UserId = "admin",
            TokenHash = SemEndpoints.Hash("revoked-cookie-session"),
            RevokedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        });
        await db.SaveChangesAsync();
    }

    await app.StartAsync();
    started = true;
    var address = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(address)
    };

    var csrf = await GetCsrfAsync(client);
    var csrfByActor = new Dictionary<(string Actor, string Stamp, string Session), (string Token, string Cookie)>
    {
        [("admin", stamps["admin"], "synthetic-session-admin")] = csrf
    };
    var adminCookie = await GetApplicationCookieAsync(client, "admin");
    var staleAdminCookie = await GetApplicationCookieAsync(client, "admin", "old-admin-stamp", "stale-cookie-session");
    var revokedAdminCookie = await GetApplicationCookieAsync(client, "admin", session: "revoked-cookie-session");

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body = null,
        string? actor = null, string? stamp = null, string? session = null, string? applicationCookie = null,
        bool includeCsrfHeader = true, string? origin = Origin, HttpContent? suppliedContent = null,
        bool duplicateCsrfHeader = false, bool includeCsrfCookie = true)
    {
        var effectiveStamp = actor is null ? null : stamp ?? stamps.GetValueOrDefault(actor, "unknown-stamp");
        var effectiveSession = actor is null ? null : session ?? "synthetic-session-" + actor;
        var requestCsrf = csrf;
        if (actor is not null && applicationCookie is null)
        {
            var key = (actor, effectiveStamp!, effectiveSession!);
            if (!csrfByActor.TryGetValue(key, out requestCsrf))
            {
                requestCsrf = await GetCsrfAsync(client, actor, effectiveStamp, effectiveSession);
                csrfByActor[key] = requestCsrf;
            }
        }
        var request = new HttpRequestMessage(method, path);
        if (suppliedContent is not null) request.Content = suppliedContent;
        else if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var cookies = new List<string>();
        if (includeCsrfCookie) cookies.Add(requestCsrf.Cookie);
        if (applicationCookie is not null) cookies.Add(applicationCookie);
        if (cookies.Count > 0) request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        if (actor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Actor", actor);
            request.Headers.TryAddWithoutValidation("X-Test-Stamp", effectiveStamp);
            request.Headers.TryAddWithoutValidation("X-Test-Session", effectiveSession);
        }
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (includeCsrfHeader)
        {
            if (duplicateCsrfHeader)
                request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", new[] { requestCsrf.Token, requestCsrf.Token });
            else request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", requestCsrf.Token);
        }
        return await client.SendAsync(request);
    }

    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations", applicationCookie: adminCookie))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "real Identity.Application cookie authenticates");
        Check(response.Headers.CacheControl?.NoStore == true, "list response is no-store");
    }
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations", actor: "not-a-user"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "unknown actor denied");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "anonymous list denied");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations", applicationCookie: staleAdminCookie))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "old security-stamp application cookie denied");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations", applicationCookie: revokedAdminCookie))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "revoked application-cookie session denied");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations", actor: "disabled-user"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "disabled synthetic account denied");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations", actor: "unconfirmed-user"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "unconfirmed synthetic account denied");

    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations", actor: "owner-a"))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "member inventory succeeds");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var page = json.RootElement;
        Check(page.GetProperty("actorId").GetString() == "owner-a" && !page.GetProperty("isAdmin").GetBoolean(),
            "member page reports live actor and role");
        Check(page.GetProperty("page").GetInt32() == 1 && page.GetProperty("pageSize").GetInt32() == 50,
            "default page and fixed page size");
        Check(page.GetProperty("hasMoreInvitations").GetBoolean() && page.GetProperty("hasMoreRequests").GetBoolean(),
            "51-row lookahead advertises both next pages");
        var invitations = page.GetProperty("invitations");
        var requests = page.GetProperty("requests");
        Check(invitations.GetArrayLength() == 50 && requests.GetArrayLength() == 50,
            "member page returns at most 50 rows of each kind");
        Check(invitations[0].GetProperty("id").GetString() == "tie-a" &&
              invitations[1].GetProperty("id").GetString() == "tie-b", "created ties sort by id");
        Check(invitations.EnumerateArray().All(x => x.GetProperty("ownerId").GetString() == "owner-a") &&
              requests.EnumerateArray().All(x => x.GetProperty("userId").GetString() == "owner-a"),
            "member sees only own invitations and requests");
        Check(invitations.EnumerateArray().All(x => x.GetProperty("id").GetString() != "owner-b-only") &&
              requests.EnumerateArray().All(x => x.GetProperty("id").GetString() != "request-b-only"),
            "other owner and requester rows are excluded");
        var unknownIssuer = invitations.EnumerateArray().Single(x => x.GetProperty("id").GetString() == "used-registered");
        Check(unknownIssuer.GetProperty("issuerName").GetString() == "不明", "missing issuer display name uses fallback");
        Check(unknownIssuer.GetProperty("purpose").ValueKind == JsonValueKind.Null &&
              unknownIssuer.GetProperty("issuanceSource").ValueKind == JsonValueKind.Null,
            "historical invitation retains null issuance metadata");
        var unknownDecider = requests.EnumerateArray().Single(x => x.GetProperty("id").GetString() == "request-a-00");
        Check(unknownDecider.GetProperty("deciderName").GetString() == "不明", "missing decider display name uses fallback");
        Check(unknownDecider.GetProperty("decisionComment").ValueKind == JsonValueKind.Null,
            "historical request comments remain null");
        CheckExactKeys(page, ["actorId", "isAdmin", "page", "pageSize", "hasMoreInvitations", "hasMoreRequests", "invitations", "requests"]);
        CheckExactKeys(invitations[0], ["id", "ownerId", "ownerName", "issuerId", "issuerName", "requestId", "limit", "uses", "created", "expires", "disabled", "purpose", "issuanceSource", "revokedAt", "revokedBy", "revokerName", "revocationReason"]);
        CheckExactKeys(requests[0], ["id", "userId", "userName", "reason", "count", "days", "status", "created", "decidedAt", "decidedBy", "deciderName", "decisionComment"]);
        var wire = page.GetRawText();
        Check(!wire.Contains("TOKEN_HASH_SECRET", StringComparison.Ordinal) &&
              !wire.Contains("PROTECTED_TOKEN_SECRET", StringComparison.Ordinal) &&
              !wire.Contains("@example.test", StringComparison.Ordinal) &&
              !wire.Contains("BEARER_SECRET", StringComparison.Ordinal),
            "response excludes token material, bearer markers, and email addresses");
    }
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations?page=2", actor: "owner-a"))
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("invitations").GetArrayLength() == 1 &&
              json.RootElement.GetProperty("requests").GetArrayLength() == 1 &&
              !json.RootElement.GetProperty("hasMoreInvitations").GetBoolean() &&
              !json.RootElement.GetProperty("hasMoreRequests").GetBoolean(), "member page two returns final rows");
    }
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations", actor: "requester-b"))
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var page = json.RootElement;
        Check(response.StatusCode == HttpStatusCode.OK && page.GetProperty("invitations").GetArrayLength() == 0 &&
              page.GetProperty("requests").GetArrayLength() == 1 &&
              page.GetProperty("requests")[0].GetProperty("userId").GetString() == "requester-b",
            "requester can see own request without gaining another user's invitations");
    }
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations?page=10000", actor: "owner-a"))
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("page").GetInt32() == 10000 &&
              json.RootElement.GetProperty("invitations").GetArrayLength() == 0,
            "maximum supported page is accepted");
    }
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations", applicationCookie: adminCookie))
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var page = json.RootElement;
        Check(response.StatusCode == HttpStatusCode.OK && page.GetProperty("isAdmin").GetBoolean(), "admin inventory succeeds");
        Check(page.GetProperty("invitations").EnumerateArray().Any(x => x.GetProperty("id").GetString() == "owner-b-only") &&
              page.GetProperty("requests").EnumerateArray().Any(x => x.GetProperty("id").GetString() == "request-b-only"),
            "admin sees records across owners and requesters");
        Check(page.GetProperty("invitations").GetArrayLength() == 50 && page.GetProperty("hasMoreInvitations").GetBoolean(),
            "admin inventory uses fixed-size pagination");
    }
    foreach (var invalidPage in new[] { "?page=0", "?page=10001", "?page=abc", "?page=1&page=2", "?other=1" })
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/core/invitations" + invalidPage, actor: "owner-a");
        Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid page query rejected " + invalidPage);
    }
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations", actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed && response.Content.Headers.Allow.Contains("GET"),
            "inventory endpoint rejects wrong method with Allow");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations/revoke?expectedActor=admin", actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed && response.Content.Headers.Allow.Contains("POST"),
            "revoke endpoint rejects wrong method with Allow");

    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke",
        body: "{", actor: "admin", includeCsrfHeader: false))
    {
        Check(response.StatusCode == HttpStatusCode.BadRequest,
            "new invitation API requires expectedActor");
        Check(response.Headers.TryGetValues("X-Test-Body-Read", out var read) && read.Single() == "0",
            "missing expectedActor leaves request body unread");
    }
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=owner-a",
        body: "{", actor: "admin", includeCsrfHeader: false))
    {
        Check(response.StatusCode == HttpStatusCode.Forbidden, "expectedActor mismatch denied before CSRF and JSON");
        Check(response.Headers.TryGetValues("X-Test-Body-Read", out var read) && read.Single() == "0",
            "expectedActor mismatch leaves request body unread");
    }
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/core/invitations/revoke?expectedActor=admin&expectedActor=admin", body: "{", actor: "admin", includeCsrfHeader: false))
    {
        Check(response.StatusCode == HttpStatusCode.BadRequest, "duplicate expectedActor rejected");
        Check(response.Headers.TryGetValues("X-Test-Body-Read", out var read) && read.Single() == "0",
            "invalid expectedActor query leaves request body unread");
    }
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
        body: "{", actor: "admin", origin: "https://foreign.invalid", includeCsrfHeader: false))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "foreign Origin denied before body parsing");
    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin")
    {
        Content = new StringContent("{", Encoding.UTF8, "application/json")
    })
    {
        request.Headers.TryAddWithoutValidation("Cookie", csrf.Cookie);
        request.Headers.TryAddWithoutValidation("X-Test-Actor", "admin");
        request.Headers.TryAddWithoutValidation("X-Test-Stamp", stamps["admin"]);
        request.Headers.TryAddWithoutValidation("X-Test-Session", "synthetic-session-admin");
        request.Headers.TryAddWithoutValidation("Origin", new[] { Origin, Origin });
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.Forbidden, "multiple Origin values denied");
    }
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
        body: "{", actor: "owner-a", includeCsrfHeader: false))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "non-admin cannot revoke");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
        body: "{", actor: "admin", includeCsrfHeader: false, origin: null))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "missing Origin denied");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
        body: "{", actor: "admin", includeCsrfHeader: false, duplicateCsrfHeader: true))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "missing CSRF header denied");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
        body: "{}", actor: "admin", duplicateCsrfHeader: true))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "multiple CSRF header values rejected");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
        body: "{\"id\":\"form-target\",\"reason\":\"test\",\"explicitlyConfirmed\":true}",
        actor: "admin", includeCsrfHeader: false,
        suppliedContent: new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf.Token,
            ["id"] = "form-target", ["reason"] = "test", ["explicitlyConfirmed"] = "true"
        })))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "form-token CSRF fallback is rejected");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
        body: "{\"id\":\"form-target\",\"reason\":\"x\",\"explicitlyConfirmed\":true}",
        actor: "admin", includeCsrfHeader: false))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "JSON revoke requires exact CSRF header");

    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
        body: new string('x', 4097), actor: "admin", includeCsrfHeader: false))
        Check((int)response.StatusCode == 413, "oversized Content-Length rejected before CSRF");
    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin"))
    {
        request.Content = new StreamContent(new NonSeekableReadStream(Encoding.UTF8.GetBytes(new string('x', 4097))));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = true;
        request.Headers.TryAddWithoutValidation("Cookie", csrf.Cookie);
        request.Headers.TryAddWithoutValidation("X-Test-Actor", "admin");
        request.Headers.TryAddWithoutValidation("X-Test-Stamp", stamps["admin"]);
        request.Headers.TryAddWithoutValidation("X-Test-Session", "synthetic-session-admin");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf.Token);
        using var response = await client.SendAsync(request);
        var status = (int)response.StatusCode;
        var receivedLength = response.Headers.TryGetValues("X-Test-Content-Length", out var lengths)
            ? lengths.Single()
            : "missing";
        Check(status == 413 && receivedLength == "null",
            $"chunked body bounded; status={status}; received Content-Length={receivedLength}");
    }

    var invalidBodies = new[]
    {
        "{",
        "{\"id\":\"used-registered\",\"id\":\"used-registered\",\"reason\":\"x\",\"explicitlyConfirmed\":true}",
        "{\"id\":\"used-registered\",\"reason\":\"x\",\"explicitlyConfirmed\":true,\"extra\":1}",
        "{\"id\":3,\"reason\":\"x\",\"explicitlyConfirmed\":true}",
        "{\"id\":\"used-registered\",\"reason\":\"x\",\"explicitlyConfirmed\":\"true\"}",
        "{\"id\":\"used-registered\",\"reason\":\"x\"}",
        "{\"id\":\"used-registered\",\"reason\":\"x\",\"explicitlyConfirmed\":false}"
    };
    foreach (var invalid in invalidBodies)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
            body: invalid, actor: "admin");
        Check(response.StatusCode == HttpStatusCode.BadRequest, "strict revoke JSON rejected " + invalid);
    }
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
        body: "{\"id\":\"missing-id\",\"reason\":\"valid\",\"explicitlyConfirmed\":true}", actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.NotFound, "valid request for unknown invitation returns 404");
    foreach (var (id, reason, label) in new[]
    {
        ("", "valid", "empty id"), (new string('i', 129), "valid", "long id"),
        ("bad\u0001id", "valid", "control in id"),
        ("used-registered", "", "empty reason"), ("used-registered", new string('r', 501), "long reason"),
        ("used-registered", "bad\nreason", "control in reason")
    })
    {
        var body = JsonSerializer.Serialize(new { id, reason, explicitlyConfirmed = true });
        using var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
            body: body, actor: "admin");
        Check(response.StatusCode == HttpStatusCode.BadRequest, "revoke bound/control validation: " + label);
    }

    const string validRevoke = "{\"id\":\"used-registered\",\"reason\":\"  compromised credential  \",\"explicitlyConfirmed\":true}";
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
        body: validRevoke, actor: "admin"))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "admin revocation succeeds");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("actorId").GetString() == "admin" &&
              json.RootElement.GetProperty("id").GetString() == "used-registered" &&
              json.RootElement.GetProperty("disabled").GetBoolean(), "revoke response contains only expected success fields");
        CheckExactKeys(json.RootElement, ["actorId", "id", "disabled"]);
    }
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/revoke?expectedActor=admin",
        body: "{\"id\":\"used-registered\",\"reason\":\"replacement reason\",\"explicitlyConfirmed\":true}", actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.OK, "repeated revocation is idempotent success");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var invitation = await db.Invitations.SingleAsync(x => x.Id == "used-registered");
        var account = await db.Users.SingleAsync(x => x.Id == "registered-user");
        Check(invitation.Disabled && invitation.RevokedBy == "admin" && invitation.RevokedAt is not null,
            "revocation records disabled state and administrator");
        Check(invitation.RevocationReason == "compromised credential", "revocation trims and preserves original reason on replay");
        Check(invitation.Uses == 1 && account.InvitationId == "used-registered" && account.Enabled,
            "revocation does not undo registration or invitation consumption");
        Check(await db.Audits.CountAsync(x => x.Action == "InvitationRevoked" && x.EntityId == "used-registered") == 1,
            "idempotent replay does not create a second audit");
        Check(await db.Invitations.AnyAsync(x => x.Id == "form-target" && !x.Disabled),
            "rejected form fallback has no revocation effect");
    }

    foreach (var (reason, count, days, label) in new[]
    {
        ("", 1, 1, "empty reason"), (new string('r', 501), 1, 1, "long reason"),
        ("bad\nreason", 1, 1, "control in reason"), ("valid", 0, 1, "count below range"),
        ("valid", 6, 1, "count above range"), ("valid", 1, 0, "days below range"),
        ("valid", 1, 8, "days above range")
    })
    {
        using var invalidRequest = await SendAsync(HttpMethod.Post,
            "/api/core/invitations/request?expectedActor=requester-a",
            body: JsonSerializer.Serialize(new { reason, count, days }), actor: "requester-a");
        Check(invalidRequest.StatusCode == HttpStatusCode.BadRequest, "request input validation: " + label);
    }
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/core/invitations/request?expectedActor=requester-a",
        body: "{\"reason\":\"valid\",\"count\":1,\"days\":1}", actor: "requester-a", includeCsrfHeader: false))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "request mutation requires the CSRF header");
    using (var invalidUtf8 = new ByteArrayContent(new byte[] { 0xC3 }))
    {
        invalidUtf8.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await SendAsync(HttpMethod.Post,
            "/api/core/invitations/request?expectedActor=requester-a",
            actor: "requester-a", suppliedContent: invalidUtf8);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "request API rejects malformed UTF-8");
    }

    using (var response = await SendAsync(HttpMethod.Post,
        "/api/core/invitations/request?expectedActor=requester-a",
        body: "{\"reason\":\"  family invitation  \",\"count\":3,\"days\":4}", actor: "requester-a"))
    {
        Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
            "member can create invitation request with no-store response");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = json.RootElement;
        CheckExactKeys(result, ["actorId", "id", "status"]);
        Check(result.GetProperty("actorId").GetString() == "requester-a" &&
              result.GetProperty("status").GetString() == "Pending", "request response returns only safe request identity");
        var requestId = result.GetProperty("id").GetString()!;

        using (var response2 = await SendAsync(HttpMethod.Post,
            "/api/core/invitations/decide?expectedActor=admin",
            body: JsonSerializer.Serialize(new { id = requestId, approve = true, comment = "  承認済みの判断メモ  " }),
            actor: "admin"))
        {
            Check(response2.StatusCode == HttpStatusCode.OK && response2.Headers.CacheControl?.NoStore == true,
                "global administrator can conditionally approve request with no-store response");
            using var decisionJson = JsonDocument.Parse(await response2.Content.ReadAsStringAsync());
            var decision = decisionJson.RootElement;
            Check(decision.GetProperty("status").GetString() == "Approved" &&
                  decision.GetProperty("decisionComment").GetString() == "承認済みの判断メモ" &&
                  decision.GetProperty("invitationId").ValueKind == JsonValueKind.String,
                "approval returns decision metadata without the bearer link");
            Check(!decisionJson.RootElement.GetRawText().Contains("invite=", StringComparison.Ordinal),
                "approval response never exposes the invitation bearer link");
        }
        using (var duplicateDecision = await SendAsync(HttpMethod.Post,
            "/api/core/invitations/decide?expectedActor=admin",
            body: JsonSerializer.Serialize(new { id = requestId, approve = true, comment = "duplicate" }),
            actor: "admin"))
            Check(duplicateDecision.StatusCode == HttpStatusCode.Conflict,
                "conditional Pending claim rejects a second decision without issuing again");

        using (var response3 = await SendAsync(HttpMethod.Get,
            "/api/core/invitations/link/" + Uri.EscapeDataString(
                await GetInvitationIdAsync(app.Services, requestId)) + "?expectedActor=requester-a",
            actor: "requester-a"))
        {
            Check(response3.StatusCode == HttpStatusCode.OK && response3.Headers.CacheControl?.NoStore == true,
                "request owner can fetch their active link with no-store");
            using var linkJson = JsonDocument.Parse(await response3.Content.ReadAsStringAsync());
            Check(linkJson.RootElement.GetProperty("link").GetString()!.Contains("#invite=", StringComparison.Ordinal) &&
                  linkJson.RootElement.GetProperty("limit").GetInt32() == 3 &&
                  linkJson.RootElement.GetProperty("uses").GetInt32() == 0,
                "owner link response includes only the owner's count, uses, expiry, and link");
        }

        var invitationId = await GetInvitationIdAsync(app.Services, requestId);
        using (var response4 = await SendAsync(HttpMethod.Get,
            "/api/core/invitations/link/" + Uri.EscapeDataString(invitationId) + "?expectedActor=admin",
            actor: "admin"))
            Check(response4.StatusCode == HttpStatusCode.NotFound, "administrator cannot retrieve another user's link");
        using (var response5 = await SendAsync(HttpMethod.Get,
            "/api/core/invitations/link/" + Uri.EscapeDataString(invitationId) + "?expectedActor=owner-b",
            actor: "owner-b"))
            Check(response5.StatusCode == HttpStatusCode.NotFound, "different owner cannot retrieve the private link");

        await using var verifyScope = app.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var createdRequest = await verifyDb.InvitationRequests.SingleAsync(x => x.Id == requestId);
        var createdInvitation = await verifyDb.Invitations.SingleAsync(x => x.RequestId == requestId);
        Check(createdRequest.Count == 3 && createdRequest.Days == 4 &&
              createdRequest.DecisionComment == "承認済みの判断メモ",
            "request values and optional comment are persisted without backfilling old rows");
        Check(createdInvitation.Limit == 3 && createdInvitation.Expires == createdInvitation.Created + 4 * 86400L &&
              createdInvitation.Purpose is null && createdInvitation.IssuanceSource == "Request",
            "request issuance uses saved count/days, null purpose, and Request source");
        Check(await verifyDb.Messages.CountAsync(x => x.UserId == "admin" && x.Title == "招待リンク発行リクエスト") == 1 &&
              await verifyDb.Messages.CountAsync(x => x.UserId == "requester-a" && x.Title == "招待リクエストが許可されました") == 1,
            "request and decision messages are persisted for the intended recipients");
        Check(await verifyDb.Audits.AnyAsync(x => x.Action == "InvitationRequested" && x.EntityId == requestId) &&
              await verifyDb.Audits.AnyAsync(x => x.Action == "InvitationApproved" && x.EntityId == requestId) &&
              await verifyDb.Audits.AnyAsync(x => x.Action == "InvitationIssued" && x.EntityId == createdInvitation.Id),
            "request, approval, issuance audits persist with the transaction");

        var uniqueBlocked = false;
        verifyDb.Invitations.Add(new Invitation
        {
            Id = "duplicate-request-invitation",
            TokenHash = SemEndpoints.Hash("duplicate-request-token"),
            ProtectedToken = "protected",
            OwnerId = "requester-a",
            IssuerId = "admin",
            RequestId = requestId,
            Limit = 3,
            Created = createdInvitation.Created,
            Expires = createdInvitation.Expires
        });
        try { await verifyDb.SaveChangesAsync(); }
        catch (DbUpdateException) { uniqueBlocked = true; }
        Check(uniqueBlocked, "filtered unique index rejects a second invitation for the same request");
    }

    using (var response = await SendAsync(HttpMethod.Post,
        "/api/core/invitations/request?expectedActor=admin",
        body: "{\"reason\":\"admin cannot request\",\"count\":1,\"days\":1}", actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "global administrators use direct issuance instead of requests");
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/core/invitations/request?expectedActor=requester-a",
        body: "{\"reason\":\"x\",\"reason\":\"y\",\"count\":1,\"days\":1}", actor: "requester-a"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "request JSON rejects duplicate keys");

    using (var response = await SendAsync(HttpMethod.Post,
        "/api/core/invitations/decide?expectedActor=admin",
        body: JsonSerializer.Serialize(new { id = "request-b-only", approve = false, comment = new string('c', 501) }),
        actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "decision comment limit validated before claim");
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/core/invitations/decide?expectedActor=owner-a",
        body: "{\"id\":\"request-b-only\",\"approve\":false,\"comment\":\"\"}", actor: "owner-a"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "non-admin cannot decide invitation requests");
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/core/invitations/decide?expectedActor=admin",
        body: "{\"id\":\"request-b-only\",\"approve\":false,\"comment\":\"\"}", actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.OK, "global administrator can reject pending request");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rejected = await db.InvitationRequests.SingleAsync(x => x.Id == "request-b-only");
        Check(rejected.Status == "Rejected" && rejected.DecisionComment is null &&
              !await db.Invitations.AnyAsync(x => x.RequestId == "request-b-only"),
            "rejection stores no comment or invitation when the optional comment is blank");
    }

    var legacyCases = new[]
    {
        (Route: "/api/request-invitation", Actor: "requester-b",
            Unknown: "{\"reason\":\"legacy request\",\"extra\":true}",
            Duplicate: "{\"reason\":\"legacy request\",\"reason\":\"duplicate\"}"),
        (Route: "/api/decide-invitation", Actor: "admin",
            Unknown: "{\"id\":\"request-b-only\",\"approve\":false,\"extra\":true}",
            Duplicate: "{\"id\":\"request-b-only\",\"approve\":false,\"approve\":true}"),
        (Route: "/api/revoke-invitation", Actor: "admin",
            Unknown: "{\"id\":\"form-target\",\"reason\":\"test\",\"confirmed\":true,\"extra\":true}",
            Duplicate: "{\"id\":\"form-target\",\"id\":\"form-target\",\"reason\":\"test\",\"confirmed\":true}")
    };
    foreach (var legacy in legacyCases)
    {
        var target = legacy.Route + "?expectedActor=" + Uri.EscapeDataString(legacy.Actor);
        using (var unknown = await SendAsync(HttpMethod.Post, target, body: legacy.Unknown, actor: legacy.Actor))
            Check(unknown.StatusCode == HttpStatusCode.BadRequest && unknown.Headers.CacheControl?.NoStore == true,
                "legacy strict JSON rejects unknown property: " + legacy.Route);
        using (var duplicate = await SendAsync(HttpMethod.Post, target, body: legacy.Duplicate, actor: legacy.Actor))
            Check(duplicate.StatusCode == HttpStatusCode.BadRequest && duplicate.Headers.CacheControl?.NoStore == true,
                "legacy strict JSON rejects duplicate property: " + legacy.Route);
        using (var oversized = await SendAsync(HttpMethod.Post, target,
            body: new string(' ', 4097), actor: legacy.Actor))
            Check((int)oversized.StatusCode == 413 && oversized.Headers.CacheControl?.NoStore == true,
                "legacy endpoint enforces the 4 KiB body limit: " + legacy.Route);
        using (var anonymous = await SendAsync(HttpMethod.Post, target, body: legacy.Unknown, actor: null))
            Check(anonymous.StatusCode == HttpStatusCode.Forbidden && anonymous.Headers.CacheControl?.NoStore == true,
                "legacy endpoint rejects anonymous actor: " + legacy.Route);
    }
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/request-invitation?expectedActor=requester-b",
        body: "{\"reason\":\"actor mismatch\"}", actor: "requester-a"))
        Check(response.StatusCode == HttpStatusCode.Forbidden,
            "legacy request rejects a live actor that differs from expectedActor");
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/request-invitation?expectedActor=requester-b&expectedActor=requester-b",
        body: "{\"reason\":\"invalid expected actor\"}", actor: "requester-b"))
        Check(response.StatusCode == HttpStatusCode.BadRequest,
            "legacy request rejects duplicate expectedActor before mutation");
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/request-invitation?unexpected=value",
        body: "{\"reason\":\"unknown query\"}", actor: "requester-b"))
        Check(response.StatusCode == HttpStatusCode.BadRequest,
            "legacy request rejects unknown query parameters");
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/request-invitation?expectedActor=requester-b",
        body: "{\"reason\":\"foreign origin\"}", actor: "requester-b", origin: "https://foreign.invalid"))
        Check(response.StatusCode == HttpStatusCode.Forbidden,
            "legacy request rejects a foreign Origin");
    using (var request = new HttpRequestMessage(HttpMethod.Post,
        "/api/revoke-invitation?expectedActor=admin")
    {
        Content = new StringContent("{", Encoding.UTF8, "application/json")
    })
    {
        request.Headers.TryAddWithoutValidation("Cookie", csrf.Cookie);
        request.Headers.TryAddWithoutValidation("X-Test-Actor", "admin");
        request.Headers.TryAddWithoutValidation("X-Test-Stamp", stamps["admin"]);
        request.Headers.TryAddWithoutValidation("X-Test-Session", "synthetic-session-admin");
        request.Headers.TryAddWithoutValidation("Origin", new[] { Origin, Origin });
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.Forbidden && response.Headers.CacheControl?.NoStore == true,
            "legacy revoke rejects multiple Origin values");
    }
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/decide-invitation?expectedActor=admin",
        body: "{\"id\":\"request-b-only\",\"approve\":false}", actor: "admin", origin: null))
        Check(response.StatusCode == HttpStatusCode.Forbidden,
            "legacy decision rejects missing Origin");
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/request-invitation?expectedActor=requester-b",
        body: "{\"reason\":\"missing csrf\"}", actor: "requester-b", includeCsrfHeader: false))
        Check(response.StatusCode == HttpStatusCode.BadRequest,
            "legacy request requires the CSRF header");
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/decide-invitation?expectedActor=admin",
        body: "{\"id\":\"request-b-only\",\"approve\":false}", actor: "admin", duplicateCsrfHeader: true))
        Check(response.StatusCode == HttpStatusCode.BadRequest,
            "legacy decision rejects multiple CSRF header values");
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/revoke-invitation?expectedActor=admin",
        body: "{\"id\":\"form-target\",\"reason\":\"test\",\"confirmed\":true}",
        actor: "admin", includeCsrfHeader: false))
        Check(response.StatusCode == HttpStatusCode.BadRequest,
            "legacy revoke requires the explicit CSRF header");
    using (var malformedUtf8 = new ByteArrayContent(new byte[] { 0xC3 }))
    {
        malformedUtf8.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await SendAsync(HttpMethod.Post,
            "/api/request-invitation?expectedActor=requester-b",
            actor: "requester-b", suppliedContent: malformedUtf8);
        Check(response.StatusCode == HttpStatusCode.BadRequest,
            "legacy request rejects malformed UTF-8");
    }

    string legacyRequestId;
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/request-invitation",
        body: "{\"reason\":\"legacy workflow\"}", actor: "requester-b"))
    {
        Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
            "legacy request success is no-store");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("message").GetString() == "発行をリクエストしました。",
            "legacy request preserves its success message");
        legacyRequestId = json.RootElement.GetProperty("id").GetString()
            ?? throw new Exception("Legacy request response omitted the request id.");
    }
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/decide-invitation",
        body: JsonSerializer.Serialize(new { id = legacyRequestId, approve = true, comment = "legacy approval note" }),
        actor: "admin"))
    {
        Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
            "legacy approval delegates successfully with no-store");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("message").GetString() == "許可して招待リンクを発行しました。",
            "legacy decision preserves its approval success message");
        Check(!json.RootElement.GetRawText().Contains("invite=", StringComparison.Ordinal),
            "legacy approval response does not disclose the bearer link");
    }
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = await db.InvitationRequests.SingleAsync(x => x.Id == legacyRequestId);
        var invitation = await db.Invitations.SingleAsync(x => x.RequestId == legacyRequestId);
        Check(request.Count == 1 && request.Days == 1 && request.Status == "Approved" &&
              request.DecisionComment == "legacy approval note",
            "legacy request uses old count/day defaults and shared approval persistence");
        Check(invitation.Limit == 1 && invitation.Expires == invitation.Created + 86400L &&
              invitation.Purpose is null && invitation.IssuanceSource == "Request",
            "legacy approval issues from saved request values with null purpose and Request source");
    }
    string legacyRejectedRequestId;
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/request-invitation",
        body: "{\"reason\":\"legacy rejection workflow\"}", actor: "requester-b"))
    {
        Check(response.StatusCode == HttpStatusCode.OK,
            "legacy request can create another independently reviewable request");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        legacyRejectedRequestId = json.RootElement.GetProperty("id").GetString()
            ?? throw new Exception("Legacy rejection request omitted the request id.");
    }
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/decide-invitation",
        body: JsonSerializer.Serialize(new
        {
            id = legacyRejectedRequestId, approve = false, comment = "legacy rejection note"
        }), actor: "admin"))
    {
        Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
            "legacy rejection delegates successfully with no-store");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("message").GetString() == "拒否しました。",
            "legacy decision preserves its rejection success message");
    }
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = await db.InvitationRequests.SingleAsync(x => x.Id == legacyRejectedRequestId);
        Check(request.Status == "Rejected" && request.DecisionComment == "legacy rejection note" &&
              !await db.Invitations.AnyAsync(x => x.RequestId == legacyRejectedRequestId),
            "legacy rejection uses the shared conditional decision service without issuing an invitation");
    }

    using (var response = await SendAsync(HttpMethod.Post,
        "/api/revoke-invitation?expectedActor=admin",
        body: "{\"id\":\"form-target\",\"reason\":\"unconfirmed\",\"confirmed\":false}", actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.BadRequest,
            "legacy revoke requires explicit confirmation");
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/revoke-invitation",
        body: "{\"id\":\"form-target\",\"reason\":\"  first legacy reason  \",\"confirmed\":true}", actor: "admin"))
    {
        Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
            "legacy revoke succeeds with no-store");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("message").GetString() == "即時失効しました。",
            "legacy revoke preserves its success message");
    }
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/revoke-invitation?expectedActor=admin",
        body: "{\"id\":\"form-target\",\"reason\":\"legacy retry reason\",\"confirmed\":true}",
        actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
            "legacy revoke retry is idempotent and no-store");
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/core/invitations/revoke?expectedActor=admin",
        body: "{\"id\":\"form-target\",\"reason\":\"new route retry reason\",\"explicitlyConfirmed\":true}",
        actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.OK,
            "new route can safely replay a legacy revocation");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var invitation = await db.Invitations.SingleAsync(x => x.Id == "form-target");
        Check(invitation.Disabled && invitation.RevocationReason == "first legacy reason" &&
              invitation.RevokedBy == "admin" && invitation.RevokedAt is not null,
            "cross-route revoke replay preserves the first reason and actor");
        Check(await db.Audits.CountAsync(x => x.Action == "InvitationRevoked" && x.EntityId == "form-target") == 1,
            "cross-route revoke replay writes exactly one audit");
    }

    var sourceRoot = FindSourceRoot();
    var legacySource = await File.ReadAllTextAsync(Path.Combine(sourceRoot, "src", "Sem.Web", "Runtime", "SemEndpoints.cs"));
    var invitationEndpoints = await File.ReadAllTextAsync(Path.Combine(sourceRoot, "src", "Sem.Web", "Runtime", "InvitationManagementEndpoints.cs"));
    var uiSource = await File.ReadAllTextAsync(Path.Combine(sourceRoot, "src", "Sem.Web", "wwwroot", "sem.js"));
    Check(invitationEndpoints.Contains("app.MapMethods(\"/api/request-invitation\"", StringComparison.Ordinal) &&
          invitationEndpoints.Contains("app.MapMethods(\"/api/decide-invitation\"", StringComparison.Ordinal) &&
          invitationEndpoints.Contains("app.MapMethods(\"/api/revoke-invitation\"", StringComparison.Ordinal) &&
          invitationEndpoints.Contains("LegacyActionAsync", StringComparison.Ordinal) &&
          invitationEndpoints.Contains("service.RequestAsync", StringComparison.Ordinal) &&
          invitationEndpoints.Contains("service.DecideAsync", StringComparison.Ordinal) &&
          invitationEndpoints.Contains("service.RevokeAsync", StringComparison.Ordinal),
        "legacy invitation routes use strict adapters and the shared atomic service");
    Check(!legacySource.Contains("action == \"request-invitation\"", StringComparison.Ordinal) &&
          !legacySource.Contains("action == \"decide-invitation\"", StringComparison.Ordinal) &&
          !legacySource.Contains("action == \"revoke-invitation\"", StringComparison.Ordinal) &&
          legacySource.Contains("AccountPasskeyEndpoints.TryHandleLegacyAsync", StringComparison.Ordinal),
        "legacy invitation branches are removed while the passkey legacy adapter remains");
    Check(legacySource.Contains("link = i.OwnerId == user.Id && !i.Disabled", StringComparison.Ordinal),
        "legacy snapshot emits bearer links only for the current owner");
    Check(uiSource.Contains("state?.user?.id", StringComparison.Ordinal) &&
          uiSource.Contains("expectedActor=", StringComparison.Ordinal) &&
          uiSource.Contains("data.confirmed=true", StringComparison.Ordinal),
        "legacy invitation UI sends its live expectedActor and explicit revoke confirmation");

    Console.WriteLine("Invitation management HTTP checks passed: " + checks.ToString(CultureInfo.InvariantCulture));
}
finally
{
    if (started) await app.StopAsync();
    await app.DisposeAsync();
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static ApplicationUser User(string id, string email, string displayName, string stamp,
    bool isAdmin = false, bool enabled = true, string? invitationId = null, bool emailConfirmed = true) => new()
{
    Id = id,
    UserName = email,
    NormalizedUserName = email.ToUpperInvariant(),
    Email = email,
    NormalizedEmail = email.ToUpperInvariant(),
    EmailConfirmed = emailConfirmed,
    Enabled = enabled,
    IsAdmin = isAdmin,
    DisplayName = displayName,
    SecurityStamp = stamp,
    InvitationId = invitationId
};

static IEnumerable<Invitation> SeedInvitations()
{
    yield return Invitation("tie-b", "owner-a", "issuer", 13001);
    yield return Invitation("tie-a", "owner-a", "issuer", 13001);
    yield return Invitation("used-registered", "owner-a", "missing-issuer", 12000, uses: 1);
    for (var i = 0; i < 48; i++)
        yield return Invitation($"owner-a-{i:D2}", "owner-a", "issuer", 11999 - i);
    yield return Invitation("owner-b-only", "owner-b", "issuer", 20000);
    yield return Invitation("form-target", "owner-b", "issuer", 19999);
}

static IEnumerable<InvitationRequest> SeedRequests()
{
    yield return new InvitationRequest
    {
        Id = "request-a-00", UserId = "owner-a", Reason = "synthetic request",
        Count = 2, Days = 14, Status = "Pending", Created = 12000, DecidedBy = "missing-decider"
    };
    for (var i = 1; i < 51; i++)
        yield return new InvitationRequest
        {
            Id = $"request-a-{i:D2}", UserId = "owner-a", Reason = "synthetic request",
            Count = 1, Days = 7, Status = "Pending", Created = 11999 - i
        };
    yield return new InvitationRequest
    {
        Id = "request-b-only", UserId = "requester-b", Reason = "synthetic request",
        Count = 1, Days = 7, Status = "Pending", Created = 20000
    };
}

static Invitation Invitation(string id, string owner, string issuer, long created, int uses = 0) => new()
{
    Id = id,
    TokenHash = "TOKEN_HASH_SECRET_" + id,
    ProtectedToken = "PROTECTED_TOKEN_SECRET_" + id,
    OwnerId = owner,
    IssuerId = issuer,
    RequestId = null,
    Limit = 5,
    Uses = uses,
    Created = created,
    Expires = created + 86400,
    Disabled = false
};

static void CheckExactKeys(JsonElement element, string[] expected)
{
    var actual = element.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
    if (!actual.SetEquals(expected))
        throw new Exception("Unexpected JSON fields: " + string.Join(",", actual.Order(StringComparer.Ordinal)));
}

static async Task<string> GetInvitationIdAsync(IServiceProvider services, string requestId)
{
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    return await db.Invitations.Where(x => x.RequestId == requestId)
        .Select(x => x.Id).SingleAsync();
}

static string FindSourceRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "Sem.Web", "Runtime", "SemEndpoints.cs")))
                return directory.FullName;
        }
    }
    throw new DirectoryNotFoundException("Repository source tree was not found for source assertions.");
}

static async Task<(string Token, string Cookie)> GetCsrfAsync(HttpClient client, string actor = "admin",
    string? stamp = null, string? session = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/fixture/csrf");
    request.Headers.TryAddWithoutValidation("X-Test-Actor", actor);
    request.Headers.TryAddWithoutValidation("X-Test-Stamp", stamp ?? actor switch
    {
        "admin" => "stamp-admin-current",
        "owner-a" => "stamp-owner-a-current",
        "owner-b" => "stamp-owner-b-current",
        "issuer" => "stamp-issuer-current",
        "requester-a" => "stamp-requester-a-current",
        "requester-b" => "stamp-requester-b-current",
        "registered-user" => "stamp-registered-current",
        "disabled-user" => "stamp-disabled-current",
        "unconfirmed-user" => "stamp-unconfirmed-current",
        _ => "unknown-stamp"
    });
    request.Headers.TryAddWithoutValidation("X-Test-Session", session ?? "synthetic-session-" + actor);
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var token = json.RootElement.GetProperty("token").GetString()
        ?? throw new Exception("Antiforgery request token missing.");
    var cookie = response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith("test-csrf=", StringComparison.Ordinal))
        .Split(';', 2)[0];
    return (token, cookie);
}

static async Task<string> GetApplicationCookieAsync(HttpClient client, string userId,
    string? stamp = null, string? session = null)
{
    var query = new List<string>();
    if (stamp is not null) query.Add("stamp=" + Uri.EscapeDataString(stamp));
    if (session is not null) query.Add("session=" + Uri.EscapeDataString(session));
    var suffix = query.Count == 0 ? "" : "?" + string.Join("&", query);
    using var response = await client.GetAsync("/fixture/application-cookie/" + Uri.EscapeDataString(userId) + suffix);
    response.EnsureSuccessStatusCode();
    return response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith("test-application=", StringComparison.Ordinal))
        .Split(';', 2)[0];
}

sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var actor = Request.Headers["X-Test-Actor"].ToString();
        if (string.IsNullOrWhiteSpace(actor)) return Task.FromResult(AuthenticateResult.NoResult());
        var stamp = Request.Headers["X-Test-Stamp"].ToString();
        var session = Request.Headers["X-Test-Session"].ToString();
        var claimTypes = Context.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(claimTypes.UserIdClaimType, actor),
            new Claim(claimTypes.SecurityStampClaimType, stamp),
            new Claim(PasskeyAdditionService.SessionClaim, session)
        ], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

sealed class CountingReadStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
        BytesRead += read;
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        BytesRead += read;
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class NonSeekableReadStream(byte[] bytes) : Stream
{
    private readonly MemoryStream inner = new(bytes, writable: false);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
