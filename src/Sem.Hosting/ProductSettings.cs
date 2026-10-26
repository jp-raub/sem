using System.Net;
using Sem.Authentication;
namespace Sem.Hosting;

// 既定は独立した画面デモ。実認証は全配置値を明示した場合だけ起動する。
public sealed record ProductSettings(string Database, string BackupDirectory, AuthenticationSettings Authentication)
{
    public static ProductSettings? Load(Func<string, string?> env)
    {
        var mode = env("SEM_APPLICATION_MODE");
        string[] names = ["SEM_AUTH_ORIGIN", "SEM_AUTH_DATABASE", "SEM_AUTH_KEYS_DIRECTORY", "SEM_AUTH_BACKUP_DIRECTORY", "SEM_AUTH_TRUSTED_PROXIES"];
        if (mode is null or "demo") {
            if (names.Any(n => env(n) is not null)) throw new InvalidOperationException("SEM_PRODUCT_MIXED_CONFIGURATION");
            return null;
        }
        if (mode != "authenticated") throw new InvalidOperationException("SEM_PRODUCT_INVALID_MODE");
        try {
            string Required(string name) => env(name) is { Length: > 0 } value ? value : throw new ArgumentException();
            string Absolute(string name) {
                var value = Required(name);
                if (!Path.IsPathFullyQualified(value) || value.Any(char.IsControl)) throw new ArgumentException();
                return Path.GetFullPath(value);
            }
            var db = Absolute("SEM_AUTH_DATABASE");
            var backup = Absolute("SEM_AUTH_BACKUP_DIRECTORY");
            var keys = Absolute("SEM_AUTH_KEYS_DIRECTORY");
            var proxies = env("SEM_AUTH_TRUSTED_PROXIES") is { Length: > 0 } raw
                ? raw.Split(',').Select(v => IPAddress.Parse(v.Trim())).ToArray() : [];
            var auth = new AuthenticationSettings(Required("SEM_AUTH_ORIGIN"), keys, proxies);
            var data = Path.GetDirectoryName(db)!;
            bool Within(string a, string b) => a == b || a.StartsWith(b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            if (data == Path.GetPathRoot(data) || keys == Path.GetPathRoot(keys) || backup == Path.GetPathRoot(backup) ||
                Within(backup, data) || Within(data, backup) || Within(backup, keys) || Within(keys, backup) || Within(db, keys)) throw new ArgumentException();
            return new(db, backup, auth);
        } catch (Exception e) when (e is ArgumentException or FormatException or NotSupportedException or OverflowException) {
            throw new InvalidOperationException("SEM_PRODUCT_INVALID_CONFIGURATION");
        }
    }
}
