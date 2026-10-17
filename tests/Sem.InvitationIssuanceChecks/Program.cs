using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

const string Origin = "https://sem-issuance-test.invalid";
const string PreviousMigration = "20261016024500_AddHostEmergencyPasskeyRecovery";
var root = Path.Combine(Path.GetTempPath(), "sem-invitation-issuance-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var stamps = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["admin"] = "stamp-admin-current",
    ["member"] = "stamp-member-current"
};
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite($"Data Source={Path.Combine(root, "test.db")};Default Timeout=10"));
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
    ["Smtp:From"] = "noreply@example.test",
    ["Push:PublicKey"] = "synthetic-push-public-key",
    ["Push:PrivateKey"] = "synthetic-push-private-key"
});
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddScoped<PasskeyAdditionService>();
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
    var contentLength = context.Request.ContentLength;
    context.Request.Body = meter;
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Test-Body-Read"] = meter.BytesRead.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-Test-Content-Length"] =
            contentLength?.ToString(CultureInfo.InvariantCulture) ?? "null";
        return Task.CompletedTask;
    });
    try { await next(); }
    finally { context.Request.Body = original; }
});
app.UseAuthentication();
app.UseSemLiveAuthenticationSession();
app.UseAntiforgery();
app.MapGet("/fixture/application-cookie/{userId}", async (HttpContext context, string userId,
    IOptions<IdentityOptions> identityOptions) =>
{
    var types = identityOptions.Value.ClaimsIdentity;
    var session = context.Request.Query["session"].FirstOrDefault() ?? "application-session-" + userId;
    var stamp = context.Request.Query["stamp"].FirstOrDefault() ?? stamps.GetValueOrDefault(userId, "unknown-stamp");
    var principal = new ClaimsPrincipal(new ClaimsIdentity(
    [
        new Claim(types.UserIdClaimType, userId),
        new Claim(types.SecurityStampClaimType, stamp),
        new Claim(PasskeyAdditionService.SessionClaim, session)
    ], IdentityConstants.ApplicationScheme));
    await context.SignInAsync(IdentityConstants.ApplicationScheme, principal,
        new AuthenticationProperties { IsPersistent = false });
    return Results.NoContent();
});
app.MapInvitationManagement();
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
    // Keep this fixture separate from the normal migration path: the unique
    // filtered index must fail closed rather than repair existing duplicates.
    var duplicateDbPath = Path.Combine(root, "preexisting-duplicate-request-id.db");
    var duplicateServices = new ServiceCollection();
    duplicateServices.AddLogging();
    duplicateServices.AddDbContextFactory<ApplicationDbContext>(options =>
        options.UseSqlite($"Data Source={duplicateDbPath};Default Timeout=10"));
    duplicateServices.AddIdentityCore<ApplicationUser>(options =>
        {
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            options.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>();
    await using (var duplicateProvider = duplicateServices.BuildServiceProvider())
    {
        await using var duplicateDb = await duplicateProvider
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
        await duplicateDb.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await duplicateDb.Database.OpenConnectionAsync();
        try
        {
            await using var command = (SqliteCommand)duplicateDb.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                INSERT INTO "Invitations"
                    ("Id","TokenHash","OwnerId","IssuerId","RequestId","Limit","Uses","Expires","Created","Disabled","RevokedBy","RevocationReason","RevokedAt","ProtectedToken")
                VALUES ($id,$hash,$owner,$issuer,$requestId,2,0,200000,100000,0,NULL,NULL,NULL,$protected)
                """;
            foreach (var id in new[] { "duplicate-a", "duplicate-b" })
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$hash", SemEndpoints.Hash("synthetic-token-" + id));
                command.Parameters.AddWithValue("$owner", "member");
                command.Parameters.AddWithValue("$issuer", "admin");
                command.Parameters.AddWithValue("$requestId", "preexisting-duplicate-request");
                command.Parameters.AddWithValue("$protected", "synthetic-protected-" + id);
                await command.ExecuteNonQueryAsync();
            }
        }
        finally { await duplicateDb.Database.CloseConnectionAsync(); }

        Exception? migrationFailure = null;
        try { await duplicateDb.Database.MigrateAsync(); }
        catch (Exception error) when (HasSqliteException(error)) { migrationFailure = error; }
        Check(migrationFailure is not null &&
              ExceptionMessages(migrationFailure).Contains("UNIQUE", StringComparison.OrdinalIgnoreCase),
            "pre-existing duplicate non-null RequestIds stop migration at the unique index");

        await duplicateDb.Database.OpenConnectionAsync();
        try
        {
            await using var command = (SqliteCommand)duplicateDb.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT "Id" || '|' || "RequestId" || '|' || COALESCE("Purpose", '<null>') || '|' ||
                       COALESCE("IssuanceSource", '<null>')
                FROM "Invitations" WHERE "RequestId" = 'preexisting-duplicate-request'
                ORDER BY "Id"
                """;
            var afterRows = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) afterRows.Add(reader.GetString(0));
            Check(afterRows.SequenceEqual([
                    "duplicate-a|preexisting-duplicate-request|<null>|<null>",
                    "duplicate-b|preexisting-duplicate-request|<null>|<null>"
                ]),
                "failed unique-index migration leaves duplicate rows and nullable metadata unrepaired");

            command.CommandText = """
                SELECT COUNT(*) FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = '20261017032500_AddInvitationRequestDecisionAndUniqueRequestIndex'
                """;
            Check(Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 0,
                "failed duplicate migration is not recorded as applied");
            command.CommandText = """
                SELECT COUNT(*) FROM "sqlite_master"
                WHERE "type" = 'index' AND "name" = 'IX_Invitations_RequestId'
                """;
            Check(Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 0,
                "failed duplicate migration leaves no partial unique index");
        }
        finally { await duplicateDb.Database.CloseConnectionAsync(); }
    }

    var tokenProtector = app.Services.GetRequiredService<IDataProtectionProvider>()
        .CreateProtector("SEM.Tokens.v1");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        db.Users.AddRange(
            User("admin", "admin@example.test", "Admin", stamps["admin"], isAdmin: true),
            User("member", "member@example.test", "Member", stamps["member"]));
        await db.SaveChangesAsync();

        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = (SqliteCommand)db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                INSERT INTO "Invitations"
                    ("Id","TokenHash","OwnerId","IssuerId","RequestId","Limit","Uses","Expires","Created","Disabled","RevokedBy","RevocationReason","RevokedAt","ProtectedToken")
                VALUES ($id,$hash,$owner,$issuer,NULL,2,0,$expires,$created,0,NULL,NULL,NULL,$protected)
                """;
            command.Parameters.AddWithValue("$id", "legacy-existing");
            command.Parameters.AddWithValue("$hash", SemEndpoints.Hash("old-baseline-secret"));
            command.Parameters.AddWithValue("$owner", "admin");
            command.Parameters.AddWithValue("$issuer", "admin");
            command.Parameters.AddWithValue("$expires", SemEndpoints.Now + 86400);
            command.Parameters.AddWithValue("$created", SemEndpoints.Now - 10);
            command.Parameters.AddWithValue("$protected", tokenProtector.Protect("old-baseline-secret"));
            await command.ExecuteNonQueryAsync();

            command.CommandText = """
                INSERT INTO "Invitations"
                    ("Id","TokenHash","OwnerId","IssuerId","RequestId","Limit","Uses","Expires","Created","Disabled","RevokedBy","RevocationReason","RevokedAt","ProtectedToken")
                VALUES ($id,$hash,$owner,$issuer,NULL,2,0,$expires,$created,0,NULL,NULL,NULL,$protected)
                """;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", "legacy-member-owned");
            command.Parameters.AddWithValue("$hash", SemEndpoints.Hash("snapshot-member-secret"));
            command.Parameters.AddWithValue("$owner", "member");
            command.Parameters.AddWithValue("$issuer", "admin");
            command.Parameters.AddWithValue("$expires", SemEndpoints.Now + 86400);
            command.Parameters.AddWithValue("$created", SemEndpoints.Now - 9);
            command.Parameters.AddWithValue("$protected", tokenProtector.Protect("snapshot-member-secret"));
            await command.ExecuteNonQueryAsync();
        }
        finally { await db.Database.CloseConnectionAsync(); }

        await db.Database.MigrateAsync();
        Check(!db.Database.HasPendingModelChanges(), "issuance migration snapshot matches the EF model");
        var oldRow = await db.Invitations.SingleAsync(x => x.Id == "legacy-existing");
        Check(oldRow.Purpose is null && oldRow.IssuanceSource is null,
            "metadata migration preserves pre-existing invitation rows as null/null");
        db.RevokedAuthSessions.Add(new RevokedAuthSession
        {
            UserId = "admin",
            TokenHash = SemEndpoints.Hash("revoked-admin-session"),
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
    var adminCookie = await GetApplicationCookieAsync(client, "admin");
    var adminCookieCsrf = await GetCsrfAsync(client, appCookie: adminCookie);
    var adminSyntheticCsrf = await GetCsrfAsync(client, actor: "admin");
    var staleAdminCsrf = await GetCsrfAsync(client, actor: "admin", stamp: "stale-admin-stamp");
    var memberCsrf = await GetCsrfAsync(client, actor: "member");

    using (var response = await SendAsync(HttpMethod.Get, "/api/snapshot", content: null,
        adminSyntheticCsrf, actor: "admin"))
    {
        Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
            "legacy administrator snapshot remains no-store");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var otherOwner = json.RootElement.GetProperty("invites").EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == "legacy-member-owned");
        Check(otherOwner.GetProperty("link").ValueKind == JsonValueKind.Null &&
              !json.RootElement.GetRawText().Contains("snapshot-member-secret", StringComparison.Ordinal),
            "legacy snapshot with administrator scope never exposes another owner's link");
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content,
        CsrfTokens csrf, string? actor = "admin", string? stamp = null, string? session = null,
        string? applicationCookie = null, bool includeCsrfHeader = true,
        bool includeCsrfCookie = true, string? origin = Origin,
        bool duplicateCsrfHeader = false, bool duplicateOrigin = false)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        var cookies = new List<string>();
        if (includeCsrfCookie) cookies.Add(csrf.Cookie);
        if (applicationCookie is not null) cookies.Add(applicationCookie);
        if (cookies.Count > 0) request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        if (applicationCookie is null && actor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Actor", actor);
            request.Headers.TryAddWithoutValidation("X-Test-Stamp", stamp ?? stamps.GetValueOrDefault(actor, "unknown-stamp"));
            request.Headers.TryAddWithoutValidation("X-Test-Session", session ?? "synthetic-session-" + actor);
        }
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

    Task<HttpResponseMessage> IssueAsync(string purpose, int count, int days, string? expectedActor = "admin",
        CsrfTokens? csrf = null, string? actor = "admin", string? stamp = null,
        string? session = null, string? applicationCookie = null, bool includeCsrfHeader = true,
        string? origin = Origin, string? querySuffix = null, string? rawBody = null,
        bool duplicateCsrfHeader = false, bool duplicateOrigin = false) =>
        SendAsync(HttpMethod.Post,
            "/api/core/invitations/issue?expectedActor=" + Uri.EscapeDataString(expectedActor ?? "") + (querySuffix ?? ""),
            rawBody is null ? JsonContent.Create(new { purpose, count, days }) : new StringContent(rawBody, Encoding.UTF8, "application/json"),
            csrf ?? adminSyntheticCsrf, actor, stamp, session, applicationCookie,
            includeCsrfHeader, origin: origin, duplicateCsrfHeader: duplicateCsrfHeader,
            duplicateOrigin: duplicateOrigin);

    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations", content: null,
        adminSyntheticCsrf, actor: "admin"))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "synthetic live admin can read the invitation list");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var old = json.RootElement.GetProperty("invitations").EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == "legacy-existing");
        Check(old.GetProperty("purpose").ValueKind == JsonValueKind.Null &&
              old.GetProperty("issuanceSource").ValueKind == JsonValueKind.Null,
            "HTTP list shows nullable metadata for a legacy row");
        CheckExactKeys(old, ["id", "ownerId", "ownerName", "issuerId", "issuerName", "requestId", "limit", "uses", "created", "expires", "disabled", "purpose", "issuanceSource", "revokedAt", "revokedBy", "revokerName", "revocationReason"]);
        var wire = json.RootElement.GetRawText();
        Check(!wire.Contains("TokenHash", StringComparison.OrdinalIgnoreCase) &&
              !wire.Contains("ProtectedToken", StringComparison.OrdinalIgnoreCase) &&
              !wire.Contains("old-baseline-secret", StringComparison.Ordinal),
            "inventory projection never serializes token secrets");
    }

    using (var response = await SendAsync(HttpMethod.Get, "/api/core/invitations/issue?expectedActor=admin",
        content: null, adminSyntheticCsrf, actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed && response.Content.Headers.Allow.Contains("POST"),
            "issue route is POST-only");
    using (var response = await IssueAsync("queryless", 1, 1, querySuffix: "&unexpected=1"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "issue rejects extra query parameters");
    using (var response = await IssueAsync("missing origin", 1, 1, origin: null))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "issue rejects a missing Origin");
    using (var response = await IssueAsync("foreign origin", 1, 1, origin: "https://foreign.invalid"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "issue rejects a foreign Origin");
    using (var response = await IssueAsync("duplicate origin", 1, 1, duplicateOrigin: true))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "issue requires exactly one Origin value");
    using (var response = await IssueAsync("missing csrf", 1, 1, includeCsrfHeader: false))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "issue requires CSRF header");
    using (var response = await IssueAsync("duplicate csrf", 1, 1, duplicateCsrfHeader: true))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "issue requires exactly one CSRF header value");
    using (var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/issue?expectedActor=admin",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = adminSyntheticCsrf.Token,
            ["purpose"] = "form", ["count"] = "1", ["days"] = "1"
        }), adminSyntheticCsrf, actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "form CSRF fallback is rejected");

    using (var response = await IssueAsync("wrong actor", 1, 1, expectedActor: "member",
        includeCsrfHeader: false, rawBody: "not-json"))
    {
        Check(response.StatusCode == HttpStatusCode.Forbidden, "expectedActor mismatch is denied before parsing");
        Check(response.Headers.TryGetValues("X-Test-Body-Read", out var reads) && reads.Single() == "0",
            "expectedActor mismatch leaves the request body unread");
    }
    using (var response = await IssueAsync("duplicate actor", 1, 1,
        querySuffix: "&expectedActor=admin", includeCsrfHeader: false, rawBody: "not-json"))
    {
        Check(response.StatusCode == HttpStatusCode.BadRequest, "duplicate expectedActor is rejected before parsing");
        Check(response.Headers.TryGetValues("X-Test-Body-Read", out var reads) && reads.Single() == "0",
            "duplicate expectedActor leaves the request body unread");
    }
    using (var response = await IssueAsync("role denied", 1, 1, expectedActor: "member",
        csrf: memberCsrf, actor: "member", includeCsrfHeader: false, rawBody: "not-json"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "non-admin user cannot issue invitations");
    using (var response = await IssueAsync("stale stamp", 1, 1, stamp: "stale-admin-stamp",
        csrf: staleAdminCsrf, includeCsrfHeader: false, rawBody: "not-json"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "stale security stamp cannot issue");
    using (var response = await IssueAsync("revoked session", 1, 1, session: "revoked-admin-session",
        includeCsrfHeader: false, rawBody: "not-json"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "revoked session cannot issue");
    using (var response = await IssueAsync("authenticated cookie", 1, 1, csrf: adminCookieCsrf,
        actor: null, applicationCookie: adminCookie))
        Check(response.StatusCode == HttpStatusCode.OK, "real Identity.Application cookie is accepted for live admin");

    const string raceRequestId = "legacy-new-decision-race";
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.InvitationRequests.Add(new InvitationRequest
        {
            Id = raceRequestId, UserId = "member", Reason = "synthetic race fixture",
            Count = 2, Days = 3, Status = "Pending", Created = SemEndpoints.Now
        });
        await db.SaveChangesAsync();
    }
    var legacyDecision = SendAsync(HttpMethod.Post, "/api/decide-invitation",
        JsonContent.Create(new { id = raceRequestId, approve = true, comment = "legacy path" }),
        adminSyntheticCsrf, actor: "admin");
    var currentDecision = SendAsync(HttpMethod.Post,
        "/api/core/invitations/decide?expectedActor=admin",
        JsonContent.Create(new { id = raceRequestId, approve = true, comment = "current path" }),
        adminSyntheticCsrf, actor: "admin");
    using (var legacyResponse = await legacyDecision)
    using (var currentResponse = await currentDecision)
    {
        var successCount = new[] { legacyResponse, currentResponse }
            .Count(x => x.StatusCode == HttpStatusCode.OK);
        Check(successCount == 1,
            "simultaneous legacy and current decisions have exactly one successful claimant");
        Check(new[] { legacyResponse, currentResponse }.Single(x => x.StatusCode != HttpStatusCode.OK)
                .StatusCode == HttpStatusCode.Conflict,
            "simultaneous legacy and current decisions reject the losing claimant with conflict");
    }
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var raced = await db.InvitationRequests.SingleAsync(x => x.Id == raceRequestId);
        var issued = await db.Invitations.Where(x => x.RequestId == raceRequestId).ToListAsync();
        Check(raced.Status == "Approved" && raced.DecidedBy == "admin" &&
              raced.DecisionComment is "legacy path" or "current path" && issued.Count == 1,
            "legacy/current decision race commits one approved request and one invitation");
        Check(await db.Audits.CountAsync(x => x.Action == "InvitationApproved" && x.EntityId == raceRequestId) == 1 &&
              await db.Audits.CountAsync(x => x.Action == "InvitationIssued" && x.EntityId == issued[0].Id) == 1 &&
              await db.Messages.CountAsync(x => x.UserId == "member" &&
                  x.Title == "招待リクエストが許可されました") == 1 &&
              await db.Deliveries.CountAsync(x => x.UserId == "member" && x.Kind == "Push" &&
                  x.Subject == "招待リクエストが許可されました") == 1,
            "the winning race decision produces one audit, message, and Push delivery");
    }

    const string rollbackRequestId = "push-atomicity-rollback";
    const string rollbackInvitationId = "push-atomicity-existing";
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Check(scope.ServiceProvider.GetRequiredService<NotificationService>().PushConfigured,
            "atomicity fixture has Push queueing enabled");
        db.InvitationRequests.Add(new InvitationRequest
        {
            Id = rollbackRequestId, UserId = "member", Reason = "synthetic rollback fixture",
            Count = 1, Days = 2, Status = "Pending", Created = SemEndpoints.Now
        });
        db.Invitations.Add(new Invitation
        {
            Id = rollbackInvitationId,
            TokenHash = SemEndpoints.Hash("rollback-existing-token"),
            ProtectedToken = tokenProtector.Protect("rollback-existing-token"),
            OwnerId = "member",
            IssuerId = "admin",
            RequestId = rollbackRequestId,
            Limit = 1,
            Uses = 0,
            Created = SemEndpoints.Now,
            Expires = SemEndpoints.Now + 2 * 86400L
        });
        await db.SaveChangesAsync();
    }
    long beforeMessages;
    long beforeDeliveries;
    long beforeAudits;
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        beforeMessages = await db.Messages.LongCountAsync(x => x.UserId == "member" &&
            x.Title == "招待リクエストが許可されました");
        beforeDeliveries = await db.Deliveries.LongCountAsync(x => x.UserId == "member" &&
            x.Kind == "Push" && x.Subject == "招待リクエストが許可されました");
        beforeAudits = await db.Audits.LongCountAsync();
    }
    using (var response = await SendAsync(HttpMethod.Post,
        "/api/core/invitations/decide?expectedActor=admin",
        JsonContent.Create(new { id = rollbackRequestId, approve = true, comment = "must roll back" }),
        adminSyntheticCsrf, actor: "admin"))
        Check(response.StatusCode == HttpStatusCode.Conflict,
            "duplicate RequestId blocks approval after Push decision notification is queued");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = await db.InvitationRequests.SingleAsync(x => x.Id == rollbackRequestId);
        Check(request.Status == "Pending" && request.DecidedAt is null &&
              request.DecidedBy is null && request.DecisionComment is null,
            "failed Push-enabled decision rolls back the conditional claim and comment");
        Check(await db.Invitations.CountAsync(x => x.RequestId == rollbackRequestId) == 1 &&
              await db.Invitations.AnyAsync(x => x.Id == rollbackInvitationId),
            "failed Push-enabled decision retains only the pre-existing invitation");
        Check(await db.Messages.LongCountAsync(x => x.UserId == "member" &&
                  x.Title == "招待リクエストが許可されました") == beforeMessages &&
              await db.Deliveries.LongCountAsync(x => x.UserId == "member" &&
                  x.Kind == "Push" && x.Subject == "招待リクエストが許可されました") == beforeDeliveries &&
              await db.Audits.LongCountAsync() == beforeAudits &&
              !await db.Audits.AnyAsync(x => x.Action == "InvitationApproved" && x.EntityId == rollbackRequestId),
            "failed Push-enabled decision atomically rolls back invitation, message, delivery, and audits");
    }


    var invalidRequests = new[]
    {
        JsonSerializer.Serialize(new { purpose = "", count = 1, days = 1 }),
        JsonSerializer.Serialize(new { purpose = "   ", count = 1, days = 1 }),
        JsonSerializer.Serialize(new { purpose = new string('p', 501), count = 1, days = 1 }),
        JsonSerializer.Serialize(new { purpose = "bad\u0085purpose", count = 1, days = 1 }),
        JsonSerializer.Serialize(new { purpose = "valid", count = 0, days = 1 }),
        JsonSerializer.Serialize(new { purpose = "valid", count = 6, days = 1 }),
        JsonSerializer.Serialize(new { purpose = "valid", count = 1, days = 0 }),
        JsonSerializer.Serialize(new { purpose = "valid", count = 1, days = 8 }),
        "{",
        """{"purpose":"x","purpose":"y","count":1,"days":1}""",
        """{"purpose":"x","count":1,"days":1,"extra":1}""",
        """{"purpose":"x","count":"1","days":1}""",
        """{"purpose":"x","count":1.5,"days":1}""",
        """{"purpose":"x","count":1}"""
    };
    foreach (var body in invalidRequests)
    {
        using var response = await IssueAsync("ignored", 1, 1, rawBody: body);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "issue rejects invalid purpose/count/days or non-exact JSON");
    }
    using (var content = new ByteArrayContent([0xff, 0xfe, 0xfd]))
    {
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await SendAsync(HttpMethod.Post, "/api/core/invitations/issue?expectedActor=admin",
            content, adminSyntheticCsrf, actor: "admin");
        Check(response.StatusCode == HttpStatusCode.BadRequest, "issue rejects invalid UTF-8 strictly");
    }
    using (var response = await IssueAsync("oversized", 1, 1, includeCsrfHeader: false,
        rawBody: new string('x', 4097)))
        Check((int)response.StatusCode == 413, "Content-Length over 4 KiB is rejected before antiforgery");
    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/core/invitations/issue?expectedActor=admin"))
    {
        request.Content = new StreamContent(new NonSeekableReadStream(Encoding.UTF8.GetBytes(new string('x', 4097))));
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = true;
        request.Headers.TryAddWithoutValidation("Cookie", adminSyntheticCsrf.Cookie);
        request.Headers.TryAddWithoutValidation("X-Test-Actor", "admin");
        request.Headers.TryAddWithoutValidation("X-Test-Stamp", stamps["admin"]);
        request.Headers.TryAddWithoutValidation("X-Test-Session", "synthetic-session-admin");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", adminSyntheticCsrf.Token);
        using var response = await client.SendAsync(request);
        var status = (int)response.StatusCode;
        var receivedLength = response.Headers.TryGetValues("X-Test-Content-Length", out var lengths)
            ? lengths.Single()
            : "missing";
        Check(status == 413 && receivedLength == "null",
            $"chunked issue body bounded; status={status}; received Content-Length={receivedLength}");
    }

    long initialUsers;
    long initialInvitations;
    long initialAudits;
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        initialUsers = await db.Users.LongCountAsync();
        initialInvitations = await db.Invitations.LongCountAsync();
        initialAudits = await db.Audits.LongCountAsync();
    }
    using (var response = await IssueAsync("  Workforce onboarding  ", 3, 5))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "admin issuance succeeds");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        CheckExactKeys(json.RootElement, ["actorId", "id", "purpose", "issuanceSource", "limit", "uses", "created", "expires", "link"]);
        Check(json.RootElement.GetProperty("actorId").GetString() == "admin" &&
              json.RootElement.GetProperty("purpose").GetString() == "Workforce onboarding" &&
              json.RootElement.GetProperty("issuanceSource").GetString() == "Direct" &&
              json.RootElement.GetProperty("limit").GetInt32() == 3 &&
              json.RootElement.GetProperty("uses").GetInt32() == 0,
            "issue response is exact, trimmed, direct, and initially unused");
        var created = json.RootElement.GetProperty("created").GetInt64();
        var expires = json.RootElement.GetProperty("expires").GetInt64();
        Check(expires == created + 5 * 86400L, "issue expiry is exactly the requested number of days");
        var link = new Uri(json.RootElement.GetProperty("link").GetString()!);
        Check(link.GetLeftPart(UriPartial.Authority) == Origin && link.AbsolutePath == "/join" &&
              link.Query.Length == 0 && link.Fragment.StartsWith("#invite=", StringComparison.Ordinal),
            "new invitation uses the fixed public /join#invite link without query");
        var token = Uri.UnescapeDataString(link.Fragment["#invite=".Length..]);
        Check(token.Length == 64 && token.All(Uri.IsHexDigit), "issued link token has the expected synthetic secret form");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var invitation = await db.Invitations.SingleAsync(x => x.Id == json.RootElement.GetProperty("id").GetString());
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("SEM.Tokens.v1");
        Check(invitation.TokenHash == SemEndpoints.Hash(token) && protector.Unprotect(invitation.ProtectedToken) == token &&
              !invitation.ProtectedToken.Contains(token, StringComparison.Ordinal),
            "token hash and protected token both bind the issued link secret");
        Check(invitation.Purpose == "Workforce onboarding" && invitation.IssuanceSource == "Direct" &&
              invitation.OwnerId == "admin" && invitation.IssuerId == "admin" && invitation.RequestId is null &&
              invitation.Limit == 3 && invitation.Uses == 0 && invitation.Created == created && invitation.Expires == expires,
            "direct issue row persists metadata and leaves uses at zero");
        var audit = await db.Audits.SingleAsync(x => x.Action == "InvitationIssued" && x.EntityId == invitation.Id);
        Check(audit.ActorId == "admin" && audit.At == created && audit.EntityId == invitation.Id,
            "issuance audit is atomic and contains actor/id/time only");
        Check(await db.Users.LongCountAsync() == initialUsers &&
              await db.Invitations.LongCountAsync() == initialInvitations + 1 &&
              await db.Audits.LongCountAsync() == initialAudits + 1,
            "issuance creates one invitation and audit without users or other writes");
        Check(audit.EntityId != token && !audit.EntityId.Contains(token, StringComparison.Ordinal),
            "audit never stores the link token");
    }

    var parallelBodies = new[]
    {
        JsonContent.Create(new { purpose = "parallel-one", count = 1, days = 1 }),
        JsonContent.Create(new { purpose = "parallel-two", count = 2, days = 7 })
    };
    using (var requestOne = new HttpRequestMessage(HttpMethod.Post, "/api/core/invitations/issue?expectedActor=admin") { Content = parallelBodies[0] })
    using (var requestTwo = new HttpRequestMessage(HttpMethod.Post, "/api/core/invitations/issue?expectedActor=admin") { Content = parallelBodies[1] })
    {
        foreach (var request in new[] { requestOne, requestTwo })
        {
            request.Headers.TryAddWithoutValidation("Cookie", adminSyntheticCsrf.Cookie);
            request.Headers.TryAddWithoutValidation("X-Test-Actor", "admin");
            request.Headers.TryAddWithoutValidation("X-Test-Stamp", stamps["admin"]);
            request.Headers.TryAddWithoutValidation("X-Test-Session", "synthetic-session-admin");
            request.Headers.TryAddWithoutValidation("Origin", Origin);
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", adminSyntheticCsrf.Token);
        }
        var responses = await Task.WhenAll(client.SendAsync(requestOne), client.SendAsync(requestTwo));
        using var responseOne = responses[0];
        using var responseTwo = responses[1];
        Check(responseOne.StatusCode == HttpStatusCode.OK && responseTwo.StatusCode == HttpStatusCode.OK,
            "parallel distinct issuance requests both commit");
        using var jsonOne = JsonDocument.Parse(await responseOne.Content.ReadAsStringAsync());
        using var jsonTwo = JsonDocument.Parse(await responseTwo.Content.ReadAsStringAsync());
        var idOne = jsonOne.RootElement.GetProperty("id").GetString()!;
        var idTwo = jsonTwo.RootElement.GetProperty("id").GetString()!;
        var tokenOne = Uri.UnescapeDataString(new Uri(jsonOne.RootElement.GetProperty("link").GetString()!).Fragment["#invite=".Length..]);
        var tokenTwo = Uri.UnescapeDataString(new Uri(jsonTwo.RootElement.GetProperty("link").GetString()!).Fragment["#invite=".Length..]);
        Check(idOne != idTwo && tokenOne != tokenTwo, "parallel issuance produces distinct invitation IDs and tokens");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Check(await db.Invitations.CountAsync(x => x.Id == idOne || x.Id == idTwo) == 2 &&
              await db.Audits.CountAsync(x => x.Action == "InvitationIssued" && (x.EntityId == idOne || x.EntityId == idTwo)) == 2,
            "parallel issuance persists one audit per committed invitation");
    }

    using (var response = await SendAsync(HttpMethod.Post, "/api/issue-invitation",
        JsonContent.Create(new { count = 2, days = 3 }), adminCookieCsrf,
        actor: null, applicationCookie: adminCookie))
        Check(response.StatusCode == HttpStatusCode.OK, "legacy issue-invitation API remains available");
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var legacy = await db.Invitations.OrderByDescending(x => x.Created).ThenByDescending(x => x.Id)
            .FirstAsync(x => x.IssuanceSource == null && x.Id != "legacy-existing");
        Check(legacy.Purpose is null && legacy.IssuanceSource is null && legacy.Uses == 0,
            "legacy issuance retains nullable metadata and unchanged initial use count");
        var listResponse = await SendAsync(HttpMethod.Get, "/api/core/invitations", content: null,
            adminSyntheticCsrf, actor: "admin");
        using (listResponse)
        {
            using var json = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
            var metadata = json.RootElement.GetProperty("invitations").EnumerateArray()
                .Single(x => x.GetProperty("id").GetString() == legacy.Id);
            Check(metadata.GetProperty("purpose").ValueKind == JsonValueKind.Null &&
                  metadata.GetProperty("issuanceSource").ValueKind == JsonValueKind.Null,
                "legacy issue remains null/null in management projection");
        }
    }

    Console.WriteLine("Invitation issuance HTTP checks passed: " + checks.ToString(CultureInfo.InvariantCulture));
}
finally
{
    if (started) await app.StopAsync();
    await app.DisposeAsync();
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static ApplicationUser User(string id, string email, string displayName, string stamp,
    bool isAdmin = false) => new()
{
    Id = id,
    UserName = email,
    NormalizedUserName = email.ToUpperInvariant(),
    Email = email,
    NormalizedEmail = email.ToUpperInvariant(),
    EmailConfirmed = true,
    Enabled = true,
    IsAdmin = isAdmin,
    DisplayName = displayName,
    SecurityStamp = stamp
};

static void CheckExactKeys(JsonElement element, string[] expected)
{
    var actual = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    if (!actual.SetEquals(expected))
        throw new Exception("Unexpected JSON fields: " + string.Join(",", actual.OrderBy(x => x, StringComparer.Ordinal)));
}

static bool HasSqliteException(Exception error)
{
    for (Exception? current = error; current is not null; current = current.InnerException)
        if (current is SqliteException) return true;
    return false;
}

static string ExceptionMessages(Exception? error)
{
    var messages = new List<string>();
    for (var current = error; current is not null; current = current.InnerException)
        messages.Add(current.Message);
    return string.Join(" | ", messages);
}

static async Task<string> GetApplicationCookieAsync(HttpClient client, string userId)
{
    using var response = await client.GetAsync("/fixture/application-cookie/" + Uri.EscapeDataString(userId));
    response.EnsureSuccessStatusCode();
    return response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith("test-application=", StringComparison.Ordinal))
        .Split(';', 2)[0];
}

static async Task<CsrfTokens> GetCsrfAsync(HttpClient client, string? actor = null,
    string? stamp = null, string? session = null, string? appCookie = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/csrf");
    if (appCookie is not null) request.Headers.TryAddWithoutValidation("Cookie", appCookie);
    else if (actor is not null)
    {
        request.Headers.TryAddWithoutValidation("X-Test-Actor", actor);
        request.Headers.TryAddWithoutValidation("X-Test-Stamp", stamp ?? actor switch
        {
            "admin" => "stamp-admin-current",
            "member" => "stamp-member-current",
            _ => "unknown-stamp"
        });
        request.Headers.TryAddWithoutValidation("X-Test-Session", session ?? "synthetic-session-" + actor);
    }
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var token = json.RootElement.GetProperty("token").GetString()
        ?? throw new Exception("Antiforgery request token missing.");
    var cookie = response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith("test-csrf=", StringComparison.Ordinal))
        .Split(';', 2)[0];
    return new CsrfTokens(token, cookie);
}

sealed record CsrfTokens(string Token, string Cookie);

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
        var types = Context.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(types.UserIdClaimType, actor),
            new Claim(types.SecurityStampClaimType, stamp),
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
