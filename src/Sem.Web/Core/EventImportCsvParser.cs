using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;

namespace Sem.Web.Core;

/// <summary>Parses the deliberately narrow CSV format used for event imports.</summary>
public static class EventImportCsvParser
{
    public const int MaxUtf8Bytes = 1_048_576;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> SupportedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Kind", "Title", "Duration", "Artist", "Prep", "Parts"
    };

    /// <summary>Parses a UTF-8 CSV payload, optionally prefixed with a UTF-8 BOM.</summary>
    public static IReadOnlyList<EventImportCsvRow> Parse(ReadOnlySpan<byte> utf8Csv)
    {
        if (utf8Csv.Length > MaxUtf8Bytes)
            throw new ValidationException("CSVは1 MiB以下にしてください。");

        if (utf8Csv.Length >= 3 && utf8Csv[0] == 0xEF && utf8Csv[1] == 0xBB && utf8Csv[2] == 0xBF)
            utf8Csv = utf8Csv[3..];

        string text;
        try
        {
            text = StrictUtf8.GetString(utf8Csv);
        }
        catch (DecoderFallbackException)
        {
            throw new ValidationException("CSVは正しいUTF-8である必要があります。");
        }

        var records = ReadRecords(text);
        if (records.Count == 0)
            throw new ValidationException("CSVにヘッダー行がありません。");

        var columns = MapHeaders(records[0].Cells);
        var rows = new List<EventImportCsvRow>(records.Count - 1);
        for (var index = 1; index < records.Count; index++)
            rows.Add(ParseRow(records[index], columns));
        return rows;
    }

    /// <summary>Encodes a .NET string strictly as UTF-8 before parsing.</summary>
    public static IReadOnlyList<EventImportCsvRow> Parse(string csv)
    {
        if (csv is null)
            throw new ValidationException("CSVを指定してください。");

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(csv);
        }
        catch (EncoderFallbackException)
        {
            throw new ValidationException("CSVに不正なUnicode文字があります。");
        }
        if (byteCount > MaxUtf8Bytes)
            throw new ValidationException("CSVは1 MiB以下にしてください。");
        var bytes = StrictUtf8.GetBytes(csv);
        return Parse(bytes);
    }

    private static HeaderMap MapHeaders(IReadOnlyList<string> cells)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignmentColumns = new List<int>();

        for (var i = 0; i < cells.Count; i++)
        {
            var name = cells[i].Trim();
            if (name.Length == 0)
                throw new ValidationException($"CSVの{i + 1}列目のヘッダーが空です。");
            if (!seen.Add(name))
                throw new ValidationException($"CSVに重複したヘッダーがあります（{i + 1}列目）。");

            if (IsAssignmentHeader(name))
            {
                assignmentColumns.Add(i);
                continue;
            }
            if (!SupportedHeaders.Contains(name))
                throw new ValidationException($"CSVの{i + 1}列目は未対応のヘッダーです。");
            map.Add(name, i);
        }

        foreach (var required in new[] { "Kind", "Title", "Duration" })
            if (!map.ContainsKey(required))
                throw new ValidationException($"CSVに必須ヘッダー「{required}」がありません。");

        return new HeaderMap(map, assignmentColumns, cells.Count);
    }

    private static bool IsAssignmentHeader(string name) =>
        name.Contains("Assigned", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Assignee", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("UserId", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("UserToken", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("担当者", StringComparison.Ordinal);

    private static EventImportCsvRow ParseRow(CsvRecord record, HeaderMap headerMap)
    {
        var columns = headerMap.Columns;
        var errors = new List<string>();
        if (record.Cells.Count != headerMap.TotalColumns)
            errors.Add("列数がヘッダーと一致しません。");

        string Cell(string name) => columns.TryGetValue(name, out var column) && column < record.Cells.Count
            ? record.Cells[column]
            : "";

        var kindValue = Cell("Kind");
        string? kind = kindValue switch
        {
            "Song" => "Song",
            "Transition" => "Transition",
            _ => null
        };
        if (kind is null)
            errors.Add("KindはSongまたはTransitionを指定してください。");

        var title = Cell("Title").Trim();
        if (title.Length == 0)
            errors.Add("Titleは必須です。");
        else if (ContainsControl(title))
            errors.Add("Titleに制御文字は指定できません。");
        else if (title.Length > 200)
            errors.Add("Titleは200文字以内にしてください。");

        var artist = Cell("Artist").Trim();
        if (ContainsControl(artist))
            errors.Add("Artistに制御文字は指定できません。");
        else if (artist.Length > 200)
            errors.Add("Artistは200文字以内にしてください。");

        int? duration = null;
        if (TryParseSeconds(Cell("Duration"), out var parsedDuration))
        {
            duration = parsedDuration;
            if (kind == "Song" && parsedDuration < 1)
                errors.Add("SongのDurationは1秒以上にしてください。");
            else if (kind == "Transition" && parsedDuration < 0)
                errors.Add("TransitionのDurationは0秒以上にしてください。");
        }
        else
        {
            errors.Add("Durationはm:ssまたはh:mm:ss形式の秒数で指定してください。");
        }

        var requiredFieldError = errors.Count > 0;
        int? prep = null;
        if (columns.ContainsKey("Prep"))
        {
            var prepValue = Cell("Prep").Trim();
            if (prepValue.Length > 0)
            {
                if (TryParseSeconds(prepValue, out var parsedPrep))
                    prep = parsedPrep;
                else
                    errors.Add("Prepはm:ssまたはh:mm:ss形式の0秒以上で指定してください。");
            }
        }

        var partNames = new List<string>();
        if (columns.ContainsKey("Parts"))
        {
            var partsValue = Cell("Parts");
            if (partsValue.Trim().Length > 0)
            {
                var seenParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rawPart in partsValue.Split('|'))
                {
                    var part = rawPart.Trim();
                    if (part.Length == 0)
                    {
                        errors.Add("Partsに空のパート名があります。");
                        continue;
                    }
                    if (ContainsControl(part))
                    {
                        errors.Add("Partsに制御文字は指定できません。");
                        continue;
                    }
                    if (!seenParts.Add(part))
                    {
                        errors.Add("Partsに大文字・小文字を区別しない重複があります。");
                        continue;
                    }
                    partNames.Add(part);
                }
            }
        }
        if (kind == "Song" && partNames.Count == 0)
            errors.Add("Songには1つ以上のPartsが必要です。");

        var hasAssignment = false;
        foreach (var assignmentColumn in headerMap.AssignmentColumns)
        {
            if (assignmentColumn < record.Cells.Count && record.Cells[assignmentColumn].Trim().Length > 0)
                hasAssignment = true;
        }

        return new EventImportCsvRow(record.LineNumber, kind, title, artist, duration, prep,
            partNames.ToArray(), hasAssignment, errors.Count == 0 ? null : string.Join(" ", errors),
            record.Cells.Count != headerMap.TotalColumns, requiredFieldError);
    }

    private static bool TryParseSeconds(string input, out int seconds)
    {
        seconds = 0;
        var value = input.Trim();
        var segments = value.Split(':');
        if (segments.Length is not (2 or 3))
            return false;

        Span<int> numbers = stackalloc int[3];
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
                return false;
            foreach (var ch in segment)
                if (ch is < '0' or > '9')
                    return false;
            if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        long total;
        if (segments.Length == 2)
        {
            if (numbers[1] > 59)
                return false;
            total = (long)numbers[0] * 60 + numbers[1];
        }
        else
        {
            if (numbers[1] > 59 || numbers[2] > 59)
                return false;
            total = (long)numbers[0] * 3600 + (long)numbers[1] * 60 + numbers[2];
        }
        if (total > int.MaxValue)
            return false;
        seconds = (int)total;
        return true;
    }

    private static bool ContainsControl(string value)
    {
        foreach (var rune in value.EnumerateRunes())
            if (Rune.IsControl(rune))
                return true;
        return false;
    }

    private static List<CsvRecord> ReadRecords(string text)
    {
        var records = new List<CsvRecord>();
        var cells = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var closedQuote = false;
        var recordHasContent = false;
        var line = 1;
        var recordLine = 1;

        void FinishField()
        {
            cells.Add(field.ToString());
            field.Clear();
            closedQuote = false;
        }

        void FinishRecord()
        {
            FinishField();
            records.Add(new CsvRecord(recordLine, cells.ToArray()));
            cells.Clear();
            recordHasContent = false;
            recordLine = line + 1;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                        closedQuote = true;
                    }
                }
                else if (ch == '\r')
                {
                    if (i + 1 >= text.Length || text[i + 1] != '\n')
                        throw CsvSyntaxError(line);
                    field.Append('\n');
                    i++;
                    line++;
                }
                else if (ch == '\n')
                {
                    field.Append('\n');
                    line++;
                }
                else
                {
                    field.Append(ch);
                }
                recordHasContent = true;
                continue;
            }

            if (closedQuote && ch is not (',' or '\r' or '\n'))
                throw CsvSyntaxError(line);

            if (ch == '"')
            {
                if (field.Length != 0 || closedQuote)
                    throw CsvSyntaxError(line);
                inQuotes = true;
                recordHasContent = true;
            }
            else if (ch == ',')
            {
                FinishField();
                recordHasContent = true;
            }
            else if (ch == '\r')
            {
                if (i + 1 >= text.Length || text[i + 1] != '\n')
                    throw CsvSyntaxError(line);
                i++;
                FinishRecord();
                line++;
                recordLine = line;
            }
            else if (ch == '\n')
            {
                FinishRecord();
                line++;
                recordLine = line;
            }
            else
            {
                field.Append(ch);
                recordHasContent = true;
            }
        }

        if (inQuotes)
            throw CsvSyntaxError(line);
        if (recordHasContent || cells.Count > 0 || field.Length > 0 || closedQuote)
            FinishRecord();
        return records;
    }

    private static ValidationException CsvSyntaxError(int line) =>
        new($"CSVの引用符または改行の形式が正しくありません（{line}行目）。");

    private sealed record HeaderMap(
        IReadOnlyDictionary<string, int> Columns,
        IReadOnlyList<int> AssignmentColumns,
        int TotalColumns);

    private sealed record CsvRecord(int LineNumber, IReadOnlyList<string> Cells);
}

/// <summary>One data record; assignment values are intentionally never retained.</summary>
public sealed record EventImportCsvRow(
    int LineNumber,
    string? Kind,
    string Title,
    string Artist,
    int? DurationSeconds,
    int? PrepSeconds,
    IReadOnlyList<string> PartNames,
    bool HasAssignment,
    string? Error,
    bool StructuralError = false,
    bool RequiredFieldError = false);
