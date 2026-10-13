using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Sem.Web.Core;

int passed = 0;
void Check(bool value, string label) {
    if (!value) throw new Exception(label);
    passed++; Console.WriteLine("PASS " + label);
}
void Deny(Action action, string label) {
    try { action(); } catch (ValidationException) { Check(true, label); return; }
    throw new Exception("Expected denial: " + label);
}
var start = DateTimeOffset.Parse("2026-11-01T03:00:00.000Z");
ProgressBaseline Plan(bool rounded, int? goal, params (string Kind, int Prep, int Length)[] parts) {
    var cursor = start; var rows = new List<TimedPart>();
    for (int i = 0; i < parts.Length; i++) {
        var (kind, prep, length) = parts[i]; var begin = cursor;
        DateTimeOffset Ceil(DateTimeOffset at) {
            var rem = at.UtcTicks % TimeSpan.TicksPerMinute;
            return new(rem == 0 ? at.UtcTicks : at.UtcTicks + TimeSpan.TicksPerMinute - rem, TimeSpan.Zero);
        }
        DateTimeOffset? prepAt = kind == "Song" ? rounded ? Ceil(begin) : begin : null;
        var performAt = kind == "Song" ? prepAt!.Value.AddSeconds(prep) : begin;
        if (kind == "Song" && rounded) performAt = Ceil(performAt);
        cursor = performAt.AddSeconds(length);
        rows.Add(new("p" + i, kind, "Part " + i, "", "", kind == "Song" ? "song" + i : null,
            prepAt, performAt, cursor, length, kind == "Song" ? prep : null));
    }
    return new(start.ToString("O"), rounded, 60, "seconds",
        goal is int seconds ? start.AddSeconds(seconds).ToString("O") : null,
        rows.ToArray(), rows.Where(r => r.Kind == "Song").ToDictionary(r => r.SongId!, r => (int?)r.ConfiguredPrepSeconds));
}
ShorteningPolicy Prep(string id, int min) => new(id, true, min, false, 0);
ShorteningPolicy Duration(string id, int min) => new(id, false, 0, true, min);
ShorteningProposal Build(ProgressBaseline b, ShorteningPolicy[]? policies = null,
    ProgressMark[]? marks = null, ProgressEstimate[]? estimates = null, long ev = 7, long live = 3,
    string eventId = "event") => ShorteningCalculator.Build(eventId, ev, live, b,
        marks ?? [], estimates ?? [], policies ?? []);
ProgressMark Mark(string id, string point, DateTimeOffset at) =>
    new() { EventId = "event", PartId = id, Point = point, At = at.ToString("O"), ActorId = "manager", OperationId = "record" };

var breakPlan = Plan(false, 480, ("Transition", 0, 900));
var p = Build(breakPlan, [Duration("p0", 300)]);
Check(p.DelaySeconds == 420 && p.Cuts.Single().AfterSeconds == 480 &&
    p.AbsorbedSeconds == 420 && p.RemainingSeconds == 0, "15min break / minimum5 / delay7 proposes8min");
Check(p.Cuts.Single().BeforeSeconds == 900 && p.Cuts.Single().CutSeconds == 420,
    "Proposal shows exact before after and reduction");
var protectedPlan = Build(breakPlan);
Check(!protectedPlan.HasCuts && protectedPlan.RemainingSeconds == 420, "Missing policy defaults to time protection");
Check(!Build(breakPlan, [new("p0", false, 0, false, 0)]).HasCuts, "Disabled policy never creates cuts");
p = Build(Plan(false, 0, ("Transition", 0, 900)), [Duration("p0", 300)]);
Check(p.Cuts.Single().AfterSeconds == 300 && p.RemainingSeconds == 300 &&
    p.AbsorbedSeconds == 600, "Unrecoverable delay remains visible without crossing minimum");
Check(!Build(Plan(false, 1000, ("Transition", 0, 900)), [Duration("p0", 0)]).HasCuts,
    "No delay produces no proposal");
