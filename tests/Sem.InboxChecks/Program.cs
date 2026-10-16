using System.Globalization;
using System.Net;
using System.Security.Claims;
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

const string Origin = "https://sem-inbox-test.invalid";
var root = Path.Combine(Path.GetTempPath(), "sem-inbox-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var stamps = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["member"] = "stamp-member",
    ["other"] = "stamp-other",
    ["admin"] = "stamp-admin",
    ["disabled"] = "stamp-disabled",
    ["unconfirmed"] = "stamp-unconfirmed"
};
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite("Data Source=" + Path.Combine(root, "inbox.db")));
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager();
builder.Services.AddDataProtection();
builder.Services.AddSingleton(new DeploymentSettings { PublicUrl = Origin + "/" });
builder.Services.AddScoped<InboxService>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "inbox-test-csrf";
    options.Cookie.SecurePolicy = CookieSecurePolicy.None;
    options.Cookie.HttpOnly = true;
    options.Cookie.Path = "/";
});
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "InboxTestSelector";
        options.DefaultChallengeScheme = "InboxTestSelector";
        options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
    })
    .AddPolicyScheme("InboxTestSelector", "Synthetic or Identity.Application", options =>
    {
        options.ForwardDefaultSelector = context => context.Request.Cookies.ContainsKey("inbox-test-application")
            ? IdentityConstants.ApplicationScheme
            : "InboxSynthetic";
    })
    .AddScheme<AuthenticationSchemeOptions, InboxTestAuthenticationHandler>("InboxSynthetic", _ => { })
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        options.Cookie.Name = "inbox-test-application";
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
    var measured = new CountingReadStream(original);
    var receivedLength = context.Request.ContentLength;
    context.Request.Body = measured;
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Test-Body-Read"] = measured.BytesRead.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Test-Content-Length"] = receivedLength?.ToString(CultureInfo.InvariantCulture) ?? "null";
        return Task.CompletedTask;
    });
    try { await next(); }
    finally { context.Request.Body = original; }
});
app.UseAuthentication();
app.UseSemLiveAuthenticationSession();
app.UseAntiforgery();
app.MapGet("/fixture/csrf", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Json(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
app.MapGet("/fixture/application-cookie/{userId}", async (HttpContext context, string userId,
    IOptions<IdentityOptions> identityOptions) =>
{
    if (!stamps.TryGetValue(userId, out var stamp)) return Results.NotFound();
    var session = context.Request.Query["session"].FirstOrDefault() ?? "cookie-session-" + userId;
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
app.MapApplicationInbox();

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
            User("member", "member@example.test", stamps["member"]),
            User("other", "other@example.test", stamps["other"]),
            User("admin", "admin@example.test", stamps["admin"], isAdmin: true),
            User("disabled", "disabled@example.test", stamps["disabled"], enabled: false),
            User("unconfirmed", "unconfirmed@example.test", stamps["unconfirmed"], emailConfirmed: false));
        db.Messages.AddRange(SeedMessages());
        db.RevokedAuthSessions.Add(new RevokedAuthSession
        {
            UserId = "member",
            TokenHash = SemEndpoints.Hash("revoked-session"),
            RevokedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        });
        await db.SaveChangesAsync();
    }

    await app.StartAsync();
    started = true;
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(address)
    };

    var memberCsrf = await GetCsrfAsync(client, actor: "member");
    var otherCsrf = await GetCsrfAsync(client, actor: "other");
    var adminApplicationCookie = await GetApplicationCookieAsync(client, "admin");
    var adminCookieCsrf = await GetCsrfAsync(client, applicationCookie: adminApplicationCookie);
    var staleCsrf = await GetCsrfAsync(client, actor: "member", stamp: "stale-stamp");
    var revokedCsrf = await GetCsrfAsync(client, actor: "member", session: "revoked-session");

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body = null,
        string? actor = "member", string? stamp = null, string? session = null,
        CsrfTokens? csrf = null, string? applicationCookie = null, bool includeCsrfHeader = true,
        string? origin = null, bool duplicateOrigin = false, bool duplicateCsrfHeader = false,
        HttpContent? suppliedContent = null, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, path);
        if (suppliedContent is not null) request.Content = suppliedContent;
        else if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, contentType);
        var cookies = new List<string>();
        if (csrf is not null) cookies.Add(csrf.Cookie);
        if (applicationCookie is not null) cookies.Add(applicationCookie);
        if (cookies.Count != 0) request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        if (applicationCookie is null && actor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Actor", actor);
            request.Headers.TryAddWithoutValidation("X-Test-Stamp", stamp ?? stamps.GetValueOrDefault(actor, "unknown-stamp"));
            request.Headers.TryAddWithoutValidation("X-Test-Session", session ?? "session-" + actor);
        }
        if (origin is not null)
        {
            if (duplicateOrigin) request.Headers.TryAddWithoutValidation("Origin", new[] { origin, origin });
            else request.Headers.TryAddWithoutValidation("Origin", origin);
        }
        if (includeCsrfHeader && csrf is not null)
        {
            if (duplicateCsrfHeader) request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", new[] { csrf.Token, csrf.Token });
            else request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf.Token);
        }
        return await client.SendAsync(request);
    }

    using (var response = await client.GetAsync("/api/core/inbox"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "anonymous list request is rejected");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox", actor: "member"))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "live member can list inbox");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var page = json.RootElement;
        CheckExactKeys(page, ["actorId", "page", "pageSize", "hasMore", "messages"]);
        Check(page.GetProperty("actorId").GetString() == "member" && page.GetProperty("page").GetInt32() == 1 &&
              page.GetProperty("pageSize").GetInt32() == 50 && page.GetProperty("hasMore").GetBoolean(),
            "list reports actor, default page, fixed page size, and lookahead");
        var messages = page.GetProperty("messages");
        Check(messages.GetArrayLength() == 50, "page one is capped at fifty rows");
        Check(messages[0].GetProperty("id").GetString() == "member-target" &&
              messages[1].GetProperty("id").GetString() == "member-read" &&
              messages[2].GetProperty("id").GetString() == "tie-a" &&
              messages[3].GetProperty("id").GetString() == "tie-b",
            "messages sort by Created descending and ID ascending on ties");
        CheckExactKeys(messages[0], ["id", "title", "created", "readAt"]);
        Check(messages.EnumerateArray().All(x => x.GetProperty("id").GetString() is not "other-private" and not "admin-private"),
            "member list contains only the member's messages");
        Check(!page.GetRawText().Contains("PRIVATE_BODY_SENTINEL", StringComparison.Ordinal),
            "list projection does not expose message bodies");
    }
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox?page=2", actor: "member"))
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("messages").GetArrayLength() == 2 &&
              !json.RootElement.GetProperty("hasMore").GetBoolean(), "page two returns the remaining messages");
    }
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox?page=10000", actor: "member"))
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("messages").GetArrayLength() == 0,
            "maximum page is accepted");
    }
    foreach (var invalidPage in new[] { "?page=0", "?page=10001", "?page=abc", "?page=1&page=2", "?unexpected=1" })
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/core/inbox" + invalidPage, actor: "member");
        Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid list query rejected " + invalidPage);
    }
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox", actor: "member"))
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed && response.Content.Headers.Allow.Contains("GET"),
            "list route is GET-only");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/message?id=member-target&expectedActor=member", actor: "member"))
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed && response.Content.Headers.Allow.Contains("GET"),
            "message route is GET-only");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox/read?expectedActor=member", actor: "member"))
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed && response.Content.Headers.Allow.Contains("POST"),
            "read route is POST-only");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox/unknown", actor: "member"))
        Check(response.StatusCode == HttpStatusCode.NotFound, "unknown action is not routed");

    const string detailPath = "/api/core/inbox/message?id=member-target&expectedActor=member";
    using (var response = await SendAsync(HttpMethod.Get, detailPath, actor: "member"))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "owner can retrieve full message detail");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        CheckExactKeys(json.RootElement, ["actorId", "id", "title", "body", "created", "readAt"]);
        Check(json.RootElement.GetProperty("actorId").GetString() == "member" &&
              json.RootElement.GetProperty("id").GetString() == "member-target" &&
              json.RootElement.GetProperty("body").GetString() == "PRIVATE_BODY_SENTINEL; exact body remains intact" &&
              json.RootElement.GetProperty("readAt").ValueKind == JsonValueKind.Null,
            "detail returns intact body and does not mark unread message read");
    }
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox/message?id=other-private&expectedActor=member", actor: "member"))
        Check(response.StatusCode == HttpStatusCode.NotFound, "foreign message detail is indistinguishable from missing");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox/message?id=missing&expectedActor=member", actor: "member"))
        Check(response.StatusCode == HttpStatusCode.NotFound, "missing message detail returns 404");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox/message?id=member-target&expectedActor=other", actor: "member"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "detail expectedActor must match the live actor");
    foreach (var badDetail in new[]
    {
        "/api/core/inbox/message?id=member-target",
        "/api/core/inbox/message?id=member-target&expectedActor=member&extra=1",
        "/api/core/inbox/message?id=member-target&id=other-private&expectedActor=member",
        "/api/core/inbox/message?id=member-target&expectedActor=member&expectedActor=member",
        "/api/core/inbox/message?id=&expectedActor=member",
        "/api/core/inbox/message?id=" + new string('x', 65) + "&expectedActor=member",
        "/api/core/inbox/message?id=bad%0Avalue&expectedActor=member"
    })
    {
        using var response = await SendAsync(HttpMethod.Get, badDetail, actor: "member");
        Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid detail query rejected");
    }

    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox", actor: "member", stamp: "stale-stamp"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "stale security stamp is rejected");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox", actor: "member", session: "revoked-session"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "revoked session is rejected");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox", actor: "disabled"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "disabled account is rejected");
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox", actor: "unconfirmed"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "unconfirmed account is rejected");

    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox", applicationCookie: adminApplicationCookie))
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = json.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
        Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("actorId").GetString() == "admin" &&
              ids.SequenceEqual(["admin-private"]), "real Identity.Application cookie works, but admin sees only own inbox");
    }
    using (var response = await SendAsync(HttpMethod.Get, "/api/core/inbox/message?id=member-target&expectedActor=admin",
               actor: null, applicationCookie: adminApplicationCookie))
        Check(response.StatusCode == HttpStatusCode.NotFound, "admin privilege does not grant access to another user's message");

    using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Check(await db.Messages.Where(x => x.Id == "member-target").Select(x => x.ReadAt).SingleAsync() is null &&
              await db.Messages.Where(x => x.Id == "member-read").Select(x => x.ReadAt).SingleAsync() == 777,
            "list and detail GETs leave persisted read timestamps unchanged");
    }

    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
               "{\"id\":\"member-target\"}", actor: "member", csrf: memberCsrf, origin: Origin))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "owner can explicitly mark message read");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        CheckExactKeys(json.RootElement, ["actorId", "id", "readAt"]);
        Check(json.RootElement.GetProperty("actorId").GetString() == "member" &&
              json.RootElement.GetProperty("id").GetString() == "member-target",
            "read result identifies the current actor and message");
        var firstReadAt = json.RootElement.GetProperty("readAt").GetInt64();
        using (var again = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
                   "{\"id\":\"member-target\"}", actor: "member", csrf: memberCsrf, origin: Origin))
        {
            using var second = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
            Check(again.StatusCode == HttpStatusCode.OK && second.RootElement.GetProperty("readAt").GetInt64() == firstReadAt,
                "repeated read returns the original timestamp");
        }
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Check(await db.Messages.Where(x => x.Id == "member-target").Select(x => x.ReadAt).SingleAsync() == firstReadAt,
            "idempotent read preserves the original persisted timestamp");
    }
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
               "{\"id\":\"member-read\"}", actor: "member", csrf: memberCsrf, origin: Origin))
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.OK && json.RootElement.GetProperty("readAt").GetInt64() == 777,
            "already-read message retains its original timestamp");
    }
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
               "{\"id\":\"other-private\"}", actor: "member", csrf: memberCsrf, origin: Origin))
        Check(response.StatusCode == HttpStatusCode.NotFound, "read mutation cannot target another user's message");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Check(await db.Messages.Where(x => x.Id == "other-private").Select(x => x.ReadAt).SingleAsync() is null,
            "foreign read request leaves the owner's message unchanged");
    }
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=admin",
               "{\"id\":\"member-target\"}", actor: null, applicationCookie: adminApplicationCookie,
               csrf: adminCookieCsrf, origin: Origin))
        Check(response.StatusCode == HttpStatusCode.NotFound, "admin cannot mark another user's message read");

    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=other",
               "{", actor: "member", csrf: memberCsrf, origin: Origin, includeCsrfHeader: false))
    {
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "expectedActor mismatch is rejected before body and CSRF");
        Check(response.Headers.TryGetValues("X-Test-Body-Read", out var reads) && reads.Single() == "0",
            "expectedActor mismatch leaves body unread");
    }
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member&expectedActor=member",
               "{", actor: "member", csrf: memberCsrf, origin: Origin, includeCsrfHeader: false))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "duplicate expectedActor is rejected");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member&extra=1",
               "{", actor: "member", csrf: memberCsrf, origin: Origin, includeCsrfHeader: false))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "unknown read query is rejected");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read",
               "{", actor: "member", csrf: memberCsrf, origin: Origin, includeCsrfHeader: false))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "missing expectedActor is rejected");

    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
               "{\"id\":\"missing-origin\"}", actor: "member", csrf: memberCsrf, origin: null))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "read requires Origin");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
               "{\"id\":\"foreign-origin\"}", actor: "member", csrf: memberCsrf, origin: "https://foreign.invalid"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "read rejects foreign Origin");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
               "{\"id\":\"duplicate-origin\"}", actor: "member", csrf: memberCsrf, origin: Origin, duplicateOrigin: true))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "read requires exactly one Origin");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
               "{\"id\":\"missing-csrf\"}", actor: "member", csrf: memberCsrf, origin: Origin, includeCsrfHeader: false))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "read requires CSRF header");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
               "{\"id\":\"duplicate-csrf\"}", actor: "member", csrf: memberCsrf, origin: Origin,
               duplicateCsrfHeader: true))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "read requires exactly one CSRF header");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
               "", actor: "member", csrf: memberCsrf, origin: Origin, includeCsrfHeader: false,
               contentType: "application/x-www-form-urlencoded",
               suppliedContent: new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = memberCsrf.Token,
                   ["id"] = "member-target"
               })))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "form anti-forgery fallback is not accepted");

    using (var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
               new string('x', 4097), actor: "member", csrf: memberCsrf, origin: Origin, includeCsrfHeader: false))
    {
        Check((int)response.StatusCode == 413, "body Content-Length above 4 KiB is rejected before CSRF");
        Check(response.Headers.TryGetValues("X-Test-Body-Read", out var reads) && reads.Single() == "0",
            "oversized Content-Length is rejected without reading the body");
    }
    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member"))
    {
        request.Content = new StreamContent(new NonSeekableReadStream(Encoding.UTF8.GetBytes(new string('x', 4097))));
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = true;
        request.Headers.TryAddWithoutValidation("Cookie", memberCsrf.Cookie);
        request.Headers.TryAddWithoutValidation("X-Test-Actor", "member");
        request.Headers.TryAddWithoutValidation("X-Test-Stamp", stamps["member"]);
        request.Headers.TryAddWithoutValidation("X-Test-Session", "session-member");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", memberCsrf.Token);
        using var response = await client.SendAsync(request);
        var status = (int)response.StatusCode;
        var contentLength = response.Headers.TryGetValues("X-Test-Content-Length", out var lengths)
            ? lengths.Single() : "missing";
        Check(status == 413 && contentLength == "null", "chunked read body is capped without Content-Length");
    }

    foreach (var invalidBody in new[]
    {
        "{",
        "[]",
        "{}",
        "{\"id\":\"member-target\",\"id\":\"member-target\"}",
        "{\"id\":\"member-target\",\"extra\":1}",
        "{\"id\":7}",
        "{\"id\":\"bad\\nvalue\"}",
        "{\"id\":\"\"}"
    })
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
            invalidBody, actor: "member", csrf: memberCsrf, origin: Origin);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "strict read JSON rejected");
    }
    using (var content = new ByteArrayContent([0xff, 0xfe, 0xfd]))
    {
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await SendAsync(HttpMethod.Post, "/api/core/inbox/read?expectedActor=member",
            actor: "member", csrf: memberCsrf, origin: Origin, suppliedContent: content);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid UTF-8 read body is rejected strictly");
    }
    Console.WriteLine("Inbox HTTP checks passed: " + checks.ToString(CultureInfo.InvariantCulture));
}
finally
{
    if (started) await app.StopAsync();
    await app.DisposeAsync();
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static ApplicationUser User(string id, string email, string stamp, bool isAdmin = false,
    bool enabled = true, bool emailConfirmed = true) => new()
{
    Id = id,
    UserName = email,
    NormalizedUserName = email.ToUpperInvariant(),
    Email = email,
    NormalizedEmail = email.ToUpperInvariant(),
    EmailConfirmed = emailConfirmed,
    Enabled = enabled,
    IsAdmin = isAdmin,
    DisplayName = id,
    SecurityStamp = stamp
};

static IEnumerable<Message> SeedMessages()
{
    yield return new Message
    {
        Id = "member-target", UserId = "member", Title = "Target message",
        Body = "PRIVATE_BODY_SENTINEL; exact body remains intact", Created = 20_000
    };
    yield return new Message
    {
        Id = "member-read", UserId = "member", Title = "Already read",
        Body = "previously read body", Created = 19_000, ReadAt = 777
    };
    yield return new Message { Id = "tie-b", UserId = "member", Title = "Tie B", Body = "body B", Created = 18_000 };
    yield return new Message { Id = "tie-a", UserId = "member", Title = "Tie A", Body = "body A", Created = 18_000 };
    for (var i = 0; i < 48; i++)
        yield return new Message
        {
            Id = $"member-{i:D2}", UserId = "member", Title = $"Member {i:D2}",
            Body = "filler body", Created = 17_900 - i
        };
    yield return new Message
    {
        Id = "other-private", UserId = "other", Title = "Other user's message",
        Body = "other private body", Created = 21_000
    };
    yield return new Message
    {
        Id = "admin-private", UserId = "admin", Title = "Admin's own message",
        Body = "admin message body", Created = 22_000
    };
}

static void CheckExactKeys(JsonElement element, string[] expected)
{
    var actual = element.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
    if (!actual.SetEquals(expected))
        throw new Exception("Unexpected JSON fields: " + string.Join(",", actual.OrderBy(x => x, StringComparer.Ordinal)));
}

static async Task<string> GetApplicationCookieAsync(HttpClient client, string userId)
{
    using var response = await client.GetAsync("/fixture/application-cookie/" + Uri.EscapeDataString(userId));
    response.EnsureSuccessStatusCode();
    return response.Headers.GetValues("Set-Cookie")
        .Single(x => x.StartsWith("inbox-test-application=", StringComparison.Ordinal))
        .Split(';', 2)[0];
}

static async Task<CsrfTokens> GetCsrfAsync(HttpClient client, string? actor = null, string? stamp = null,
    string? session = null, string? applicationCookie = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/fixture/csrf");
    if (applicationCookie is not null)
        request.Headers.TryAddWithoutValidation("Cookie", applicationCookie);
    else if (actor is not null)
    {
        request.Headers.TryAddWithoutValidation("X-Test-Actor", actor);
        request.Headers.TryAddWithoutValidation("X-Test-Stamp", stamp ?? (actor switch
        {
            "member" => "stamp-member",
            "other" => "stamp-other",
            "admin" => "stamp-admin",
            "disabled" => "stamp-disabled",
            "unconfirmed" => "stamp-unconfirmed",
            _ => "unknown-stamp"
        }));
        request.Headers.TryAddWithoutValidation("X-Test-Session", session ?? "session-" + actor);
    }
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var token = json.RootElement.GetProperty("token").GetString() ?? throw new Exception("Missing CSRF token.");
    var cookie = response.Headers.GetValues("Set-Cookie")
        .Single(x => x.StartsWith("inbox-test-csrf=", StringComparison.Ordinal)).Split(';', 2)[0];
    return new CsrfTokens(token, cookie);
}

sealed record CsrfTokens(string Token, string Cookie);

sealed class InboxTestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = Request.Headers["X-Test-Actor"].ToString();
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(AuthenticateResult.NoResult());
        var identityOptions = Context.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value;
        var stamp = Request.Headers["X-Test-Stamp"].ToString();
        var session = Request.Headers["X-Test-Session"].ToString();
        var claims = new[]
        {
            new Claim(identityOptions.ClaimsIdentity.UserIdClaimType, id),
            new Claim(identityOptions.ClaimsIdentity.SecurityStampClaimType, stamp),
            new Claim(PasskeyAdditionService.SessionClaim, session)
        };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}

sealed class CountingReadStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }
    public override bool CanRead => true;
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
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        BytesRead += read;
        return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = inner.ReadAsync(buffer, offset, count, cancellationToken);
        return CountAsync(read);
        async Task<int> CountAsync(Task<int> pending)
        {
            var value = await pending;
            BytesRead += value;
            return value;
        }
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
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
