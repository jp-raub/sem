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

var root = Path.Combine(Path.GetTempPath(), "sem-reset-check-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var passed = new List<string>();
try
{
    foreach (var version in new[] { "legacy", "baseline", "proof" })
    {
        var path = Path.Combine(root, version + ".db");
        await SeedAsync(path, version);
        var beforeHash = Hash(path);
        var beforeRows = await RetainedAsync(path);
        var auditBefore = await QueryAsync(path, """SELECT * FROM "Audits" ORDER BY "Id";""");
        var plan = await LegacyEventResetMaintenance.ExecuteAsync(new(path));
        Check(plan.Status == "inspection-only" && plan.Events == 2 && plan.Participations == 2 &&
            plan.DatabaseSha256 == beforeHash && Hash(path) == beforeHash,
            version + ": inspection is read-only and counts old events/participations");
        var backup = path + ".backup";
        var result = await LegacyEventResetMaintenance.ExecuteAsync(new(path, true, beforeHash, 2, 2, backup));
        Check(result.Status == "reset-completed" && result.BackupSha256 == beforeHash && Hash(backup) == beforeHash,
            version + ": backup is byte-identical before reset");
        if (!OperatingSystem.IsWindows())
            Check(File.GetUnixFileMode(backup) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                version + ": backup permissions are 0600");
        Check(await QueryAsync(path, """SELECT COUNT(*) FROM "Events";""") == "[[0]]" &&
            await QueryAsync(path, """SELECT COUNT(*) FROM "Participations";""") == "[[0]]",
            version + ": events and related participation are deleted together");
        Check(await RetainedAsync(path) == beforeRows,
            version + ": every other table is unchanged, including Identity, invites, inbox, delivery, Push and history");
        Check(await QueryAsync(path, """SELECT * FROM "Audits" WHERE "Id"='old-audit' ORDER BY "Id";""") == auditBefore &&
            await QueryAsync(path, """SELECT COUNT(*) FROM "Audits" WHERE "ActorId"='host-maintenance' AND "Action"='LegacyEventsReset:events=2;participations=2';""") == "[[1]]",
            version + ": old audit is retained and one host-maintenance audit is appended");
        Check(await QueryAsync(path, "PRAGMA integrity_check;") == "[[\"ok\"]]" &&
            await QueryAsync(path, "PRAGMA foreign_key_check;") == "[]",
            version + ": resulting SQLite integrity and foreign keys pass");
        var afterHash = Hash(path);
        var again = await LegacyEventResetMaintenance.ExecuteAsync(new(path, true, afterHash, 0, 0, path + ".unused-backup"));
        Check(again.Status == "already-empty" && Hash(path) == afterHash && !File.Exists(path + ".unused-backup"),
            version + ": repeat apply is a no-op with no second audit or backup");
    }
    foreach (var (label, mutation) in new (string, string)[]
    {
        ("Unknown table", "CREATE TABLE Unknown(Id TEXT);"),
        ("Unknown trigger", "CREATE TRIGGER Unexpected AFTER DELETE ON Events BEGIN DELETE FROM Messages; END;"),
        ("Changed column", "ALTER TABLE Events ADD COLUMN FutureField TEXT;"),
        ("Missing index", "DROP INDEX IX_Participations_EventId_UserId;"),
        ("Unknown history", """INSERT INTO "__EFMigrationsHistory" VALUES ('20990101000000_Future','10.0.12');"""),
        ("Empty history", """DELETE FROM "__EFMigrationsHistory";"""),
        ("Migration lock remains", """INSERT INTO "__EFMigrationsLock" VALUES (1,'synthetic-lock');"""),
        ("Orphan participation", """INSERT INTO Participations VALUES ('orphan','not-an-event','user');"""),
        ("Foreign key violation", """PRAGMA foreign_keys=OFF; INSERT INTO AspNetUserClaims(UserId,ClaimType,ClaimValue) VALUES ('missing-user','test','test');""")
    })
    {
        var path = Path.Combine(root, Guid.NewGuid() + ".db");
        await SeedAsync(path, "proof");
        await SqlAsync(path, mutation);
        await RefusedUnchangedAsync(path, new(path, true, Hash(path), 2, 2, path + ".backup"), label);
        Check(!File.Exists(path + ".backup"), label + ": no backup or deletion before validation");
    }
    var safe = Path.Combine(root, "safe.db");
    await SeedAsync(safe, "proof");
    await RefusedUnchangedAsync(safe, new(safe, true, new string('0', 64), 2, 2, safe + ".backup"), "Stale hash");
    await RefusedUnchangedAsync(safe, new(safe, true, Hash(safe), 1, 2, safe + ".backup"), "Stale event count");
    await RefusedUnchangedAsync(safe, new(safe, true, Hash(safe), 2, 1, safe + ".backup"), "Stale participation count");
    await RefusedUnchangedAsync(safe, new(safe, true), "Missing explicit approval arguments");
    await RefusedUnchangedAsync(safe, new(safe, Backup: safe + ".backup"), "Apply option without --apply");
    await RefusedUnchangedAsync(safe, new(safe, true, Hash(safe), 2, 2, safe), "Backup points to database");
    var existingBackup = safe + ".existing";
    File.WriteAllText(existingBackup, "retained backup");
    await RefusedUnchangedAsync(safe, new(safe, true, Hash(safe), 2, 2, existingBackup), "Existing backup path");
    Check(File.ReadAllText(existingBackup) == "retained backup", "Existing backup is never overwritten");
    var sidecar = safe + "-wal";
    File.WriteAllText(sidecar, "pending WAL");
    await RefusedUnchangedAsync(safe, new(safe), "Pending sidecar");
    File.Delete(sidecar);
    var wal = Path.Combine(root, "wal.db");
    await SeedAsync(wal, "proof");
    await SqlAsync(wal, "PRAGMA journal_mode=WAL;");
    await RefusedUnchangedAsync(wal, new(wal), "WAL database");
    if (!OperatingSystem.IsWindows())
    {
        var symlink = Path.Combine(root, "link.db");
        File.CreateSymbolicLink(symlink, safe);
        await RefusedUnchangedAsync(safe, new(symlink), "Symlink target");
    }
    using (var lockConnection = new SqliteConnection(new SqliteConnectionStringBuilder
           { DataSource = safe, Pooling = false }.ToString()))
    {
        await lockConnection.OpenAsync();
        using var transaction = lockConnection.BeginTransaction();
        await RefusedUnchangedAsync(safe, new(safe, true, Hash(safe), 2, 2, safe + ".locked-backup"), "Competing writer lock");
    }
    var cliHash = Hash(safe);
    Check(await LegacyEventResetMaintenance.RunAsync(["--database", safe]) == 0 && Hash(safe) == cliHash,
        "CLI defaults to read-only inspection");
    Check(await LegacyEventResetMaintenance.RunAsync(["--database", safe, "--apply"]) == 1 && Hash(safe) == cliHash,
        "CLI refuses implicit or incomplete approval");
    Check(await LegacyEventResetMaintenance.RunAsync(["--database", safe, "--apply", "--apply"]) == 1,
        "CLI rejects duplicate flags");
    Check(await LegacyEventResetMaintenance.RunAsync(["--database", safe, "--unknown", "value"]) == 1,
        "CLI rejects unknown options");
    var missing = Path.Combine(root, "missing.db");
    Check(await LegacyEventResetMaintenance.RunAsync(["--database", missing]) == 1 && !File.Exists(missing),
        "CLI never creates a missing database");
    Console.WriteLine(JsonSerializer.Serialize(new { passed = passed.Count, checks = passed }));
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}

void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + label);
    passed.Add(label);
    Console.WriteLine("PASS: " + label);
}
string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
async Task RefusedUnchangedAsync(string path, LegacyEventResetMaintenance.Options options, string label)
{
    var before = Hash(path);
    bool refused = false;
    try { await LegacyEventResetMaintenance.ExecuteAsync(options); }
    catch (Exception ex) when (ex is InvalidOperationException or SqliteException or IOException) { refused = true; }
    Check(refused && Hash(path) == before, label + ": refused without database writes");
}
async Task SqlAsync(string path, string sql)
{
    using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false }.ToString());
    await c.OpenAsync();
    using var command = c.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}
