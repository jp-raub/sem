using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
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

internal static class Program
{
    const string Origin = "https://sem-account-passkeys-test.invalid";
    static int checks;

    static async Task Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "sem-account-passkeys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(AccountPasskeyEndpoints).Assembly.GetName().Name
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite("Data Source=" + Path.Combine(root, "account.db")));
        builder.Services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            options.User.RequireUniqueEmail = true;
        }).AddEntityFrameworkStores<ApplicationDbContext>().AddSignInManager();
        builder.Services.Configure<IdentityPasskeyOptions>(options =>
        {
            options.ServerDomain = new Uri(Origin).Host;
            options.UserVerificationRequirement = "required";
        });
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton(new DeploymentSettings { PublicUrl = Origin + "/" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "synthetic.invalid",
            ["Smtp:From"] = "noreply@example.invalid"
        });
        builder.Services.AddSingleton<NotificationService>();
        builder.Services.AddScoped<PasskeyAdditionService>();
        builder.Services.AddScoped<AccountPasskeyService>();
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "account-passkeys-test-csrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        });
        builder.Services.AddAuthentication("Synthetic")
            .AddCookie(IdentityConstants.TwoFactorUserIdScheme)
            .AddScheme<AuthenticationSchemeOptions, SyntheticAuthenticationHandler>("Synthetic", _ => { });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAntiforgery();
        app.MapGet("/__test/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Json(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
        app.MapAccountPasskeys();
        app.MapMethods("/api/{**action}", ["GET", "POST"], async (HttpContext context) =>
        {
            if (!await AccountPasskeyEndpoints.TryHandleLegacyAsync(
                    context, context.Request.RouteValues["action"]?.ToString() ?? ""))
                context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
        var started = false;
        try
        {
            await SeedAsync(app, clock);
            await ServiceChecks(app, clock);
            await app.StartAsync();
            started = true;
            await HttpChecks(app);
            Console.WriteLine($"Account passkey checks passed: {checks}");
        }
        finally
        {
            if (started) await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    static async Task SeedAsync(WebApplication app, TestClock clock)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        db.Users.AddRange(
            MakeUser("owner", "owner@example.invalid", "owner-stamp"),
            MakeUser("other", "other@example.invalid", "other-stamp"),
            MakeUser("disabled", "disabled@example.invalid", "disabled-stamp", enabled: false),
            MakeUser("unconfirmed", "unconfirmed@example.invalid", "unconfirmed-stamp", confirmed: false),
            MakeUser("last-key", "last@example.invalid", "last-stamp"),
            MakeUser("delete-target", "delete@example.invalid", "delete-stamp"),
            MakeUser("wrong-target", "wrong-target@example.invalid", "wrong-target-stamp"));
        await db.SaveChangesAsync();
        await AddKeyAsync(users, "owner", "owner-key-a");
        await AddKeyAsync(users, "owner", "owner-key-b");
        await AddKeyAsync(users, "other", "other-key");
        await AddKeyAsync(users, "last-key", "last-key-only");
        await AddKeyAsync(users, "delete-target", "delete-key-a");
        await AddKeyAsync(users, "delete-target", "delete-key-b");
        await AddKeyAsync(users, "wrong-target", "wrong-target-key-a");
        await AddKeyAsync(users, "wrong-target", "wrong-target-key-b");
        db.RevokedAuthSessions.Add(new RevokedAuthSession
        {
            UserId = "owner", TokenHash = SemEndpoints.Hash("owner-revoked-session"),
            RevokedAt = clock.GetUtcNow().ToString("O")
        });
        var addFlowId = Guid.NewGuid().ToString("N");
        db.PasskeyAdditions.Add(new PasskeyAddition
        {
            Id = addFlowId, UserId = "owner", SessionHash = SemEndpoints.Hash("owner-session"),
            Email = "owner@example.invalid", SecurityStamp = "owner-stamp",
            Created = clock.GetUtcNow().ToUnixTimeSeconds(), Expires = clock.GetUtcNow().ToUnixTimeSeconds() + 900,
            ReauthenticatedAt = clock.GetUtcNow().ToUnixTimeSeconds()
        });
        await db.SaveChangesAsync();
    }

    static ApplicationUser MakeUser(string id, string email, string stamp, bool enabled = true, bool confirmed = true) => new()
    {
        Id = id,
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        EmailConfirmed = confirmed,
        Enabled = enabled,
        SecurityStamp = stamp
    };

    static async Task AddKeyAsync(UserManager<ApplicationUser> users, string userId, string keyName)
    {
        var user = await users.FindByIdAsync(userId) ?? throw new InvalidOperationException("Missing test user.");
        var passkey = new UserPasskeyInfo(
            credentialId: Encoding.UTF8.GetBytes(keyName), publicKey: [0x01, 0x02, 0x03],
            createdAt: DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), signCount: 0,
            transports: ["internal"], isUserVerified: true, isBackupEligible: false, isBackedUp: false,
            attestationObject: [0x04, 0x05], clientDataJson: Encoding.UTF8.GetBytes("{}"));
        passkey.Name = keyName;
        var result = await users.AddOrUpdatePasskeyAsync(user, passkey);
        if (!result.Succeeded) throw new InvalidOperationException("Could not seed synthetic native Identity passkey.");
    }

    static async Task ServiceChecks(WebApplication app, TestClock clock)
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var service = sp.GetRequiredService<AccountPasskeyService>();
            var data = await service.ListAsync(Context("owner", "owner-stamp", "owner-session"), "owner", default);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(data));
            var passkeys = json.RootElement.GetProperty("passkeys");
            Check(passkeys.GetArrayLength() == 2 && json.RootElement.GetProperty("email").GetString() == "owner@example.invalid" &&
                  json.RootElement.GetProperty("emailConfirmed").GetBoolean(),
                "list returns the authenticated owner's passkeys and confirmed own email");
            var serialized = json.RootElement.GetRawText();
            Check(!serialized.Contains("other@example.invalid", StringComparison.Ordinal) &&
                  !serialized.Contains(Id("other-key"), StringComparison.Ordinal),
                "list does not disclose another user's account or credential identifier");
            await Reject(() => service.ListAsync(Context("owner", "owner-stamp", "owner-session"), "other", default),
                "expected actor mismatch is denied");
            await Reject(() => service.ListAsync(Context("other", "other-stamp", "other-session"), "owner", default),
                "another account cannot list the owner's passkeys");
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var service = sp.GetRequiredService<AccountPasskeyService>();
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            await service.RenameAsync(Principal("owner", "owner-stamp", "owner-session"), "owner",
                Id("owner-key-a"), "  Travel key  ", default);
            var owner = await users.FindByIdAsync("owner");
            var renamed = await users.GetPasskeyAsync(owner!, Encoding.UTF8.GetBytes("owner-key-a"));
            Check(renamed?.Name == "Travel key", "rename trims a valid name and changes only the owned key");
            await Reject(() => service.RenameAsync(Principal("owner", "owner-stamp", "owner-session"), "owner",
                Id("owner-key-a"), "   ", default), "empty rename is rejected");
            await Reject(() => service.RenameAsync(Principal("owner", "owner-stamp", "owner-session"), "owner",
                Id("owner-key-a"), new string('x', 201), default), "rename over 200 characters is rejected");
            await Reject(() => service.RenameAsync(Principal("owner", "owner-stamp", "owner-session"), "owner",
                Id("owner-key-a"), "bad\nname", default), "control characters in rename are rejected");
            await Reject(() => service.RenameAsync(Principal("owner", "owner-stamp", "owner-session"), "owner",
                Id("other-key"), "cross-account rename", default), "a key owned by another account cannot be renamed");
            var other = await users.FindByIdAsync("other");
            var otherPasskey = await users.GetPasskeyAsync(other!, Encoding.UTF8.GetBytes("other-key"));
            Check(otherPasskey?.Name == "other-key", "foreign-key rename leaves the other account untouched");
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AccountPasskeyService>();
            await Reject(() => service.ActorAsync(Principal("owner", "stale-stamp", "owner-session"), "owner"),
                "stale security-stamp claim is denied");
            await Reject(() => service.ActorAsync(Principal("owner", "owner-stamp", null), "owner"),
                "sessionless legacy principal is denied");
            await Reject(() => service.ActorAsync(Principal("disabled", "disabled-stamp", "disabled-session"), "disabled"),
                "disabled account is denied");
            await Reject(() => service.ActorAsync(Principal("unconfirmed", "unconfirmed-stamp", "unconfirmed-session"), "unconfirmed"),
                "unconfirmed email account is denied");
            await Reject(() => service.ActorAsync(Principal("owner", "owner-stamp", "owner-revoked-session"), "owner"),
                "revoked authentication session is denied");
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var service = sp.GetRequiredService<AccountPasskeyService>();
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            var lastProof = await SeedProofAsync(db, "last-key", "last-session", "last-key-only", "last-stamp", now, now + 300);
            var lastDeleteResult = await service.ConfirmDeleteAsync(Context("last-key", "last-stamp", "last-session"), "last-key",
                lastProof, true, default);
            Check(!lastDeleteResult, "last-key deletion consumes the dedicated proof but is refused");
            var lastUser = await users.FindByIdAsync("last-key");
            Check((await users.GetPasskeysAsync(lastUser!)).Count == 1 &&
                  (await db.PasskeyDeletionProofs.SingleAsync(p => p.Id == lastProof)).ConsumedAt == now,
                "last key remains and its deletion proof is one-use");
            await Reject(() => service.ConfirmDeleteAsync(Context("last-key", "last-stamp", "last-session"), "last-key",
                lastProof, true, default), "a consumed last-key proof cannot be replayed");
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var service = sp.GetRequiredService<AccountPasskeyService>();
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            var targetProof = await SeedProofAsync(db, "delete-target", "delete-session", "delete-key-a", "delete-stamp", now, now + 300);
            Check(await service.ConfirmDeleteAsync(Context("delete-target", "delete-stamp", "delete-session"), "delete-target",
                targetProof, true, default), "a valid, explicitly confirmed deletion removes its bound key");
            var targetUser = await users.FindByIdAsync("delete-target");
            var remaining = await users.GetPasskeysAsync(targetUser!);
            Check(remaining.Count == 1 && remaining[0].CredentialId.SequenceEqual(Encoding.UTF8.GetBytes("delete-key-b")),
                "deletion is limited to the proof-bound target key");
            await Reject(() => service.ConfirmDeleteAsync(Context("delete-target", "delete-stamp", "delete-session"), "delete-target",
                targetProof, true, default), "a successful deletion proof cannot be reused");
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var service = sp.GetRequiredService<AccountPasskeyService>();
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            var wrongSession = await SeedProofAsync(db, "wrong-target", "original-session", "wrong-target-key-a", "wrong-target-stamp", now, now + 300);
            await Reject(() => service.ConfirmDeleteAsync(Context("wrong-target", "wrong-target-stamp", "different-session"), "wrong-target",
                wrongSession, true, default), "proof cannot cross authentication sessions");
            var wrongStamp = await SeedProofAsync(db, "wrong-target", "wrong-session", "wrong-target-key-a", "old-stamp", now, now + 300);
            await Reject(() => service.ConfirmDeleteAsync(Context("wrong-target", "wrong-target-stamp", "wrong-session"), "wrong-target",
                wrongStamp, true, default), "proof cannot cross a security-stamp change");
            var wrongTarget = await SeedProofAsync(db, "wrong-target", "target-session", "not-an-owned-key", "wrong-target-stamp", now, now + 300);
            await Reject(() => service.ConfirmDeleteAsync(Context("wrong-target", "wrong-target-stamp", "target-session"), "wrong-target",
                wrongTarget, true, default), "proof cannot be redirected to a different credential");
            var wrongUserProof = await SeedProofAsync(db, "wrong-target", "other-session", "wrong-target-key-a", "wrong-target-stamp", now, now + 300);
            await Reject(() => service.ConfirmDeleteAsync(Context("owner", "owner-stamp", "other-session"), "owner",
                wrongUserProof, true, default), "proof is bound to its original user");
            var expired = await SeedProofAsync(db, "wrong-target", "expired-session", "wrong-target-key-a", "wrong-target-stamp", now, now + 300);
            clock.Advance(TimeSpan.FromSeconds(300));
            await Reject(() => service.ConfirmDeleteAsync(Context("wrong-target", "wrong-target-stamp", "expired-session"), "wrong-target",
                expired, true, default), "delete proof is invalid at the exclusive five-minute expiry");
            var wrongTargetUser = await users.FindByIdAsync("wrong-target");
            Check((await users.GetPasskeysAsync(wrongTargetUser!)).Count == 2,
                "wrong-session, wrong-stamp, wrong-user, wrong-target, and expired proofs delete no key");
            var noConfirm = await SeedProofAsync(db, "wrong-target", "no-confirm-session", "wrong-target-key-a", "wrong-target-stamp", now + 300, now + 600);
            await Reject(() => service.ConfirmDeleteAsync(Context("wrong-target", "wrong-target-stamp", "no-confirm-session"), "wrong-target",
                noConfirm, false, default), "delete requires explicit affirmative confirmation");
            var addFlow = await db.PasskeyAdditions.SingleAsync(p => p.UserId == "owner");
            await Reject(() => service.ConfirmDeleteAsync(Context("owner", "owner-stamp", "owner-session"), "owner",
                addFlow.Id, true, default), "an add-operation identifier is not a deletion-purpose proof");
            var verifiedCancel = await SeedProofAsync(db, "wrong-target", "cancel-verified-session",
                "wrong-target-key-a", "wrong-target-stamp", now + 300, now + 600, verified: true);
            await service.CancelDeleteAsync(Context("wrong-target", "wrong-target-stamp", "cancel-verified-session"),
                "wrong-target", verifiedCancel, default);
            var cancelledVerifiedRow = await db.PasskeyDeletionProofs.SingleAsync(p => p.Id == verifiedCancel);
            Check(cancelledVerifiedRow.ConsumedAt == clock.GetUtcNow().ToUnixTimeSeconds() &&
                  cancelledVerifiedRow.ChallengeHash is null,
                "a verified deletion proof can be explicitly cancelled and consumed");
            await Reject(() => service.CancelDeleteAsync(Context("wrong-target", "wrong-target-stamp", "cancel-verified-session"),
                "wrong-target", verifiedCancel, default), "verified cancellation cannot be replayed");

            var pendingCancel = await SeedProofAsync(db, "wrong-target", "cancel-pending-session",
                "wrong-target-key-b", "wrong-target-stamp", now + 300, now + 600, verified: false);
            await service.CancelDeleteAsync(Context("wrong-target", "wrong-target-stamp", "cancel-pending-session"),
                "wrong-target", pendingCancel, default);
            var cancelledPendingRow = await db.PasskeyDeletionProofs.SingleAsync(p => p.Id == pendingCancel);
            Check(cancelledPendingRow.ConsumedAt == clock.GetUtcNow().ToUnixTimeSeconds() &&
                  cancelledPendingRow.ChallengeHash is null,
                "an unverified but live target proof can also be cancelled and consumed");

            var expiredCancel = await SeedProofAsync(db, "wrong-target", "cancel-expired-session",
                "wrong-target-key-b", "wrong-target-stamp", now, now + 300, verified: true);
            await Reject(() => service.CancelDeleteAsync(Context("wrong-target", "wrong-target-stamp", "cancel-expired-session"),
                "wrong-target", expiredCancel, default), "expired delete proof cannot be cancelled as if active");
            var otherUserCancel = await SeedProofAsync(db, "wrong-target", "cancel-other-session",
                "wrong-target-key-b", "wrong-target-stamp", now + 300, now + 600, verified: true);
            await Reject(() => service.CancelDeleteAsync(Context("owner", "owner-stamp", "cancel-other-session"),
                "owner", otherUserCancel, default), "another user cannot consume a cancellation proof");

            var removedTarget = await SeedProofAsync(db, "wrong-target", "cancel-missing-key-session",
                "wrong-target-key-a", "wrong-target-stamp", now + 300, now + 600, verified: true);
            var removed = await users.RemovePasskeyAsync(wrongTargetUser!, Encoding.UTF8.GetBytes("wrong-target-key-a"));
            Check(removed.Succeeded, "test removes the proof target to exercise stale cancellation");
            await Reject(() => service.CancelDeleteAsync(Context("wrong-target", "wrong-target-stamp", "cancel-missing-key-session"),
                "wrong-target", removedTarget, default), "cancellation rejects a proof whose target key no longer exists");

            var proof = await db.PasskeyDeletionProofs.AsNoTracking().ToListAsync();
            Check(proof.All(p => !p.CredentialHash.Contains("wrong-target-key-a", StringComparison.Ordinal) &&
                  !p.SessionHash.Contains("cancel-pending-session", StringComparison.Ordinal)) &&
                  proof.All(p => p.CredentialHash.Length == 64 && p.SessionHash.Length == 64),
                "proof storage contains one-way credential and session hashes, not raw identifiers");
        }
    }

    static async Task HttpChecks(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(address) };
        using var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/__test/csrf");
        csrfRequest.Headers.Add("X-Test-User", "owner");
        csrfRequest.Headers.Add("X-Test-Stamp", "owner-stamp");
        csrfRequest.Headers.Add("X-Test-Session", "owner-session");
        using var csrfResponse = await client.SendAsync(csrfRequest);
        var csrfJson = JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync());
        var csrfToken = csrfJson.RootElement.GetProperty("token").GetString()!;
        var csrfCookie = csrfResponse.Headers.GetValues("Set-Cookie").Single()
            .Split(';', 2)[0];

        async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body,
            string? contentType = "application/json; charset=utf-8", bool includeCsrf = true,
            bool includeCookie = true, string? origin = Origin, bool authenticated = true)
        {
            var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType!);
            }
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (includeCsrf) request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            if (includeCookie) request.Headers.Add("Cookie", csrfCookie);
            if (authenticated)
            {
                request.Headers.Add("X-Test-User", "owner");
                request.Headers.Add("X-Test-Stamp", "owner-stamp");
                request.Headers.Add("X-Test-Session", "owner-session");
            }
            return await client.SendAsync(request);
        }

        using (var response = await Send(HttpMethod.Get, "/api/account-passkeys/list", null))
        {
            Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
                "authenticated list is no-store");
        }
        using (var response = await Send(HttpMethod.Get, "/api/account-passkeys/list?email=private@example.invalid", null))
        {
            var text = await response.Content.ReadAsStringAsync();
            Check(response.StatusCode == HttpStatusCode.BadRequest && response.Headers.CacheControl?.NoStore == true &&
                  !text.Contains("private@example.invalid", StringComparison.Ordinal),
                "list rejects query data and does not echo it");
        }
        using (var response = await Send(HttpMethod.Post, "/api/account-passkeys/rename", "{}", includeCsrf: false,
                   includeCookie: false, origin: "https://foreign.invalid"))
            Check(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.CacheControl?.NoStore == true,
                "foreign Origin is rejected before state-changing work");
        using (var response = await Send(HttpMethod.Post, "/api/account-passkeys/rename", "{}", includeCsrf: false))
            Check(response.StatusCode == HttpStatusCode.BadRequest && response.Headers.CacheControl?.NoStore == true,
                "state-changing request requires CSRF");
        using (var response = await Send(HttpMethod.Post, "/api/account-passkeys/rename", "{",
                   includeCsrf: false, includeCookie: false, authenticated: false))
            Check(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.CacheControl?.NoStore == true,
                "anonymous account mutation is rejected before CSRF validation or JSON body parsing");
        using (var response = await Send(HttpMethod.Post, "/api/passkey-options", "{}"))
            Check(response.StatusCode == HttpStatusCode.BadRequest && response.Headers.CacheControl?.NoStore == true,
                "legacy authenticated passkey-options adapts to the email-confirmed addition gate");
        using (var response = await Send(HttpMethod.Post, "/api/passkey-complete",
                   "{\"credential\":\"synthetic-invalid-credential\"}"))
            Check(response.StatusCode == HttpStatusCode.BadRequest && response.Headers.CacheControl?.NoStore == true,
                "legacy authenticated passkey-complete cannot bypass email confirmation");
        using (var response = await Send(HttpMethod.Post, "/api/passkey-add-cancel",
                   "{\"id\":\"" + Guid.NewGuid().ToString("N") + "\"}"))
            Check(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.CacheControl?.NoStore == true,
                "legacy account-add route uses the new service and refuses an unknown operation");

        async Task AssertBodyRejected(string label, HttpContent content, HttpStatusCode expected)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account-passkeys/rename") { Content = content };
            request.Headers.Add("Origin", Origin);
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            request.Headers.Add("Cookie", csrfCookie);
            request.Headers.Add("X-Test-User", "owner");
            request.Headers.Add("X-Test-Stamp", "owner-stamp");
            request.Headers.Add("X-Test-Session", "owner-session");
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == expected && response.Headers.CacheControl?.NoStore == true, label);
        }

        await AssertBodyRejected("non-JSON content type is rejected", new StringContent("{}", Encoding.UTF8, "text/plain"), HttpStatusCode.BadRequest);
        await AssertBodyRejected("duplicate JSON names are rejected", new StringContent(
            "{\"expectedActor\":\"owner\",\"expectedActor\":\"owner\",\"credentialId\":\"" + Id("owner-key-a") + "\",\"name\":\"x\"}",
            Encoding.UTF8, "application/json"), HttpStatusCode.BadRequest);
        await AssertBodyRejected("unknown JSON names are rejected", new StringContent(
            "{\"expectedActor\":\"owner\",\"credentialId\":\"" + Id("owner-key-a") + "\",\"name\":\"x\",\"email\":\"private@example.invalid\"}",
            Encoding.UTF8, "application/json"), HttpStatusCode.BadRequest);
        await AssertBodyRejected("trailing commas are rejected", new StringContent(
            "{\"expectedActor\":\"owner\",\"credentialId\":\"" + Id("owner-key-a") + "\",\"name\":\"x\",}",
            Encoding.UTF8, "application/json"), HttpStatusCode.BadRequest);

        var prefix = Encoding.UTF8.GetBytes("{\"expectedActor\":\"owner\",\"credentialId\":\"" + Id("owner-key-a") + "\",\"name\":\"");
        var suffix = Encoding.UTF8.GetBytes("\"}");
        await using (var bytes = new MemoryStream())
        {
            await bytes.WriteAsync(prefix);
            await bytes.WriteAsync(new byte[] { 0xC3, 0x28 });
            await bytes.WriteAsync(suffix);
            await AssertBodyRejected("invalid UTF-8 body is rejected", new ByteArrayContent(bytes.ToArray())
            {
                Headers = { ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json; charset=utf-8") }
            }, HttpStatusCode.BadRequest);
        }
        await AssertBodyRejected("request body over 64 KiB is rejected", new StringContent(
            new string('x', 64 * 1024 + 1), Encoding.UTF8, "application/json"), HttpStatusCode.RequestEntityTooLarge);

        using (var response = await Send(HttpMethod.Post, "/api/account-passkeys/rename?token=synthetic-secret", "{}"))
        {
            var text = await response.Content.ReadAsStringAsync();
            Check(response.StatusCode == HttpStatusCode.BadRequest && !text.Contains("synthetic-secret", StringComparison.Ordinal),
                "POST query parameters are rejected and not reflected");
        }
        var valid = "{\"expectedActor\":\"owner\",\"credentialId\":\"" + Id("owner-key-b") + "\",\"name\":\"HTTP renamed key\"}";
        using (var response = await Send(HttpMethod.Post, "/api/account-passkeys/rename", valid))
        {
            Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true &&
                  response.Headers.Location is null,
                "valid same-origin CSRF-protected rename succeeds without URL output");
        }
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await users.FindByIdAsync("owner");
        var key = await users.GetPasskeyAsync(owner!, Encoding.UTF8.GetBytes("owner-key-b"));
        Check(key?.Name == "HTTP renamed key", "HTTP rename persists to the authenticated owner's native Identity passkey");

        // A fully gated, synthetic addition flow must expose JSON to the strict UI.
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds();
        var flow = await db.PasskeyAdditions.SingleAsync(p => p.UserId == "owner");
        flow.Cancelled = false;
        flow.CompletedAt = null;
        flow.Expires = now + 900;
        flow.ReauthenticatedAt = now;
        flow.EmailVerifiedAt = now;
        flow.RegistrationUntil = now + 300;
        await db.SaveChangesAsync();
        foreach (var path in new[] { "/api/account-passkeys/add-create-options", "/api/passkey-options" })
        {
            var body = path.StartsWith("/api/account-passkeys/", StringComparison.Ordinal)
                ? "{\"expectedActor\":\"owner\"}" : "{}";
            using var response = await Send(HttpMethod.Post, path, body);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Creation options fixture failed HTTP " + (int)response.StatusCode + ": " +
                    (await response.Content.ReadAsStringAsync()).Substring(0, Math.Min(400, (await response.Content.ReadAsStringAsync()).Length)));
            Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
                "confirmed addition options remain behind owner, session, CSRF and no-store gates: " + path);
            Check(response.Content.Headers.ContentType?.MediaType == "application/json",
                "confirmed addition options declare JSON for the strict browser client: " + path);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(json.RootElement.GetProperty("challenge").GetString()?.Length > 0 &&
                  json.RootElement.GetProperty("rp").GetProperty("id").GetString() == new Uri(Origin).Host &&
                  json.RootElement.GetProperty("excludeCredentials").GetArrayLength() >= 1,
                "addition options preserve native challenge, RP and existing-key exclusion: " + path);
        }

    }

    static async Task<string> SeedProofAsync(ApplicationDbContext db, string userId, string session,
        string keyName, string stamp, long created, long expires, bool verified = true)
    {
        var proof = new PasskeyDeletionProof
        {
            Id = Guid.NewGuid().ToString("N"), UserId = userId,
            CredentialHash = SemEndpoints.Hash(Id(keyName)), SessionHash = SemEndpoints.Hash(session),
            SecurityStamp = stamp, CreatedAt = created, ExpiresAt = expires,
            VerifiedAt = verified ? created : null,
            ChallengeHash = verified ? null : SemEndpoints.Hash("pending-delete-challenge")
        };
        db.PasskeyDeletionProofs.Add(proof);
        await db.SaveChangesAsync();
        return proof.Id;
    }

    static HttpContext Context(string user, string stamp, string? session) =>
        new DefaultHttpContext { User = Principal(user, stamp, session) };

    static ClaimsPrincipal Principal(string user, string? stamp, string? session)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };
        if (stamp is not null) claims.Add(new Claim("AspNet.Identity.SecurityStamp", stamp));
        if (session is not null) claims.Add(new Claim(PasskeyAdditionService.SessionClaim, session));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Synthetic"));
    }

    static string Id(string key) => Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static void Check(bool success, string label)
    {
        if (!success) throw new InvalidOperationException("FAIL " + label);
        checks++;
        Console.WriteLine("PASS " + label);
    }

    static async Task Reject(Func<Task> action, string label)
    {
        try { await action(); }
        catch (Exception ex) when (ex is ValidationException or UnauthorizedAccessException or AccountPasskeyConflictException)
        {
            Check(true, label);
            return;
        }
        throw new InvalidOperationException("Expected safe rejection: " + label);
    }

    sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    sealed class SyntheticAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var values) || values.Count != 1)
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, values[0]!) };
            if (Request.Headers.TryGetValue("X-Test-Stamp", out var stamp) && stamp.Count == 1)
                claims.Add(new Claim("AspNet.Identity.SecurityStamp", stamp[0]!));
            if (Request.Headers.TryGetValue("X-Test-Session", out var session) && session.Count == 1)
                claims.Add(new Claim(PasskeyAdditionService.SessionClaim, session[0]!));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
