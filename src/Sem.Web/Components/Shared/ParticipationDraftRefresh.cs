using Sem.Web.Core;

namespace Sem.Web.Components.Shared;

// UI-only reconciliation. Authorization and optimistic concurrency remain in Core.
public sealed record ParticipationDraftUpdate(HashSet<string> Selected,
    Dictionary<string, string?> Answers, string[] RemovedSlots, bool RequiresReview);

public static class ParticipationDraftRefresh
{
    public static ParticipationDraftUpdate Reconcile(ParticipationPlan previous, ParticipationPlan latest,
        IReadOnlySet<string> selected, IReadOnlyDictionary<string, string?> answers,
        bool releaseMode, bool additionalDraft = false)
    {
        if (previous.EventId != latest.EventId || previous.OwnUserId != latest.OwnUserId)
            throw new InvalidOperationException("Cannot reconcile a different event or subject");
        var dirty = additionalDraft || selected.Count > 0 || previous.Questions.Any(q =>
            !string.Equals(answers.GetValueOrDefault(q.Id), q.OwnValue, StringComparison.Ordinal));
        var oldSlots = previous.Slots.ToDictionary(s => s.Id);
        var newSlots = latest.Slots.ToDictionary(s => s.Id);
        var retained = new HashSet<string>(StringComparer.Ordinal);
        var removed = new List<string>();
        var selectedVersionChanged = false;
        foreach (var id in selected)
        {
            if (oldSlots.TryGetValue(id, out var old) && newSlots.TryGetValue(id, out var next)
                && old.SongId == next.SongId && old.PartName == next.PartName && old.Category == next.Category
                && (releaseMode ? next.AssignedUserId == latest.OwnUserId && latest.CanManage : next.AssignedUserId is null))
            {
                retained.Add(id);
                selectedVersionChanged |= old.RowVersion != next.RowVersion;
            }
            else removed.Add(oldSlots.TryGetValue(id, out var slot) ? $"{slot.Title} / {slot.PartName}" : id);
        }
        var oldQuestions = previous.Questions.ToDictionary(q => q.Id);
        var nextAnswers = latest.Questions.ToDictionary(q => q.Id, q => dirty
            && oldQuestions.TryGetValue(q.Id, out var old) && old.Type == q.Type
                ? answers.GetValueOrDefault(q.Id) : q.OwnValue);
        var questionShapeChanged = !previous.Questions.Select(q => (q.Id,q.Text,q.Type,q.Required,q.Public,q.PublicText))
            .SequenceEqual(latest.Questions.Select(q => (q.Id,q.Text,q.Type,q.Required,q.Public,q.PublicText)));
        var review = dirty && (previous.RowVersion != latest.RowVersion
            || previous.ParticipantVersion != latest.ParticipantVersion
            || previous.PhaseId != latest.PhaseId || previous.PhaseKind != latest.PhaseKind
            || previous.MaxPicks != latest.MaxPicks || previous.CanManage != latest.CanManage
            || questionShapeChanged || selectedVersionChanged);
        return new(retained, nextAnswers, removed.ToArray(), review);
    }
}
