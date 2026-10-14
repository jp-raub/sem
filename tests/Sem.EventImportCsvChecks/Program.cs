using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Sem.Web.Core;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    passed++;
    Console.WriteLine("PASS " + name);
}
void ThrowsValidation(Action action, string name, string? notContains = null)
{
    try { action(); }
    catch (ValidationException ex)
    {
        Check(notContains is null || !ex.Message.Contains(notContains, StringComparison.Ordinal), name);
        return;
    }
    throw new Exception("FAIL " + name + " (no ValidationException)");
}
string Csv(string rows, string headers = "Kind,Title,Duration,Artist,Prep,Parts") => headers + "\n" + rows;
EventImportCsvRow One(string row, string headers = "Kind,Title,Duration,Artist,Prep,Parts") =>
    EventImportCsvParser.Parse(Csv(row, headers)).Single();

var normalized = One("Song,  Title  ,1:02,  Artist  ,0:00,  Vocal | Guitar  ");
Check(normalized.Error is null && normalized.Kind == "Song" && normalized.Title == "Title" && normalized.Artist == "Artist", "trimmed row text and exact kind");
Check(normalized.DurationSeconds == 62 && normalized.PrepSeconds == 0 && normalized.PartNames.SequenceEqual(new[] { "Vocal", "Guitar" }), "duration, explicit zero prep, and trimmed parts");
var inheritedPrep = One("Song,Title,1:02,, ,Lead");
Check(inheritedPrep.Error is null && inheritedPrep.PrepSeconds is null, "blank prep remains null for inheritance");
Check(One("Transition,Pause,0:00,,, ").Error is null, "transition allows zero duration");
Check(One("Song,Song,0:00,,,Lead").Error?.Contains("1秒以上", StringComparison.Ordinal) == true, "song duration must be at least one second");

var bomBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Csv("Song,歌,1:00,歌手,,ギター"))).ToArray();
var bomRow = EventImportCsvParser.Parse(bomBytes).Single();
Check(bomRow.Error is null && bomRow.Title == "歌" && bomRow.PartNames.Single() == "ギター", "UTF-8 BOM and multibyte text");
ThrowsValidation(() => EventImportCsvParser.Parse(new byte[] { 0xEF, 0xBB, 0xBF, 0xC3, 0x28 }), "invalid UTF-8 rejected");
ThrowsValidation(() => EventImportCsvParser.Parse("Kind,Title,Duration,Parts\nSong,\uD800,1:00,Lead"), "unpaired UTF-16 surrogate rejected");

var maxPayloadPrefix = "Kind,Title,Duration,Artist\nSong,x,0:01,";
var exactSizeText = maxPayloadPrefix + new string('a', EventImportCsvParser.MaxUtf8Bytes - Encoding.UTF8.GetByteCount(maxPayloadPrefix));
var exactSizeRow = EventImportCsvParser.Parse(Encoding.UTF8.GetBytes(exactSizeText)).Single();
Check(exactSizeText.Length == EventImportCsvParser.MaxUtf8Bytes && exactSizeRow.Error?.Contains("200文字以内", StringComparison.Ordinal) == true, "exact 1 MiB UTF-8 payload accepted");
ThrowsValidation(() => EventImportCsvParser.Parse(Encoding.UTF8.GetBytes(exactSizeText + "a")), "payload one byte over limit rejected");
var multibytePrefix = "Kind,Title,Duration,Artist\nSong,x,0:01,";
var multibytePadding = EventImportCsvParser.MaxUtf8Bytes - Encoding.UTF8.GetByteCount(multibytePrefix);
var exactMultibyteText = multibytePrefix + new string('é', multibytePadding / 2) + (multibytePadding % 2 == 0 ? "" : "a");
Check(Encoding.UTF8.GetByteCount(exactMultibyteText) == EventImportCsvParser.MaxUtf8Bytes &&
      EventImportCsvParser.Parse(exactMultibyteText).Single().Error?.Contains("200文字以内", StringComparison.Ordinal) == true,
    "exact 1 MiB UTF-8 payload with multibyte text accepted");
var multibyteOver = "Kind,Title,Duration,Artist\nSong,x,0:01," + new string('é', (EventImportCsvParser.MaxUtf8Bytes / 2) + 1);
ThrowsValidation(() => EventImportCsvParser.Parse(multibyteOver), "byte limit counts UTF-8 bytes for multibyte text");