Check(!Build(Plan(false, null, ("Transition", 0, 900)), [Duration("p0", 0)]).HasCuts,
    "Absent goal falls back to original final end");
var fallback = Plan(false, null, ("Song", 60, 60));
p = Build(fallback, [Prep("p0", 0)], estimates: [new() { EventId = "event", PartId = "p0", PrepSeconds = 120 }]);
Check(p.Target == fallback.Rows[^1].End && p.Cuts.Single().AfterSeconds == 60,
    "Fallback target remains original when latest estimate changes");
p = Build(Plan(false, 20, ("Song", 60, 20)), [Prep("p0", 0)]);
Check(p.Cuts.Single().AfterSeconds == 0 && p.AfterEnd == start.AddSeconds(20),
    "Only song preparation is reduced; performance stays intact");
Deny(() => Build(Plan(false, 1, ("Song", 60, 20)), [Duration("p0", 0)]), "Forged performance shortening policy rejected");
Deny(() => Build(breakPlan, [Prep("p0", 0)]), "Transition cannot forge preparation shortening");
Deny(() => Build(Plan(false, 0, ("Opening", 0, 60)), [Duration("p0", 0)]), "Opening cannot become a shortening target");
Deny(() => Build(breakPlan, [Duration("foreign", 0)]), "Unknown policy part rejected");
Deny(() => Build(breakPlan, [Duration("p0", 0), Duration("p0", 0)]), "Duplicate policy rejected");
Deny(() => Build(breakPlan, [Duration("p0", -1)]), "Negative floor rejected");
Deny(() => Build(breakPlan, [Duration("p0", 901)]), "Floor above latest duration rejected");
Check(!Build(breakPlan, [Duration("p0", 900)]).HasCuts, "Equal duration floor protects all time");
var songPlan = Plan(false, 20, ("Song", 60, 20));
Check(!Build(songPlan, [Prep("p0", 0)], [Mark("p0", "PrepStart", start)]).HasCuts,
    "Recorded prep start protects whole song even before performance");
Check(!Build(breakPlan, [Duration("p0", 0)], [Mark("p0", "Start", start)]).HasCuts,
    "Started transition protected");
Check(!Build(breakPlan, [Duration("p0", 0)], [Mark("p0", "End", start.AddSeconds(900))]).HasCuts,
    "Completed part protected");
var ordered = Plan(false, 300, ("Transition", 0, 500), ("Song", 100, 100), ("Transition", 0, 200));
p = Build(ordered, [Duration("p2", 0), Prep("p1", 0), Duration("p0", 200)]);
Check(p.Cuts.Select(c => c.PartId).SequenceEqual(new[] { "p0", "p1", "p2" }),
    "Reduction order follows timeline not policy array");
Check(p.Cuts[0].AfterSeconds == 200 && p.Cuts[1].AfterSeconds == 0 &&
    p.Cuts[2].AfterSeconds == 0 && p.RemainingSeconds == 0, "Sequential minima stop once target is met");
var exact = Plan(false, 500, ("Transition", 0, 500), ("Transition", 0, 500));
p = Build(exact, [Duration("p0", 0), Duration("p1", 0)]);
Check(p.Cuts.Length == 1 && p.Cuts[0].PartId == "p0", "Later permitted parts unchanged once recovered");
var rounded = Plan(true, 120, ("Song", 61, 60));
p = Build(rounded, [Prep("p0", 0)]);
Check(p.Cuts.Single().AfterSeconds == 60 && p.DelaySeconds == 60 && p.AbsorbedSeconds == 60,
    "Minute rounding: one-second cut can recover an entire minute");
var plateau = Plan(true, 90, ("Song", 61, 60));
p = Build(plateau, [Prep("p0", 59)]);
Check(p.Cuts.Single().AfterSeconds == 59 && p.RemainingSeconds == 30 &&
    p.AbsorbedSeconds == 60, "Minute plateau cannot promise unreachable recovery");
