using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sem.Web.Runtime;

namespace Sem.Web.Data;

/// <summary>Explicit, offline, host-authorized preparation. No HTTP host, migration or mail worker.</summary>
public static class PasskeyEmergencyRecoveryMaintenance
{
    internal sealed record Options(string Target, string Reason, bool Apply, string? ConfirmTarget,
        string? ExpectedSha256, string? BackupDirectory);
    internal sealed record Result(string Status, string DatabaseSha256, string? BackupId = null,
        string? OperationId = null, DateTimeOffset? ExpiresAt = null, string? MaskedRecipient = null, string? OperationStatus = null);

    public static async Task<int> RunAsync(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var options = Parse(args);
            var actor = EmergencyHostOperatorPolicy.RequireOperator();
            var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
            var settings = configuration.GetSection("Sem").Get<DeploymentSettings>() ?? new();
            settings.Validate();
            var result = await ExecuteAsync(options, actor, configuration, settings, timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Do not print addresses, credential material, filesystem/key contents or SQL parameters.
            Console.Error.WriteLine("Emergency command did not complete (" + ex.GetType().Name +
                "). If apply was requested, mail preparation MAY have committed. Do not blindly retry; " +
                "inspect the same target read-only and reconcile OperationId/OperationStatus with the backup manifest. " +
                "No automatic restore was attempted; preserve all evidence.");
            return 1;
        }
    }

