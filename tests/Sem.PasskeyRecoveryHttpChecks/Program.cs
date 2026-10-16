using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

const string Origin = "https://sem-recovery-test.invalid";
var root = Path.Combine(Path.GetTempPath(), "sem-recovery-http-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var clock = new TestClock(DateTimeOffset.UtcNow);
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
builder.Services.AddSingleton<TimeProvider>(clock);
builder.Services.AddSingleton(new DeploymentSettings { PublicUrl = Origin + "/" });
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Smtp:Host"] = "synthetic.invalid",
    ["Smtp:From"] = "noreply@example.test"
});
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<BusinessNotificationStore>();
builder.Services.AddScoped<CoreFoundationService>();
builder.Services.AddScoped<PasskeyAdditionService>();
builder.Services.AddScoped<IPasskeyRecoveryIdentity, FakeRecoveryIdentity>();
builder.Services.AddScoped<PasskeyRecoveryService>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "test-csrf";
    options.Cookie.SecurePolicy = CookieSecurePolicy.None;
});
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "TestSelector";
        options.DefaultChallengeScheme = "TestSelector";
    })
    .AddPolicyScheme("TestSelector", "Synthetic-or-application-cookie", options =>
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
    })
    .AddCookie("SEM.Enrollment", options =>
    {
        options.Cookie.Name = "test-enrollment";
        options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        options.Cookie.HttpOnly = true;
        options.Cookie.Path = "/";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
        options.SlidingExpiration = false;
    });

var savedTargetPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
[
    new Claim(ClaimTypes.NameIdentifier, "target"),
    new Claim("AspNet.Identity.SecurityStamp", "target-stamp"),
    new Claim(PasskeyAdditionService.SessionClaim, "target-session-before-recovery")
], IdentityConstants.ApplicationScheme));
var app = builder.Build();
app.UseAuthentication();
app.UseSemLiveAuthenticationSession();
app.UseAntiforgery();
app.MapGet("/fixture/enrollment-cookie", async (HttpContext context) =>
{
    await context.SignInAsync("SEM.Enrollment",
        new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "target")], "SEM.Enrollment")),
        new AuthenticationProperties { IsPersistent = false });
    return Results.NoContent();
});
app.MapGet("/fixture/application-cookie", async (HttpContext context) =>
{
    await context.SignInAsync(IdentityConstants.ApplicationScheme, savedTargetPrincipal,
        new AuthenticationProperties { IsPersistent = false });
    return Results.NoContent();
});
app.MapGet("/fixture/application-protected", (HttpContext context) =>
    context.User.Identity?.IsAuthenticated == true ? Results.Ok() : Results.StatusCode(403));
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
        db.Users.AddRange(
            User("admin", "admin@example.test", "admin-stamp", enabled: true, isAdmin: true),
            User("member", "member@example.test", "member-stamp", enabled: true),
            User("target", "target@example.test", "target-stamp", enabled: true),
            User("expired-target", "expired@example.test", "expired-stamp", enabled: true));
        await db.SaveChangesAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var oldKey = new UserPasskeyInfo(
            credentialId: Encoding.UTF8.GetBytes("synthetic-old-credential"),
            publicKey: [0x01, 0x02, 0x03],
            createdAt: clock.GetUtcNow(),
            signCount: 0,
            transports: ["internal"],
            isUserVerified: true,
            isBackupEligible: false,
            isBackedUp: false,
            attestationObject: [0x04, 0x05],
            clientDataJson: Encoding.UTF8.GetBytes("{}"));
        var oldKeyResult = await users.AddOrUpdatePasskeyAsync(await db.Users.SingleAsync(user => user.Id == "target"), oldKey);
        if (!oldKeyResult.Succeeded) throw new Exception("Could not seed synthetic old passkey.");
        db.RevokedAuthSessions.Add(new()
        {
            UserId = "admin",
            TokenHash = SemEndpoints.Hash("revoked-admin-session"),
            RevokedAt = clock.GetUtcNow().ToString("O")
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

    async Task<(string Token, string Cookie)> GetCsrf(string? authKind)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/csrf");
        if (authKind is not null) request.Headers.Add("Cookie", "test-auth=" + authKind);
        using var response = await client.SendAsync(request);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("token").GetString()!;
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("test-csrf=", StringComparison.Ordinal))
            .Split(';', 2)[0];
        return (token, cookie);
    }

    async Task<string> GetApplicationCookie()
    {
        using var response = await client.GetAsync("/fixture/application-cookie");
        if (!response.IsSuccessStatusCode) throw new Exception("Could not issue fixture application cookie.");
        return response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("test-application=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    async Task<string> GetEnrollmentCookie()
    {
        using var response = await client.GetAsync("/fixture/enrollment-cookie");
        if (!response.IsSuccessStatusCode) throw new Exception("Could not issue fixture enrollment cookie.");
        return response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("test-enrollment=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    async Task<HttpResponseMessage> Get(string path, string? cookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    async Task<HttpResponseMessage> SendPost(
        string path, HttpContent content, string? cookie, string? csrf,
        string origin = Origin, bool chunked = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("Origin", origin);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (chunked) request.Headers.TransferEncodingChunked = true;
        return await client.SendAsync(request);
    }

    Task<HttpResponseMessage> Post(
        string path, string body, string? cookie, string? csrf,
        string origin = Origin, string contentType = "application/json") =>
        SendPost(path, new StringContent(body, Encoding.UTF8, contentType), cookie, csrf, origin);

    static string Cookie(string kind, string csrfCookie) => "test-auth=" + kind + "; " + csrfCookie;
    static string RecoveryBody(string targetId, string reason) => JsonSerializer.Serialize(new
    {
        targetUserId = targetId,
        reason
    });
    static string Credential(string challenge) => JsonSerializer.Serialize(new
    {
        id = "synthetic-credential",
        type = "public-key",
        response = new
        {
            clientDataJSON = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { challenge })))
        }
    });

    var adminCsrf = await GetCsrf("admin");
    var adminCookie = Cookie("admin", adminCsrf.Cookie);
    var anonymousCsrf = await GetCsrf(null);
    var anonymousCookie = anonymousCsrf.Cookie;
    var savedTargetCookie = await GetApplicationCookie();
    var retainedEnrollmentCookie = await GetEnrollmentCookie();

    using (var response = await Get("/api/passkey-recovery-admin-context", adminCookie))
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Check(response.IsSuccessStatusCode && body.GetProperty("actorId").GetString() == "admin",
            "admin context requires and identifies the current administrator");
    }
    using (var response = await Get("/api/passkey-recovery-admin-context?id=target", adminCookie))
    {
        var target = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("target");
        Check(response.IsSuccessStatusCode && target.GetProperty("enabled").GetBoolean() &&
              !target.GetProperty("recovering").GetBoolean(), "admin context reads eligible target state");
    }
    using (var response = await Get("/api/passkey-recovery-admin-context?id=target&id=member", adminCookie))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "duplicate admin-context query rejected");
    using (var response = await Get("/api/passkey-recovery-admin-context", Cookie("member", adminCsrf.Cookie)))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "non-admin actor rejected");
    using (var response = await Get("/api/passkey-recovery-admin-context", Cookie("admin-stale-stamp", adminCsrf.Cookie)))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "stale administrator security stamp rejected");
    using (var response = await Get("/api/passkey-recovery-admin-context", Cookie("admin-no-session", adminCsrf.Cookie)))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "administrator without session claim rejected");
    using (var response = await Get("/api/passkey-recovery-admin-context", Cookie("revoked-admin", adminCsrf.Cookie)))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "revoked administrator session rejected");

    using (var response = await Get("/api/passkey-recovery-admin-options", adminCookie))
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed && response.Headers.CacheControl?.NoStore == true,
            "GET admin action is 405 and no-store");
    using (var response = await Post("/api/passkey-recovery-admin-options?expectedActor=other", "{", adminCookie, null))
        Check(response.StatusCode == HttpStatusCode.Forbidden,
            "expected administrator is checked before body parsing and CSRF");
    using (var response = await Post("/api/passkey-recovery-admin-options?expectedActor=admin&expectedActor=admin", "{", adminCookie, null))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "duplicate expectedActor rejected before body parsing");
    using (var response = await Post("/api/passkey-recovery-admin-options?expectedActor=admin", "{}", adminCookie, null,
               origin: "https://foreign.invalid"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "foreign Origin rejected");
    using (var response = await Post("/api/passkey-recovery-admin-options?expectedActor=admin", "{}", adminCookie, null))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "CSRF token required");
    using (var response = await Post("/api/passkey-recovery-confirm?unexpected=1", "{}", anonymousCookie, anonymousCsrf.Token))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "anonymous endpoint rejects query parameters");
    using (var response = await Post("/api/passkey-recovery-admin-options?expectedActor=admin",
               "{\"targetUserId\":true,\"reason\":\"Support request\"}", adminCookie, adminCsrf.Token))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "wrong JSON field type rejected");
    using (var response = await Post("/api/passkey-recovery-admin-options?expectedActor=admin",
               "{\"targetUserId\":\"target\",\"targetUserId\":\"member\",\"reason\":\"Support request\"}", adminCookie, adminCsrf.Token))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "duplicate JSON field rejected");
    using (var response = await Post("/api/passkey-recovery-admin-options?expectedActor=admin",
               "{\"targetUserId\":\"target\",\"reason\":\"Support request\",\"extra\":true}", adminCookie, adminCsrf.Token))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "unknown JSON field rejected");
    using (var response = await Post("/api/passkey-recovery-admin-options?expectedActor=admin",
               "{\"targetUserId\":\"admin\",\"reason\":\"Self recovery\"}", adminCookie, adminCsrf.Token))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "administrator cannot target self");
    using (var response = await Post("/api/passkey-recovery-admin-options?expectedActor=admin",
               new string('x', 4097), adminCookie, adminCsrf.Token))
        Check((int)response.StatusCode == 413, "Content-Length body limit enforced");
    using (var requestContent = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 4097)))))
    {
        requestContent.Headers.ContentType = new("application/json");
        using var response = await SendPost("/api/passkey-recovery-admin-options?expectedActor=admin",
            requestContent, adminCookie, adminCsrf.Token, chunked: true);
        Check((int)response.StatusCode == 413 && response.Headers.CacheControl?.NoStore == true,
            "chunked body limit enforced without cacheability");
    }
    using (var invalidUtf8 = new ByteArrayContent([0xC3, 0x28]))
    {
        invalidUtf8.Headers.ContentType = new("application/json");
        using var response = await SendPost("/api/passkey-recovery-admin-options?expectedActor=admin",
            invalidUtf8, adminCookie, adminCsrf.Token);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid UTF-8 rejected strictly");
    }
    using (var response = await Post("/api/passkey-recovery-admin-options?expectedActor=admin", "{}",
               adminCookie, adminCsrf.Token, contentType: "text/plain"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "non-JSON content type rejected");

    var reason = "Member lost access to registered passkeys";
    string adminChallenge;
    using (var response = await Post("/api/passkey-recovery-admin-options?expectedActor=admin",
               RecoveryBody("target", reason), adminCookie, adminCsrf.Token))
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        adminChallenge = json.GetProperty("publicKey").GetProperty("challenge").GetString()!;
        Check(response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(adminChallenge),
            "admin proof options issued for eligible target");
    }
    var startBody = JsonSerializer.Serialize(new
    {
        targetUserId = "target",
        reason,
        credential = Credential(adminChallenge),
        explicitlyConfirmed = true
    });
    string operationId;
    using (var response = await Post("/api/passkey-recovery-start?expectedActor=admin", startBody,
               adminCookie, adminCsrf.Token))
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        operationId = json.GetProperty("operationId").GetString()!;
        Check(response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(operationId),
            "synthetic administrator proof starts recovery");
        Check(!HasAuthCookie(response), "start does not create an authentication session");
    }

    var enrollmentAndAnonymousCsrfCookie = retainedEnrollmentCookie + "; " + anonymousCookie;
    using (var response = await Post("/api/passkey-options", "{}",
               enrollmentAndAnonymousCsrfCookie, anonymousCsrf.Token))
        Check(response.StatusCode == HttpStatusCode.Forbidden,
            "disabled recovery target rejects a retained synthetic SEM.Enrollment cookie for options");
    using (var response = await Post("/api/passkey-complete", "{\"credential\":\"not-a-valid-credential\"}",
               enrollmentAndAnonymousCsrfCookie, anonymousCsrf.Token))
        Check(response.StatusCode == HttpStatusCode.Forbidden,
            "disabled recovery target rejects malformed enrollment attestation before verification");

    string mailToken;
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var target = await db.Users.SingleAsync(user => user.Id == "target");
        var operation = await db.PasskeyRecoveryOperations.SingleAsync(item => item.Id == operationId);
        var mails = await db.PasskeyRecoveryMails.Where(mail => mail.OperationId == operationId).ToListAsync();
        Check(!target.Enabled && target.SecurityStamp == operation.SecurityStamp &&
              target.SecurityStamp != "target-stamp" && operation.Status == "Pending" &&
              operation.DisabledForRecovery, "start disables target and rotates its security stamp");
        Check(await db.Set<IdentityUserPasskey<string>>().CountAsync(key => key.UserId == "target") == 0,
            "start deletes the target's previous passkeys");
        Check(mails.Count == 1 && mails[0].Status == "Pending",
            "start durably queues one protected mail without sending SMTP");
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("SEM.PasskeyRecovery.Mail.v1");
        var payload = JsonSerializer.Deserialize<PasskeyRecoveryMailPayload>(
            protector.Unprotect(mails[0].ProtectedPayload))!;
        var marker = payload.Body.LastIndexOf("#op=", StringComparison.Ordinal);
        Check(marker >= 0, "queued protected mail contains a recovery link");
        var fragment = payload.Body[(marker + 4)..].Split('\n', 2)[0];
        var pairs = fragment.Split('&');
        var linkedOperation = Uri.UnescapeDataString(pairs[0]);
        mailToken = Uri.UnescapeDataString(pairs.Single(pair => pair.StartsWith("token=", StringComparison.Ordinal))[6..]);
        Check(linkedOperation == operationId && !string.IsNullOrWhiteSpace(mailToken),
            "recovery token is extractable only from the protected queued payload");
    }

    using (var form = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["operationId"] = operationId,
        ["token"] = mailToken,
        ["__RequestVerificationToken"] = anonymousCsrf.Token
    }))
    using (var response = await SendPost("/api/passkey-recovery-confirm", form, anonymousCookie, null))
        Check(response.StatusCode == HttpStatusCode.BadRequest,
            "anonymous confirmation rejects a valid antiforgery form token without the required header");
    using (var oversizedForm = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["operationId"] = operationId,
        ["token"] = mailToken,
        ["__RequestVerificationToken"] = anonymousCsrf.Token,
        ["padding"] = new string('x', 4100)
    }))
    using (var response = await SendPost("/api/passkey-recovery-confirm", oversizedForm, anonymousCookie, null))
        Check(response.StatusCode == (HttpStatusCode)413,
            "oversized anonymous form is rejected by the body cap before antiforgery parsing");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var operation = await db.PasskeyRecoveryOperations.SingleAsync(item => item.Id == operationId);
        Check(operation.Status == "Pending" && operation.TokenHash is not null && operation.GrantHash is null,
            "rejected form submissions do not consume the pending recovery operation");
    }

    var confirmBody = JsonSerializer.Serialize(new { operationId, token = mailToken });
    string grant;
    using (var response = await Post("/api/passkey-recovery-confirm", confirmBody,
               anonymousCookie, anonymousCsrf.Token))
    {
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Confirmation failed with {(int)response.StatusCode}: {responseBody}");
        var json = JsonDocument.Parse(responseBody).RootElement;
        grant = json.GetProperty("grant").GetString()!;
        Check(json.GetProperty("userId").GetString() == "target" &&
              !string.IsNullOrWhiteSpace(grant), "anonymous email confirmation exchanges token for grant");
        Check(!HasAuthCookie(response), "mail confirmation grants no normal login cookie");
    }
    var creationOptionsBody = JsonSerializer.Serialize(new { userId = "target", grant });
    string creationChallenge;
    using (var response = await Post("/api/passkey-recovery-options", creationOptionsBody,
               anonymousCookie, anonymousCsrf.Token))
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        creationChallenge = json.GetProperty("publicKey").GetProperty("challenge").GetString()!;
        Check(response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(creationChallenge),
            "email grant authorizes passkey-creation options anonymously");
    }
    var completeBody = JsonSerializer.Serialize(new
    {
        userId = "target",
        grant,
        credential = Credential(creationChallenge)
    });
    string completedStamp;
    using (var response = await Post("/api/passkey-recovery-complete", completeBody,
               anonymousCookie, anonymousCsrf.Token))
    {
        var body = await response.Content.ReadAsStringAsync();
        Check(response.IsSuccessStatusCode && body.Contains("通常のパスキーログイン", StringComparison.Ordinal),
            "new passkey completes recovery and requires a fresh login");
        Check(!HasAuthCookie(response), "passkey completion does not create an authentication session");
    }
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var target = await db.Users.SingleAsync(user => user.Id == "target");
        var operation = await db.PasskeyRecoveryOperations.SingleAsync(item => item.Id == operationId);
        completedStamp = target.SecurityStamp!;
        Check(target.Enabled && completedStamp != operation.SecurityStamp && operation.Status == "Completed" &&
              !operation.DisabledForRecovery && operation.GrantHash is null,
            "completion enables the target, rotates stamp, and consumes grant");
        Check(await db.Set<IdentityUserPasskey<string>>().CountAsync(key => key.UserId == "target") == 1,
            "synthetic adapter persists the newly registered key");
        var identityOptions = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value;
        Check(!await LiveAuthenticationSession.ValidAsync(db, savedTargetPrincipal, identityOptions),
            "live-session helper rejects the saved pre-recovery principal after re-enable");
    }
    using (var response = await Get("/fixture/application-protected", savedTargetCookie))
        Check(response.StatusCode == HttpStatusCode.Forbidden,
            "saved synthetic Identity.Application cookie is denied after recovery re-enables the account");
    using (var response = await Get("/api/status", savedTargetCookie))
    {
        var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Check(response.IsSuccessStatusCode && status.GetProperty("user").ValueKind == JsonValueKind.Null,
            "status returns no authenticated user for the saved pre-recovery cookie");
    }
    await using (var coreScope = app.Services.CreateAsyncScope())
    {
        var core = coreScope.ServiceProvider.GetRequiredService<CoreFoundationService>();
        var denied = false;
        try { _ = await core.ReadAsync(savedTargetPrincipal); }
        catch (UnauthorizedAccessException) { denied = true; }
        Check(denied, "CoreFoundationService.ReadAsync denies the old principal immediately after re-enable");
    }
    using (var response = await Post("/api/passkey-recovery-confirm", confirmBody,
               anonymousCookie, anonymousCsrf.Token))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "mail confirmation token replay rejected");
    using (var response = await Post("/api/passkey-recovery-options", creationOptionsBody,
               anonymousCookie, anonymousCsrf.Token))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "consumed grant cannot obtain new options");
    using (var response = await Post("/api/passkey-recovery-complete", completeBody,
               anonymousCookie, anonymousCsrf.Token))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "consumed grant cannot complete twice");

    clock.Advance(TimeSpan.FromSeconds(61));
    const string expiredReason = "Member replacement device recovery";
    string expiredChallenge;
    using (var response = await Post("/api/passkey-recovery-admin-options?expectedActor=admin",
               RecoveryBody("expired-target", expiredReason), adminCookie, adminCsrf.Token))
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        expiredChallenge = json.GetProperty("publicKey").GetProperty("challenge").GetString()!;
        Check(response.IsSuccessStatusCode, "second target obtains independent admin proof");
    }
    var expiredStartBody = JsonSerializer.Serialize(new
    {
        targetUserId = "expired-target",
        reason = expiredReason,
        credential = Credential(expiredChallenge),
        explicitlyConfirmed = true
    });
    string expiredOperationId;
    using (var response = await Post("/api/passkey-recovery-start?expectedActor=admin", expiredStartBody,
               adminCookie, adminCsrf.Token))
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        expiredOperationId = json.GetProperty("operationId").GetString()!;
        Check(response.IsSuccessStatusCode, "second recovery starts for expiry check");
    }
    string expiredToken;
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mail = await db.PasskeyRecoveryMails.SingleAsync(item => item.OperationId == expiredOperationId);
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("SEM.PasskeyRecovery.Mail.v1");
        var payload = JsonSerializer.Deserialize<PasskeyRecoveryMailPayload>(protector.Unprotect(mail.ProtectedPayload))!;
        var marker = payload.Body.LastIndexOf("#op=", StringComparison.Ordinal);
        var fragment = payload.Body[(marker + 4)..].Split('\n', 2)[0];
        expiredToken = Uri.UnescapeDataString(fragment.Split('&')
            .Single(pair => pair.StartsWith("token=", StringComparison.Ordinal))[6..]);
    }
    clock.Advance(TimeSpan.FromSeconds(PasskeyRecoveryService.OperationSeconds + 1));
    using (var response = await Post("/api/passkey-recovery-confirm",
               JsonSerializer.Serialize(new { operationId = expiredOperationId, token = expiredToken }),
               anonymousCookie, anonymousCsrf.Token))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "expired recovery email token rejected");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Check(!((await db.Users.SingleAsync(user => user.Id == "expired-target")).Enabled),
            "expired recovery leaves the account disabled");
    }

    Console.WriteLine("Passkey recovery HTTP checks passed: " + checks);
}
finally
{
    if (started) await app.StopAsync();
    await app.DisposeAsync();
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}

