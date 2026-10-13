using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sem.Web.Core;

// Input policy only. Persistence, authorization and transactional approval are not wired yet.
public sealed record ShorteningPolicy(string PartId, bool AllowPrep, int MinPrepSeconds,
    bool AllowDuration, int MinDurationSeconds);
public sealed record ShorteningCut(string PartId, string Title, string Field,
    int BeforeSeconds, int AfterSeconds) {
    public int CutSeconds => BeforeSeconds - AfterSeconds;
}
public sealed record ShorteningProposal(string Token, DateTimeOffset? Target,
    DateTimeOffset? BeforeEnd, DateTimeOffset? AfterEnd, long DelaySeconds,
    long AbsorbedSeconds, long RemainingSeconds, ShorteningCut[] Cuts) {
    public bool HasCuts => Cuts.Length > 0;
}

public static class ShorteningCalculator {
    private static long Delay(DateTimeOffset end, DateTimeOffset target) =>
        Math.Max(0, (long)Math.Ceiling((end - target).TotalSeconds));

    public static ShorteningProposal Build(string eventId, long eventVersion, long progressVersion,
        ProgressBaseline baseline, IEnumerable<ProgressMark> storedMarks,
        IEnumerable<ProgressEstimate> storedEstimates, IEnumerable<ShorteningPolicy> storedPolicies) {
        if (string.IsNullOrWhiteSpace(eventId) || eventVersion < 0 || progressVersion < 0)
            throw new ValidationException("イベントと版を確認してください。");
        var marks = storedMarks.ToArray();
        var estimates = storedEstimates.Select(e => new ProgressEstimate {
            EventId = e.EventId, PartId = e.PartId,
            PrepSeconds = e.PrepSeconds, DurationSeconds = e.DurationSeconds
        }).ToList();
        var policies = storedPolicies.ToArray();
        var ids = baseline.Rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != baseline.Rows.Length || ids.Any(string.IsNullOrWhiteSpace) ||
            baseline.Rows.Any(r => r.Kind is not ("Song" or "Transition" or "Opening")))
            throw new ValidationException("基準のタイムパートを確認してください。");
        if (marks.Any(m => m.EventId != eventId || !ids.Contains(m.PartId)) ||
            marks.GroupBy(m => (m.PartId, m.Point)).Any(g => g.Count() > 1) ||
            estimates.Any(e => e.EventId != eventId || !ids.Contains(e.PartId) ||
                e.PrepSeconds < 0 || e.DurationSeconds < 0) ||
            estimates.GroupBy(e => e.PartId).Any(g => g.Count() > 1) ||
            policies.Any(p => !ids.Contains(p.PartId) || p.MinPrepSeconds < 0 ||
                p.MinDurationSeconds < 0) ||
            policies.GroupBy(p => p.PartId).Any(g => g.Count() > 1))
            throw new ValidationException("実績・見積・短縮条件の対象と秒数を確認してください。");
        foreach (var mark in marks) {
            var row = baseline.Rows.Single(r => r.Id == mark.PartId);
            if (mark.Point is not ("PrepStart" or "Start" or "End") ||
                mark.Point == "PrepStart" && row.Kind != "Song")
                throw new ValidationException("記録点を確認してください。");
        }
        foreach (var edit in estimates) {
            var row = baseline.Rows.Single(r => r.Id == edit.PartId);
            if (row.Kind == "Opening" ||
                row.Kind == "Song" && edit.DurationSeconds is not null ||
                row.Kind == "Transition" && (edit.PrepSeconds is not null || edit.DurationSeconds is null))
                throw new ValidationException("演奏時間と開場行は短縮できません。");
        }
        var rows = ProgressCalculator.Calculate(baseline, marks, estimates);
        var byPolicy = policies.ToDictionary(p => p.PartId, StringComparer.Ordinal);
        foreach (var policy in policies) {
            var row = rows.Single(r => r.Original.Id == policy.PartId);
            if (policy.AllowPrep && row.Original.Kind != "Song" ||
                policy.AllowDuration && row.Original.Kind != "Transition" ||
                policy.AllowPrep && policy.MinPrepSeconds > row.Latest.ConfiguredPrepSeconds ||
                policy.AllowDuration && policy.MinDurationSeconds > row.Latest.LengthSeconds)
                throw new ValidationException("短縮対象と最短時間を確認してください。演奏は短縮不可です。");
        }

