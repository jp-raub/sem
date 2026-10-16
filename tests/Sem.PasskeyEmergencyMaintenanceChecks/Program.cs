using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sem.Web.Data;
using Sem.Web.Runtime;

const string OperatorId = "linux-euid:42420";
var tempRoot = Path.Combine(Path.GetTempPath(), "sem-emergency-maintenance-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
var evidenceRoot = Path.Combine(tempRoot, "evidence");
Directory.CreateDirectory(evidenceRoot);
var verificationRoot = Path.Combine(Directory.GetCurrentDirectory(), "verification");
var checks = 0;

void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL " + label);
    Console.WriteLine("PASS " + label);
    checks++;
}

void Reject<TException>(Action action, string label) where TException : Exception
{
    try { action(); }
    catch (TException) { Check(true, label); return; }
    throw new Exception("FAIL accepted " + label);
}

async Task RejectAsync<TException>(Func<Task> action, string label) where TException : Exception
{
    try { await action(); }
    catch (TException) { Check(true, label); return; }
    throw new Exception("FAIL accepted " + label);
}

PasskeyEmergencyRecoveryMaintenance.Options InspectOptions(string target = "sole-admin") =>
    new(target, "Synthetic host recovery", false, null, null, null);

PasskeyEmergencyRecoveryMaintenance.Options ApplyOptions(string hash, string backupDirectory,
    string target = "sole-admin") => new(target, "Synthetic host recovery", true, target, hash, backupDirectory);

DeploymentSettings Settings(Store store) => new()
{
    PublicUrl = "https://sem-emergency.synthetic.invalid/",
    DataDirectory = store.DataDirectory
};

IConfiguration SyntheticConfiguration() => new ConfigurationBuilder().AddInMemoryCollection(
    new Dictionary<string, string?>
    {
        ["Smtp:Host"] = "smtp.synthetic.invalid",
        ["Smtp:From"] = "noreply@example.test"
    }).Build();

ServiceProvider ContextProvider(string databasePath)
{
    var services = new ServiceCollection();
    services.AddLogging();
    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
        ForeignKeys = true,
        DefaultTimeout = 1
    }.ToString();
    services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite(connectionString),
        ServiceLifetime.Scoped);
    services.AddIdentityCore<ApplicationUser>(options =>
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
        .AddEntityFrameworkStores<ApplicationDbContext>();
    return services.BuildServiceProvider();
}

async Task SetJournalModeDelete(string databasePath)
{
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
        ForeignKeys = true
    }.ToString());
    await connection.OpenAsync();
    await using (var checkpoint = connection.CreateCommand())
    {
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await checkpoint.ExecuteScalarAsync();
    }
    await using var journalMode = connection.CreateCommand();
    journalMode.CommandText = "PRAGMA journal_mode=DELETE;";
    var mode = Convert.ToString(await journalMode.ExecuteScalarAsync());
    if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Synthetic fixture could not establish DELETE journal mode.");
}

async Task SetJournalModeWalAndCheckpoint(string databasePath)
{
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
        ForeignKeys = true
    }.ToString());
    await connection.OpenAsync();
    await using (var journalMode = connection.CreateCommand())
    {
        journalMode.CommandText = "PRAGMA journal_mode=WAL;";
        var mode = Convert.ToString(await journalMode.ExecuteScalarAsync());
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Synthetic fixture could not establish WAL journal mode.");
    }
    await using (var update = connection.CreateCommand())
    {
        update.CommandText = "UPDATE AspNetUsers SET DisplayName = DisplayName || ' wal-fixture' WHERE Id = 'sole-admin';";
        await update.ExecuteNonQueryAsync();
    }
    await using (var checkpoint = connection.CreateCommand())
    {
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await checkpoint.ExecuteScalarAsync();
    }
}

Task CreateDataProtectionKeyRing(string keysDirectory)
{
    Directory.CreateDirectory(keysDirectory);
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDataProtection().SetApplicationName("SEM.ExternalTest")
        .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
    using var provider = services.BuildServiceProvider();
    _ = provider.GetRequiredService<IDataProtectionProvider>()
        .CreateProtector("SEM.SyntheticKeyRingCheck").Protect("synthetic-only");
    return Task.CompletedTask;
}

