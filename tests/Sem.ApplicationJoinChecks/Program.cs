using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;

const string Origin = "https://sem-join-test.invalid";
const string AppInvite = "app-join-invite-token";
const string LegacyConfirmInvite = "legacy-confirm-invite-token";
const string LegacyVerifyInvite = "legacy-verify-invite-token";
const string ExpiryInvite = "expiry-join-invite-token";
const string DisabledInvite = "disabled-join-invite-token";
const string ExpiredInvite = "expired-join-invite-token";
const string FullInvite = "full-join-invite-token";
var root = Path.Combine(Path.GetTempPath(), "sem-application-join-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContext<ApplicationDbContext>(options =>
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
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Smtp:Host"] = "synthetic.invalid",
    ["Smtp:From"] = "noreply@example.test"
});
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddScoped<PasskeyAdditionService>();
builder.Services.AddScoped<ApplicationJoinService>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "test-csrf";
    options.Cookie.SecurePolicy = CookieSecurePolicy.None;
    options.Cookie.HttpOnly = true;
    options.Cookie.Path = "/";
});
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "test-application";
    options.Cookie.SecurePolicy = CookieSecurePolicy.None;
    options.Cookie.HttpOnly = true;
    options.Cookie.Path = "/";
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.SlidingExpiration = false;
});
builder.Services.AddAuthentication().AddCookie("SEM.Enrollment", options =>
{
    options.Cookie.Name = "test-enrollment";
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
    var contentLength = context.Request.ContentLength;
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Test-Request-Content-Length"] =
            contentLength?.ToString(CultureInfo.InvariantCulture) ?? "null";
        return Task.CompletedTask;
    });
    await next();
});
app.UseAuthentication();
app.UseAntiforgery();
app.MapGet("/fixture/application-cookie/{userId}", async (HttpContext context, string userId) =>
{
    var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, userId)],
        IdentityConstants.ApplicationScheme));
    await context.SignInAsync(IdentityConstants.ApplicationScheme, principal,
        new AuthenticationProperties { IsPersistent = false });
    return Results.NoContent();
});
app.MapGet("/fixture/cookie-state", async (HttpContext context) =>
{
    var application = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
    var enrollment = await context.AuthenticateAsync("SEM.Enrollment");
    return Results.Json(new
    {
        applicationAuthenticated = application.Succeeded,
        enrollmentAuthenticated = enrollment.Succeeded,
        expiresUtc = enrollment.Properties?.ExpiresUtc
    });
});
app.MapApplicationJoin();
app.MapSem();

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
        db.SiteStates.Add(new SiteState { Id = 1, Name = "Synthetic", Initialized = true });
        db.Users.Add(new ApplicationUser
        {
            Id = "logged-in-user",
            UserName = "logged-in@example.test",
            NormalizedUserName = "LOGGED-IN@EXAMPLE.TEST",
            Email = "logged-in@example.test",
            NormalizedEmail = "LOGGED-IN@EXAMPLE.TEST",
            EmailConfirmed = true,
            Enabled = true,
            DisplayName = "Logged In",
            SecurityStamp = "synthetic-stamp"
        });
        db.Invitations.AddRange(
            Invite("app-join", AppInvite),
            Invite("legacy-confirm", LegacyConfirmInvite),
            Invite("legacy-verify", LegacyVerifyInvite),
            Invite("expiry-join", ExpiryInvite),
            Invite("disabled-join", DisabledInvite, disabled: true),
            Invite("expired-join", ExpiredInvite, expires: SemEndpoints.Now - 1),
            Invite("full-join", FullInvite, uses: 1, limit: 1));
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
    var appCookie = await GetApplicationCookieAsync(client);

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null,
        bool includeCsrfHeader = true, string? applicationCookie = null, string? origin = Origin,
        bool duplicateCsrfHeader = false, bool duplicateOrigin = false)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        var cookies = new List<string> { csrf.Cookie };
        if (applicationCookie is not null) cookies.Add(applicationCookie);
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        if (origin is not null)
        {
            if (duplicateOrigin) request.Headers.TryAddWithoutValidation("Origin", new[] { origin, origin });
            else request.Headers.TryAddWithoutValidation("Origin", origin);
        }
        if (includeCsrfHeader)
        {
            if (duplicateCsrfHeader)
                request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", new[] { csrf.Token, csrf.Token });
            else request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf.Token);
        }
        return await client.SendAsync(request);
    }

    Task<HttpResponseMessage> RegisterAsync(string path, string invite, string name, string email,
        bool includeCsrfHeader = true, string? applicationCookie = null, string? origin = Origin) =>
        SendAsync(HttpMethod.Post, path, JsonContent.Create(new { invite, name, email }),
            includeCsrfHeader, applicationCookie, origin);

    Task<HttpResponseMessage> ConfirmAsync(string path, string token, bool includeCsrfHeader = true,
        string? applicationCookie = null, string? origin = Origin) =>
        SendAsync(HttpMethod.Post, path, JsonContent.Create(new { token }),
            includeCsrfHeader, applicationCookie, origin);

    async Task<JoinMail> FindActiveMailAsync(string email)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(x => x.Email == email);
        var tickets = await db.EmailTickets.Where(x => x.UserId == user.Id).ToArrayAsync();
        var deliveries = await db.Deliveries.Where(x => x.UserId == user.Id && x.Kind == "Email").ToArrayAsync();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("SEM.Notifications.v1");
        foreach (var delivery in deliveries.OrderByDescending(x => x.Due).ThenByDescending(x => x.Id))
        {
            var plaintext = protector.Unprotect(delivery.ProtectedBody);
            var link = plaintext.Split('\n').Last().Trim();
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) continue;
            var token = TicketToken(uri);
            var ticket = tickets.SingleOrDefault(x => x.Hash == SemEndpoints.Hash(token) && !x.Used);
            if (ticket is not null)
                return new JoinMail(token, uri, delivery.ProtectedBody, plaintext, ticket.Expires, ticket.Used);
        }
        throw new Exception("No unconsumed ticket matches the queued protected email.");
    }

    async Task<string> AssertEnrollmentCookieAsync(HttpResponseMessage response, string expectedMessage)
    {
        Check(response.IsSuccessStatusCode, "confirmation succeeds");
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            CheckExactKeys(json.RootElement, ["message"]);
            Check(json.RootElement.GetProperty("message").GetString() == expectedMessage,
                "confirmation response has the contract message");
            var raw = json.RootElement.GetRawText();
            Check(!raw.Contains("token", StringComparison.OrdinalIgnoreCase) &&
                  !raw.Contains("@example.test", StringComparison.Ordinal),
                "confirmation response contains no ticket or email");
        }
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
        Check(cookies.Length == 1 && cookies[0].StartsWith("test-enrollment=", StringComparison.Ordinal),
            "only SEM.Enrollment cookie is issued");
        var cookie = cookies[0].Split(';', 2)[0];
        using var request = new HttpRequestMessage(HttpMethod.Get, "/fixture/cookie-state");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var stateResponse = await client.SendAsync(request);
        using var state = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync());
        Check(stateResponse.IsSuccessStatusCode && state.RootElement.GetProperty("enrollmentAuthenticated").GetBoolean(),
            "SEM.Enrollment cookie authenticates in its own scheme");
        Check(!state.RootElement.GetProperty("applicationAuthenticated").GetBoolean(),
            "enrollment confirmation does not create an Application cookie");
        var expiry = state.RootElement.GetProperty("expiresUtc").GetDateTimeOffset();
        var remaining = expiry - DateTimeOffset.UtcNow;
        Check(remaining > TimeSpan.FromMinutes(19) && remaining <= TimeSpan.FromMinutes(20),
            "enrollment ticket expires within the 20-minute window");
        return cookie;
    }

    void AssertNoAuthenticationCookie(HttpResponseMessage response, string label)
    {
        Check(!response.Headers.TryGetValues("Set-Cookie", out var values) ||
              !values.Any(x => x.Contains("test-enrollment=", StringComparison.Ordinal) ||
                               x.Contains("test-application=", StringComparison.Ordinal) ||
                               x.Contains("Identity.Application", StringComparison.Ordinal)), label);
    }

    using (var response = await SendAsync(HttpMethod.Get, "/api/join/register"))
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed && response.Content.Headers.Allow.Contains("POST"),
            "register route is POST-only");
    using (var response = await SendAsync(HttpMethod.Get, "/api/join/confirm"))
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed && response.Content.Headers.Allow.Contains("POST"),
            "confirm route is POST-only");
    using (var response = await RegisterAsync("/api/join/register?unexpected=1", AppInvite, "Valid Name", "query@example.test"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "register rejects query parameters");
    using (var response = await ConfirmAsync("/api/join/confirm?unexpected=1", "synthetic-token"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "confirm rejects query parameters");

    using (var response = await RegisterAsync("/api/join/register", AppInvite, "Valid Name", "auth-reject@example.test",
        applicationCookie: appCookie))
    {
        Check(response.StatusCode == HttpStatusCode.Forbidden, "authenticated Application cookie cannot register through anonymous API");
        AssertNoAuthenticationCookie(response, "authenticated register rejection issues no auth cookie");
    }
    using (var response = await ConfirmAsync("/api/join/confirm", "synthetic-token", applicationCookie: appCookie))
    {
        Check(response.StatusCode == HttpStatusCode.Forbidden, "authenticated Application cookie cannot confirm through anonymous API");
        AssertNoAuthenticationCookie(response, "authenticated confirm rejection issues no auth cookie");
    }

    using (var response = await RegisterAsync("/api/join/register", AppInvite, "Valid Name", "missing-csrf@example.test",
        includeCsrfHeader: false))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "anonymous JSON request requires CSRF header");
    using (var response = await RegisterAsync("/api/join/register", AppInvite, "Valid Name", "foreign-origin@example.test",
        origin: "https://foreign.invalid"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "foreign Origin is denied");
    using (var response = await RegisterAsync("/api/join/register", AppInvite, "Valid Name", "missing-origin@example.test",
        origin: null))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "missing Origin is denied");
    using (var response = await SendAsync(HttpMethod.Post, "/api/join/register",
        JsonContent.Create(new { invite = AppInvite, name = "Name", email = "duplicate-origin@example.test" }),
        duplicateOrigin: true))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "duplicate Origin values are denied");
    using (var response = await SendAsync(HttpMethod.Post, "/api/join/register",
        JsonContent.Create(new { invite = AppInvite, name = "Name", email = "duplicate-csrf@example.test" }),
        duplicateCsrfHeader: true))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "multiple CSRF header values are denied");
    using (var response = await SendAsync(HttpMethod.Post, "/api/join/register",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf.Token,
            ["invite"] = AppInvite, ["name"] = "Form Name", ["email"] = "form@example.test"
        })))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "form antiforgery fallback is rejected");

    var malformedRegisterBodies = new[]
    {
        "{",
        """{"invite":"app-join-invite-token","invite":"other","name":"n","email":"x@example.test"}""",
        """{"invite":"app-join-invite-token","name":"n","email":"x@example.test","extra":true}""",
        """{"invite":3,"name":"n","email":"x@example.test"}""",
        """{"invite":"app-join-invite-token","name":null,"email":"x@example.test"}""",
        """{"invite":"app-join-invite-token","name":"n"}"""
    };
    foreach (var body in malformedRegisterBodies)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/join/register",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Check(response.StatusCode == HttpStatusCode.BadRequest, "strict register JSON rejects malformed/duplicate/unknown/type/missing fields");
    }
    using (var content = new ByteArrayContent([0xff, 0xfe, 0xfd]))
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await SendAsync(HttpMethod.Post, "/api/join/register", content);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid UTF-8 JSON is rejected strictly");
    }
    using (var content = new ByteArrayContent(Encoding.UTF8.GetPreamble()
        .Concat(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { invite = AppInvite, name = "n", email = "bom@example.test" }))).ToArray()))
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await SendAsync(HttpMethod.Post, "/api/join/register", content);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "UTF-8 BOM is rejected");
    }
    foreach (var (invite, name, email, label) in new[]
    {
        (new string('i', 129), "Name", "invite-bound@example.test", "invite length bound"),
        (AppInvite, new string('n', 61), "name-bound@example.test", "name length bound"),
        (AppInvite, "Name", new string('e', 255) + "@example.test", "email length bound"),
        (AppInvite, "Name", "not-an-email", "email format bound")
    })
    {
        using var response = await RegisterAsync("/api/join/register", invite, name, email);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "registration validates " + label);
    }
    using (var response = await RegisterAsync("/api/join/register", AppInvite, "Valid Name", new string('x', 254)))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid email boundary rejected");

    using (var response = await SendAsync(HttpMethod.Post, "/api/join/register",
        new StringContent(new string('x', 4097), Encoding.UTF8, "application/json"), includeCsrfHeader: false))
        Check((int)response.StatusCode == 413, "Content-Length over 4 KiB rejected before CSRF");
    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/join/register"))
    {
        request.Content = new StreamContent(new NonSeekableReadStream(Encoding.UTF8.GetBytes(new string('x', 4097))));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = true;
        request.Headers.TryAddWithoutValidation("Cookie", csrf.Cookie);
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf.Token);
        using var response = await client.SendAsync(request);
        var status = (int)response.StatusCode;
        var receivedLength = response.Headers.TryGetValues("X-Test-Request-Content-Length", out var lengths)
            ? lengths.Single()
            : "missing";
        Check(status == 413 && receivedLength == "null",
            $"chunked join request bounded; status={status}; received Content-Length={receivedLength}");
    }

    foreach (var (invite, email, label) in new[]
    {
        (DisabledInvite, "disabled-invite@example.test", "disabled invite"),
        (ExpiredInvite, "expired-invite@example.test", "expired invite"),
        (FullInvite, "full-invite@example.test", "full invite")
    })
    {
        using var response = await RegisterAsync("/api/join/register", invite, "Invalid Invite", email);
        Check(response.StatusCode == HttpStatusCode.BadRequest, label + " cannot start registration");
        AssertNoAuthenticationCookie(response, label + " rejection sets no auth cookie");
    }
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Check(await db.Invitations.Where(x => x.Id != "full-join").AllAsync(x => x.Uses == 0) &&
              await db.Invitations.Where(x => x.Id == "full-join").AllAsync(x => x.Uses == x.Limit),
            "invalid invites and registration requests do not consume invitation uses");
        Check(!await db.Users.AnyAsync(x => x.Email != null &&
              (x.Email.StartsWith("disabled-invite") || x.Email.StartsWith("expired-invite") || x.Email.StartsWith("full-invite"))),
            "invalid invitations create no pending users");
    }

    using (var response = await RegisterAsync("/api/join/register", AppInvite, "  Join Member  ", "New.Join@Example.test "))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "application join registration is queued");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        CheckExactKeys(json.RootElement, ["message"]);
        Check(json.RootElement.GetProperty("message").GetString() == ApplicationJoinService.QueuedMessage,
            "register success returns generic queued message");
        Check(!json.RootElement.GetRawText().Contains("@example.test", StringComparison.Ordinal),
            "register response does not disclose the submitted email");
        AssertNoAuthenticationCookie(response, "register does not authenticate the applicant");
    }
    var firstMail = await FindActiveMailAsync("new.join@example.test");
    Check(firstMail.Link.AbsolutePath == "/join" && firstMail.Link.Query.Length == 0 &&
          firstMail.Link.Fragment.StartsWith("#token=", StringComparison.Ordinal),
        "application join queues the fixed /join#token link");
    Check(firstMail.Expires - SemEndpoints.Now is >= 3590 and <= 3600,
        "email ticket lifetime is one hour");
    Check(!firstMail.ProtectedBody.Contains(firstMail.Token, StringComparison.Ordinal) &&
          !firstMail.ProtectedBody.Contains("new.join@example.test", StringComparison.Ordinal),
        "delivery stores protected body rather than plaintext token or email");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(x => x.Email == "new.join@example.test");
        Check(user.DisplayName == "Join Member" && user.InvitationId == "app-join" &&
              !user.Enabled && !user.EmailConfirmed && !user.IsAdmin,
            "join creates a normalized disabled pending account");
    }

    using (var response = await RegisterAsync("/api/join/register", AppInvite, "Join Member", "new.join@example.test"))
        Check(response.StatusCode == HttpStatusCode.OK, "reissuing a pending registration succeeds");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var old = await db.EmailTickets.SingleAsync(x => x.Hash == SemEndpoints.Hash(firstMail.Token));
        Check(old.Used, "reissue invalidates the prior confirmation ticket");
    }
    var secondMail = await FindActiveMailAsync("new.join@example.test");
    Check(secondMail.Token != firstMail.Token && secondMail.Link.AbsolutePath == "/join" &&
          secondMail.Link.Fragment.StartsWith("#token=", StringComparison.Ordinal),
        "reissue queues a fresh application-join link");
    using (var response = await ConfirmAsync("/api/join/confirm", firstMail.Token))
    {
        Check(response.StatusCode == HttpStatusCode.BadRequest, "reissued old ticket is rejected by new confirm route");
        AssertNoAuthenticationCookie(response, "old ticket rejection does not authenticate");
    }
    using (var response = await ConfirmAsync("/api/join/confirm", secondMail.Token))
        await AssertEnrollmentCookieAsync(response, ApplicationJoinService.EnrollmentMessage);
    using (var response = await ConfirmAsync("/api/join/confirm", secondMail.Token))
    {
        Check(response.StatusCode == HttpStatusCode.BadRequest, "confirmation ticket is one-use");
        AssertNoAuthenticationCookie(response, "replay rejection issues no auth cookie");
    }
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(x => x.Email == "new.join@example.test");
        Check(user.EmailConfirmed && !user.Enabled && !user.IsAdmin,
            "email confirmation leaves the account disabled and non-admin for passkey enrollment");
        Check(await db.Invitations.SingleAsync(x => x.Id == "app-join") is { Uses: 0 },
            "email confirmation does not consume invitation uses");
        Check(await db.EmailTickets.CountAsync(x => x.UserId == user.Id && x.Used) == 2,
            "reissued and confirmed tickets are both consumed");
    }

    using (var response = await RegisterAsync("/api/register", LegacyConfirmInvite,
        "Legacy Via New Confirm", "legacy-new-confirm@example.test"))
        Check(response.StatusCode == HttpStatusCode.OK, "legacy /api/register queues through shared join service");
    var legacyNewConfirmMail = await FindActiveMailAsync("legacy-new-confirm@example.test");
    Check(legacyNewConfirmMail.Link.AbsolutePath == "/verify" &&
          legacyNewConfirmMail.Link.Query.StartsWith("?token=", StringComparison.Ordinal) &&
          legacyNewConfirmMail.Link.Fragment.Length == 0,
        "legacy registration retains its /verify?token link");
    using (var response = await ConfirmAsync("/api/join/confirm", legacyNewConfirmMail.Token))
        await AssertEnrollmentCookieAsync(response, ApplicationJoinService.EnrollmentMessage);
    Check(!legacyNewConfirmMail.ProtectedBody.Contains(legacyNewConfirmMail.Token, StringComparison.Ordinal),
        "legacy queued email body is protected at rest");

    using (var response = await RegisterAsync("/api/register", LegacyVerifyInvite,
        "Legacy Verify Redirect", "legacy-verify@example.test"))
        Check(response.StatusCode == HttpStatusCode.OK, "second legacy registration queues");
    var legacyVerifyMail = await FindActiveMailAsync("legacy-verify@example.test");
    Check(legacyVerifyMail.Link.AbsolutePath == "/verify" &&
          legacyVerifyMail.Link.Query.StartsWith("?token=", StringComparison.Ordinal),
        "legacy link remains compatible with GET /verify");
    using (var response = await client.GetAsync("/verify?token=" + Uri.EscapeDataString(legacyVerifyMail.Token)))
    {
        Check(response.StatusCode == HttpStatusCode.Redirect &&
              response.Headers.Location?.OriginalString == "/?enroll=1",
            "legacy GET /verify preserves enrollment redirect");
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
        Check(cookies.Length == 1 && cookies[0].StartsWith("test-enrollment=", StringComparison.Ordinal),
            "legacy verify also issues only SEM.Enrollment cookie");
    }

    using (var response = await RegisterAsync("/api/join/register", ExpiryInvite,
        "Expiry Pending", "expiry-ticket@example.test"))
        Check(response.StatusCode == HttpStatusCode.OK, "expiry fixture registration queues");
    var expiryMail = await FindActiveMailAsync("expiry-ticket@example.test");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ticket = await db.EmailTickets.SingleAsync(x => x.Hash == SemEndpoints.Hash(expiryMail.Token));
        ticket.Expires = SemEndpoints.Now - 1;
        await db.SaveChangesAsync();
    }
    using (var response = await ConfirmAsync("/api/join/confirm", expiryMail.Token))
    {
        Check(response.StatusCode == HttpStatusCode.BadRequest, "expired ticket is rejected");
        AssertNoAuthenticationCookie(response, "expired ticket issues no auth cookie");
    }
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Check(await db.EmailTickets.SingleAsync(x => x.Hash == SemEndpoints.Hash(expiryMail.Token)) is { Used: false },
            "expired ticket remains unconsumed");
        Check(await db.Invitations.SingleAsync(x => x.Id == "expiry-join") is { Uses: 0 },
            "expired ticket does not consume invitation uses");
    }

    using (var response = await ConfirmAsync("/api/join/confirm", "invalid-token"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "unknown ticket rejected");
    using (var response = await ConfirmAsync("/api/join/confirm",
        new string('t', 129)))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "confirmation token length bound enforced");
    using (var response = await ConfirmAsync("/api/join/confirm", "\u0001"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "confirmation token rejects control characters");
    var malformedConfirmBodies = new[]
    {
        "{",
        """{"token":"x","token":"y"}""",
        """{"token":"x","unexpected":1}""",
        """{"token":1}""",
        "{}"
    };
    foreach (var body in malformedConfirmBodies)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/join/confirm",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Check(response.StatusCode == HttpStatusCode.BadRequest, "strict confirmation JSON rejects malformed or extra fields");
    }

    Console.WriteLine("Application join HTTP checks passed: " + checks.ToString(CultureInfo.InvariantCulture));
}
finally
{
    if (started) await app.StopAsync();
    await app.DisposeAsync();
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static Invitation Invite(string id, string token, bool disabled = false, long? expires = null,
    int uses = 0, int limit = 1) => new()
{
    Id = id,
    TokenHash = SemEndpoints.Hash(token),
    ProtectedToken = "synthetic-protected-invitation-token",
    OwnerId = "synthetic-owner",
    IssuerId = "synthetic-issuer",
    Limit = limit,
    Uses = uses,
    Created = SemEndpoints.Now,
    Expires = expires ?? SemEndpoints.Now + 86400,
    Disabled = disabled
};

static string TicketToken(Uri uri)
{
    var queryOrFragment = uri.Fragment.Length > 0 ? uri.Fragment[1..] : uri.Query.TrimStart('?');
    var tokenValue = queryOrFragment.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Single(part => part.StartsWith("token=", StringComparison.Ordinal))["token=".Length..];
    return Uri.UnescapeDataString(tokenValue);
}

static void CheckExactKeys(JsonElement element, string[] expected)
{
    var actual = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    if (!actual.SetEquals(expected))
        throw new Exception("Unexpected JSON fields: " + string.Join(",", actual.OrderBy(x => x, StringComparer.Ordinal)));
}

static async Task<(string Token, string Cookie)> GetCsrfAsync(HttpClient client)
{
    using var response = await client.GetAsync("/api/csrf");
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var token = json.RootElement.GetProperty("token").GetString()
        ?? throw new Exception("Anonymous antiforgery request token missing.");
    var cookie = response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith("test-csrf=", StringComparison.Ordinal))
        .Split(';', 2)[0];
    return (token, cookie);
}

static async Task<string> GetApplicationCookieAsync(HttpClient client)
{
    using var response = await client.GetAsync("/fixture/application-cookie/logged-in-user");
    response.EnsureSuccessStatusCode();
    return response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith("test-application=", StringComparison.Ordinal))
        .Split(';', 2)[0];
}

sealed record JoinMail(string Token, Uri Link, string ProtectedBody, string Plaintext, long Expires, bool Used);

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
