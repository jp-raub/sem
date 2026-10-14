using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Core;

// Immutable receipt only: no raw CSV, assignment values or client-provided row IDs.
public sealed class EventImportOperation
{
    public string Id { get; set; } = "";
    public string EventId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public long ResultVersion { get; set; }
    public int NewSongs { get; set; }
    public int NewTransitions { get; set; }
    public int SkippedRows { get; set; }
    public long AcceptedAt { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EventImportApply(
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] long RowVersion,
    [property: JsonRequired] string PreviewFingerprint,
    [property: JsonRequired] string Csv,
    [property: JsonRequired] bool Confirmed,
    [property: JsonRequired] bool ConfirmOverrun);
public sealed record EventImportAcceptance(string RequestId, long RowVersion, int NewSongs,
    int NewTransitions, int SkippedRows, long AcceptedAt, bool Replayed = false);

public sealed partial class CoreFoundationService
{
    public async Task<EventImportAcceptance> ApplyEventImportAsync(ClaimsPrincipal principal,
        string eventId, EventImportApply edit, CancellationToken ct = default)
    {
        if (edit is null || !edit.Confirmed || edit.RowVersion < 0 ||
            !Guid.TryParseExact(edit.RequestId, "D", out var request) || request.ToString("D") != edit.RequestId ||
            edit.PreviewFingerprint is null || edit.PreviewFingerprint.Length != 64 ||
            edit.PreviewFingerprint.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            edit.Csv is null)
            throw new ValidationException("取込内容・確認・版・操作Idを確認してください。");

        // Parser output has assignment-presence booleans, never assignment plaintext.
        var input = EventImportCsvParser.Parse(edit.Csv);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Protocol = "event-import-apply-v1", Event = eventId,
                edit.RowVersion, edit.PreviewFingerprint, Input = input, edit.Confirmed, edit.ConfirmOverrun }))));
        await using var db = await factory.CreateDbContextAsync(ct);
        // SQLite uses a non-deferred write transaction: concurrent accepts serialize.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (e, actor, phase, manager) = await ParticipantEvent(db, principal, eventId, ct);
        if (!manager) throw new UnauthorizedAccessException();
        // Current authorization is required even to recover an old successful result.
        var previous = await db.Set<EventImportOperation>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == edit.RequestId, ct);
        if (previous is not null)
        {
            if (previous.EventId != eventId || previous.ActorId != actor.Id || previous.RequestHash != hash)
                throw new ValidationException("操作Idは別の取込に再利用できません。");
            await tx.CommitAsync(ct);
            return ImportAcceptance(previous, true);
        }
        if (e.RowVersion != edit.RowVersion) throw new CoreConflictException();
        var preview = await BuildEventImportPreview(db, e, actor, phase, edit.Csv, ct);
        if (!preview.IsValid) throw new ValidationException("取込プレビューのエラーを修正してください。");
        if (preview.Fingerprint != edit.PreviewFingerprint) throw new CoreConflictException();
        if (preview.OverrunSeconds is > 0 && !edit.ConfirmOverrun)
            throw new ValidationException("終了予定の超過を確認してください。");
        if (e.RowVersion == long.MaxValue) throw new ValidationException("イベントの版が上限です。");

        var definitions = await db.EventPartDefinitions.AsNoTracking()
            .Where(x => x.EventId == eventId && x.DeletedAt == null).ToArrayAsync(ct);
        var lastSongOrder = await db.Songs.Where(x => x.EventId == eventId && x.DeletedAt == null)
            .Select(x => (int?)x.SortOrder).MaxAsync(ct);
        var lastPartOrder = await db.TimeParts.Where(x => x.EventId == eventId && x.DeletedAt == null)
            .Select(x => (int?)x.SortOrder).MaxAsync(ct);
        var ready = preview.Rows.Where(x => x.Disposition == "Ready").ToArray();
        if ((long)(lastSongOrder ?? -1) + preview.NewSongs > int.MaxValue ||
            (long)(lastPartOrder ?? -1) + ready.Length > int.MaxValue)
            throw new ValidationException("並び順が上限です。");
        var songOrder = (long)(lastSongOrder ?? -1) + 1;
        var partOrder = (long)(lastPartOrder ?? -1) + 1;
        foreach (var row in ready)
        {
            if (row.Kind == "Song")
            {
                var song = new Song { EventId = eventId, Title = row.Title, Artist = row.Artist,
                    PerformanceSeconds = row.DurationSeconds!.Value, PrepSeconds = row.PrepSeconds,
                    SortOrder = checked((int)songOrder++) };
                db.Songs.Add(song);
                db.TimeParts.Add(new TimePart { EventId = eventId, Kind = "Song", SongId = song.Id,
                    SortOrder = checked((int)partOrder++) });
                foreach (var name in row.PartNames)
                {
                    var definition = definitions.Single(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    db.SongParts.Add(new SongPart { EventId = eventId, SongId = song.Id,
                        PartDefinitionId = definition.Id });
                }
            }
            else
                db.TimeParts.Add(new TimePart { EventId = eventId, Kind = "Transition", Name = row.Title,
                    DurationSeconds = row.DurationSeconds!.Value, SortOrder = checked((int)partOrder++) });
        }
        e.RowVersion++;
        var receipt = new EventImportOperation { Id = edit.RequestId, EventId = eventId, ActorId = actor.Id,
            RequestHash = hash, ResultVersion = e.RowVersion, NewSongs = preview.NewSongs,
            NewTransitions = preview.NewTransitions, SkippedRows = preview.SkippedRows,
            AcceptedAt = clock.GetUtcNow().ToUnixTimeSeconds() };
        db.Add(receipt);
        Audit(db, actor.Id, "EventImportApplied", receipt.Id);
        // Plan + event version + immutable receipt + audit succeed or roll back together.
        await db.SaveChangesAsync(ct);
        await ValidatePlannedPolicies(db, e, ct);
        await tx.CommitAsync(ct);
        return ImportAcceptance(receipt, false);
    }

    private static EventImportAcceptance ImportAcceptance(EventImportOperation x, bool replayed) =>
        new(x.Id, x.ResultVersion, x.NewSongs, x.NewTransitions, x.SkippedRows, x.AcceptedAt, replayed);
}