    internal static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var apply = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--apply")
            {
                if (apply) throw new ValidationException("Duplicate apply flag.");
                apply = true; continue;
            }
            var key = args[i];
            if (key is not ("--target-user-id" or "--reason" or "--confirm-target" or
                "--expected-sha256" or "--backup-directory") || i + 1 >= args.Length ||
                args[i + 1].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(key, args[++i]))
                throw new ValidationException("Unknown, duplicate, or missing argument.");
        }
        var target = values.GetValueOrDefault("--target-user-id") ?? "";
        var reason = (values.GetValueOrDefault("--reason") ?? "").Trim();
        if (target.Length is < 1 or > 128 || target.Any(char.IsControl) || string.IsNullOrWhiteSpace(target) ||
            reason.Length is < 3 or > 500 || reason.Any(char.IsControl))
            throw new ValidationException("Target/reason is invalid.");
        var confirm = values.GetValueOrDefault("--confirm-target");
        var hash = values.GetValueOrDefault("--expected-sha256");
        var backup = values.GetValueOrDefault("--backup-directory");
        if (apply)
        {
            if (confirm != target || hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit) ||
                backup is null || !Path.IsPathFullyQualified(backup))
                throw new ValidationException("Apply requires the inspected hash, exact target confirmation, and a new absolute backup directory.");
        }
        else if (confirm is not null || hash is not null || backup is not null)
            throw new ValidationException("Apply-only arguments are not accepted during inspection.");
        return new(target, reason, apply, confirm, hash, backup);
    }

    internal static async Task<Result> ExecuteAsync(Options options, string authorizedOperator,
        IConfiguration configuration, DeploymentSettings settings, CancellationToken ct = default)
    {
        settings.Validate();
        if (!authorizedOperator.StartsWith("linux-euid:", StringComparison.Ordinal) || authorizedOperator.Length > 64)
            throw new UnauthorizedAccessException();
        var directory = Path.GetFullPath(settings.DataDirectory);
        using var lease = SemStorageRuntimeLease.Acquire(directory);
        var path = Path.Combine(directory, "sem.db");
        SemStorageRuntimeLease.RejectLinks(path);
        if (!File.Exists(path)) throw new InvalidOperationException("Existing database required; no database is created.");
        EmergencyHostOperatorPolicy.RequireRegularFile(path);
        EmergencyHostOperatorPolicy.RequireProtectedDirectory(directory);
        CheckSidecars(path);
        var initialHash = Hash(path);
        if (options.Apply && !initialHash.Equals(options.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Database differs from inspection.");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = options.Apply ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false, ForeignKeys = true, DefaultTimeout = 1
        }.ToString();
        string expectedStamp;
        string? backupId = null;
        Result? inspection = null;
        // Immediate apply transaction excludes SQLite writers while verifying and copying the backup.
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(ct);
            using var transaction = connection.BeginTransaction(deferred: !options.Apply);
            CheckSidecars(path);
            if (Hash(path) != initialHash) throw new InvalidOperationException("Storage changed while acquiring SQLite lock.");
            var journal = Convert.ToString(await Scalar(connection, transaction, "PRAGMA journal_mode;", ct));
            if (journal is not ("delete" or "truncate" or "persist"))
                throw new InvalidOperationException("Use stopped, checkpointed non-WAL storage; this command does not checkpoint or change journal mode.");
            if (Convert.ToString(await Scalar(connection, transaction, "PRAGMA integrity_check;", ct)) != "ok")
                throw new InvalidOperationException("Database integrity check failed.");
            var inspectionServices = new ServiceCollection();
            inspectionServices.AddLogging();
            inspectionServices.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connection));
            inspectionServices.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
            await using var inspectionProvider = inspectionServices.BuildServiceProvider();
            var db = inspectionProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.UseTransaction(transaction);
            await ValidateSchemaAsync(db, connection, transaction, ct);
            var target = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == options.Target, ct)
                ?? throw new ValidationException("Target is absent.");
            expectedStamp = target.SecurityStamp ?? throw new InvalidOperationException("Target stamp is missing.");
            if (!target.EmailConfirmed || !target.IsAdmin || string.IsNullOrWhiteSpace(target.Email) ||
                await db.Users.AnyAsync(x => x.Id != target.Id && x.IsAdmin && x.Enabled && x.EmailConfirmed, ct))
                throw new ValidationException("Target is not the sole eligible global administrator.");
            // Service independently rechecks disabled-restart provenance and binding before any write.
            if (!target.Enabled)
            {
                var emailHash = SemEndpoints.Hash(new UpperInvariantLookupNormalizer().NormalizeEmail(target.Email) ?? "");
                if (!await db.PasskeyRecoveryOperations.AsNoTracking().AnyAsync(x => x.UserId == target.Id &&
                    x.RecoveryMode == "HostEmergency" && x.DisabledForRecovery && x.SecurityStamp == expectedStamp &&
                    x.EmailHash == emailHash && (x.Status == "Pending" || x.Status == "EmailConfirmed" || x.Status == "Expired"), ct))
                    throw new ValidationException("Disabled target was not disabled by matching host recovery.");
            }
            if (!options.Apply)
            {
                var existing = await db.PasskeyRecoveryOperations.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == target.Id, ct);
                inspection = new("inspection-only", initialHash, existing?.BackupId, existing?.Id,
                    existing is null ? null : DateTimeOffset.FromUnixTimeSeconds(existing.ExpiresAt),
                    null, existing?.Status);
            }
            if (options.Apply)
                backupId = await BackupAsync(directory, options.BackupDirectory!, path, initialHash, ct);
            if (Hash(path) != initialHash) throw new InvalidOperationException("Storage changed during verification.");
            transaction.Rollback(); // No user rows or schema are changed by inspection/backup.
        }
        if (!options.Apply) return inspection!;
        // Only now may the dedicated host service queue a protected mail capability.
        // No HTTP context, impersonated application admin, SMTP transport or background worker is created.
        var services = new ServiceCollection();
        services.AddLogging(o => o.SetMinimumLevel(LogLevel.Error));
        services.AddSingleton(configuration); services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(settings); services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor(); services.AddAuthentication().AddIdentityCookies();
        services.AddDataProtection().SetApplicationName("SEM.ExternalTest")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys"))).DisableAutomaticKeyGeneration();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connectionString));
        services.AddIdentityCore<ApplicationUser>(o =>
        {
            o.SignIn.RequireConfirmedAccount = true; o.User.RequireUniqueEmail = true;
            o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        }).AddEntityFrameworkStores<ApplicationDbContext>().AddSignInManager().AddDefaultTokenProviders();
        services.AddSingleton<NotificationService>();
        services.AddScoped<IPasskeyRecoveryIdentity, NativePasskeyRecoveryIdentity>();
        services.AddScoped<PasskeyRecoveryService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<PasskeyRecoveryService>()
            .PrepareHostEmergencyAsync(options.Target, options.Reason, authorizedOperator, backupId!, expectedStamp, ct);
        return new("mail-prepared-no-reset", initialHash, backupId, result.OperationId, result.ExpiresAt, result.MaskedRecipient);
    }

    internal static async Task<string> BackupAsync(string directory, string destination, string database,
        string expectedDatabaseHash, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Emergency backups require Linux.");
        destination = Path.GetFullPath(destination);
        SemStorageRuntimeLease.RejectLinks(destination);
        EmergencyHostOperatorPolicy.RequireProtectedDirectory(Path.GetDirectoryName(destination)!);
        if (destination == directory || destination.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Backup must be outside live storage.");
        var keys = Path.Combine(directory, "keys");
        SemStorageRuntimeLease.RejectLinks(keys);
        if (!Directory.Exists(keys)) throw new InvalidOperationException("Existing Data Protection keys are required.");
        EmergencyHostOperatorPolicy.RequireProtectedDirectory(keys);
        var sources = CollectKeyFiles(keys);
        if (sources.Length == 0) throw new InvalidOperationException("Existing Data Protection keys are required.");
        // mkdir is exclusive; Directory.CreateDirectory would silently reuse an existing destination.
        if (Mkdir(destination, 0x1c0) != 0) throw new InvalidOperationException("Backup directory must be new and its parent must exist.");
        
        var entries = new List<object>();
        await CopyVerified(database, Path.Combine(destination, "sem.db"), expectedDatabaseHash, ct);
        entries.Add(new { path = "sem.db", sha256 = expectedDatabaseHash });
        Directory.CreateDirectory(Path.Combine(destination, "keys"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var keyHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var relative = Path.GetRelativePath(keys, source);
            var hash = Hash(source); keyHashes[relative] = hash;
            var output = Path.Combine(destination, "keys", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await CopyVerified(source, output, hash, ct);
            entries.Add(new { path = "keys/" + relative.Replace(Path.DirectorySeparatorChar, '/'), sha256 = hash });
        }
        EmergencyHostOperatorPolicy.RequireProtectedDirectory(Path.GetDirectoryName(destination)!);
        SemStorageRuntimeLease.RejectLinks(destination);
        var after = CollectKeyFiles(keys).Select(x => Path.GetRelativePath(keys, x)).ToArray();
        if (!after.SequenceEqual(keyHashes.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            keyHashes.Any(x => Hash(Path.Combine(keys, x.Key)) != x.Value) || Hash(database) != expectedDatabaseHash)
            throw new InvalidOperationException("Source changed while backing up.");
        // Backup identifier binds the recorded database/key-file digests, not an arbitrary operator label.
        var id = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entries)));
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { backupId = id, createdAt = DateTimeOffset.UtcNow, files = entries });
        await using (var output = NewPrivateFile(Path.Combine(destination, "manifest.json")))
        { await output.WriteAsync(manifest, ct); output.Flush(true); }
        return id;
    }
    private static string[] CollectKeyFiles(string directory)
    {
        var files = new List<string>();
        void Visit(string current)
        {
            SemStorageRuntimeLease.RejectLinks(current);
            foreach (var path in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
            {
                SemStorageRuntimeLease.RejectLinks(path);
                if (Directory.Exists(path)) Visit(path);
                else { EmergencyHostOperatorPolicy.RequireRegularFile(path); files.Add(path); }
            }
        }
        Visit(directory); return files.Order(StringComparer.Ordinal).ToArray();
    }
    private static async Task CopyVerified(string source, string destination, string expectedHash, CancellationToken ct)
    {
        SemStorageRuntimeLease.RejectLinks(source);
        EmergencyHostOperatorPolicy.RequireRegularFile(source);
        await using var input = File.OpenRead(source);
        await using (var output = NewPrivateFile(destination))
        { await input.CopyToAsync(output, ct); output.Flush(true); }
        if (Hash(destination) != expectedHash || Hash(source) != expectedHash)
            throw new InvalidOperationException("Backup hash verification failed.");
    }
    private static FileStream NewPrivateFile(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Private backups require Linux.");
        return new(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }
    internal static string Hash(string path)
    { EmergencyHostOperatorPolicy.RequireRegularFile(path); using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static void CheckSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = path + suffix; SemStorageRuntimeLease.RejectLinks(sidecar);
            if (File.Exists(sidecar) && new FileInfo(sidecar).Length > 0)
                throw new InvalidOperationException("Pending SQLite sidecar; stop/checkpoint using approved maintenance first.");
        }
    }
    private static async Task<object?> Scalar(SqliteConnection connection, SqliteTransaction tx, string sql, CancellationToken ct)
    { using var cmd = connection.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; return await cmd.ExecuteScalarAsync(ct); }
    private static async Task ValidateSchemaAsync(ApplicationDbContext db, SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        if (db.Database.HasPendingModelChanges()) throw new InvalidOperationException("Unmigrated application model.");
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        var known = db.Database.GetMigrations().ToArray();
        if (!applied.SequenceEqual(known, StringComparer.Ordinal))
            throw new InvalidOperationException("Fully migrated exact current schema required; no migrations are applied by this command.");
        await using var reference = new SqliteConnection("Data Source=:memory:"); await reference.OpenAsync(ct);
        using (var create = reference.CreateCommand())
        {
            create.CommandText = db.GetService<IMigrator>().GenerateScript(Migration.InitialDatabase, known[^1], MigrationsSqlGenerationOptions.NoTransactions);
            await create.ExecuteNonQueryAsync(ct);
        }
        async Task<string[]> Read(SqliteConnection c, SqliteTransaction? t)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = t;
            cmd.CommandText = "SELECT type || '|' || name || '|' || tbl_name || '|' || coalesce(sql,'') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsLock' ORDER BY type,name;";
            var rows = new List<string>(); using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(reader.GetString(0).Replace("\r\n", "\n", StringComparison.Ordinal));
            return rows.ToArray();
        }
        var actualSchema = await Read(connection, tx);
        var referenceSchema = await Read(reference, null);
        if (!actualSchema.SequenceEqual(referenceSchema, StringComparer.Ordinal))
            throw new InvalidOperationException("Database schema does not match the migration chain.");
        using var foreign = connection.CreateCommand(); foreign.Transaction = tx; foreign.CommandText = "PRAGMA foreign_key_check;";
        using var violations = await foreign.ExecuteReaderAsync(ct);
        if (await violations.ReadAsync(ct)) throw new InvalidOperationException("Foreign key integrity check failed.");
    }
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, uint mode);
}
