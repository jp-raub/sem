using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sem.Web.Data;

/// <summary>Explicit offline maintenance only. Never invoked by web startup or migrations.</summary>
public static class LegacyEventResetMaintenance
{
    public sealed record Options(string Database, bool Apply = false, string? ExpectedSha256 = null,
        long? ExpectedEvents = null, long? ExpectedParticipations = null, string? Backup = null);
    public sealed record Report(string Status, long Events, long Participations, string DatabaseSha256,
        string? BackupSha256 = null);
    private static readonly string[] SupportedMigrations =
        [SqliteStorageInitializer.BaselineId, "20261012093941_AddPasskeyAdditionProof"];

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            bool apply = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--apply")
                {
                    if (apply) throw new InvalidOperationException("Duplicate --apply.");
                    apply = true;
                }
                else
                {
                    if (args[i] is not ("--database" or "--expected-sha256" or "--expected-events"
                        or "--expected-participations" or "--backup") || i + 1 == args.Length ||
                        args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unknown option or missing value.");
                    var key = args[i++];
                    if (!values.TryAdd(key, args[i])) throw new InvalidOperationException("Duplicate option.");
                }
            }
            if (!values.TryGetValue("--database", out var database))
                throw new InvalidOperationException("--database is required. Default mode is read-only inspection.");
            long? Count(string key) => values.TryGetValue(key, out var value)
                ? long.TryParse(value, out var number) && number >= 0 ? number
                    : throw new InvalidOperationException("Expected counts must be nonnegative integers.")
                : null;
            var report = await ExecuteAsync(new(database, apply, values.GetValueOrDefault("--expected-sha256"),
                Count("--expected-events"), Count("--expected-participations"), values.GetValueOrDefault("--backup")));
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SqliteException or IOException
                                  or UnauthorizedAccessException or ArgumentException)
        {
            // Do not print DB paths, raw rows, credentials or provider exception details.
            Console.Error.WriteLine(ex is InvalidOperationException
                ? ex.Message : "Maintenance failed. Keep the original database and backup; inspect offline.");
            return 1;
        }
    }

    public static async Task<Report> ExecuteAsync(Options options, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(options.Database);
        if (!Path.IsPathFullyQualified(options.Database) || !File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("An existing absolute, non-symlink database path is required.");
        if (options.Apply && (options.ExpectedSha256 is null ||
            !Regex.IsMatch(options.ExpectedSha256, "^[a-fA-F0-9]{64}$") ||
            options.ExpectedEvents is null or < 0 || options.ExpectedParticipations is null or < 0 ||
            options.Backup is null || !Path.IsPathFullyQualified(options.Backup)))
            throw new InvalidOperationException("Apply requires the inspected SHA-256, both counts, and a new absolute backup path.");
        if (!options.Apply && (options.Backup is not null || options.ExpectedSha256 is not null ||
            options.ExpectedEvents is not null || options.ExpectedParticipations is not null))
            throw new InvalidOperationException("Inspection accepts only --database; apply options require --apply.");
        var backupPath = options.Backup is null ? null : Path.GetFullPath(options.Backup);
        if (backupPath == path || (backupPath is not null && File.Exists(backupPath)))
            throw new InvalidOperationException("Backup must be a new file, never the database or an existing backup.");
        CheckSidecars(path);
        var originalHash = Hash(path);
        if (options.Apply && !originalHash.Equals(options.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Database hash changed; inspect again before applying.");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = options.Apply ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false, DefaultTimeout = 1, ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(ct);
        // Deferred inspection is read-only; immediate apply excludes competing writers.
        using var transaction = connection.BeginTransaction(deferred: !options.Apply);
        CheckSidecars(path);
        if (Hash(path) != originalHash)
            throw new InvalidOperationException("Database changed while acquiring the maintenance lock.");
        var journal = Convert.ToString(await ScalarAsync(connection, transaction, "PRAGMA journal_mode;", ct));
        if (journal is not ("delete" or "truncate" or "persist"))
            throw new InvalidOperationException("Use a stopped, checkpointed non-WAL copy for this maintenance operation.");
        await ValidateSchemaAsync(connection, transaction, ct);
        await CheckIntegrityAsync(connection, transaction, ct);
        var orphans = Convert.ToInt64(await ScalarAsync(connection, transaction,
            """SELECT COUNT(*) FROM "Participations" p LEFT JOIN "Events" e ON e."Id"=p."EventId" WHERE e."Id" IS NULL;""", ct));
        if (orphans != 0)
            throw new InvalidOperationException("Orphan participation records exist; review their scope before deleting anything.");
        long events = Convert.ToInt64(await ScalarAsync(connection, transaction, """SELECT COUNT(*) FROM "Events";""", ct));
        long participations = Convert.ToInt64(await ScalarAsync(connection, transaction, """SELECT COUNT(*) FROM "Participations";""", ct));
        if (!options.Apply)
        {
            if (Hash(path) != originalHash)
                throw new InvalidOperationException("Database changed during inspection.");
            return new("inspection-only", events, participations, originalHash);
        }
        if (events != options.ExpectedEvents || participations != options.ExpectedParticipations)
            throw new InvalidOperationException("Expected event/participation counts changed; inspect again.");
        if (events == 0) return new("already-empty", 0, 0, originalHash);

        // Exact supported schema means every other table can be retained and verified.
        var tables = new List<string>();
        using (var command = Command(connection, transaction,
            """SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"""))
        using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (reader.GetString(0) is not ("Events" or "Participations" or "Audits"))
                    tables.Add(reader.GetString(0));
        var retained = new Dictionary<string, string>();
        foreach (var table in tables) retained[table] = await TableHashAsync(connection, transaction, table, ct);
        var audits = await TableHashAsync(connection, transaction, "Audits", ct);
        // The source is locked, has no pending journal/WAL and has not changed since inspection.
        // CreateNew + 0600 prevents accidental overwrites or world-readable backup bytes.
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var source = File.OpenRead(path))
        using (var destination = new FileStream(backupPath!, fileOptions))
        {
            await source.CopyToAsync(destination, ct);
            destination.Flush(flushToDisk: true);
        }
        var backupHash = Hash(backupPath!);
        if (backupHash != originalHash || Hash(path) != originalHash)
            throw new InvalidOperationException("Backup verification failed; no deletion was committed.");

        // No interpolated user identifiers. Both deletes and the audit are atomic.
        var deletedParticipations = await ExecuteSqlAsync(connection, transaction, """
            DELETE FROM "Participations" WHERE "EventId" IN (SELECT "Id" FROM "Events");
            """, ct);
        var deletedEvents = await ExecuteSqlAsync(connection, transaction, """DELETE FROM "Events";""", ct);
        if (deletedEvents != events || deletedParticipations != participations)
            throw new InvalidOperationException("Deletion counts did not match; transaction will roll back.");
        foreach (var table in tables)
            if (retained[table] != await TableHashAsync(connection, transaction, table, ct))
                throw new InvalidOperationException("A retained table changed; transaction will roll back.");
        if (audits != await TableHashAsync(connection, transaction, "Audits", ct))
            throw new InvalidOperationException("Existing audit history changed; transaction will roll back.");
        using (var command = Command(connection, transaction, """
            INSERT INTO "Audits" ("Id","ActorId","Action","EntityId","At")
            VALUES ($id,'host-maintenance',$action,$operation,$at);
            """))
        {
            var id = Guid.NewGuid().ToString();
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$operation", id);
            command.Parameters.AddWithValue("$action", $"LegacyEventsReset:events={events};participations={participations}");
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(ct);
        }
        await CheckIntegrityAsync(connection, transaction, ct);
        ct.ThrowIfCancellationRequested();
        transaction.Commit();
        return new("reset-completed", events, participations, Hash(path), backupHash);
    }

    private static void CheckSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(path + suffix) && new FileInfo(path + suffix).Length != 0)
                throw new InvalidOperationException("SQLite sidecar detected. Stop the app and prepare a clean offline copy first.");
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken ct)
    {
        using var command = Command(connection, transaction, sql);
        return await command.ExecuteScalarAsync(ct);
    }
    private static async Task<int> ExecuteSqlAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken ct)
    {
        using var command = Command(connection, transaction, sql);
        return await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task CheckIntegrityAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = Command(connection, transaction, "PRAGMA integrity_check;");
        using (var reader = await command.ExecuteReaderAsync(ct))
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != "ok" || await reader.ReadAsync(ct))
                throw new InvalidOperationException("SQLite integrity check failed.");
        command.CommandText = "PRAGMA foreign_key_check;";
        using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) throw new InvalidOperationException("SQLite foreign-key check failed.");
    }
    private static async Task<string[]> SchemaAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        using var command = Command(connection, transaction, """
            SELECT type,name,tbl_name,COALESCE(sql,'') FROM sqlite_master
            WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\' ORDER BY type,name;
            """);
        var rows = new List<string>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sql = Regex.Replace(reader.GetString(3).Trim(),
                """'([^']|'')*'|"([^"]|"")*"|\[[^\]]*\]|`([^`]|``)*`|\s+""",
                match => char.IsWhiteSpace(match.Value[0]) ? " " : match.Value);
            rows.Add(JsonSerializer.Serialize(new[] { reader.GetString(0), reader.GetString(1), reader.GetString(2), sql }));
        }
        return rows.ToArray();
    }
    private static async Task ValidateSchemaAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var hasHistory = Convert.ToInt64(await ScalarAsync(connection, transaction,
            """SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';""", ct)) == 1;
        var hasLock = Convert.ToInt64(await ScalarAsync(connection, transaction,
            """SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsLock';""", ct)) == 1;
        if (hasLock && Convert.ToInt64(await ScalarAsync(connection, transaction,
            """SELECT COUNT(*) FROM "__EFMigrationsLock";""", ct)) != 0)
            throw new InvalidOperationException("An EF migration lock remains; investigate offline before event maintenance.");
        var applied = new List<string>();
        if (hasHistory)
        {
            using var command = Command(connection, transaction, """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";""");
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) applied.Add(reader.GetString(0));
            if (applied.Count == 0 || !applied.SequenceEqual(SupportedMigrations.Take(applied.Count)))
                throw new InvalidOperationException("Unsupported migration history; this command resets only the fixed legacy test model.");
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite("Data Source=:memory:"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<ApplicationDbContext>();
        using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<ApplicationDbContext>();
        if (!db.Database.GetMigrations().Take(SupportedMigrations.Length).SequenceEqual(SupportedMigrations))
            throw new InvalidOperationException("The frozen legacy-reset migrations are unavailable.");
        var script = db.GetService<IMigrator>().GenerateScript(Migration.InitialDatabase,
            applied.Count == 0 ? SupportedMigrations[0] : applied[^1], MigrationsSqlGenerationOptions.NoTransactions);
        using var reference = new SqliteConnection("Data Source=:memory:");
        await reference.OpenAsync(ct);
        await ExecuteSqlAsync(reference, null, script, ct);
        if (!hasHistory) await ExecuteSqlAsync(reference, null, """DROP TABLE "__EFMigrationsHistory";""", ct);
        if (hasLock)
            await ExecuteSqlAsync(reference, null, """
                CREATE TABLE "__EFMigrationsLock" ( "Id" INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY, "Timestamp" TEXT NOT NULL );
                """, ct);
        var actualSchema = await SchemaAsync(connection, transaction, ct);
        var expectedSchema = await SchemaAsync(reference, null, ct);
        if (!actualSchema.SequenceEqual(expectedSchema))
            throw new InvalidOperationException("Schema drift detected; no deletion was applied.");
    }
    private static async Task<string> TableHashAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var identifier = "\"" + table.Replace("\"", "\"\"") + "\"";
        // Every supported table has a rowid; order by all columns also works for composite keys.
        using var command = Command(connection, transaction, $"SELECT * FROM {identifier} ORDER BY rowid;");
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var values = new object?[reader.FieldCount];
            for (int i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values) + "\n"));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}