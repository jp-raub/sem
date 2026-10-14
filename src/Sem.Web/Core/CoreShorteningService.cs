using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;

namespace Sem.Web.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ShorteningPolicyEdit(
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] long RowVersion,
    [property: JsonRequired] long ProgressVersion,
    [property: JsonRequired] string PartId,
    [property: JsonRequired] bool AllowPrep,
    [property: JsonRequired] int MinPrepSeconds,
    [property: JsonRequired] bool AllowDuration,
    [property: JsonRequired] int MinDurationSeconds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ShorteningApproveEdit(
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] long RowVersion,
    [property: JsonRequired] long ProgressVersion,
    [property: JsonRequired] string Token);
public sealed record ShorteningPolicyView(ShorteningPolicy Policy, string Title, string Kind,
    int CurrentSeconds, bool CanEdit);
public sealed record ShorteningPlan(string EventId, long RowVersion, long ProgressVersion,
    bool Live, bool EventDay, ShorteningPolicyView[] Policies, ShorteningProposal Proposal);
public sealed record ShorteningResult(string RequestId, long RowVersion, long ProgressVersion,
    string Kind, ShorteningCut[] Cuts, bool Replayed = false);

public sealed partial class CoreFoundationService {
    private static ShorteningPolicy Policy(EventShorteningPolicy p) =>
        new(p.PartId, p.AllowPrep, p.MinPrepSeconds, p.AllowDuration, p.MinDurationSeconds);

    private static async Task<ShorteningPolicy[]> EffectivePolicies(ApplicationDbContext db,
        string id, ProgressBaseline baseline, bool live, CancellationToken ct) {
        var ids = baseline.Rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var saved = await db.EventShorteningPolicies.AsNoTracking()
            .Where(p => p.EventId == id && p.Scope == (live ? "Live" : "Plan")).ToArrayAsync(ct);
        var byId = (live ? baseline.PlannedPolicies ?? [] : [])
            .Where(p => ids.Contains(p.PartId)).ToDictionary(p => p.PartId, StringComparer.Ordinal);
        foreach (var policy in saved.Where(p => ids.Contains(p.PartId)))
            byId[policy.PartId] = Policy(policy);
        return byId.Values.OrderBy(p => p.PartId, StringComparer.Ordinal).ToArray();
    }
    private static async Task<ProgressBaseline> BaselineWithPolicies(ApplicationDbContext db,
        SessionEvent e, bool manager, CancellationToken ct) {
        var baseline = Baseline(await Timetable(db, e, manager, ct));
        return baseline with { PlannedPolicies = await EffectivePolicies(db, e.Id, baseline, false, ct) };
    }
    private static async Task ValidatePlannedPolicies(ApplicationDbContext db, SessionEvent e, CancellationToken ct) {
        var baseline = await BaselineWithPolicies(db, e, true, ct);
        ShorteningCalculator.Build(e.Id, e.RowVersion, 0, baseline, [], [], baseline.PlannedPolicies ?? []);
    }
    private async Task<(EventProgressState? State, ProgressBaseline Baseline,
        ProgressMark[] Marks, List<ProgressEstimate> Estimates, ShorteningPolicy[] Policies)>
        ShorteningContext(ApplicationDbContext db, SessionEvent e, CancellationToken ct) {
        var state = await db.EventProgressStates.SingleOrDefaultAsync(s => s.EventId == e.Id, ct);
        var baseline = state is null ? await BaselineWithPolicies(db, e, true, ct)
            : JsonSerializer.Deserialize<ProgressBaseline>(state.PlanJson)!;
        return (state, baseline,
            await db.ProgressMarks.Where(m => m.EventId == e.Id).ToArrayAsync(ct),
            await db.ProgressEstimates.Where(m => m.EventId == e.Id).ToListAsync(ct),
            await EffectivePolicies(db, e.Id, baseline, state is not null, ct));
    }
    public async Task<ShorteningPlan> ReadShorteningAsync(ClaimsPrincipal principal, string id, CancellationToken ct = default) {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var e = await Managed(db, principal, id, ct);
        var (state, baseline, marks, estimates, policies) = await ShorteningContext(db, e, ct);
        var day = ProgressCalculator.IsEventDay(baseline.ScheduledDate, clock.GetUtcNow());
        var rows = ProgressCalculator.Calculate(baseline, marks, estimates);
        var views = rows.Where(r => r.Original.Kind != "Opening").Select(r => {
            var value = r.Original.Kind == "Song" ? r.Latest.ConfiguredPrepSeconds!.Value : r.Latest.LengthSeconds;
            var policy = policies.SingleOrDefault(p => p.PartId == r.Original.Id)
                ?? new(r.Original.Id, false, r.Original.Kind == "Song" ? value : 0,
                    false, r.Original.Kind == "Transition" ? value : 0);
            return new ShorteningPolicyView(policy, r.Original.Title, r.Original.Kind, value,
                (state is null || day) && !r.Points.Any(p => p.Actual is not null));
        }).ToArray();
        return new(id, e.RowVersion, state?.RowVersion ?? 0, state is not null, day, views,
            ShorteningCalculator.Build(id, e.RowVersion, state?.RowVersion ?? 0, baseline, marks, estimates, policies));
    }
    public Task<ShorteningResult> SaveShorteningPolicyAsync(ClaimsPrincipal principal, string id,
        ShorteningPolicyEdit edit, CancellationToken ct = default) =>
        WriteShortening(principal, id, edit.RequestId, edit.RowVersion, edit.ProgressVersion,
            "Policy", JsonSerializer.Serialize(edit), edit, null, ct);
    public Task<ShorteningResult> ApproveShorteningAsync(ClaimsPrincipal principal, string id,
        ShorteningApproveEdit edit, CancellationToken ct = default) =>
        WriteShortening(principal, id, edit.RequestId, edit.RowVersion, edit.ProgressVersion,
            "Approve", JsonSerializer.Serialize(edit), null, edit.Token, ct);

