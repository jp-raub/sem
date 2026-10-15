using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Core;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-profile-history-http-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var database = Path.Combine(root, "test.db");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = typeof(CoreEndpoints).Assembly.GetName().Name });
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite("Data Source=" + database), ServiceLifetime.Scoped);
builder.Services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<BusinessNotificationStore>();
builder.Services.AddScoped<CoreFoundationService>();
builder.Services.AddSingleton(new DeploymentSettings { PublicUrl = "https://profile-history.test/", DataDirectory = root });
builder.Services.AddSingleton(new ManualPushRuntime(false));
builder.Services.AddSingleton<IEventPushTransport, NoopPushTransport>();
builder.Services.AddAntiforgery();
builder.Services.AddAuthentication("SyntheticCookie").AddScheme<AuthenticationSchemeOptions, SyntheticCookieHandler>("SyntheticCookie", _ => { });
var app = builder.Build();
app.UseAuthentication();
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
        db.Users.AddRange(
            new ApplicationUser { Id = "viewer", UserName = "viewer@test.invalid", Email = "viewer-private@test.invalid", DisplayName = "Viewer", Enabled = true, EmailConfirmed = true, SecurityStamp = "viewer-stamp" },
            new ApplicationUser { Id = "target", UserName = "target@test.invalid", Email = "target-private@test.invalid", DisplayName = "Target", Enabled = true, EmailConfirmed = true, SecurityStamp = "target-stamp" });
        db.SessionEvents.AddRange(
            new SessionEvent { Id = "visible", Title = "Visible <history>", ScheduledDate = "2026-10-14T12:00:00.000Z", CreatedAt = "2026-10-01T00:00:00.000Z", PhaseKind = "View", ManualPhaseOverride = true },
            new SessionEvent { Id = "hidden", Title = "Hidden candidate", ScheduledDate = "2026-10-15T12:00:00.000Z", CreatedAt = "2026-10-01T00:00:00.000Z", PhaseKind = "Hidden", ManualPhaseOverride = true });
        db.Phases.AddRange(
            new EventPhase { Id = "visible-view", EventId = "visible", Kind = "View", Name = "Public viewing", SortOrder = 0, IsActive = true },
            new EventPhase { Id = "hidden-phase", EventId = "hidden", Kind = "Hidden", Name = "Private", SortOrder = 0, IsActive = true });
        db.EventAdmins.Add(new EventAdmin { EventId = "hidden", UserId = "target", GrantedAt = "2026-10-01T00:00:00.000Z" });
        db.EventSpectators.AddRange(
            new EventSpectator { EventId = "visible", UserId = "target", RegisteredAt = "2026-10-01T00:00:00.000Z", RegisteredBy = "target" },
            new EventSpectator { EventId = "hidden", UserId = "target", RegisteredAt = "2026-10-01T00:00:00.000Z", RegisteredBy = "target" });
        db.EventPartDefinitions.Add(new EventPartDefinition { Id = "vocals", EventId = "visible", Name = "Vocals", Category = "Sing", NormalizedName = "VOCALS", SortOrder = 0 });
        db.Songs.Add(new Song { Id = "song-1", EventId = "visible", Title = "Current song", SortOrder = 0 });
        db.SongParts.Add(new SongPart { Id = "part-1", EventId = "visible", SongId = "song-1", PartDefinitionId = "vocals", AssignedUserId = "target", PhaseId = "visible-view" });
        db.EventQuestions.Add(new EventQuestion { Id = "private-question", EventId = "visible", Text = "Private", Type = "Text" });
        db.EventQuestionAnswers.Add(new EventQuestionAnswer { QuestionId = "private-question", UserId = "target", Value = "private-answer", UpdatedAt = "2026-10-01T00:00:00.000Z" });
        await db.SaveChangesAsync();
    }

    await app.StartAsync(); started = true;
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(address) };
    async Task<HttpResponseMessage> Get(string query, string? cookie = "test-auth=viewer")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/core/profile-registration-history" + (query.Length == 0 ? "" : "?" + query));
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return await client.SendAsync(request);
    }
    async Task CheckNoStore(HttpResponseMessage response, string label)
    {
        Check(response.Headers.CacheControl?.NoStore == true, label);
        await Task.CompletedTask;
    }

    await using var observer = new SqliteConnection("Data Source=" + database);
    await observer.OpenAsync();
    async Task<long> DataVersion()
    {
        await using var command = observer.CreateCommand(); command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    var before = await DataVersion();
    using (var response = await Get("id=target"))
    {
        Check(response.StatusCode == HttpStatusCode.OK, "current authenticated actor reads target history");
        await CheckNoStore(response, "authorized history response is no-store");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rootJson = json.RootElement;
        Check(rootJson.GetProperty("userId").GetString() == "target", "response binds the exact requested target id");
        var events = rootJson.GetProperty("events");
        Check(events.GetArrayLength() == 1 && events[0].GetProperty("eventId").GetString() == "visible", "viewer sees authorized current event but not target-managed hidden candidate");
        Check(events[0].GetProperty("title").GetString() == "Visible <history>" && events[0].GetProperty("spectator").GetBoolean(), "response contains event title and current spectator status");
        var parts = events[0].GetProperty("parts");
        Check(parts.GetArrayLength() == 1 && parts[0].GetProperty("songTitle").GetString() == "Current song" && parts[0].GetProperty("partName").GetString() == "Vocals", "response contains only current song and part display fields");
        Check(rootJson.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(["events", "userId"])
            && events[0].EnumerateObject().Select(x => x.Name).Order().SequenceEqual(["eventId", "parts", "scheduledDate", "spectator", "title"])
            && parts[0].EnumerateObject().Select(x => x.Name).Order().SequenceEqual(["category", "partDefinitionId", "partName", "songId", "songTitle"]), "wire DTO remains minimal with no count, cursor, or attendance fields");
        var body = rootJson.GetRawText();
        Check(!body.Contains("private@test.invalid", StringComparison.Ordinal) && !body.Contains("private-answer", StringComparison.Ordinal) && !body.Contains("viewer-session", StringComparison.Ordinal), "history response excludes emails, answers, and session secrets");
    }
    using (var profileRequest = new HttpRequestMessage(HttpMethod.Get, "/api/core/profile?id=target"))
    {
        profileRequest.Headers.TryAddWithoutValidation("Cookie", "test-auth=viewer");
        using var profile = await client.SendAsync(profileRequest);
        Check(profile.StatusCode == HttpStatusCode.OK && profile.Headers.CacheControl?.NoStore == true, "basic profile remains readable through the current cookie principal");
    }

    foreach (var (query, label) in new[]
    {
        ("", "id is required"), ("id=", "id must be nonempty"), ("id=target&id=target", "id must occur exactly once"),
        ("id=" + Uri.EscapeDataString(new string('x', 129)), "id is limited to 128 characters"), ("id=bad%0Avalue", "control characters are rejected")
    })
    {
        using var invalid = await Get(query);
        Check(invalid.StatusCode == HttpStatusCode.BadRequest, label);
        await CheckNoStore(invalid, label + " response is no-store");
    }
    using (var anonymous = await Get("id=target", null))
    {
        Check(anonymous.StatusCode == HttpStatusCode.Forbidden, "anonymous cookie cannot read history");
        await CheckNoStore(anonymous, "anonymous denial is no-store");
    }
    using (var stale = await Get("id=target", "test-auth=viewer-stale"))
        Check(stale.StatusCode == HttpStatusCode.Forbidden, "stale security stamp is rejected");
    using (var post = new HttpRequestMessage(HttpMethod.Post, "/api/core/profile-registration-history?id=target"))
    {
        post.Content = new StringContent("{");
        using var response = await client.SendAsync(post);
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed, "POST returns 405 before attempting to parse its body");
        await CheckNoStore(response, "POST 405 response is no-store");
    }
    await using (var revoke = app.Services.CreateAsyncScope())
    {
        var factory = revoke.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.RevokedAuthSessions.Add(new RevokedAuthSession { UserId = "viewer", TokenHash = SemEndpoints.Hash("viewer-session"), RevokedAt = "2026-10-15T00:00:00.000Z" });
        await db.SaveChangesAsync();
    }
    before = await DataVersion();
    using (var revoked = await Get("id=target"))
    {
        Check(revoked.StatusCode == HttpStatusCode.Forbidden, "revoked current cookie session cannot read history");
        await CheckNoStore(revoked, "revoked-session denial is no-store");
    }
    var after = await DataVersion();
    Check(before == after, "history and profile reads do not mutate application data");
    Console.WriteLine($"Profile registration history HTTP checks passed: {checks}");
}
finally
{
    if (started) await app.StopAsync();
    await app.DisposeAsync();
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, true);
}

sealed class SyntheticCookieHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var value = Request.Cookies["test-auth"];
        if (string.IsNullOrEmpty(value)) return Task.FromResult(AuthenticateResult.NoResult());
        var stale = value.EndsWith("-stale", StringComparison.Ordinal);
        var id = stale ? value[..^6] : value;
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, id),
            new Claim("AspNet.Identity.SecurityStamp", stale ? "invalid-stamp" : id + "-stamp"),
            new Claim(PasskeyAdditionService.SessionClaim, id + "-session")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

sealed class NoopPushTransport : IEventPushTransport
{
    public bool Configured => false;
    public Task Send(PushDevice device, EventPushPayload payload, int ttl, CancellationToken ct) => Task.CompletedTask;
}
