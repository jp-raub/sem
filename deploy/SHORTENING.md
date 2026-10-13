# Shortening conditions and explicit proposal approval

English is authoritative; update the Japanese companion in the same change.
Stacked on the verified PR #14 live-progress implementation. No DXP rollout or image delivery is performed by this change.

## Persistence and preservation

`20261013063027_AddShortening` adds `EventShorteningPolicies` (event/part/Plan-or-Live key, enabled flags, integer-second minima) and `ShorteningOperations` (request ID, real actor, event, server time, kind, fingerprint and saved result). A restrictive composite FK targets the same event's TimePart; the migration adds the required alternate key without deleting existing rows. Restrictive event/actor FKs and JSON/kind/seconds/boolean checks apply.

Before live capture, condition saves update planned policy and the event structural version, without creating actuals or a progress snapshot. The first successful live record, estimate or approval captures `PlannedPolicies` with the immutable original baseline. Old snapshots lacking that optional JSON field remain byte-identical and default to protected time. Live condition edits are separate overrides and increment only the live version. Planned rows may be retained after soft deletion but are excluded from active proposals.

Normal song/timetable saves cannot invalidate an enabled planned minimum; the entire transaction, including version and audit, rolls back. An unstarted live estimate cannot undercut an enabled minimum. Existing manual estimates of an already-started transition remain supported: that entire part is excluded from automatic shortening, so an unused policy minimum does not block reading it.

## Proposal rules

- Missing/disabled policy protects time. Only song preparation and transition duration may be shortened, down to nonnegative integer seconds no greater than the current estimate.
- Performance and opening are never targets. Any actual point protects the entire part, including preparation already underway.
- Visit permitted unstarted parts in timeline order. Stop once the end target is met; otherwise use each permitted minimum. The target is the stored planned end or the original final end if unspecified.
- Recompute the entire live chain for each candidate, retaining song minute-boundary rounding and UTC millisecond actual anchors. A one-second cut may recover a minute; a plateau may absorb less than expected. Never equate nominal cuts to actual recovery. Bounded binary search finds the largest integer duration meeting the target.
- Return current/shortened duration, before/after end, delay, absorption and remaining delay. Displaying a proposal never persists changes. Caller input, original, actuals, inherited null and explicit zero remain protected.

## Authorization, versions and replay

Manager-only GET `/api/core/shortening?id=...` returns policy views and a coherent proposal.
POST `/api/core/shortening-policy?id=...` accepts a strict `ShorteningPolicyEdit`.
POST `/api/core/shortening-approve?id=...` accepts a strict `ShorteningApproveEdit` with request ID, both versions and token only. Client cuts/timestamps are rejected as unknown fields. Existing Origin, CSRF and 64KiB body guards apply.

Current DB identity/enabled/email-confirmation/security-stamp/session and event management role checks occur before replay as well as fresh writes. Fresh live condition saves and approvals require the Japanese event day; pre-live planned condition editing is permitted before that day. Replays return the authorized saved result without a second mutation, including after the event day. Request ID reuse by a different actor/event/payload/kind is rejected.

The canonical SHA-256 token binds algorithm version, event ID, structural/live versions, original baseline including planned policies, mark details, explicit estimate inputs and current policies. Enumeration order alone does not churn it; an inherited-to-explicit change does. The token is not authorization.

Approval loads and recalculates the current proposal inside the authorized write transaction, compares both versions and token, and rejects changed/stale/empty proposals. Only server-recomputed cuts are applied to live estimates. The original and actuals are not changed; first approval may create the baseline but never invents actual records. Version, saved request result and real-actor audit commit together. Concurrent approval or approval versus record/policy/estimate must not commit partial second work. The common HTTP layer reports stale/concurrency/SQLite-busy conflicts as409.

## Manager UI

`ShorteningPanel` is permanently shown above the optional comparison table in the manager's progress perspective, including mobile. It displays latest proposal and one **Approve this proposal** action without a second confirmation modal. No delay/eligible target disables approval. A changed proposal is rejected and prompts review of the refreshed one.

The planned/live condition editor is staged with save/cancel and unsaved-navigation protection. While a condition draft or write is active, adjacent record/estimate and perspective switching are blocked. Parent authorized refresh updates proposals on structural/live version changes; automatic refresh does not overwrite drafts. Participants and the manager's participant perspective receive no proposal controls; the proposal API itself also rejects ordinary members.

## Verification and acceptance boundaries

Run `tests/Sem.ShorteningChecks` for calculation protection, rounding and canonical tokens; run `tests/Sem.ShorteningIntegrationChecks` for populated PR14 migration, planned/live separation, byte-exact old/new baselines, current authorization/replay, version conflicts, concurrent writes, restrictive FK, restart and SQLite integrity. Existing progress/domain/storage regressions must remain green.

The event-shortening workflow includes current/fixed-legacy/foundation Docker builds, assets, empty DB/restart/rate limits, legacy upgrade, real synthetic SMTP, native virtual passkeys and existing real Blazor acceptance plus `scripts/shortening-checks.cjs`. Report the actual completed counts and source in the PR/dashboard; do not infer success from a partial run. These are synthetic checks, not DXP/iOS/new physical-key acceptance.

Business notifications, registration notifications, QR/manual reception, import, aggregates/comments/profile completion and full v33 styling remain separate work. Ordinary timing/order amendments after baseline capture remain guarded; do not delete snapshots to bypass protection. No production settings, secrets, keys, volumes, RP, delivered packages or PR merges are changed. No specification decision is currently pending.
