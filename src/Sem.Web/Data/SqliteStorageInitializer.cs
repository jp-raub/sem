using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sem.Web.Data;

/// <summary>Adopt only the exact external-test schema, never an arbitrary existing DB.</summary>
public static class SqliteStorageInitializer
{
    public const string BaselineId = "20261012082319_ExternalTestBaseline";
    private const string HistoryTable = "__EFMigrationsHistory";
    private const string LockTable = "__EFMigrationsLock";

    public static async Task InitializeAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        if (db.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
            throw new InvalidOperationException("This storage upgrade supports SQLite only.");
        if (db.Database.HasPendingModelChanges())
            throw new InvalidOperationException("The application model has changes without a migration.");

        var known = db.Database.GetMigrations().ToArray();
        if (known.Length == 0 || known[0] != BaselineId)
            throw new InvalidOperationException("The external-test baseline migration is missing.");

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            // Microsoft.Data.Sqlite uses an immediate transaction here. No application
            // worker is running yet. Keep schema validation and adoption atomic.
            using (var transaction = connection.BeginTransaction())
            {
                var actual = await SchemaAsync(connection, transaction, ct);
                var hasHistory = actual.Any(x => x.Type == "table" && x.Name == HistoryTable);
                var applied = hasHistory
                    ? await AppliedAsync(connection, transaction, ct)
                    : [];
                if (!applied.SequenceEqual(known.Take(applied.Length), StringComparer.Ordinal))
                    throw new InvalidOperationException("Unknown or non-prefix migration history; no upgrade was applied.");

                var business = actual.Where(x => x.Name != HistoryTable && x.Name != LockTable).ToArray();
                var empty = business.Length == 0 && applied.Length == 0;
                var target = applied.Length == 0 ? BaselineId : applied[^1];
                var script = db.GetService<IMigrator>().GenerateScript(
                    Migration.InitialDatabase, target, MigrationsSqlGenerationOptions.NoTransactions);
                using var reference = new SqliteConnection("Data Source=:memory:");
                await reference.OpenAsync(ct);
                await ExecuteAsync(reference, null, script, ct);
                // The EF SQLite migration lock is infrastructure, not a business table.
                await ExecuteAsync(reference, null,
                    """CREATE TABLE "__EFMigrationsLock" ( "Id" INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY, "Timestamp" TEXT NOT NULL );""", ct);
                var expected = await SchemaAsync(reference, null, ct);
                var presentInfrastructure = new HashSet<string>(
                    actual.Where(x => x.Name is HistoryTable or LockTable).Select(x => x.Name),
                    StringComparer.Ordinal);
                var expectedInfrastructure = expected.Where(x => presentInfrastructure.Contains(x.Name)).ToArray();
                var actualInfrastructure = actual.Where(x => presentInfrastructure.Contains(x.Name)).ToArray();
                if (!actualInfrastructure.SequenceEqual(expectedInfrastructure))
                    throw new InvalidOperationException("Migration infrastructure schema does not match.");
                if (!empty && !business.SequenceEqual(
                    expected.Where(x => x.Name != HistoryTable && x.Name != LockTable)))
                    throw new InvalidOperationException(
                        "SQLite schema does not match the supported baseline/history. Keep the backup and review the DB; no upgrade was applied.");

                await CheckIntegrityAsync(connection, transaction, ct);
                if (!empty && applied.Length == 0)
                {
                    var history = db.GetService<IHistoryRepository>();
                    await ExecuteAsync(connection, transaction, history.GetCreateIfNotExistsScript(), ct);
                    await ExecuteAsync(connection, transaction,
                        history.GetInsertScript(new HistoryRow(BaselineId, ProductInfo.GetVersion())), ct);
                }
                transaction.Commit();
            }
            await db.Database.MigrateAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private sealed record SchemaObject(string Type, string Name, string Table, string Sql);

    private static async Task<SchemaObject[]> SchemaAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT type, name, tbl_name, COALESCE(sql, '')
            FROM sqlite_master
            WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\'
            ORDER BY type, name;
            """;
        var result = new List<SchemaObject>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Regex.Replace(reader.GetString(3).Trim(),
                    """'([^']|'')*'|"([^"]|"")*"|\[[^\]]*\]|`([^`]|``)*`|\s+""",
                    match => char.IsWhiteSpace(match.Value[0]) ? " " : match.Value)));
        return result.ToArray();
    }

    private static async Task<string[]> AppliedAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";""";
        var result = new List<string>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result.ToArray();
    }

    private static async Task CheckIntegrityAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA integrity_check;";
        using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != "ok" || await reader.ReadAsync(ct))
                throw new InvalidOperationException("SQLite integrity check failed; no upgrade was applied.");
        }
        command.CommandText = "PRAGMA foreign_key_check;";
        using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                throw new InvalidOperationException("SQLite foreign-key check failed; no upgrade was applied.");
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}