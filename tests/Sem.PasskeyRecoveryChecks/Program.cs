using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-passkey-recovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var evidence = Path.Combine(Directory.GetCurrentDirectory(), "verification");
Directory.CreateDirectory(evidence);
var backup = Path.Combine(evidence, "passkey-recovery-before-tests-" +
    DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ") + ".db");
var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}
async Task Deny(Func<Task> run, string name)
{
    try { await run(); throw new Exception("FAIL accepted " + name); }
    catch (Exception e) when (e is ValidationException or UnauthorizedAccessException or PasskeyRecoveryRateLimitException)
    { Check(true, name); }
}

try
{
    await Run(async f =>
    {
        Check(!f.Db.Database.HasPendingModelChanges(), "runtime model matches the reconstructed migration snapshot");
        f.Db.EmailTickets.Add(new EmailTicket
        {
            UserId = "target", Hash = SemEndpoints.Hash("synthetic-old-enrollment-ticket"),
            Expires = f.Clock.Now + 1200, Used = false
        });
        await f.Db.SaveChangesAsync();
        await f.Db.Database.OpenConnectionAsync();
        try
        {
            await using var command = f.Db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
            INSERT INTO "AspNetUserPasskeys" ("CredentialId","UserId","Data")
            VALUES (X'01020304','target','{"AttestationObject":"01","ClientDataJson":"01","CreatedAt":"2024-01-01T00:00:00+00:00","IsBackedUp":false,"IsBackupEligible":false,"IsUserVerified":true,"Name":"synthetic-old","PublicKey":"01","SignCount":0,"Transports":[]}')
            """;
            await command.ExecuteNonQueryAsync();
        }
        finally { await f.Db.Database.CloseConnectionAsync(); }
        var options = await f.Service.CreateAdminProofOptionsAsync(f.AdminContext, f.Admin,
            "target", "端末紛失の申告を確認");
        var challenge = Challenge(options);
        await Deny(() => f.Service.StartAsync(f.AdminContext, f.Admin, "target", "理由が異なる",
            Credential(challenge), true), "approval challenge is bound to exact target and reason");

        options = await f.Service.CreateAdminProofOptionsAsync(f.AdminContext, f.Admin,
            "target", "端末紛失の申告を確認");
        challenge = Challenge(options);
        var response = await f.Service.StartAsync(f.AdminContext, f.Admin, "target",
            "端末紛失の申告を確認", Credential(challenge), true);
        var op = await f.Db.PasskeyRecoveryOperations.SingleAsync();
        Check(response.OperationId == op.Id && response.ExpiresAt.ToUnixTimeSeconds() == f.Clock.Now + 900,
            "reset creates one 15-minute operation");
        Check(!f.Target.Enabled && op.DisabledForRecovery &&
            op.EmailHash == SemEndpoints.Hash(f.Target.NormalizedEmail!),
            "start disables only the confirmed target and binds its saved email");
        Check(await f.Db.Set<IdentityUserPasskey<string>>().CountAsync() == 0,
            "all old passkeys are removed before recovery proceeds");
        Check(!await f.Db.EmailTickets.AnyAsync(x => x.UserId == "target"),
            "unused legacy enrollment tickets are removed during reset");
        Check(f.Target.SecurityStamp != "target-stamp" &&
            await f.Db.PasskeyRecoveryAudits.AnyAsync(x => x.Action == "RecoveryStarted" &&
                x.AdminId == "admin" && x.TargetUserId == "target" &&
                x.Reason == "端末紛失の申告を確認" && x.At == f.Clock.Now),
            "security stamp rotates and audit stores actor, target, reason, and time");
        Check((await f.Db.PasskeyRecoveryRateStates.SingleAsync()).SendTimes == $"[{f.Clock.Now}]",
            "accepted mail admission is persisted");

        var link = await f.Link();
        var uri = new Uri(link.Url);
        Check(uri.AbsolutePath == "/recovery" && uri.Query.Length == 0 &&
            uri.Fragment.Contains("token=", StringComparison.Ordinal),
            "recovery token is in public URL fragment, never query");
        var grant = await f.Service.ConfirmMailAsync(op.Id, link.Token);
        Check(grant.UserId == "target" && (await f.Db.PasskeyRecoveryOperations.SingleAsync()).Status == "EmailConfirmed",
            "email proof yields only a registration grant; account stays disabled");
        await Deny(() => f.Service.ConfirmMailAsync(op.Id, link.Token), "email proof is one use");

        var registrationOptions = await f.Service.MakePasskeyCreationOptionsAsync(
            new DefaultHttpContext(), grant.UserId, grant.Grant);
        var complete = new DefaultHttpContext();
        await f.Service.CompletePasskeyRegistrationAsync(complete, grant.UserId, grant.Grant,
            Credential(Challenge(registrationOptions)));
        op = await f.Db.PasskeyRecoveryOperations.SingleAsync();
        Check(f.Target.Enabled && f.Target.Email == "target@example.test" &&
            op.Status == "Completed" && !op.DisabledForRecovery &&
            op.GrantHash is null && op.TokenHash is null,
            "synthetic identity adapter success enables the saved account and clears recovery");
        Check(!complete.Response.Headers.ContainsKey("Set-Cookie"),
            "completion does not create a normal login session");
        Check((await f.Db.PasskeyRecoveryMails.SingleAsync(x => x.Kind == "RecoveryComplete"))
            .ProtectedPayload.Length > 0,
            "completion notice is queued in a protected recipient outbox");
    });

    await Run(async f =>
    {
        f.Target.Enabled = false;
        f.Target.SecurityStamp = "admin-disabled-stamp";
        await f.Db.SaveChangesAsync();
        var targetView = await f.Service.ReadAdminTargetAsync(f.AdminContext, "target");
        Check(targetView is { Enabled: false, Recovering: false, OperationId: null },
            "admin-disabled account is not labeled as recovery-disabled");
        await Deny(() => f.Service.CreateAdminProofOptionsAsync(f.AdminContext, f.Admin,
            "target", "管理者停止中"), "admin-disabled account cannot start recovery");
        await Deny(() => f.Service.CreateAdminProofOptionsAsync(f.AdminContext, f.Admin,
            "admin", "自分の認証を解除"), "self-reset is prohibited");
    });

    await Run(async f =>
    {
        var first = await f.Start();
        var originalExpiry = first.ExpiresAt;
        var oldLink = await f.Link();
        f.Clock.Advance(60);
        var options = await f.Service.CreateResendProofOptionsAsync(f.AdminContext, f.Admin, first.OperationId);
        var result = await f.Service.ResendAsync(f.AdminContext, f.Admin, first.OperationId,
            Credential(Challenge(options)));
        Check(result.ExpiresAt == originalExpiry, "resend never extends original deadline");
        await Deny(() => f.Service.ConfirmMailAsync(first.OperationId, oldLink.Token),
            "resend rotates and invalidates prior email token");
        var currentLink = await f.Link();
        Check(currentLink.Token != oldLink.Token && currentLink.Url.Contains("token=", StringComparison.Ordinal),
            "resend queues a fresh fragment-based token");

        f.Clock.Advance(60);
        options = await f.Service.CreateResendProofOptionsAsync(f.AdminContext, f.Admin, first.OperationId);
        await f.Service.ResendAsync(f.AdminContext, f.Admin, first.OperationId, Credential(Challenge(options)));
        f.Clock.Advance(60);
        await Deny(() => f.Service.CreateResendProofOptionsAsync(f.AdminContext, f.Admin, first.OperationId),
            "three accepted sends per rolling hour enforced");
        Check(JsonSerializer.Deserialize<long[]>((await f.Db.PasskeyRecoveryRateStates.SingleAsync()).SendTimes)!.Length == 3,
            "resends persist under durable account/purpose quota");
    });

    await Run(async f =>
    {
        var options = await f.Service.CreateAdminProofOptionsAsync(f.AdminContext, f.Admin,
            "target", "管理者確認済みの復旧");
        var firstChallenge = Challenge(options);
        var started = await f.Service.StartAsync(f.AdminContext, f.Admin, "target",
            "管理者確認済みの復旧", Credential(firstChallenge), true);
        var originalLink = await f.Link();
        var staleAdminOptions = await f.Service.CreateAdminProofOptionsAsync(f.AdminContext, f.Admin,
            "target", "再開前の確認");
        var staleAdminCredential = Credential(Challenge(staleAdminOptions));
        for (var i = 0; i < 5; i++)
            await Deny(() => f.Service.ConfirmMailAsync(started.OperationId, "synthetic-wrong-token"),
                "bad mail proof counted " + i);
        await Deny(() => f.Service.ConfirmMailAsync(started.OperationId, "another-wrong-token"),
            "five proof failures throttle for 15 minutes");
        var persisted = await f.Db.PasskeyRecoveryRateStates.AsNoTracking().SingleAsync();
        Check(JsonSerializer.Deserialize<long[]>(persisted.EmailFailures)!.Length == 5,
            "email failures survive operation state");
        var operation = await f.Db.PasskeyRecoveryOperations.SingleAsync();
        var oldMail = await f.Db.PasskeyRecoveryMails.AsNoTracking().SingleAsync(x => x.Kind == "RecoveryLink");
        Check(operation.Status == "Expired" && operation.ExpiresAt == f.Clock.Now &&
            operation.TokenHash is null && operation.GrantHash is null &&
            operation.GrantExpiresAt is null && operation.EmailVerifiedAt is null &&
            operation.CreationChallengeHash is null,
            "fifth mail-proof failure expires operation and clears every proof/grant");
        Check(oldMail.Status == "Cancelled" && oldMail.ProtectedPayload == "" && oldMail.ProofHash is null &&
            !await f.Db.PasskeyRecoveryAdminChallenges.AnyAsync(x => x.TargetUserId == "target"),
            "fifth failure cancels protected mail and clears stored admin challenges");
        await Deny(() => f.Service.StartAsync(f.AdminContext, f.Admin, "target", "再開前の確認",
            staleAdminCredential, true), "start is blocked during email-proof cooldown");
        await Deny(() => f.Service.CreateAdminProofOptionsAsync(f.AdminContext, f.Admin,
            "target", "再開前の確認"), "new admin proof options are blocked during cooldown");

        f.Clock.Advance(900);
        options = await f.Service.CreateAdminProofOptionsAsync(f.AdminContext, f.Admin,
            "target", "管理者承認後の再開");
        var freshChallenge = Challenge(options);
        Check(freshChallenge != firstChallenge, "restart requires a fresh administrator challenge");
        var restarted = await f.Service.StartAsync(f.AdminContext, f.Admin, "target",
            "管理者承認後の再開", Credential(freshChallenge), true);
        operation = await f.Db.PasskeyRecoveryOperations.SingleAsync();
        Check(restarted.ExpiresAt.ToUnixTimeSeconds() == f.Clock.Now + 900 &&
            operation.Status == "Pending" && operation.DisabledForRecovery && !f.Target.Enabled &&
            operation.EmailHash == SemEndpoints.Hash(f.Target.NormalizedEmail!),
            "cooldown expiry permits a new proof while preserving saved email and disabled state");
        var newLink = await f.Link();
        await Deny(() => f.Service.ConfirmMailAsync(restarted.OperationId, originalLink.Token),
            "pre-cooldown token remains unusable after restart");
        await f.Service.ConfirmMailAsync(restarted.OperationId, newLink.Token);
        Check((await f.Db.PasskeyRecoveryOperations.SingleAsync()).Status == "EmailConfirmed",
            "fresh saved-address proof works after the durable cooldown expires");
    });

    await Run(async f =>
    {
        var started = await f.Start();
        var mail = await f.Link();
        var transport = new RecordingTransport();
        var options = f.Provider.GetRequiredService<DbContextOptions<ApplicationDbContext>>();
        var dispatcher = new PasskeyRecoveryMailDispatcher(new DbFactory(options), f.Protection,
            transport, f.Clock, NullLogger<PasskeyRecoveryMailDispatcher>.Instance);
        await dispatcher.DispatchOnce();
        Check(transport.Messages.Count == 1 && transport.Messages[0].Recipient == "target@example.test",
            "dispatcher delivers only with an injected synthetic transport to saved recipient");
        Check((await f.Db.PasskeyRecoveryMails.AsNoTracking().FirstAsync(x => x.Kind == "RecoveryLink"))
            .ProtectedPayload == "",
            "successful outbox delivery clears protected body");
        Check(mail.Token.Length >= 40 && !transport.Messages[0].Body.Contains("?" + mail.Token),
            "mail payload includes fragment token and no query token");
        Check((await f.Db.PasskeyRecoveryRateStates.SingleAsync()).SendTimes == $"[{f.Clock.Now}]",
            "SMTP dispatch does not count as another accepted send");
        _ = started;
    });

    Console.WriteLine($"Passkey recovery checks passed: {checks}");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

async Task Run(Func<Fixture, Task> test)
{
    var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");
    await using var fixture = await Fixture.Create(path, backup);
    await test(fixture);
}

static string Challenge(string options) =>
    JsonDocument.Parse(options).RootElement.GetProperty("challenge").GetString()!;

static string Credential(string challenge) => JsonSerializer.Serialize(new
{
    response = new
    {
        clientDataJSON = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { challenge })))
    }
});

sealed class Clock : TimeProvider
{
    public long Now = 1_800_000_000;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Now);
    public void Advance(long seconds) => Now += seconds;
}

sealed class FakeRecoveryIdentity : IPasskeyRecoveryIdentity
{
    int sequence;
    public bool Succeeds = true;
    string NewOptions() => JsonSerializer.Serialize(new { challenge = "synthetic-challenge-" + ++sequence });
    public Task<string> MakeRequestOptionsAsync(ApplicationUser user) => Task.FromResult(NewOptions());
    public Task<bool> VerifyAndUpdateAssertionAsync(string credential, ApplicationUser expectedUser) =>
        Task.FromResult(Succeeds && expectedUser.IsAdmin);
    public Task<string> MakeCreationOptionsAsync(ApplicationUser user) => Task.FromResult(NewOptions());
    public Task<bool> VerifyAndSaveAttestationAsync(string credential, ApplicationUser expectedUser) =>
        Task.FromResult(Succeeds && expectedUser.Id == "target");
}

sealed class Fixture : IAsyncDisposable
{
    public ServiceProvider Provider = null!;
    public ApplicationDbContext Db = null!;
    public PasskeyRecoveryService Service = null!;
    public ApplicationUser Admin = null!;
    public ApplicationUser Target = null!;
    public Clock Clock = new();
    public FakeRecoveryIdentity Identity = new();
    public ClaimsPrincipal Principal = null!;
    public DefaultHttpContext AdminContext = null!;
    public EphemeralDataProtectionProvider Protection = new();

    public static async Task<Fixture> Create(string path, string backup)
    {
        using (File.Create(path)) { }
        if (!File.Exists(backup)) File.Copy(path, backup);
        var f = new Fixture();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite("Data Source=" + path));
        services.AddIdentityCore<ApplicationUser>(o =>
        {
            o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            o.User.RequireUniqueEmail = true;
        }).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton<TimeProvider>(f.Clock);
        services.AddSingleton<IPasskeyRecoveryIdentity>(f.Identity);
        services.AddSingleton<IDataProtectionProvider>(f.Protection);
        services.AddSingleton(new DeploymentSettings { PublicUrl = "https://sem.example.test/" });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Smtp:Host"] = "synthetic.invalid",
                ["Smtp:From"] = "noreply@example.test"
            }).Build());
        services.AddSingleton<NotificationService>();
        services.AddScoped<PasskeyRecoveryService>();
        f.Provider = services.BuildServiceProvider();
        f.Db = f.Provider.GetRequiredService<ApplicationDbContext>();
        await f.Db.Database.MigrateAsync();
        f.Admin = new ApplicationUser
        {
            Id = "admin", UserName = "admin@example.test", NormalizedUserName = "ADMIN@EXAMPLE.TEST",
            Email = "admin@example.test", NormalizedEmail = "ADMIN@EXAMPLE.TEST",
            EmailConfirmed = true, Enabled = true, IsAdmin = true,
            SecurityStamp = "admin-stamp", DisplayName = "Synthetic admin"
        };
        f.Target = new ApplicationUser
        {
            Id = "target", UserName = "target@example.test", NormalizedUserName = "TARGET@EXAMPLE.TEST",
            Email = "target@example.test", NormalizedEmail = "TARGET@EXAMPLE.TEST",
            EmailConfirmed = true, Enabled = true, IsAdmin = false,
            SecurityStamp = "target-stamp", DisplayName = "Synthetic target"
        };
        f.Db.Users.AddRange(f.Admin, f.Target);
        await f.Db.SaveChangesAsync();
        f.Principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, f.Admin.Id),
            new Claim(PasskeyAdditionService.SessionClaim, "synthetic-admin-session"),
            new Claim("AspNet.Identity.SecurityStamp", f.Admin.SecurityStamp!)
        ], "Synthetic"));
        f.AdminContext = new DefaultHttpContext { User = f.Principal };
        f.Service = f.Provider.GetRequiredService<PasskeyRecoveryService>();
        return f;
    }

    public async Task<PasskeyRecoveryStartResult> Start()
    {
        var options = await Service.CreateAdminProofOptionsAsync(AdminContext, Admin,
            Target.Id, "管理者確認済みの復旧");
        return await Service.StartAsync(AdminContext, Admin, Target.Id, "管理者確認済みの復旧",
            MakeCredential(ProgramChallenge(options)), true);
    }

    public async Task<(PasskeyRecoveryMailPayload Payload, string Token, string Url)> Link()
    {
        var mail = await Db.PasskeyRecoveryMails.Where(x => x.Kind == "RecoveryLink" &&
                x.Status == "Pending").OrderByDescending(x => x.Due).FirstAsync();
        var payload = JsonSerializer.Deserialize<PasskeyRecoveryMailPayload>(
            Protection.CreateProtector("SEM.PasskeyRecovery.Mail.v1").Unprotect(mail.ProtectedPayload))!;
        var match = System.Text.RegularExpressions.Regex.Match(payload.Body,
            @"https://[^\s]+#op=([^&\s]+)&token=([^\s]+)");
        if (!match.Success) throw new Exception("Expected a recovery URL with fragment token.");
        return (payload, Uri.UnescapeDataString(match.Groups[2].Value),
            match.Value);
    }

    static string ProgramChallenge(string options) =>
        JsonDocument.Parse(options).RootElement.GetProperty("challenge").GetString()!;
    static string MakeCredential(string challenge) => JsonSerializer.Serialize(new
    {
        response = new
        {
            clientDataJSON = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { challenge })))
        }
    });

    public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
}

sealed class RecordingTransport : IPasskeyRecoveryMailTransport
{
    public List<PasskeyRecoveryMailPayload> Messages { get; } = [];
    public Task Send(string id, PasskeyRecoveryMailPayload payload, CancellationToken ct)
    {
        Messages.Add(payload);
        return Task.CompletedTask;
    }
}

sealed class DbFactory(DbContextOptions<ApplicationDbContext> options)
    : IDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext() => new(options);
}