    private async Task<ShorteningResult> WriteShortening(ClaimsPrincipal principal, string id,
        string requestId, long version, long progressVersion, string kind, string payload,
        ShorteningPolicyEdit? edit, string? token, CancellationToken ct) {
        if (!Guid.TryParseExact(requestId, "D", out _) || version < 0 || progressVersion < 0 ||
            edit is not null && (edit.MinPrepSeconds < 0 || edit.MinDurationSeconds < 0) ||
            kind == "Approve" && (token is null || token.Length != 64 || !token.All(Uri.IsHexDigit)))
            throw new ValidationException("操作Id・版・最短秒・提案識別子を確認してください。");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Current authorization deliberately precedes replay as well as new approval.
        var e = await Managed(db, principal, id, ct);
        var actor = await Actor(db, principal, ct);
        var fingerprint = SemEndpoints.Hash(kind + payload);
        var old = await db.ShorteningOperations.SingleOrDefaultAsync(o => o.Id == requestId, ct);
        if (old is not null) {
            if (old.EventId != id || old.ActorId != actor.Id || old.Fingerprint != fingerprint)
                throw new ValidationException("操作Idは別の操作に再利用できません。");
            return JsonSerializer.Deserialize<ShorteningResult>(old.ResultJson)! with { Replayed = true };
        }
        var (state, baseline, marks, estimates, policies) = await ShorteningContext(db, e, ct);
        if (e.RowVersion != version || (state?.RowVersion ?? 0) != progressVersion)
            throw new CoreConflictException();
        var now = clock.GetUtcNow();
        var day = ProgressCalculator.IsEventDay(baseline.ScheduledDate, now);
        if ((state is not null || kind == "Approve") && !day)
            throw new ValidationException("当日条件と短縮承認は日本時間のイベント当日だけ可能です。");
        var at = Utc(now);
        ShorteningCut[] cuts = [];
        if (edit is not null) {
            var row = ProgressCalculator.Calculate(baseline, marks, estimates)
                .SingleOrDefault(r => r.Original.Id == edit.PartId);
            if (row is null || row.Original.Kind == "Opening" || row.Points.Any(p => p.Actual is not null))
                throw new ValidationException("未開始の曲準備・進行だけを編集してください。");
            var proposed = new ShorteningPolicy(edit.PartId, edit.AllowPrep, edit.MinPrepSeconds,
                edit.AllowDuration, edit.MinDurationSeconds);
            var nextPolicies = policies.Where(p => p.PartId != edit.PartId).Append(proposed).ToArray();
            ShorteningCalculator.Build(id, e.RowVersion, state?.RowVersion ?? 0,
                baseline, marks, estimates, nextPolicies);
            var scope = state is null ? "Plan" : "Live";
            var saved = await db.EventShorteningPolicies.SingleOrDefaultAsync(p =>
                p.EventId == id && p.PartId == edit.PartId && p.Scope == scope, ct);
            if (saved is null) {
                saved = new() { EventId = id, PartId = edit.PartId, Scope = scope };
                db.EventShorteningPolicies.Add(saved);
            }
            saved.AllowPrep = edit.AllowPrep; saved.MinPrepSeconds = edit.MinPrepSeconds;
            saved.AllowDuration = edit.AllowDuration; saved.MinDurationSeconds = edit.MinDurationSeconds;
            if (state is null) e.RowVersion++; else state.RowVersion++;
        } else {
            var proposal = ShorteningCalculator.Build(id, e.RowVersion, state?.RowVersion ?? 0,
                baseline, marks, estimates, policies);
            if (!StringComparer.Ordinal.Equals(proposal.Token, token)) throw new CoreConflictException();
            if (!proposal.HasCuts) throw new ValidationException("承認できる短縮対象がありません。");
            cuts = proposal.Cuts;
            foreach (var cut in cuts) {
                var estimate = estimates.SingleOrDefault(p => p.PartId == cut.PartId);
                if (estimate is null) {
                    estimate = new() { EventId = id, PartId = cut.PartId };
                    estimates.Add(estimate); db.ProgressEstimates.Add(estimate);
                }
                if (cut.Field == "PrepSeconds") estimate.PrepSeconds = cut.AfterSeconds;
                else estimate.DurationSeconds = cut.AfterSeconds;
            }
            ProgressCalculator.Calculate(baseline, marks, estimates);
            if (state is null) {
                state = new() { EventId = id, SourceVersion = e.RowVersion,
                    PlanJson = JsonSerializer.Serialize(baseline), CreatedAt = at };
                db.EventProgressStates.Add(state);
            }
            state.RowVersion++;
        }
        var result = new ShorteningResult(requestId, e.RowVersion, state?.RowVersion ?? 0, kind, cuts);
        db.ShorteningOperations.Add(new() { Id = requestId, EventId = id, ActorId = actor.Id,
            Kind = kind, At = at, Fingerprint = fingerprint, ResultJson = JsonSerializer.Serialize(result) });
        Audit(db, actor.Id, kind == "Policy" ? "ShorteningPolicySaved" : "ShorteningApproved", requestId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }
}