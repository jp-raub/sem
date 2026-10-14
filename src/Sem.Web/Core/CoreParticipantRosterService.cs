using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;
public sealed record ParticipantRosterPart(string SongId, string SongTitle, string PartName, string Category);
public sealed record ParticipantRosterPerson(string UserId, string DisplayName, bool Spectator, ParticipantRosterPart[] Parts);
public sealed record ParticipantRosterView(string EventId, string Title, int ParticipantCount, ParticipantRosterPerson[] People, string? NextUserId);
public sealed partial class CoreFoundationService
{
    public async Task<ParticipantRosterView> ReadParticipantRosterAsync(ClaimsPrincipal principal, string id, string? afterUserId = null, CancellationToken ct = default)
    {
        if (afterUserId is not null && (afterUserId.Length is <1 or >128 || afterUserId.Any(char.IsControl)))
            throw new ValidationException("名簿の続きを確認してください。");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (e, _, _, _) = await ParticipantEvent(db, principal, id, ct);
        // Minimal public queries: never fetch question answers, receipts, profiles or email columns.
        var slots = await (from p in db.SongParts.AsNoTracking()
            join song in db.Songs.AsNoTracking() on p.SongId equals song.Id
            join definition in db.EventPartDefinitions.AsNoTracking() on p.PartDefinitionId equals definition.Id
            join user in db.Users.AsNoTracking() on p.AssignedUserId equals user.Id
            where p.EventId == id && song.EventId == id && definition.EventId == id && song.DeletedAt == null && definition.DeletedAt == null
            orderby song.SortOrder, song.Id, definition.SortOrder, p.Id
            select new { UserId = user.Id, user.DisplayName, SongId = song.Id, SongTitle = song.Title, PartName = definition.Name, definition.Category }).ToArrayAsync(ct);
        var parts = slots.GroupBy(p => p.UserId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new ParticipantRosterPerson(g.Key, g.First().DisplayName, false,
                g.Select(p => new ParticipantRosterPart(p.SongId, p.SongTitle, p.PartName, p.Category)).ToArray()), StringComparer.Ordinal);
        var spectators = await (from spectator in db.EventSpectators.AsNoTracking()
            join user in db.Users.AsNoTracking() on spectator.UserId equals user.Id
            where spectator.EventId == id select new { UserId = user.Id, user.DisplayName }).ToArrayAsync(ct);
        foreach (var spectator in spectators)
            if (parts.TryGetValue(spectator.UserId, out var performer)) parts[spectator.UserId] = performer with { Spectator = true };
            else parts.Add(spectator.UserId, new(spectator.UserId, spectator.DisplayName, true, []));
        var page = parts.Values.Where(p => afterUserId is null || StringComparer.Ordinal.Compare(p.UserId, afterUserId) > 0)
            .OrderBy(p => p.UserId, StringComparer.Ordinal).Take(101).ToArray();
        await tx.CommitAsync(ct);
        return new(id, e.Title, parts.Count, page.Take(100).ToArray(), page.Length > 100 ? page[99].UserId : null);
    }
}