async Task<string> QueryAsync(string path, string sql)
{
    using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
    await c.OpenAsync();
    using var command = c.CreateCommand();
    command.CommandText = sql;
    using var r = await command.ExecuteReaderAsync();
    var rows = new List<object?[]>();
    while (await r.ReadAsync())
    {
        var values = new object?[r.FieldCount];
        for (int i = 0; i < values.Length; i++) values[i] = r.IsDBNull(i) ? null : r.GetValue(i);
        rows.Add(values);
    }
    return JsonSerializer.Serialize(rows);
}
async Task<string> RetainedAsync(string path)
{
    var namesJson = await QueryAsync(path, """
        SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'
        AND name NOT IN ('Events','Participations','Audits') ORDER BY name;
        """);
    var names = JsonSerializer.Deserialize<string[][]>(namesJson)!;
    var result = new SortedDictionary<string, string>();
    foreach (var row in names)
        result[row[0]] = await QueryAsync(path, $"SELECT * FROM \"{row[0].Replace("\"", "\"\"")}\" ORDER BY rowid;");
    return JsonSerializer.Serialize(result);
}
async Task SeedAsync(string path, string version)
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false }.ToString()));
    services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
        .AddEntityFrameworkStores<ApplicationDbContext>();
    using var provider = services.BuildServiceProvider();
    var db = provider.GetRequiredService<ApplicationDbContext>();
    var target = version == "proof" ? "20261012093941_AddPasskeyAdditionProof" : SqliteStorageInitializer.BaselineId;
    await db.GetService<IMigrator>().MigrateAsync(target);
    db.Users.Add(new ApplicationUser { Id = "user", UserName = "fixture@example.test",
        Email = "fixture@example.test", EmailConfirmed = true, Enabled = true, IsAdmin = true, SecurityStamp = "retained-stamp" });
    db.SiteStates.Add(new SiteState { Initialized = true, Name = "Retained service" });
    db.Invitations.Add(new Invitation { Id = "invite", OwnerId = "user", IssuerId = "user", Uses = 1,
        TokenHash = "invitation-hash", ProtectedToken = "encrypted-token" });
    db.InvitationRequests.Add(new InvitationRequest { UserId = "user", Reason = "retained", Status = "Approved" });
    db.EmailTickets.Add(new EmailTicket { UserId = "user", Hash = "ticket-hash", Used = true });
    db.Messages.Add(new Message { UserId = "user", Body = "Old event reference must not cause unrelated deletion." });
    db.Deliveries.Add(new Delivery { UserId = "user", ProtectedBody = "encrypted-body", Due = 9999999999 });
    db.PushDevices.Add(new PushDevice { UserId = "user", Endpoint = "https://example.test/fixture",
        P256dh = "fixture-public", Auth = "fixture-auth" });
    db.Events.AddRange(new TestEvent { Id = "event-1", ManagerId = "user", Phase = "Custom" },
        new TestEvent { Id = "event-2", ManagerId = "user", Phase = "Hidden" });
    db.Participations.AddRange(new Participation { EventId = "event-1", UserId = "user" },
        new Participation { EventId = "event-2", UserId = "user" });
    db.Audits.Add(new Audit { Id = "old-audit", ActorId = "user", Action = "Join", EntityId = "event-1", At = 1234 });
    if (version == "proof")
        db.PasskeyAdditions.Add(new PasskeyAddition { UserId = "user", SessionHash = "retained-session",
            Email = "fixture@example.test", SecurityStamp = "retained-stamp", Cancelled = true });
    await db.SaveChangesAsync();
    await db.Database.ExecuteSqlRawAsync("""
        INSERT INTO "AspNetUserPasskeys" ("CredentialId","UserId","Data")
        VALUES (X'01020304','user',{0});
        """, """{"synthetic":"retained"}""");
    if (version == "legacy") await db.Database.ExecuteSqlRawAsync("""DROP TABLE "__EFMigrationsHistory"; DROP TABLE "__EFMigrationsLock";""");
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");
}