        // Canonical input fingerprint, scoped to event and BOTH versions. It is not authorization.
        // Approval must recalculate this inside its authorized write transaction, never trust cuts.
        var canonical = JsonSerializer.Serialize(new {
            Algorithm = "shortening-v1", EventId = eventId, EventVersion = eventVersion,
            ProgressVersion = progressVersion,
            Baseline = new {
                baseline.ScheduledDate, baseline.RoundStarts, baseline.DefaultPrepSeconds,
                baseline.ClockFormat, baseline.PlannedEndAt, baseline.Rows,
                PrepInputs = baseline.PrepInputs.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new { p.Key, p.Value }).ToArray()
            },
            Marks = marks.OrderBy(m => m.PartId, StringComparer.Ordinal)
                .ThenBy(m => m.Point, StringComparer.Ordinal).Select(m => new {
                    m.EventId, m.PartId, m.Point, m.At, m.ActorId, m.OperationId
                }).ToArray(),
            Estimates = estimates.OrderBy(e => e.PartId, StringComparer.Ordinal).Select(e => new {
                e.EventId, e.PartId, e.PrepSeconds, e.DurationSeconds
            }).ToArray(),
            Policies = policies.OrderBy(p => p.PartId, StringComparer.Ordinal).ToArray()
        });
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        if (rows.Length == 0)
            return new(token, null, null, null, 0, 0, 0, []);
        var target = baseline.PlannedEndAt is string planned
            ? TimetableCalculator.Instant(planned) : rows[^1].Original.End;
        var beforeEnd = rows[^1].Latest.End;
        var end = beforeEnd;
        var initialDelay = Delay(end, target);
        var cuts = new List<ShorteningCut>();
        foreach (var row in rows) {
            if (end <= target) break;
            // Any recorded point protects the ENTIRE part, including prep already underway.
            if (row.Original.Kind == "Opening" || row.Points.Any(p => p.Actual is not null) ||
                !byPolicy.TryGetValue(row.Original.Id, out var policy)) continue;
            var prep = row.Original.Kind == "Song";
            if (!(prep ? policy.AllowPrep : policy.AllowDuration)) continue;
            var before = prep ? row.Latest.ConfiguredPrepSeconds!.Value : row.Latest.LengthSeconds;
            var minimum = prep ? policy.MinPrepSeconds : policy.MinDurationSeconds;
            if (minimum == before) continue;
            var edit = estimates.SingleOrDefault(e => e.PartId == row.Original.Id);
            if (edit is null) {
                edit = new() { EventId = eventId, PartId = row.Original.Id };
                estimates.Add(edit);
            }
            DateTimeOffset Evaluate(int seconds) {
                if (prep) edit.PrepSeconds = seconds;
                else edit.DurationSeconds = seconds;
                return ProgressCalculator.Calculate(baseline, marks, estimates)[^1].Latest.End;
            }
            var floorEnd = Evaluate(minimum);
            var after = minimum;
            if (floorEnd <= target) {
                // Largest integer seconds meeting target: avoids over-shortening and accounts
                // for minute-boundary plateaus rather than equating cut seconds to recovery.
                var low = minimum;
                var high = before;
                while (low < high) {
                    var middle = low + (high - low + 1L) / 2;
                    if (Evaluate((int)middle) <= target) low = (int)middle;
                    else high = (int)middle - 1;
                }
                after = low;
            }
            end = Evaluate(after);
            if (after < before)
                cuts.Add(new(row.Original.Id, row.Original.Title,
                    prep ? "PrepSeconds" : "DurationSeconds", before, after));
        }
        var remaining = Delay(end, target);
        return new(token, target, beforeEnd, end, initialDelay,
            initialDelay - remaining, remaining, cuts.ToArray());
    }
}