p = Build(Plan(false, 19, ("Song", 0, 20)), [Prep("p0", 0)]);
Check(!p.HasCuts && p.RemainingSeconds == 1, "Zero preparation does not produce phantom reduction");
var fractional = Plan(false, 100, ("Transition", 0, 100)) with {
    PlannedEndAt = start.AddSeconds(99).AddMilliseconds(500).ToString("O")
};
p = Build(fractional, [Duration("p0", 0)]);
Check(p.DelaySeconds == 1 && p.Cuts.Single().AfterSeconds == 99 && p.RemainingSeconds == 0,
    "Subsecond delay safely requires an integer-second cut");
var originalJson = JsonSerializer.Serialize(ordered);
var edits = new[] { new ProgressEstimate { EventId = "event", PartId = "p1", PrepSeconds = null } };
var marks = new[] { Mark("p0", "Start", start.AddMilliseconds(123)) };
var policies = new[] { Prep("p1", 0), Duration("p2", 0), Duration("p0", 200) };
var inputs = JsonSerializer.Serialize(new { edits, marks, policies });
p = Build(ordered, policies, marks, edits);
Check(JsonSerializer.Serialize(ordered) == originalJson, "Original timeline remains byte-equivalent");
Check(JsonSerializer.Serialize(new { edits, marks, policies }) == inputs, "Proposal never mutates caller estimates marks or policies");
Check(p.Token == Build(ordered, policies.Reverse().ToArray(), marks, edits).Token,
    "Canonical token independent of policy input order");
Check(p.Token != Build(ordered, policies, marks, edits, ev: 8).Token, "Event version change invalidates token");
Check(p.Token != Build(ordered, policies, marks, edits, live: 4).Token, "Live version change invalidates token");
Check(p.Token != Build(ordered, policies.Select(x => x.PartId == "p1" ? x with { MinPrepSeconds = 1 } : x).ToArray(), marks, edits).Token,
    "Policy change invalidates token");
Check(p.Token != Build(ordered, policies, marks, [new() { EventId = "event", PartId = "p1", PrepSeconds = 100 }]).Token,
    "Equal resolved prep but changed explicit input invalidates token");
Check(p.Token != Build(ordered, policies, [Mark("p0", "Start", start.AddMilliseconds(124))], edits).Token,
    "Millisecond actual change invalidates token");
Check(p.Token.Length == 64 && Build(breakPlan).Token != Build(breakPlan, eventId: "different").Token,
    "Token is SHA256 and scoped to event");
Deny(() => Build(breakPlan, estimates: [new() { EventId = "other", PartId = "p0", DurationSeconds = 1 }]), "Cross-event estimate rejected");
Deny(() => Build(breakPlan, marks: [new() { EventId = "other", PartId = "p0", Point = "Start", At = start.ToString("O") }]), "Cross-event actual rejected");
Deny(() => Build(breakPlan, marks: [Mark("p0", "PrepStart", start)]), "Transition prep point rejected");
Deny(() => Build(songPlan, estimates: [new() { EventId = "event", PartId = "p0", DurationSeconds = 1 }]), "Performance estimate tampering rejected");
Deny(() => Build(breakPlan, estimates: [new() { EventId = "event", PartId = "p0", DurationSeconds = -1 }]), "Negative estimate rejected");
Deny(() => Build(breakPlan, ev: -1), "Negative version rejected");
var empty = Build(Plan(false, null));
Check(empty.Target is null && empty.BeforeEnd is null && empty.AfterEnd is null &&
    empty.DelaySeconds == 0 && !empty.HasCuts, "Empty event has no invented target or cuts");
