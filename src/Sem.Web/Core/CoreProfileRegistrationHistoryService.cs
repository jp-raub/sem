using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Core;

public sealed record ProfileRegistrationHistoryPart(
    string SongId,
    string SongTitle,
    string PartDefinitionId,
    string PartName,
    string Category);

public sealed record ProfileRegistrationHistoryEvent(
    string EventId,
    string Title,
    string ScheduledDate,
    bool Spectator,
    ProfileRegistrationHistoryPart[] Parts);

public sealed record ProfileRegistrationHistoryView(string UserId, ProfileRegistrationHistoryEvent[] Events);

public sealed partial class CoreFoundationService
{
    private const int ProfileRegistrationHistoryCandidateLimit = 100;

    /// <summary>
    /// Reads a bounded, current-state view of a member's event registrations.
    /// The candidate window is selected before applying the caller's event visibility.
    /// </summary>
    public async Task<ProfileRegistrationHistoryView> ReadProfileRegistrationHistoryAsync(
        ClaimsPrincipal principal, string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Validate the caller's current authenticated session before looking up the target.
        await Actor(db, principal, ct);
        if (!await db.Users.AsNoTracking().AnyAsync(
                user => user.Id == userId && user.Enabled && user.EmailConfirmed, ct))
            throw new UnauthorizedAccessException("プロフィールを閲覧できません。");

        // Candidate events come only from current assignments with live songs and event-local
        // part definitions, or from the current spectator relation. UNION deduplicates events.
        var performerEventIds =
            from part in db.SongParts.AsNoTracking()
            join song in db.Songs.AsNoTracking() on part.SongId equals song.Id
            join definition in db.EventPartDefinitions.AsNoTracking() on part.PartDefinitionId equals definition.Id
            where part.AssignedUserId == userId
                  && song.EventId == part.EventId
                  && definition.EventId == part.EventId
                  && song.DeletedAt == null
                  && definition.DeletedAt == null
            select part.EventId;
        var candidateEventIds = performerEventIds.Union(
            db.EventSpectators.AsNoTracking()
                .Where(spectator => spectator.UserId == userId)
                .Select(spectator => spectator.EventId));

        // The newest 100 candidate events are chosen before authorization filtering.
        var candidates = await (
                from sessionEvent in db.SessionEvents.AsNoTracking()
                join candidateId in candidateEventIds on sessionEvent.Id equals candidateId
                orderby sessionEvent.ScheduledDate descending, sessionEvent.Id
                select new
                {
                    sessionEvent.Id,
                    sessionEvent.Title,
                    sessionEvent.ScheduledDate
                })
            .Take(ProfileRegistrationHistoryCandidateLimit)
            .ToArrayAsync(ct);

        var authorizedEventIds = new List<string>(candidates.Length);
        foreach (var candidate in candidates)
        {
            try
            {
                // Keep event access exactly aligned with the shared current ParticipantEvent
                // logic: current actor, one active phase, current automatic/manual phase, and
                // current global/event-manager privileges. Target membership grants nothing.
                await ParticipantEvent(db, principal, candidate.Id, ct);
                authorizedEventIds.Add(candidate.Id);
            }
            catch (UnauthorizedAccessException)
            {
                // Do not return any indication that this candidate exists.
            }
            catch (InvalidOperationException)
            {
                // Invalid event phase configuration is omitted just like an inaccessible event.
            }
            catch (FormatException)
            {
                // Malformed automatic-phase dates are treated as misconfiguration, not exposed.
            }
        }

        if (authorizedEventIds.Count == 0)
        {
            await tx.CommitAsync(ct);
            return new(userId, []);
        }

        // Fetch only the target's current part rows belonging to authorized candidate events.
        var authorizedIds = authorizedEventIds.ToArray();
        var parts = await (
                from part in db.SongParts.AsNoTracking()
                join song in db.Songs.AsNoTracking() on part.SongId equals song.Id
                join definition in db.EventPartDefinitions.AsNoTracking() on part.PartDefinitionId equals definition.Id
                where part.AssignedUserId == userId
                      && authorizedIds.Contains(part.EventId)
                      && song.EventId == part.EventId
                      && definition.EventId == part.EventId
                      && song.DeletedAt == null
                      && definition.DeletedAt == null
                orderby song.SortOrder, song.Id, definition.SortOrder, definition.Id, part.Id
                select new
                {
                    part.EventId,
                    Part = new ProfileRegistrationHistoryPart(
                        song.Id, song.Title, definition.Id, definition.Name, definition.Category)
                })
            .ToArrayAsync(ct);
        var partsByEvent = parts
            .GroupBy(row => row.EventId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Part).ToArray(), StringComparer.Ordinal);

        var spectatorEventIds = await db.EventSpectators.AsNoTracking()
            .Where(spectator => spectator.UserId == userId && authorizedIds.Contains(spectator.EventId))
            .Select(spectator => spectator.EventId)
            .ToArrayAsync(ct);
        var spectatorSet = spectatorEventIds.ToHashSet(StringComparer.Ordinal);

        var result = candidates
            .Where(candidate => authorizedEventIds.Contains(candidate.Id, StringComparer.Ordinal))
            .Select(candidate => new ProfileRegistrationHistoryEvent(
                candidate.Id,
                candidate.Title,
                candidate.ScheduledDate,
                spectatorSet.Contains(candidate.Id),
                partsByEvent.GetValueOrDefault(candidate.Id, [])))
            .ToArray();

        await tx.CommitAsync(ct);
        return new(userId, result);
    }
}
