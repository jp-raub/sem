using System.Globalization;

namespace Sem.Web.Core;

/// <summary>
/// Read-only result of assessing whether a CSV import may proceed.
/// RejectionReason is a stable, user-readable explanation; previews must not persist observations.
/// </summary>
public sealed record CoreEventImportEligibility(bool IsEligible, string? RejectionReason);

/// <summary>
/// Tracks durable evidence relevant to importing registrations without rewriting prior state.
/// </summary>
public static class CoreEventImportHistory
{
    public const string EffectivePhaseMustBeHidden = "EffectivePhaseMustBeHidden";
    public const string CustomRegistrationPhaseAlreadyObserved = "CustomRegistrationPhaseAlreadyObserved";
    public const string ImportHistoryUnknown = "ImportHistoryUnknown";
    public const string UnobservedPastCustomWindow = "UnobservedPastCustomWindow";

    /// <summary>
    /// Pure assessment for preview/apply callers. A past configured Custom window is uncertainty,
    /// not proof that anyone registered; a Custom phase already observed is positive evidence.
    /// A future interval canceled before activation is not treated as a possible past entry.
    /// </summary>
    public static CoreEventImportEligibility Assess(SessionEvent current, EventPhase effectivePhase, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(effectivePhase);

        if (effectivePhase.Kind != "Hidden" || effectivePhase.DeletedAt is not null)
            return new(false, EffectivePhaseMustBeHidden);

        if (current.HasEverRegistrationPhase || HasActiveCustom(current) || effectivePhase.Kind == "Custom")
            return new(false, CustomRegistrationPhaseAlreadyObserved);

        if (!current.ImportHistoryKnown)
            return new(false, ImportHistoryUnknown);

        if (HasPossiblePastCustomWindow(current, now))
            return new(false, UnobservedPastCustomWindow);

        return new(true, null);
    }

    /// <summary>
    /// Records only evidence available before a write changes the phase schedule/state.
    /// Returns true when either server-only marker changed so callers can advance RowVersion.
    /// </summary>
    public static bool Observe(SessionEvent current, EventPhase effectivePhase, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(effectivePhase);
        var changed = false;
        var observedCustom = HasActiveCustom(current) ||
            effectivePhase.Kind == "Custom" && effectivePhase.DeletedAt is null;

        if (observedCustom && !current.HasEverRegistrationPhase)
        {
            current.HasEverRegistrationPhase = true;
            changed = true;
        }
        else if (!observedCustom && current.ImportHistoryKnown && HasPossiblePastCustomWindow(current, now))
        {
            // Persist uncertainty before a plan edit can remove the schedule that revealed it.
            current.ImportHistoryKnown = false;
            changed = true;
        }

        return changed;
    }

    private static bool HasActiveCustom(SessionEvent current) =>
        current.Phases.Any(p => p.Kind == "Custom" && p.IsActive && p.DeletedAt is null);

    private static bool HasPossiblePastCustomWindow(SessionEvent current, DateTimeOffset now)
    {
        foreach (var phase in current.Phases.Where(p => p.Kind == "Custom"))
        {
            // No automatic activation can be inferred from a missing activation timestamp.
            if (phase.ActivateAt is null)
                continue;
            if (!TryParse(phase.ActivateAt, out var activateAt))
                return true; // Corrupt schedule data cannot safely prove there was no past window.
            if (activateAt > now)
                continue;

            // A tombstone before (or exactly at) activation is a canceled future interval.
            if (phase.DeletedAt is not null)
            {
                if (!TryParse(phase.DeletedAt, out var deletedAt))
                    return true;
                if (deletedAt <= activateAt)
                    continue;
            }

            if (phase.DeactivateAt is not null)
            {
                if (!TryParse(phase.DeactivateAt, out var deactivateAt))
                    return true;
                if (deactivateAt <= activateAt)
                    continue;
            }

            return true;
        }
        return false;
    }

    private static bool TryParse(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);
}