var inherited = Plan(false, 20, ("Song", 60, 20));
var inheritEdit = new[] { new ProgressEstimate { EventId = "event", PartId = "p0", PrepSeconds = null } };
Build(inherited, [Prep("p0", 0)], estimates: inheritEdit);
Check(inheritEdit[0].PrepSeconds is null, "Inherited null preparation preserved in caller input");
var reorderBase = ordered with { PrepInputs = ordered.PrepInputs.Reverse().ToDictionary(x => x.Key, x => x.Value) };
Check(Build(ordered, policies).Token == Build(reorderBase, policies).Token,
    "Canonical baseline dictionary order does not churn token");
var changedGoal = ordered with { PlannedEndAt = start.AddSeconds(301).ToString("O") };
Check(Build(ordered, policies).Token != Build(changedGoal, policies).Token,
    "Changed end target invalidates token");
var multiMarks = new[] { Mark("p0", "Start", start), Mark("p0", "End", start.AddSeconds(500)) };
var multiEdits = new[] {
    new ProgressEstimate { EventId = "event", PartId = "p1", PrepSeconds = 100 },
    new ProgressEstimate { EventId = "event", PartId = "p2", DurationSeconds = 200 }
};
Check(Build(ordered, policies, multiMarks, multiEdits).Token ==
    Build(ordered, policies.Reverse().ToArray(), multiMarks.Reverse().ToArray(),
        multiEdits.Reverse().ToArray()).Token,
    "Canonical token independent of mark and estimate enumeration order");
Deny(() => Build(ordered, policies, multiMarks, [multiEdits[0], multiEdits[0]]),
    "Duplicate estimates cannot hide conflicting values");
Deny(() => Build(ordered, policies, [multiMarks[0], multiMarks[0]]),
    "Duplicate actual points rejected");
Deny(() => Build(ordered, policies, [Mark("foreign", "Start", start)]),
    "Unknown actual part rejected");
Deny(() => Build(ordered, policies, estimates: [
    new() { EventId = "event", PartId = "foreign", PrepSeconds = 1 }]),
    "Unknown estimate part rejected");
var protectedActual = Plan(false, 300, ("Transition", 0, 100), ("Song", 100, 100), ("Transition", 0, 100));
var actualInputs = new[] {
    Mark("p0", "Start", start.AddMilliseconds(123)),
    Mark("p0", "End", start.AddSeconds(100).AddMilliseconds(123)),
    Mark("p1", "PrepStart", start.AddSeconds(100).AddMilliseconds(123))
};
p = Build(protectedActual, [Duration("p0", 0), Prep("p1", 0), Duration("p2", 0)], actualInputs);
Check(p.Cuts.All(c => c.PartId == "p2") && p.Cuts.Single().AfterSeconds == 0 &&
    p.RemainingSeconds == 1, "Started prep remains protected; future transition absorbs only permitted delay");
Check(p.AfterEnd == start.AddSeconds(300).AddMilliseconds(123),
    "Actual millisecond anchor remains exact through proposal recomputation");
var inheritedZero = inherited with { DefaultPrepSeconds = 0 };
p = Build(inheritedZero, [Prep("p0", 0)], estimates: inheritEdit);
Check(!p.HasCuts && p.RemainingSeconds == 0, "Inherited zero default produces no phantom preparation");
var huge = Plan(false, 1, ("Transition", 0, int.MaxValue));
p = Build(huge, [Duration("p0", 0)]);
Check(p.Cuts.Single().AfterSeconds == 1 && p.RemainingSeconds == 0,
    "Binary search uses non-overflowing midpoint at integer maximum");
for (int prep = 1; prep <= 125; prep += 7) {
    var b = Plan(true, 120, ("Song", prep, 60));
    var proposal = Build(b, [Prep("p0", 0)]);
    Check(proposal.AfterEnd <= proposal.BeforeEnd && proposal.RemainingSeconds <= proposal.DelaySeconds &&
        proposal.Cuts.All(c => c.AfterSeconds >= 0 && c.AfterSeconds < c.BeforeSeconds),
        "Rounded sweep conserves bounds and never extends end: " + prep);
}
Console.WriteLine($"Shortening checks: {passed} passed");