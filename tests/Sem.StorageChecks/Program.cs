using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Data;
using Sem.Web.Runtime;

var root = Path.Combine(Path.GetTempPath(), "sem-storage-check-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var passed = new List<string>();
try
{
    var fresh = Path.Combine(root, "fresh.db");
    using (var fixture = new Fixture(fresh))
    {
        await SqliteStorageInitializer.InitializeAsync(fixture.Db);
        Check((await fixture.Db.Database.GetAppliedMigrationsAsync()).SequenceEqual(fixture.Db.Database.GetMigrations()),
            "Empty DB is created by the baseline migration");
        await SqliteStorageInitializer.InitializeAsync(fixture.Db);
        Check(await fixture.Db.Users.CountAsync() == 0, "Repeated startup is idempotent");
    }

    var baseline = Path.Combine(root, "baseline.db");
    using (var fixture = new Fixture(baseline))
    {
        await CreateLegacyAsync(fixture.Db, retainHistory: true);
        fixture.Db.Users.Add(new ApplicationUser { Id = "baseline-user", UserName = "baseline@example.test",
            Email = "baseline@example.test", EmailConfirmed = true, Enabled = true, SecurityStamp = "baseline-stamp" });
        await fixture.Db.SaveChangesAsync();
        await fixture.Db.Database.ExecuteSqlRawAsync("INSERT INTO PushDevices(Id,UserId,Endpoint,P256dh,Auth) VALUES ('fixture-device','fixture-user','https://web.push.apple.com/synthetic','fixture-key','fixture-auth')");
        await SqliteStorageInitializer.InitializeAsync(fixture.Db);
        Check(await fixture.Db.Users.AnyAsync(u => u.Id == "baseline-user" && u.SecurityStamp == "baseline-stamp") &&
            (await fixture.Db.Database.GetAppliedMigrationsAsync()).SequenceEqual(fixture.Db.Database.GetMigrations()),
            "Managed baseline DB receives the pending migrations without losing its user");
    }

    var legacy = Path.Combine(root, "legacy.db");
    using (var fixture = new Fixture(legacy))
    {
        await CreateLegacyAsync(fixture.Db); // Test fixture only: fixed pre-migration schema.
        fixture.Db.Users.Add(new ApplicationUser
        {
            Id = "fixture-user", UserName = "fixture@example.test", Email = "fixture@example.test",
            NormalizedUserName = "FIXTURE@EXAMPLE.TEST", NormalizedEmail = "FIXTURE@EXAMPLE.TEST",
            Enabled = true, EmailConfirmed = true, IsAdmin = true, DisplayName = "Synthetic admin",
            SecurityStamp = "synthetic-security-stamp"
        });
        fixture.Db.SiteStates.Add(new SiteState { Initialized = true, Name = "Synthetic site" });
        fixture.Db.Invitations.Add(new Invitation { Id = "fixture-invite", OwnerId = "fixture-user",
            IssuerId = "fixture-user", Uses = 1, Limit = 1, Disabled = true,
            RevocationReason = "synthetic reason", ProtectedToken = "synthetic-protected-token" });
        fixture.Db.InvitationRequests.Add(new InvitationRequest { Id = "fixture-request",
            UserId = "fixture-user", Status = "Rejected", DecidedBy = "fixture-user", DecidedAt = 100 });
        fixture.Db.EmailTickets.Add(new EmailTicket { UserId = "fixture-user", Hash = "fixture-ticket", Used = true });
        fixture.Db.Messages.Add(new Message { UserId = "fixture-user", Title = "synthetic", Body = "retained" });
        fixture.Db.Deliveries.Add(new Delivery { UserId = "fixture-user", Due = 9999999999,
            ProtectedBody = "synthetic-protected-body", Attempts = 2 });

        fixture.Db.Audits.Add(new Audit { ActorId = "fixture-user", Action = "Synthetic" });
        await fixture.Db.SaveChangesAsync();
        await fixture.Db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "AspNetUserPasskeys" ("CredentialId","UserId","Data")
            VALUES (X'01020304','fixture-user',{0});
            """, """{"synthetic":"retained"}""");
    }
    var before = await DumpAsync(legacy);
    using (var fixture = new Fixture(legacy))
    {
        await SqliteStorageInitializer.InitializeAsync(fixture.Db);
        Check(before == await DumpAsync(legacy), "Legacy adoption preserves every pre-existing business row including passkey bytes");
        Check(await fixture.Db.PartPresets.CountAsync()==6 && (await fixture.Db.PartPresetStates.SingleAsync()).RowVersion==0, "Legacy adoption adds only the explicit new preset seed rows");
        Check((await fixture.Db.Database.GetAppliedMigrationsAsync()).SequenceEqual(fixture.Db.Database.GetMigrations()),
            "Legacy schema receives baseline history and pending migration after validation");
        await SqliteStorageInitializer.InitializeAsync(fixture.Db);
        Check(before == await DumpAsync(legacy), "Migrated DB survives another startup without data changes");
    }
    SqliteConnection.ClearAllPools();
    var restored = Path.Combine(root, "restored.db");
    File.Copy(legacy, restored);
    using (var fixture = new Fixture(restored))
        await SqliteStorageInitializer.InitializeAsync(fixture.Db);
    Check(before == await DumpAsync(restored), "Restored migrated DB retains all business data");

    var changedMigrated = Path.Combine(root, "changed-migrated.db");
    File.Copy(legacy, changedMigrated);
    using (var connection = new SqliteConnection("Data Source=" + changedMigrated))
    {
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE Messages ADD COLUMN Unexpected TEXT;";
        await command.ExecuteNonQueryAsync();
    }
    SqliteConnection.ClearAllPools();
    var changedBytes = SHA256.HashData(await File.ReadAllBytesAsync(changedMigrated));
    bool changedRejected = false;
    using (var fixture = new Fixture(changedMigrated))
    {
        try { await SqliteStorageInitializer.InitializeAsync(fixture.Db); }
        catch (InvalidOperationException) { changedRejected = true; }
    }
    SqliteConnection.ClearAllPools();
    var changedAfter = SHA256.HashData(await File.ReadAllBytesAsync(changedMigrated));
    Check(changedRejected && changedBytes.SequenceEqual(changedAfter),
        "Schema drift in an already migrated DB is rejected without writes");

    foreach (var (name, mutation) in new (string, string)[]
    {
        ("Unknown table", "CREATE TABLE UnknownTable (Id INTEGER);"),
        ("Unknown view", "CREATE VIEW UnknownView AS SELECT Id FROM AspNetUsers;"),
        ("Unknown trigger", "CREATE TRIGGER UnknownTrigger AFTER INSERT ON Messages BEGIN SELECT 1; END;"),
        ("Extra column", "ALTER TABLE Messages ADD COLUMN Unexpected TEXT;"),
        ("Missing index", "DROP INDEX IX_Invitations_TokenHash;"),
        ("Changed unique index", "DROP INDEX IX_Invitations_TokenHash; CREATE INDEX IX_Invitations_TokenHash ON Invitations(TokenHash);"),
        ("Unknown history", """CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY, "ProductVersion" TEXT NOT NULL); INSERT INTO "__EFMigrationsHistory" VALUES ('00000000000000_CreateIdentitySchema','10.0.12');"""),
        ("Invalid history schema", """CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT, "ProductVersion" TEXT);"""),
        ("Invalid lock schema", """CREATE TABLE "__EFMigrationsLock" (Id TEXT);"""),
        ("Foreign key violation", """PRAGMA foreign_keys=OFF; INSERT INTO AspNetUserPasskeys(CredentialId,UserId,Data) VALUES (X'09','missing-user','{}');""")
    })
    {
        var file = Path.Combine(root, Guid.NewGuid() + ".db");
        using (var fixture = new Fixture(file)) await CreateLegacyAsync(fixture.Db);
        using (var connection = new SqliteConnection("Data Source=" + file))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = mutation;
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var bytes = SHA256.HashData(await File.ReadAllBytesAsync(file));
        bool rejected = false;
        using (var fixture = new Fixture(file))
        {
            try { await SqliteStorageInitializer.InitializeAsync(fixture.Db); }
            catch (Exception error) when (error is InvalidOperationException or SqliteException) { rejected = true; }
        }
        SqliteConnection.ClearAllPools();
        var afterBytes = SHA256.HashData(await File.ReadAllBytesAsync(file));
        Check(rejected && bytes.SequenceEqual(afterBytes),
            name + " is rejected without changing the DB file");
    }
    var needsReset = Path.Combine(root, "needs-reset.db");
    using (var fixture = new Fixture(needsReset))
    {
        await CreateLegacyAsync(fixture.Db);
        fixture.Db.Events.Add(new TestEvent { Id = "disposable-event", ManagerId = "fixture-user" });
        fixture.Db.Participations.Add(new Participation { EventId = "disposable-event", UserId = "fixture-user" });
        await fixture.Db.SaveChangesAsync();
    }
    SqliteConnection.ClearAllPools();
    var resetBefore = SHA256.HashData(await File.ReadAllBytesAsync(needsReset));
    bool resetRequired = false;
    using (var fixture = new Fixture(needsReset))
    {
        try { await SqliteStorageInitializer.InitializeAsync(fixture.Db); }
        catch (InvalidOperationException e) { resetRequired = e.Message.Contains("Legacy test events remain"); }
    }
    SqliteConnection.ClearAllPools();
    var resetAfter = SHA256.HashData(await File.ReadAllBytesAsync(needsReset));
    Check(resetRequired && resetBefore.SequenceEqual(resetAfter),
        "Remaining legacy test events reject the core upgrade without deletion or history writes");
    Console.WriteLine($"Storage checks passed: {passed.Count}");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, true);
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed.Add(name);
    Console.WriteLine("PASS " + name);
}

static async Task<string> DumpAsync(string path)
{
    using var connection = new SqliteConnection("Data Source=" + path);
    await connection.OpenAsync();
    var tables = new List<string>();
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EF%' AND name NOT IN ('PartPresetStates','PartPresets') ORDER BY name";
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
    }
    var rows = new List<object>();
    foreach (var table in tables)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\" ORDER BY rowid";
        // Compare every original subscription value; new nullable security bindings are tested separately.
        if (table == "PushDevices") command.CommandText = "SELECT Id,UserId,Endpoint,P256dh,Auth FROM PushDevices ORDER BY rowid";
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(new { table, values = values.Select(x => x is DBNull ? null : x).ToArray() });
        }
    }
    return JsonSerializer.Serialize(rows);
}

static async Task CreateLegacyAsync(ApplicationDbContext db, bool retainHistory = false)
{
    var script = db.GetService<IMigrator>().GenerateScript(Migration.InitialDatabase,
        SqliteStorageInitializer.BaselineId, MigrationsSqlGenerationOptions.NoTransactions);
    await db.Database.OpenConnectionAsync();
    try
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = script + (retainHistory ? "" : "\nDROP TABLE \"__EFMigrationsHistory\";");
        await command.ExecuteNonQueryAsync();
    }
    finally { await db.Database.CloseConnectionAsync(); }
}

sealed class Fixture : IDisposable
{
    private readonly ServiceProvider provider;
    public ApplicationDbContext Db { get; }
    public Fixture(string path)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite("Data Source=" + path));
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<ApplicationDbContext>();
        provider = services.BuildServiceProvider();
        Db = provider.GetRequiredService<ApplicationDbContext>();
    }
    public void Dispose() => provider.Dispose();
}