var quoted = One("Song,\"A \"\"quoted\"\" title\",1:02,,0:00,Lead,", "Kind,Title,Duration,Artist,Prep,Parts,AssignedTo");
Check(quoted.Error is null && quoted.Title == "A \"quoted\" title", "escaped quotes parsed");
var embedded = EventImportCsvParser.Parse("Kind,Title,Duration,Parts,AssignedTo\r\nSong,Title,1:00,Lead,\"user\r\nidentity\"\r\nTransition,Pause,0:00,,");
Check(embedded.Count == 2 && embedded[0].LineNumber == 2 && embedded[1].LineNumber == 4, "quoted CRLF newline preserved as one CSV record and physical line indices");
Check(embedded[0].HasAssignment && embedded[0].Error is null, "nonempty assignment field flags row without resolving identity");
Check(!JsonSerializer.Serialize(embedded[0]).Contains("identity", StringComparison.OrdinalIgnoreCase), "assignment contents are never present in returned DTO");
var emptyRecords = EventImportCsvParser.Parse("Kind,Title,Duration,Parts\n\nSong,Valid,0:01,Lead\n");
Check(emptyRecords.Count == 2 && emptyRecords[0].LineNumber == 2 && emptyRecords[0].Error is not null && emptyRecords[1].LineNumber == 3 && emptyRecords[1].Error is null, "empty record gets a row and trailing newline adds none");
var multilineRow = EventImportCsvParser.Parse("Kind,Title,Duration,Parts\nSong,\"Line one\nLine two\",1:00,Lead\nTransition,Outro,0:00,");
Check(multilineRow.Count == 2 && multilineRow[0].LineNumber == 2 && multilineRow[0].Error?.Contains("制御文字", StringComparison.Ordinal) == true && multilineRow[1].LineNumber == 4, "embedded LF line indexing and semantic control rejection");
ThrowsValidation(() => EventImportCsvParser.Parse("Kind,Title,Duration,Parts\nSong,\"unterminated,1:00,Lead"), "unterminated quote is top-level syntax error");
ThrowsValidation(() => EventImportCsvParser.Parse("Kind,Title,Duration,Parts\nSong,Title\"bad,1:00,Lead"), "quote in unquoted cell is top-level syntax error");
ThrowsValidation(() => EventImportCsvParser.Parse("Kind,Title,Duration,Parts\nSong,\"Title\" x,1:00,Lead"), "characters after closing quote are syntax error");

foreach (var (duration, expected) in new[]
{
    ("0:00", 0), ("60:00", 3600), ("1:02:03", 3723), ("35791394:07", int.MaxValue), ("596523:14:07", int.MaxValue)
})
{
    var row = One($"Transition,T,{duration},,,");
    Check(row.Error is null && row.DurationSeconds == expected, "duration accepted: " + duration);
}
foreach (var duration in new[] { "", "1", "1:60", "1:2:60", "1:60:00", "-1:00", "+1:00", "1.5:00", "1::00", "1:00:00:00", "35791394:08", "596523:14:08", "2147483648:00" })
{
    var row = One($"Transition,T,{duration},,,");
    Check(row.Error?.Contains("Duration", StringComparison.Ordinal) == true, "duration rejected: " + (duration.Length == 0 ? "blank" : duration));
}
Check(One("Transition,T,0:00,,0:00,").PrepSeconds == 0, "prep accepts explicit zero");
Check(One("Transition,T,0:00,,60:00,").PrepSeconds == 3600, "prep accepts minutes beyond 59");
Check(One("Transition,T,0:00,,0:60,").Error?.Contains("Prep", StringComparison.Ordinal) == true, "prep rejects seconds beyond 59");

ThrowsValidation(() => EventImportCsvParser.Parse("Kind,Title,Duration,Kind\nSong,T,1:00,Song"), "duplicate headers rejected case-insensitively");
ThrowsValidation(() => EventImportCsvParser.Parse("Kind,Title\nSong,T"), "missing required header rejected");
ThrowsValidation(() => EventImportCsvParser.Parse("Kind,Title,Duration,Phase\nSong,T,1:00,x"), "unsupported header rejected clearly");
ThrowsValidation(() => EventImportCsvParser.Parse("Kind,Title,Duration,AssignedTo,assignedto\nSong,T,1:00,a,b"), "duplicate assignment headers rejected");
ThrowsValidation(() => EventImportCsvParser.Parse("Kind,Title,Duration,Parts\rSong,T,1:00,Lead"), "bare CR rejected as malformed CSV");
ThrowsValidation(() => EventImportCsvParser.Parse(""), "empty file reports missing header");
var widthMismatch = EventImportCsvParser.Parse("Kind,Title,Duration,Parts\nSong,Secret,1:00,Lead,IDENTITY-RAW").Single();
Check(widthMismatch.Error is not null && !JsonSerializer.Serialize(widthMismatch).Contains("IDENTITY-RAW", StringComparison.Ordinal), "row width error does not echo extra raw CSV");

