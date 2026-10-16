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
    var adminCookie = await GetApplicationCookieAsync(client, "admin");
    var staleAdminCookie = await GetApplicationCookieAsync(client, "admin", "old-admin-stamp", "stale-cookie-session");
    var revokedAdminCookie = await GetApplicationCookieAsync(client, "admin", session: "revoked-cookie-session");

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body = null,
        string? actor = null, string? stamp = null, string? session = null, string? applicationCookie = null,
        bool includeCsrfHeader = true, string? origin = Origin, HttpContent? suppliedContent = null,
        bool duplicateCsrfHeader = false, bool includeCsrfCookie = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (suppliedContent is not null) request.Content = suppliedContent;
        else if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var cookies = new List<string>();
        if (includeCsrfCookie) cookies.Add(csrf.Cookie);
        if (applicationCookie is not null) cookies.Add(applicationCookie);
        if (cookies.Count > 0) request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        if (actor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Actor", actor);
            request.Headers.TryAddWithoutValidation("X-Test-Stamp", stamp ?? stamps.GetValueOrDefault(actor, "unknown-stamp"));
            request.Headers.TryAddWithoutValidation("X-Test-Session", session ?? "synthetic-session-" + actor);
        }
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (includeCsrfHeader)
        {
            if (duplicateCsrfHeader)
                request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", new[] { csrf.Token, csrf.Token });
            else request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf.Token);
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
        CheckExactKeys(page, ["actorId", "isAdmin", "page", "pageSize", "hasMoreInvitations", "hasMoreRequests", "invitations", "requests"]);
        CheckExactKeys(invitations[0], ["id", "ownerId", "ownerName", "issuerId", "issuerName", "requestId", "limit", "uses", "created", "expires", "disabled", "purpose", "issuanceSource", "revokedAt", "revokedBy", "revokerName", "revocationReason"]);
        CheckExactKeys(requests[0], ["id", "userId", "userName", "reason", "count", "days", "status", "created", "decidedAt", "decidedBy", "deciderName"]);
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

static async Task<(string Token, string Cookie)> GetCsrfAsync(HttpClient client)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/fixture/csrf");
    request.Headers.TryAddWithoutValidation("X-Test-Actor", "admin");
    request.Headers.TryAddWithoutValidation("X-Test-Stamp", "stamp-admin-current");
    request.Headers.TryAddWithoutValidation("X-Test-Session", "synthetic-session-admin");
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