async Task<Store> CreateStore(bool createKeys = true, bool secondAdmin = false, bool seedPasskey = true)
{
    var dataDirectory = Path.Combine(tempRoot, "storage-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dataDirectory);
    var databasePath = Path.Combine(dataDirectory, "sem.db");
    var keysDirectory = Path.Combine(dataDirectory, "keys");
    if (createKeys) await CreateDataProtectionKeyRing(keysDirectory);

    using (var contextProvider = ContextProvider(databasePath))
    {
        await using var db = contextProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
        await db.Database.MigrateAsync();
        db.Users.Add(MakeUser("sole-admin", "sole-admin@example.test", isAdmin: true));
        if (secondAdmin) db.Users.Add(MakeUser("second-admin", "second-admin@example.test", isAdmin: true));
        await db.SaveChangesAsync();
        if (seedPasskey)
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "AspNetUserPasskeys" ("CredentialId", "UserId", "Data")
                VALUES (X'0102030405', 'sole-admin', {0})
                """, """{"AttestationObject":"01","ClientDataJson":"01","CreatedAt":"2024-01-01T00:00:00+00:00","IsBackedUp":false,"IsBackupEligible":false,"IsUserVerified":true,"Name":"synthetic-emergency-seed","PublicKey":"01","SignCount":0,"Transports":[]}""");
    }
    await SetJournalModeDelete(databasePath);
    return new(dataDirectory, databasePath, keysDirectory);
}

static ApplicationUser MakeUser(string id, string email, bool isAdmin) => new()
{
    Id = id,
    UserName = email,
    NormalizedUserName = email.ToUpperInvariant(),
    Email = email,
    NormalizedEmail = email.ToUpperInvariant(),
    EmailConfirmed = true,
    Enabled = true,
    IsAdmin = isAdmin,
    SecurityStamp = "synthetic-stamp-" + id,
    DisplayName = "Synthetic " + id
};

Dictionary<string, string> HashKeyFiles(string directory) => Directory.Exists(directory)
    ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .ToDictionary(path => Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'),
            PasskeyEmergencyRecoveryMaintenance.Hash, StringComparer.Ordinal)
    : new(StringComparer.Ordinal);

async Task<(string? Stamp, bool Enabled, bool Admin, string? Email, int PasskeyCount)> ReadAccount(Store store)
{
    using var contextProvider = ContextProvider(store.DatabasePath);
    await using var db = contextProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
    var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == "sole-admin");
    var passkeys = await db.Set<IdentityUserPasskey<string>>().AsNoTracking()
        .CountAsync(x => x.UserId == "sole-admin");
    return (user.SecurityStamp, user.Enabled, user.IsAdmin, user.Email, passkeys);
}

void ParseChecks()
{
    var inspection = PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason", "  lost all devices  "]);
    Check(!inspection.Apply && inspection.Target == "sole-admin" && inspection.Reason == "lost all devices",
        "inspection options trim reason and remain non-applying");

    var hash = new string('a', 64);
    var absoluteBackup = Path.GetFullPath(Path.Combine(evidenceRoot, "parse-backup"));
    var apply = PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason", "reviewed recovery", "--apply",
         "--confirm-target", "sole-admin", "--expected-sha256", hash, "--backup-directory", absoluteBackup]);
    Check(apply.Apply && apply.ConfirmTarget == apply.Target && apply.ExpectedSha256 == hash &&
          apply.BackupDirectory == absoluteBackup, "apply requires exact target, inspected hash, and absolute backup path");

    Reject<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "a", "--target-user-id", "b", "--reason", "duplicate target"]),
        "duplicate flags rejected");
    Reject<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason", "unknown flag", "--policy-file", "/tmp/policy"]),
        "unknown policy override flag rejected");
    Reject<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason", "unknown flag", "--allowed-effective-uid", "42"]),
        "effective UID cannot be overridden on the command line");
    Reject<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason"]), "missing value rejected");
    Reject<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason", "valid reason", "--reason", "duplicate reason"]),
        "duplicate option rejected");
    Reject<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason", "valid reason", "--apply", "--apply"]),
        "duplicate apply flag rejected");
    Reject<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason", "valid reason", "--apply", "--confirm-target", "someone-else",
         "--expected-sha256", hash, "--backup-directory", absoluteBackup]), "mismatched confirmation rejected");
    Reject<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason", "valid reason", "--apply", "--confirm-target", "sole-admin",
         "--expected-sha256", "not-a-sha256", "--backup-directory", absoluteBackup]), "malformed inspected hash rejected");
    Reject<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason", "valid reason", "--apply", "--confirm-target", "sole-admin",
         "--expected-sha256", hash, "--backup-directory", "relative-backup"]), "relative backup path rejected");
    Reject<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason", "valid reason", "--confirm-target", "sole-admin"]),
        "apply-only arguments rejected during inspection");
    Reject<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason", "x"]), "too-short reason rejected");
}

void PolicyChecks()
{
    Check(EmergencyHostOperatorPolicy.PolicyPath == "/etc/sem/emergency-recovery.json",
        "operator policy uses the fixed root-controlled path, not configuration override");
    var expected = Encoding.UTF8.GetBytes("{\"allowedEffectiveUid\":42420}");
    Check(EmergencyHostOperatorPolicy.Parse(expected, 42420) == OperatorId,
        "fixed host policy parses only the matching effective UID");
    Reject<InvalidOperationException>(() => EmergencyHostOperatorPolicy.Parse(expected, 42421),
        "policy refuses a different effective UID");
    Reject<InvalidOperationException>(() => EmergencyHostOperatorPolicy.Parse(
        Encoding.UTF8.GetBytes("{\"allowedEffectiveUid\":42420,\"extra\":true}"), 42420),
        "policy rejects unknown fields");
    Reject<InvalidOperationException>(() => EmergencyHostOperatorPolicy.Parse(
        Encoding.UTF8.GetBytes("{\"allowedEffectiveUid\":42420,\"allowedEffectiveUid\":42420}"), 42420),
        "policy rejects duplicate fields");
    Reject<InvalidOperationException>(() => EmergencyHostOperatorPolicy.Parse(
        Encoding.UTF8.GetBytes("{\"allowedEffectiveUid\":\"42420\"}"), 42420),
        "policy requires an unsigned numeric UID");
    Reject<InvalidOperationException>(() => EmergencyHostOperatorPolicy.Parse(
        Encoding.UTF8.GetBytes("[]"), 42420), "policy requires one exact object property");

    EmergencyHostOperatorPolicy.ValidateMetadata(0, (ushort)(0x4000 | 0x1ed), regular: false);
    Check(true, "root-owned read-only-by-group directory metadata accepted");
    EmergencyHostOperatorPolicy.ValidateMetadata(0, (ushort)(0x8000 | 0x180), regular: true);
    Check(true, "root-owned private regular-file metadata accepted");
    Reject<InvalidOperationException>(() => EmergencyHostOperatorPolicy.ValidateMetadata(
        1000, (ushort)(0x8000 | 0x180), regular: true), "non-root policy owner rejected");
    Reject<InvalidOperationException>(() => EmergencyHostOperatorPolicy.ValidateMetadata(
        0, (ushort)(0x4000 | 0x1fd), regular: false), "group-writable policy path rejected");
    Reject<InvalidOperationException>(() => EmergencyHostOperatorPolicy.ValidateMetadata(
        0, (ushort)(0x4000 | 0x1ef), regular: false), "other-writable policy path rejected");
    Reject<InvalidOperationException>(() => EmergencyHostOperatorPolicy.ValidateMetadata(
        0, (ushort)(0x8000 | 0x180), regular: false), "wrong policy filesystem object type rejected");
}

async Task LeaseChecks()
{
    if (!OperatingSystem.IsLinux())
    {
        Check(true, "Linux-only storage lease checks skipped on non-Linux host");
        return;
    }
    var directory = Path.Combine(tempRoot, "lease");
    Directory.CreateDirectory(directory);
    var lockPath = Path.Combine(directory, "sem.runtime.lock");
    var first = SemStorageRuntimeLease.Acquire(directory);
    Check(File.Exists(lockPath), "lease creates a persistent lock inode");
    Reject<InvalidOperationException>(() => SemStorageRuntimeLease.Acquire(directory),
        "second lease is excluded while first owner is active");
    first.Dispose();
    Check(File.Exists(lockPath), "disposing lease retains the lock inode");
    using (SemStorageRuntimeLease.Acquire(directory))
        Check(true, "disposing the first lease permits a subsequent owner");
    SemStorageRuntimeLease.RejectLinks(lockPath);
    Check(true, "ordinary storage lock path passes nonsymlink validation");

    var externalLockTarget = Path.Combine(tempRoot, "lock-target");
    await File.WriteAllTextAsync(externalLockTarget, "synthetic");
    File.Delete(lockPath);
    File.CreateSymbolicLink(lockPath, externalLockTarget);
    Reject<InvalidOperationException>(() => SemStorageRuntimeLease.Acquire(directory),
        "lease refuses a symlinked lock file");
    var linkedDirectory = Path.Combine(tempRoot, "storage-link");
    Directory.CreateSymbolicLink(linkedDirectory, directory);
    Reject<InvalidOperationException>(() => SemStorageRuntimeLease.Acquire(linkedDirectory),
        "lease refuses a symlinked storage directory");
}

async Task BackupChecks()
{
    var live = Path.Combine(tempRoot, "backup-live");
    var keys = Path.Combine(live, "keys");
    Directory.CreateDirectory(keys);
    Directory.CreateDirectory(Path.Combine(keys, "nested"));
    var database = Path.Combine(live, "sem.db");
    await File.WriteAllTextAsync(database, "synthetic database bytes only");
    await File.WriteAllTextAsync(Path.Combine(keys, "root-key.xml"), "synthetic-key-root");
    await File.WriteAllTextAsync(Path.Combine(keys, "nested", "child-key.xml"), "synthetic-key-child");
    var databaseHash = PasskeyEmergencyRecoveryMaintenance.Hash(database);
    var destination = Path.Combine(evidenceRoot, "verified-backup");
    var backupId = await PasskeyEmergencyRecoveryMaintenance.BackupAsync(
        live, destination, database, databaseHash, CancellationToken.None);
    using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(destination, "manifest.json"))))
    {
        Check(manifest.RootElement.GetProperty("backupId").GetString() == backupId,
            "backup manifest binds the returned backup identifier");
        var entries = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Check(entries.Length == 3, "backup manifest contains database and every recursive key file");
        var expectedBackupId = Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entries)));
        Check(backupId == expectedBackupId,
            "backup identifier is SHA-256 of the ordered database/key hash entries");
        foreach (var entry in entries)
        {
            var relative = entry.GetProperty("path").GetString()!;
            var expected = entry.GetProperty("sha256").GetString()!;
            var copied = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(copied) && PasskeyEmergencyRecoveryMaintenance.Hash(copied) == expected,
                "backup file hash verified: " + relative);
        }
        Check(entries.Single(x => x.GetProperty("path").GetString() == "sem.db")
                  .GetProperty("sha256").GetString() == databaseHash,
            "backup manifest records the inspected database SHA-256");
    }
    Check(backupId.Length == 64 && backupId.All(Uri.IsHexDigit) && backupId == backupId.ToLowerInvariant(),
        "backup identifier satisfies the 64-lowerhex database/service contract");
    if (OperatingSystem.IsLinux())
    {
        const UnixFileMode groupOther = (UnixFileMode)0x3f;
        Check((File.GetUnixFileMode(destination) & groupOther) == 0,
            "backup directory has no group/other permissions");
        Check((File.GetUnixFileMode(Path.Combine(destination, "keys")) & groupOther) == 0,
            "backup key directory has no group/other permissions");
        Check((File.GetUnixFileMode(Path.Combine(destination, "sem.db")) & groupOther) == 0 &&
              (File.GetUnixFileMode(Path.Combine(destination, "manifest.json")) & groupOther) == 0,
            "backup database and manifest files have private permissions");
    }
    var manifestHash = PasskeyEmergencyRecoveryMaintenance.Hash(Path.Combine(destination, "manifest.json"));
    await RejectAsync<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.BackupAsync(
        live, destination, database, databaseHash, CancellationToken.None),
        "backup never overwrites an existing destination");
    Check(PasskeyEmergencyRecoveryMaintenance.Hash(Path.Combine(destination, "manifest.json")) == manifestHash,
        "failed overwrite attempt leaves existing evidence unchanged");
    await RejectAsync<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.BackupAsync(
        live, Path.Combine(live, "inside-backup"), database, databaseHash, CancellationToken.None),
        "backup destination must be outside live storage");

    if (OperatingSystem.IsLinux())
    {
        var linkedDestination = Path.Combine(evidenceRoot, "backup-destination-link");
        Directory.CreateSymbolicLink(linkedDestination, destination);
        await RejectAsync<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.BackupAsync(
            live, linkedDestination, database, databaseHash, CancellationToken.None),
            "backup refuses a symlinked destination");

        var outsideKey = Path.Combine(tempRoot, "outside-key.xml");
        await File.WriteAllTextAsync(outsideKey, "synthetic external key");
        var keyLink = Path.Combine(keys, "linked-key.xml");
        File.CreateSymbolicLink(keyLink, outsideKey);
        await RejectAsync<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.BackupAsync(
            live, Path.Combine(evidenceRoot, "backup-with-linked-key"), database, databaseHash, CancellationToken.None),
            "backup refuses symlinks anywhere in the key-ring tree");
    }

    var missingKeys = Path.Combine(tempRoot, "missing-key-live");
    Directory.CreateDirectory(missingKeys);
    var missingDatabase = Path.Combine(missingKeys, "sem.db");
    await File.WriteAllTextAsync(missingDatabase, "synthetic db");
    var missingDestination = Path.Combine(evidenceRoot, "backup-missing-keys");
    await RejectAsync<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.BackupAsync(
        missingKeys, missingDestination, missingDatabase,
        PasskeyEmergencyRecoveryMaintenance.Hash(missingDatabase), CancellationToken.None),
        "backup fails closed when the key directory is missing");
    Check(!Directory.Exists(missingDestination), "missing-key failure creates no partial backup directory");

    var emptyKeyLive = Path.Combine(tempRoot, "empty-key-live");
    Directory.CreateDirectory(Path.Combine(emptyKeyLive, "keys"));
    var emptyKeyDatabase = Path.Combine(emptyKeyLive, "sem.db");
    await File.WriteAllTextAsync(emptyKeyDatabase, "synthetic db");
    await RejectAsync<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.BackupAsync(
        emptyKeyLive, Path.Combine(evidenceRoot, "backup-empty-keys"), emptyKeyDatabase,
        PasskeyEmergencyRecoveryMaintenance.Hash(emptyKeyDatabase), CancellationToken.None),
        "backup fails closed when the key directory is incomplete/empty");
}

async Task ProtectedPathChecks()
{
    if (!OperatingSystem.IsLinux())
    {
        Check(true, "Linux-only protected-directory checks skipped on non-Linux host");
        return;
    }
    var safeDirectory = Path.Combine(tempRoot, "protected-directory");
    Directory.CreateDirectory(safeDirectory);
    EmergencyHostOperatorPolicy.RequireProtectedDirectory(safeDirectory);
    Check(true, "current-owned private directory passes protected-path validation");

    var sharedDirectory = Path.Combine(tempRoot, "direct-shared-parent");
    Directory.CreateDirectory(sharedDirectory);
    var sharedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.StickyBit;
    File.SetUnixFileMode(sharedDirectory, sharedMode);
    try
    {
        Check((File.GetUnixFileMode(sharedDirectory) & UnixFileMode.StickyBit) != 0,
            "direct shared-parent fixture actually has the sticky bit set");
        Reject<InvalidOperationException>(() => EmergencyHostOperatorPolicy.RequireProtectedDirectory(sharedDirectory),
            "direct sticky shared destination parent is still rejected");
    }
    finally { File.SetUnixFileMode(sharedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

    var writableAncestor = Path.Combine(tempRoot, "writable-ancestor");
    var protectedChild = Path.Combine(writableAncestor, "private-child");
    Directory.CreateDirectory(protectedChild);
    File.SetUnixFileMode(writableAncestor, UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
    try
    {
        Reject<InvalidOperationException>(() => EmergencyHostOperatorPolicy.RequireProtectedDirectory(protectedChild),
            "non-sticky group-writable ancestor is rejected");
    }
    finally { File.SetUnixFileMode(writableAncestor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

    var live = Path.Combine(tempRoot, "protected-backup-live");
    var keys = Path.Combine(live, "keys");
    Directory.CreateDirectory(keys);
    var database = Path.Combine(live, "sem.db");
    await File.WriteAllTextAsync(database, "synthetic database");
    await File.WriteAllTextAsync(Path.Combine(keys, "key.xml"), "synthetic key");
    var unsafeParent = Path.Combine(evidenceRoot, "group-writable-parent");
    Directory.CreateDirectory(unsafeParent);
    File.SetUnixFileMode(unsafeParent, UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
    var unsafeDestination = Path.Combine(unsafeParent, "backup");
    try
    {
        await RejectAsync<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.BackupAsync(
            live, unsafeDestination, database, PasskeyEmergencyRecoveryMaintenance.Hash(database), CancellationToken.None),
            "backup refuses a group-writable immediate destination parent");
        Check(!Directory.Exists(unsafeDestination), "unsafe destination-parent rejection creates no backup directory");
    }
    finally { File.SetUnixFileMode(unsafeParent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
}

async Task FifoChecks()
{
    if (!OperatingSystem.IsLinux())
    {
        Check(true, "Linux-only FIFO checks skipped on non-Linux host");
        return;
    }
    var live = Path.Combine(tempRoot, "fifo-live");
    var keys = Path.Combine(live, "keys");
    Directory.CreateDirectory(keys);
    var database = Path.Combine(live, "sem.db");
    await File.WriteAllTextAsync(database, "synthetic database");
    var fifo = Path.Combine(keys, "streaming-key.xml");
    if (NativeFifo.Mkfifo(fifo, 0x180) != 0)
        throw new IOException("mkfifo failed with errno " + Marshal.GetLastPInvokeError());
    Reject<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.Hash(fifo),
        "regular-file guard rejects a FIFO before opening or blocking on it");
    var destination = Path.Combine(evidenceRoot, "fifo-key-backup");
    await RejectAsync<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.BackupAsync(
        live, destination, database, PasskeyEmergencyRecoveryMaintenance.Hash(database), CancellationToken.None),
        "backup rejects a FIFO inside the key-ring tree before copying files");
    Check(!Directory.Exists(destination), "FIFO key rejection creates no incomplete backup");
}

async Task InspectionChecks()
{
    var store = await CreateStore();
    var beforeHash = PasskeyEmergencyRecoveryMaintenance.Hash(store.DatabasePath);
    var beforeKeys = HashKeyFiles(store.KeysDirectory);
    var beforeAccount = await ReadAccount(store);
    var result = await PasskeyEmergencyRecoveryMaintenance.ExecuteAsync(
        InspectOptions(), OperatorId, SyntheticConfiguration(), Settings(store));
    var afterHash = PasskeyEmergencyRecoveryMaintenance.Hash(store.DatabasePath);
    var afterKeys = HashKeyFiles(store.KeysDirectory);
    var afterAccount = await ReadAccount(store);
    Check(result.Status == "inspection-only" && result.DatabaseSha256 == beforeHash,
        "inspection reports the exact database hash without applying recovery");
    Check(beforeHash == afterHash && beforeAccount == afterAccount,
        "read-only inspection changes no database rows, passkeys, stamp, or account state");
    Check(beforeKeys.Count == afterKeys.Count && beforeKeys.All(pair => afterKeys.TryGetValue(pair.Key, out var hash) && hash == pair.Value),
        "read-only inspection leaves the data-protection key ring unchanged");
}

async Task FailureChecks()
{
    var stale = await CreateStore(createKeys: true);
    var staleHash = PasskeyEmergencyRecoveryMaintenance.Hash(stale.DatabasePath);
    using (var contextProvider = ContextProvider(stale.DatabasePath))
    {
        await using var db = contextProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE AspNetUsers SET DisplayName = 'changed after inspection' WHERE Id = 'sole-admin'");
    }
    var staleBackup = Path.Combine(evidenceRoot, "stale-hash-backup");
    await RejectAsync<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.ExecuteAsync(
        ApplyOptions(staleHash, staleBackup), OperatorId, SyntheticConfiguration(), Settings(stale)),
        "apply refuses a database whose inspected hash is stale");
    Check(!Directory.Exists(staleBackup), "stale-hash failure creates no backup or mail operation");

    var schema = await CreateStore(createKeys: false);
    await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = schema.DatabasePath, Pooling = false }.ToString()))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE SyntheticUnexpectedTable (Id TEXT PRIMARY KEY)";
        await command.ExecuteNonQueryAsync();
    }
    await RejectAsync<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.ExecuteAsync(
        InspectOptions(), OperatorId, SyntheticConfiguration(), Settings(schema)),
        "inspection refuses a schema outside the exact migration chain");

    var wal = await CreateStore(createKeys: false);
    await SetJournalModeWalAndCheckpoint(wal.DatabasePath);
    await RejectAsync<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.ExecuteAsync(
        InspectOptions(), OperatorId, SyntheticConfiguration(), Settings(wal)),
        "inspection refuses real WAL-mode storage even after fixture checkpointing");

    var multipleAdmins = await CreateStore(createKeys: false, secondAdmin: true);
    await RejectAsync<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.ExecuteAsync(
        InspectOptions(), OperatorId, SyntheticConfiguration(), Settings(multipleAdmins)),
        "inspection refuses a target when another enabled confirmed global administrator exists");

    var missingKeys = await CreateStore(createKeys: false);
    var missingBackup = Path.Combine(evidenceRoot, "apply-with-missing-keys");
    var missingHash = PasskeyEmergencyRecoveryMaintenance.Hash(missingKeys.DatabasePath);
    await RejectAsync<InvalidOperationException>(() => PasskeyEmergencyRecoveryMaintenance.ExecuteAsync(
        ApplyOptions(missingHash, missingBackup), OperatorId, SyntheticConfiguration(), Settings(missingKeys)),
        "apply refuses to prepare mail without an existing data-protection key backup");
    Check(!Directory.Exists(missingBackup), "missing-key apply creates no partial backup or recovery state");

    var invalidActor = await CreateStore(createKeys: false);
    await RejectAsync<UnauthorizedAccessException>(() => PasskeyEmergencyRecoveryMaintenance.ExecuteAsync(
        InspectOptions(), "application-admin:sole-admin", SyntheticConfiguration(), Settings(invalidActor)),
        "application administrator identity cannot substitute for the Linux host operator");
}

async Task ApplyChecks()
{
    var store = await CreateStore(createKeys: true, secondAdmin: false, seedPasskey: true);
    var beforeHash = PasskeyEmergencyRecoveryMaintenance.Hash(store.DatabasePath);
    var before = await ReadAccount(store);
    var backupDirectory = Path.Combine(evidenceRoot, "end-to-end-backup");
    var result = await PasskeyEmergencyRecoveryMaintenance.ExecuteAsync(
        ApplyOptions(beforeHash, backupDirectory), OperatorId, SyntheticConfiguration(), Settings(store));
    Check(result.Status == "mail-prepared-no-reset" && result.DatabaseSha256 == beforeHash &&
          !string.IsNullOrWhiteSpace(result.OperationId) && !string.IsNullOrWhiteSpace(result.BackupId),
        "synthetic apply prepares recovery mail only after database/key backup verification");

    using var contextProvider = ContextProvider(store.DatabasePath);
    await using (var db = contextProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext())
    {
        var target = await db.Users.AsNoTracking().SingleAsync(x => x.Id == "sole-admin");
        var operation = await db.PasskeyRecoveryOperations.AsNoTracking().SingleAsync(x => x.Id == result.OperationId);
        var mail = await db.PasskeyRecoveryMails.AsNoTracking().SingleAsync(x => x.OperationId == operation.Id && x.Kind == "RecoveryLink");
        var audit = await db.PasskeyRecoveryAudits.AsNoTracking().SingleAsync(x => x.OperationId == operation.Id);
        var passkeys = await db.Set<IdentityUserPasskey<string>>().AsNoTracking().CountAsync(x => x.UserId == "sole-admin");
        Check(operation.Status == "HostPrepared" && operation.RecoveryMode == "HostEmergency" &&
              operation.ActorType == "HostOperator" && operation.ActorId == OperatorId &&
              operation.AdminId is null && operation.BackupId is { Length: 64 } operationBackupId &&
              operationBackupId.All(Uri.IsHexDigit) && operationBackupId == result.BackupId,
            "prepared operation binds sole host actor and verified 64-hex backup evidence");
        Check(target.Enabled && target.SecurityStamp == before.Stamp && !operation.DisabledForRecovery && passkeys == before.PasskeyCount,
            "emergency preparation does not disable account, rotate stamp, or delete passkeys");
        Check(mail.Status == "Pending" && !string.IsNullOrWhiteSpace(mail.ProtectedPayload) &&
              audit.Action == "RecoveryHostPrepared" && audit.ActorType == "HostOperator" && audit.AdminId is null,
            "only protected recovery mail and host-prepared audit are queued; no SMTP worker runs");
    }

    var reconciliation = await PasskeyEmergencyRecoveryMaintenance.ExecuteAsync(
        InspectOptions(), OperatorId, SyntheticConfiguration(), Settings(store));
    Check(reconciliation.Status == "inspection-only" &&
          reconciliation.OperationId == result.OperationId &&
          reconciliation.BackupId == result.BackupId &&
          reconciliation.OperationStatus == "HostPrepared" &&
          reconciliation.ExpiresAt == result.ExpiresAt,
        "read-only rerun projects the existing operation, backup, expiry, and status for reconciliation");

    Check(result.BackupId is { Length: 64 } endToEndBackupId && endToEndBackupId.All(Uri.IsHexDigit),
        "end-to-end backup identifier is a stable SHA-256-shaped value");
    using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backupDirectory, "manifest.json"))))
    {
        var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        var databaseEntry = files.Single(x => x.GetProperty("path").GetString() == "sem.db");
        Check(databaseEntry.GetProperty("sha256").GetString() == beforeHash &&
              PasskeyEmergencyRecoveryMaintenance.Hash(Path.Combine(backupDirectory, "sem.db")) == beforeHash,
            "end-to-end evidence contains the verified pre-recovery database snapshot");
        Check(files.Any(x => x.GetProperty("path").GetString()!.StartsWith("keys/", StringComparison.Ordinal)),
            "end-to-end evidence includes the existing key ring");
        Check(manifest.RootElement.GetProperty("backupId").GetString() == result.BackupId,
            "manifest and queued operation use the same backup identifier");
    }
    if (OperatingSystem.IsLinux())
        Check((File.GetUnixFileMode(backupDirectory) & (UnixFileMode)0x3f) == 0,
            "end-to-end backup evidence directory is private");
}

async Task EntrypointChecks()
{
    Reject<ValidationException>(() => PasskeyEmergencyRecoveryMaintenance.Parse(
        ["--target-user-id", "sole-admin", "--reason", "no override", "--policy-path", "/tmp/alternate-policy"]),
        "alternate policy path cannot be supplied as an argument");
    if (OperatingSystem.IsLinux() && !File.Exists(EmergencyHostOperatorPolicy.PolicyPath))
    {
        var exitCode = await PasskeyEmergencyRecoveryMaintenance.RunAsync(
            ["--target-user-id", "sole-admin", "--reason", "synthetic fixed-policy check"]);
        Check(exitCode == 1, "real offline entrypoint fails closed when the fixed root policy is unavailable");
    }
    else
        Check(true, "real offline entrypoint invocation skipped because fixed-policy conditions are not safely testable");
}

try
{
    ParseChecks();
    PolicyChecks();
    await LeaseChecks();
    await BackupChecks();
    await ProtectedPathChecks();
    await FifoChecks();
    if (OperatingSystem.IsLinux())
    {
        await InspectionChecks();
        await FailureChecks();
        await ApplyChecks();
    }
    else Check(true, "Linux-only maintenance integration checks skipped on non-Linux host");
    await EntrypointChecks();
    Directory.CreateDirectory(verificationRoot);
    var completedUtc = DateTimeOffset.UtcNow;
    var summaryPath = Path.Combine(verificationRoot,
        "Sem.PasskeyEmergencyMaintenanceChecks-" + completedUtc.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N") + ".txt");
    await File.WriteAllTextAsync(summaryPath,
        $"status=passed\nchecks={checks}\ncompletedUtc={completedUtc:O}\n");
    Console.WriteLine($"Passkey emergency maintenance checks passed: {checks}");
}
finally
{
    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
}

sealed record Store(string DataDirectory, string DatabasePath, string KeysDirectory);

static class NativeFifo
{
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    internal static extern int Mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
