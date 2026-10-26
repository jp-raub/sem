using System.Reflection;
using System.Text.RegularExpressions;

namespace Sem.Web.Runtime;

// Reads this product assembly, never the entry assembly/test host or runtime configuration.
public sealed record RuntimeBuildInfo(string? Commit, string? PublicationRun, string? PublicationAttempt)
{
    public static RuntimeBuildInfo Current { get; } = FromMetadata(typeof(RuntimeBuildInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>());
    public bool Published => Commit is not null && PublicationRun is not null && PublicationAttempt is not null;
    public string? BuildId => Published ? $"run-{PublicationRun}-{PublicationAttempt}" : null;
    public string Display => Published ? $"{Commit![..8]} / {BuildId}" : "開発版（稼働版の識別情報なし）";
    public string? SourceUrl => Published ? $"https://github.com/jp-raub/sem/commit/{Commit}" : null;

    public static RuntimeBuildInfo FromMetadata(IEnumerable<AssemblyMetadataAttribute> attributes)
    {
        var all = attributes.ToArray();
        string? Read(string key, string pattern)
        {
            var values = all.Where(a => a.Key == key).ToArray();
            return values.Length == 1 && values[0].Value is string value &&
                Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant) ? value : null;
        }
        var sha = Read("SemCommit", "\\A[0-9a-f]{40}\\z");
        var run = Read("SemPublicationRun", "\\A[1-9][0-9]{0,19}\\z");
        var attempt = Read("SemPublicationAttempt", "\\A[1-9][0-9]{0,9}\\z");
        return sha is not null && run is not null && attempt is not null
            ? new(sha, run, attempt) : new(null, null, null);
    }

    public object Payload(string? probe) => new
    {
        schema = 1, application = "SEM", channel = Published ? "published" : "development",
        commit = Commit, buildId = BuildId, publicationRun = PublicationRun,
        publicationAttempt = PublicationAttempt, probe
    };
}
