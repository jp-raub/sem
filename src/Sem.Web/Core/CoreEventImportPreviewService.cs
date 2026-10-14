using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Sem.Web.Core;

public sealed record EventImportPreviewRow(int LineNumber, string? Kind, string Title, string Artist,
    int? DurationSeconds, int? PrepSeconds, string[] PartNames, string Disposition, string? Error);
public sealed record EventImportPreview(string EventId, long RowVersion, string Fingerprint,
    EventImportPreviewRow[] Rows, int NewSongs, int NewTransitions, int SkippedRows, string[] Errors,
    DateTimeOffset? ProposedEnd, long? OverrunSeconds)
{
    // Validation only: this version has no mutation endpoint or confirmation operation.
    public bool IsValid => Errors.Length == 0 && Rows.All(x => x.Error is null);
}

public sealed partial class CoreFoundationService
{
    public async Task<EventImportPreview> ReadEventImportPreviewAsync(ClaimsPrincipal principal,
        string eventId, string csv, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (e, actor, phase, manager) = await ParticipantEvent(db, principal, eventId, ct);
        if (!manager) throw new UnauthorizedAccessException();
        var eligibility = CoreEventImportHistory.Assess(e, phase, clock.GetUtcNow());
        if (!eligibility.IsEligible)
            throw new System.ComponentModel.DataAnnotations.ValidationException("取込条件を満たしていません: " + eligibility.RejectionReason);
        await EnsurePlanEditable(db, eventId, ct);
        // Contrary business evidence is a further refusal, never proof of a clean history.
        if (await db.SongParts.AnyAsync(x => x.EventId == eventId && x.AssignedUserId != null, ct)
            || await db.EventSpectators.AnyAsync(x => x.EventId == eventId, ct)
            || await db.RegistrationOperations.AnyAsync(x => x.EventId == eventId, ct)
            || await db.EventReceptions.AnyAsync(x => x.EventId == eventId, ct))
            throw new System.ComponentModel.DataAnnotations.ValidationException("参加／受付の記録があるイベントには取り込めません。");

        var input = EventImportCsvParser.Parse(csv);
        var definitions = await db.EventPartDefinitions.AsNoTracking()
            .Where(x => x.EventId == eventId && x.DeletedAt == null)
            .OrderBy(x => x.Id).ToArrayAsync(ct);
        var songs = await db.Songs.AsNoTracking().Where(x => x.EventId == eventId && x.DeletedAt == null)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToArrayAsync(ct);
        var parts = await db.TimeParts.AsNoTracking().Where(x => x.EventId == eventId && x.DeletedAt == null)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToArrayAsync(ct);
        var definitionNames = definitions.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var songKeys = songs.Select(x => (x.Title, x.Artist)).ToHashSet();
        var transitionKeys = parts.Where(x => x.Kind == "Transition").Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);
        var result = new List<EventImportPreviewRow>();
        var additions = new List<EventImportCsvRow>();
        foreach (var row in input)
        {
            var error = row.Error;
            var disposition = "Ready";
            if (row.HasAssignment && !row.StructuralError && !row.RequiredFieldError) { disposition = "AssignedSkipped"; error = null; }
            else if (error is not null) disposition = "Invalid";
            else if (row.Kind == "Song" && songKeys.Contains((row.Title, row.Artist))
                || row.Kind == "Transition" && transitionKeys.Contains(row.Title)) disposition = "DuplicateSkipped";
            else if (row.Kind == "Song" && row.PartNames.Any(x => !definitionNames.Contains(x)))
            { disposition = "Invalid"; error = "このイベントに未定義のパート名があります。"; }
            else
            {
                if (row.Kind == "Song") songKeys.Add((row.Title, row.Artist));
                else transitionKeys.Add(row.Title);
                additions.Add(row);
            }
            result.Add(new(row.LineNumber, row.Kind, row.Title, row.Artist, row.DurationSeconds,
                row.PrepSeconds, row.PartNames.ToArray(), disposition, error));
        }
        var newSongs = additions.Count(x => x.Kind == "Song");
        var newTransitions = additions.Count(x => x.Kind == "Transition");
        var errors = new List<string>();
        if (definitions.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != definitions.Length)
            errors.Add("有効なパート定義名が一意ではありません。");
        if (parts.Any(x => x.Kind is not ("Song" or "Transition"))
            || parts.Count(x => x.Kind == "Song") != songs.Length
            || songs.Any(song => parts.Count(x => x.Kind == "Song" && x.SongId == song.Id) != 1)
            || parts.Any(x => x.Kind == "Song" && !songs.Any(song => song.Id == x.SongId)))
            errors.Add("既存の曲とタイムパートの対応を確認してください。");
        if (songs.Length + newSongs > 300) errors.Add("曲数の上限300を超えます。");
        if (parts.Count(x => x.Kind == "Transition") + newTransitions > 300) errors.Add("進行枠の上限300を超えます。");
        if (parts.Length + additions.Count > 600) errors.Add("タイムパートの上限600を超えます。");
        if (additions.Count == 0) errors.Add("追加可能な行がありません。");
        DateTimeOffset? proposedEnd = null;
        long? overrun = null;
        if (errors.Count == 0 && result.All(x => x.Error is null))
        {
            // Synthetic IDs are local calculation keys only, never stored or returned.
            var proposedSongs = songs.ToList();
            var proposedParts = parts.ToList();
            var order = parts.Length == 0 ? 0 : parts.Max(x => x.SortOrder) + 1;
            foreach (var row in additions)
            {
                var key = "csv-preview-" + row.LineNumber;
                if (row.Kind == "Song")
                {
                    proposedSongs.Add(new Song { Id = key, EventId = e.Id, Title = row.Title, Artist = row.Artist,
                        PerformanceSeconds = row.DurationSeconds!.Value, PrepSeconds = row.PrepSeconds });
                    proposedParts.Add(new TimePart { Id = key, EventId = e.Id, Kind = "Song", SongId = key, SortOrder = order++ });
                }
                else proposedParts.Add(new TimePart { Id = key, EventId = e.Id, Kind = "Transition", Name = row.Title,
                    DurationSeconds = row.DurationSeconds!.Value, SortOrder = order++ });
            }
            try
            {
                var calculation = TimetableCalculator.Calculate(e, proposedParts, proposedSongs);
                proposedEnd = calculation.End; overrun = calculation.OverrunSeconds;
            }
            catch (System.ComponentModel.DataAnnotations.ValidationException)
            { errors.Add("取込後の日時・所要時間を計算できません。"); }
        }
        // Comparison only, not approval, authorization, replay identity, or write guarantee.
        var comparison = JsonSerializer.Serialize(new { Actor = actor.Id, Event = e.Id, e.RowVersion,
            e.DefaultPrepSeconds, e.ScheduledDate, e.OpensAt, e.RoundStarts, e.PlannedEndAt,
            Input = input, Definitions = definitions.Select(x => new { x.Id, x.Name, x.Category }), Songs = songs,
            Parts = parts, Rows = result });
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(comparison)));
        return new(e.Id, e.RowVersion, fingerprint, result.ToArray(), newSongs, newTransitions,
            result.Count(x => x.Disposition.EndsWith("Skipped", StringComparison.Ordinal)), errors.ToArray(), proposedEnd, overrun);
    }
}