var assignmentHeaders = new[] { "Assigned", "AssigneeName", "UserId", "UserToken", "担当者ID" };
foreach (var header in assignmentHeaders)
{
    var row = One($"Song,T,1:00,Lead,{header.Trim()}", "Kind,Title,Duration,Parts," + header);
    Check(row.HasAssignment && row.Error is null, "assignment indicator header recognized: " + header);
}
var blankAssignment = One("Song,T,1:00,Lead,  ", "Kind,Title,Duration,Parts,Assignee");
Check(!blankAssignment.HasAssignment && blankAssignment.Error is null, "blank assignment field does not flag row");
var assignmentJson = JsonSerializer.Serialize(One("Song,T,1:00,Lead,token-private", "Kind,Title,Duration,Parts,UserToken"));
Check(!assignmentJson.Contains("token-private", StringComparison.Ordinal), "user token is not echoed in the DTO");

var duplicateParts = One("Song,T,1:00,, ,Lead| lead ");
Check(duplicateParts.Error?.Contains("重複", StringComparison.Ordinal) == true, "part names deduplicate case-insensitively");
Check(One("Song,T,1:00,, ,Trumpet").Error is null, "part name is not hardcoded to an existing voice list");
Check(One("Song,T,1:00,, ,Lead||Bass").Error?.Contains("空のパート", StringComparison.Ordinal) == true, "empty component in pipe-separated parts is invalid");
Check(One("Song,T,1:00,, ,").Error?.Contains("Parts", StringComparison.Ordinal) == true, "song requires part names");
var badRows = EventImportCsvParser.Parse(Csv("Song,,bad,,,\nTransition,Good,0:00,,,\ninvalid,Also bad,0:00,,,"));
Check(badRows.Count == 3 && badRows[0].Error is not null && badRows[1].Error is null && badRows[2].Error is not null, "semantic row errors do not hide remaining valid rows");
Check(One("Song," + new string('x', 200) + ",1:00,,,Lead").Error is null, "200 character title boundary accepted");
Check(One("Song," + new string('x', 201) + ",1:00,,,Lead").Error?.Contains("200文字", StringComparison.Ordinal) == true, "title over 200 rejected");
Check(One("Transition,T,0:00," + new string('é', 200) + ",,").Error is null, "200 Unicode scalar artist boundary accepted");
Check(One("Transition,T,0:00," + new string('é', 201) + ",,").Error?.Contains("200文字", StringComparison.Ordinal) == true, "artist over 200 Unicode scalars rejected");
Check(One("Transition,T,0:00,\"bad\u0001artist\",,").Error?.Contains("制御文字", StringComparison.Ordinal) == true, "invalid control character in artist rejected");

Console.WriteLine($"TOTAL PASS: {passed}");

Check(One("Song," + string.Concat(Enumerable.Repeat("😀",100)) + ",1:00,,,Lead").Error is null, "UTF-16 title boundary matches Core text validation");
Check(One("Song," + string.Concat(Enumerable.Repeat("😀",101)) + ",1:00,,,Lead").Error is not null, "supplementary characters count two UTF-16 code units");
Console.WriteLine("FINAL TOTAL PASS: " + passed);
var assignedWidth = EventImportCsvParser.Parse("Kind,Title,Duration,Parts,UserToken\nSong,T,1:00,Lead,SECRET,EXTRA").Single();
Check(assignedWidth.HasAssignment && assignedWidth.StructuralError && assignedWidth.Error is not null, "assignment flag cannot erase structural width error");
Check(!JsonSerializer.Serialize(assignedWidth).Contains("SECRET", StringComparison.Ordinal), "structurally invalid row still excludes assignment value");
Console.WriteLine("FINAL TOTAL PASS: " + passed);
