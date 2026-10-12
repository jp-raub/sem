using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-proof-check-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var count = 0;
try
{
    await Run("Fresh email proof is consumed once and grants no more than five minutes", async f =>
    {
        await f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token");
        var row = await f.Db.PasskeyAdditions.SingleAsync();
        Require(row.EmailTokenHash is null && row.EmailVerifiedAt == f.Clock.Now &&
            row.RegistrationUntil == f.Clock.Now + 300);
        await Rejected(() => f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token"));
    });
    await Run("Registration permission is capped by the original operation deadline", async f =>
    {
        f.Flow.Expires = f.Clock.Now + 100; await f.Db.SaveChangesAsync();
        await f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token");
        Require(f.Flow.RegistrationUntil == f.Flow.Expires);
    });
    await Run("Wrong token/purpose does not consume the genuine proof", async f =>
    {
        await Rejected(() => f.Service.ConfirmEmailAsync(f.Context, f.User, "initial-enrollment-token"));
        Require(f.Flow.EmailTokenHash == SemEndpoints.Hash("synthetic-token"));
    });
    await Run("A different authentication session cannot use the mail link", async f =>
    {
        var other = Fixture.ContextFor(f.User, "other-session");
        await Rejected(() => f.Service.ConfirmEmailAsync(other, f.User, "synthetic-token"));
        Require(f.Flow.EmailTokenHash != null);
    });
    await Run("A different user cannot use the mail link", async f =>
    {
        var other = new ApplicationUser { Id = "other-user", Email = "other@example.test",
            UserName = "other@example.test", EmailConfirmed = true, Enabled = true, SecurityStamp = "other-stamp" };
        f.Db.Users.Add(other); await f.Db.SaveChangesAsync();
        await Rejected(() => f.Service.ConfirmEmailAsync(Fixture.ContextFor(other, "session"), other, "synthetic-token"));
    });
    await Run("Changed saved email invalidates the operation", async f =>
    {
        await f.Db.Users.Where(u => u.Id == f.User.Id).ExecuteUpdateAsync(s =>
            s.SetProperty(u => u.Email, "changed@example.test"));
        await Rejected(() => f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token"));
    });
    await Run("Changed security stamp invalidates the operation", async f =>
    {
        await f.Db.Users.Where(u => u.Id == f.User.Id).ExecuteUpdateAsync(s =>
            s.SetProperty(u => u.SecurityStamp, "new-stamp"));
        await Rejected(() => f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token"));
    });
    await Run("Disabled user cannot consume proof", async f =>
    {
        await f.Db.Users.Where(u => u.Id == f.User.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Enabled, false));
        await Rejected(() => f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token"));
    });
    await Run("Past EmailConfirmed flag alone never authorizes creation", async f =>
    {
        await Rejected(() => f.Service.CreationOptionsAsync(f.Context, f.User));
    });
    await Run("Email proof without operation-specific reauthentication is rejected", async f =>
    {
        f.Flow.ReauthenticatedAt = null; await f.Db.SaveChangesAsync();
        await Rejected(() => f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token"));
    });
    await Run("Operation expiry is exclusive at exactly fifteen minutes", async f =>
    {
        f.Clock.Advance(900);
        await Rejected(() => f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token"));
        await Rejected(() => f.Service.ResendAsync(f.Context, f.User, f.Flow.Id));
    });
    await Run("Five-minute registration expiry requires reauthentication, not resend", async f =>
    {
        await f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token");
        f.Clock.Advance(300);
        await Rejected(() => f.Service.CreationOptionsAsync(f.Context, f.User));
        await Rejected(() => f.Service.CompleteAsync(f.Context, f.User, "{}"));
        await Rejected(() => f.Service.ResendAsync(f.Context, f.User, f.Flow.Id));
        Require(await f.Service.StatusAsync(f.Context, f.User) is null);
    });
    await Run("Resend invalidates old proof without extending the overall deadline", async f =>
    {
        var expires = f.Flow.Expires;
        f.Clock.Advance(120);
        await f.Service.ResendAsync(f.Context, f.User, f.Flow.Id);
        Require(f.Flow.Expires == expires && f.Flow.EmailTokenHash != SemEndpoints.Hash("synthetic-token") &&
            f.Flow.EmailVerifiedAt is null && f.Flow.CreationChallengeHash is null);
        await Rejected(() => f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token"));
    });
    await Run("Cancellation invalidates email and creation state", async f =>
    {
        await f.Service.CancelAsync(f.Context, f.User, f.Flow.Id);
        Require(f.Flow.Cancelled && f.Flow.EmailTokenHash is null && f.Flow.CreationChallengeHash is null);
        await Rejected(() => f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token"));
    });
    await Run("Completed operations cannot be reused", async f =>
    {
        f.Flow.CompletedAt = f.Clock.Now; await f.Db.SaveChangesAsync();
        await Rejected(() => f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token"));
    });
    await Run("Legacy sessions without a session claim must log in again before addition", async f =>
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, f.User.Id)], "Synthetic"));
        await Rejected(() => f.Service.ConfirmEmailAsync(context, f.User, "synthetic-token"));
    });
    await Run("Proof and audit records never store the plaintext email token", async f =>
    {
        await f.Service.ConfirmEmailAsync(f.Context, f.User, "synthetic-token");
        Require(!(await f.Db.Audits.ToListAsync()).Any(a => a.EntityId.Contains("synthetic-token") ||
            a.Action.Contains("synthetic-token")));
    });
    Console.WriteLine($"Passkey proof checks passed: {count}");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, true);
}

async Task Run(string name, Func<Fixture, Task> check)
{
    using var fixture = await Fixture.CreateAsync(Path.Combine(root, Guid.NewGuid() + ".db"));
    await check(fixture);
    count++;
    Console.WriteLine("PASS " + name);
}
static void Require(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
static async Task Rejected(Func<Task> action)
{
    try { await action(); }
    catch (Exception error) when (error is ValidationException or UnauthorizedAccessException) { return; }
    throw new InvalidOperationException("Expected a safe rejection.");
}

sealed class TestClock : TimeProvider
{
    public long Now { get; private set; } = 1800000000;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Now);
    public void Advance(long seconds) => Now += seconds;
}
sealed class Fixture : IDisposable
{
    private readonly ServiceProvider provider;
    public ApplicationDbContext Db { get; }
    public ApplicationUser User { get; private set; } = null!;
    public PasskeyAddition Flow { get; private set; } = null!;
    public TestClock Clock { get; } = new();
    public HttpContext Context { get; private set; } = null!;
    public PasskeyAdditionService Service { get; }
    private Fixture(string path)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + path));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<ApplicationDbContext>();
        provider = services.BuildServiceProvider();
        Db = provider.GetRequiredService<ApplicationDbContext>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Smtp:Host"] = "synthetic.test", ["Smtp:From"] = "test@example.test" }).Build();
        Service = new(Db, null!, null!, new NotificationService(config, new EphemeralDataProtectionProvider()),
            new DeploymentSettings { PublicUrl = "https://localhost:18443/" }, Clock);
    }
    public static async Task<Fixture> CreateAsync(string path)
    {
        var f = new Fixture(path);
        await SqliteStorageInitializer.InitializeAsync(f.Db);
        f.User = new ApplicationUser { Id = "fixture-user", Email = "fixture@example.test",
            UserName = "fixture@example.test", Enabled = true, EmailConfirmed = true, SecurityStamp = "fixture-stamp" };
        f.Flow = new PasskeyAddition { Id = "fixture-operation", UserId = f.User.Id,
            SessionHash = SemEndpoints.Hash("session"), Email = f.User.Email, SecurityStamp = f.User.SecurityStamp,
            Created = f.Clock.Now, ReauthenticatedAt = f.Clock.Now, Expires = f.Clock.Now + 900,
            EmailTokenHash = SemEndpoints.Hash("synthetic-token") };
        f.Db.Users.Add(f.User); f.Db.PasskeyAdditions.Add(f.Flow); await f.Db.SaveChangesAsync();
        f.Context = ContextFor(f.User, "session");
        return f;
    }
    public static HttpContext ContextFor(ApplicationUser user, string session) =>
        new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(PasskeyAdditionService.SessionClaim, session)],
            "Synthetic")) };
    public void Dispose() => provider.Dispose();
}