static ApplicationUser User(string id, string email, string stamp, bool enabled, bool isAdmin = false) => new()
{
    Id = id,
    UserName = email,
    NormalizedUserName = email.ToUpperInvariant(),
    Email = email,
    NormalizedEmail = email.ToUpperInvariant(),
    DisplayName = id,
    EmailConfirmed = true,
    Enabled = enabled,
    IsAdmin = isAdmin,
    SecurityStamp = stamp
};

static bool HasAuthCookie(HttpResponseMessage response) =>
    response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(value =>
        value.Contains("Identity.Application", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("SEM-Auth", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("test-auth=", StringComparison.OrdinalIgnoreCase));

sealed class TestClock(DateTimeOffset initial) : TimeProvider
{
    DateTimeOffset now = initial;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now = now.Add(duration);
}

sealed class FakeRecoveryIdentity(
    UserManager<ApplicationUser> users,
    TimeProvider clock) : IPasskeyRecoveryIdentity
{
    int requestSequence;
    int creationSequence;

    public Task<string> MakeRequestOptionsAsync(ApplicationUser user) =>
        Task.FromResult(JsonSerializer.Serialize(new
        {
            challenge = "admin-challenge-" + Interlocked.Increment(ref requestSequence),
            rpId = "sem-recovery-test.invalid"
        }));

    public Task<bool> VerifyAndUpdateAssertionAsync(string credential, ApplicationUser expectedUser) =>
        Task.FromResult(expectedUser.Id == "admin" && HasChallenge(credential));

    public Task<string> MakeCreationOptionsAsync(ApplicationUser user) =>
        Task.FromResult(JsonSerializer.Serialize(new
        {
            challenge = "creation-challenge-" + Interlocked.Increment(ref creationSequence),
            rpId = "sem-recovery-test.invalid"
        }));

    public async Task<bool> VerifyAndSaveAttestationAsync(string credential, ApplicationUser expectedUser)
    {
        if (expectedUser.Id is not ("target" or "expired-target") || !HasChallenge(credential)) return false;
        var passkey = new UserPasskeyInfo(
            credentialId: Encoding.UTF8.GetBytes("synthetic-new-credential-" + expectedUser.Id),
            publicKey: [0x11, 0x12, 0x13],
            createdAt: clock.GetUtcNow(),
            signCount: 0,
            transports: ["internal"],
            isUserVerified: true,
            isBackupEligible: false,
            isBackedUp: false,
            attestationObject: [0x14, 0x15],
            clientDataJson: GetClientDataJson(credential));
        return (await users.AddOrUpdatePasskeyAsync(expectedUser, passkey)).Succeeded;
    }

    static byte[] GetClientDataJson(string credential)
    {
        using var json = JsonDocument.Parse(credential);
        var encoded = json.RootElement.GetProperty("response").GetProperty("clientDataJSON").GetString()!;
        return Convert.FromBase64String(encoded);
    }

    static bool HasChallenge(string credential)
    {
        try
        {
            using var clientData = JsonDocument.Parse(GetClientDataJson(credential));
            return !string.IsNullOrWhiteSpace(clientData.RootElement.GetProperty("challenge").GetString());
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }
}

sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue("test-auth", out var kind))
            return Task.FromResult(AuthenticateResult.NoResult());
        var (userId, stamp, session) = kind switch
        {
            "admin" => ("admin", "admin-stamp", "admin-session"),
            "admin-stale-stamp" => ("admin", "old-admin-stamp", "admin-session"),
            "admin-no-session" => ("admin", "admin-stamp", (string?)null),
            "revoked-admin" => ("admin", "admin-stamp", "revoked-admin-session"),
            "member" => ("member", "member-stamp", "member-session"),
            _ => ("unknown", (string?)null, (string?)null)
        };
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        if (stamp is not null) claims.Add(new Claim("AspNet.Identity.SecurityStamp", stamp));
        if (session is not null) claims.Add(new Claim(PasskeyAdditionService.SessionClaim, session));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
