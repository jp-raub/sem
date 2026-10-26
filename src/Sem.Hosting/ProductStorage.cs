using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
namespace Sem.Hosting;

// 稼働中のschema更新は行わない。既知の最終schemaだけ、起動前にDB＋鍵を保全して接続する。
public sealed class ProductStorage : IDisposable
{
    private readonly StorageLease lease;
    private ProductStorage(StorageLease lease) => this.lease = lease;
    public static string ConnectionString(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, ForeignKeys = true, DefaultTimeout = 5, Pooling = false }.ToString();
    public static async Task<ProductStorage> OpenAsync(ProductSettings settings, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("SEM_PRODUCT_STORAGE_NOT_READY");
        StorageLease? held = null;
        try {
            var data = Path.GetDirectoryName(settings.Database)!;
            foreach (var p in new[] { data, settings.Database, settings.Authentication.KeysDirectory, settings.BackupDirectory,
                settings.Database + "-wal", settings.Database + "-shm", settings.Database + "-journal" }) StorageLease.RejectLinks(p);
            if (!File.Exists(settings.Database) || !Directory.Exists(settings.Authentication.KeysDirectory) || !Directory.Exists(settings.BackupDirectory))
                throw new InvalidOperationException();
            var keys = Directory.GetFileSystemEntries(settings.Authentication.KeysDirectory);
            if (keys.Length == 0 || keys.Length > 256 || keys.Any(p => !File.Exists(p) || Path.GetExtension(p) != ".xml" || new FileInfo(p).Length > 1024 * 1024))
                throw new InvalidOperationException();
            foreach (var key in keys) StorageLease.RejectLinks(key);
            held = StorageLease.Acquire(data);
            // 起動失敗ループで無制限にコピーを増やさない。既存保全は勝手に削除しない。
            if (Directory.EnumerateFileSystemEntries(settings.BackupDirectory, "startup-*").Take(32).Count() >= 32)
                throw new InvalidOperationException();
            // 原DBはread-only。WALも含めSQLiteオンラインbackup APIで整合したコピーを作る。
            var snapshot = Path.Combine(settings.BackupDirectory, "startup-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snapshot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(snapshot, "keys"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var copy = Path.Combine(snapshot, "database.sqlite");
            using (var file = new FileStream(copy, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite })) { }
            using (var original = new SqliteConnection(ConnectionString(settings.Database, SqliteOpenMode.ReadOnly)))
            using (var destination = new SqliteConnection(ConnectionString(copy))) {
                await original.OpenAsync(ct); await destination.OpenAsync(ct); original.BackupDatabase(destination);
            }
            foreach (var key in keys) {
                using var source = File.OpenRead(key);
                using var target = new FileStream(Path.Combine(snapshot, "keys", Path.GetFileName(key)), new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
                await source.CopyToAsync(target, ct);
            }
            bool hasKey = false;
            foreach (var key in Directory.GetFiles(Path.Combine(snapshot, "keys"))) {
                using var xml = System.Xml.XmlReader.Create(key, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
                var document = System.Xml.Linq.XDocument.Load(xml);
                hasKey |= document.Root?.Name.LocalName == "key";
            }
            if (!hasKey) throw new InvalidOperationException();
            // コピー上で履歴・schema・整合性を検証。未適用migrationは原DBへ適用せず拒否する。
            var services = new ServiceCollection(); services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(ConnectionString(copy)));
            services.AddDataProtection().SetApplicationName(Sem.Authentication.AuthenticationPipeline.ProtectionApplicationName).PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(snapshot, "keys"))).DisableAutomaticKeyGeneration();
            services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
            await using (var provider = services.BuildServiceProvider()) {
                await using var scope = provider.CreateAsyncScope();
                try {
                    var ring = scope.ServiceProvider.GetRequiredService<IKeyManager>().GetAllKeys();
                    if (ring.Count == 0 || ring.Any(k => k.CreateEncryptor() is null)) throw new InvalidOperationException();
                } catch (Exception e) when (e is NullReferenceException or ArgumentException or FormatException or CryptographicException or InvalidOperationException) {
                    // 標準鍵parserが欠損descriptorを例外化しても、配置値や鍵XMLは診断へ出さない。
                    throw new InvalidOperationException("SEM_PRODUCT_INVALID_KEY_RING");
                }
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var known = db.Database.GetMigrations().ToArray();
                var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
                if (known.Length == 0 || !known.SequenceEqual(applied, StringComparer.Ordinal)) throw new InvalidOperationException();
                await SqliteStorageInitializer.InitializeAsync(db, ct);
            }
            var manifest = Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories).Order().Select(p => new {
                file = Path.GetRelativePath(snapshot, p), sha256 = HashFile(p)
            }).ToArray();
            var marker = Path.Combine(snapshot, "verified.json");
            await using (var output = new FileStream(marker, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
                await JsonSerializer.SerializeAsync(output, new { schema = 1, files = manifest }, cancellationToken: ct);
            return new(held);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException or System.Xml.XmlException or CryptographicException or FormatException or ArgumentException) {
            held?.Dispose();
            // 失敗したコピーは削除しない。verified.jsonのない領域は復旧成功の証拠にしない。
            throw new InvalidOperationException("SEM_PRODUCT_STORAGE_NOT_READY");
        } catch { held?.Dispose(); throw; }
    }
    static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    public void Dispose() => lease.Dispose();
}
