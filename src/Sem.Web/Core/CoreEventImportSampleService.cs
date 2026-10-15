using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Core;

public sealed partial class CoreFoundationService
{
    private const string SampleCsvHeader = "Kind,Title,Artist,Duration,Prep,Parts";

    public async Task<string> ReadEventImportSampleAsync(ClaimsPrincipal principal, string eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (_, _, _, manager) = await ParticipantEvent(db, principal, eventId, ct);
        if (!manager) throw new UnauthorizedAccessException();

        var definitions = await db.EventPartDefinitions.AsNoTracking()
            .Where(x => x.EventId == eventId && x.DeletedAt == null && x.Name.Length >= 1 && x.Name.Length <= 40)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Take(100)
            .Select(x => x.Name).ToArrayAsync(ct);
        var partName = definitions.FirstOrDefault(IsSafeSamplePartName);
        await tx.CommitAsync(ct);

        var rows = new List<string> { SampleCsvHeader };
        if (partName is not null)
        {
            var partCell = CsvCell(partName);
            rows.Add($"Song,{CsvCell("Sample Song 1")},{CsvCell("Sample Artist")},4:30,0:30,{partCell}");
        }
        rows.Add($"Transition,{CsvCell("Sample Transition")},,10:00,,");
        if (partName is not null)
            rows.Add($"Song,{CsvCell("Sample Song 2")},{CsvCell("Sample Artist")},1:02:03,0:00,{CsvCell(partName)}");
        return string.Join("\r\n", rows) + "\r\n";
    }

    private static bool IsSafeSamplePartName(string name) =>
        name.Length is >= 1 and <= 40 && name == name.Trim() && char.IsLetterOrDigit(name[0]) &&
        !name.Contains('|') && !name.Contains('\r') && !name.Contains('\n') && !name.Any(char.IsControl);

    private static string CsvCell(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0
        ? value
        : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
