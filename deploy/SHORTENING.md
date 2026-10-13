# Shortening proposal core (incremental implementation)

English is authoritative; the Japanese companion is updated in the same change.

## Implemented, not yet connected to the application

`ShorteningCalculator.Build` is a side-effect-free proposal calculator. It does not persist
policies, estimates, original plans or actuals. There is no public proposal endpoint, policy
editor or approval handler in this increment. Do not describe this as completed one-click
approval, and do not deploy it as a delivered application feature.

The verified PR #14 live-progress behavior remains the runtime baseline. This increment
adds only new source, tests, this documentation and a dedicated CI workflow.

## Rules

- Missing/disabled policy protects time. Only a song's preparation or a transition's
  duration can be allowed, with a nonnegative integer-second minimum no greater than the
  current estimate. Performance and opening are never shortening targets.
- Any actual point protects the entire part, including a song with preparation underway.
- Iterate permitted, unstarted parts in timeline order. Keep later parts unchanged once
  the end target is reached; otherwise use each permitted minimum.
- Use the stored end target, or the original final end when no target was specified.
  Show before/after end, delay, actual recoverable delay and remaining delay.
- Recalculate the complete live chain for each candidate. Song minute-boundary rounding
  is preserved; a one-second cut may recover a minute, while cuts on a plateau may not
  absorb the expected delay. Never equate nominal cut seconds with time actually recovered.
  A bounded binary search finds the largest allowed integer duration meeting the target.
- Original timeline, estimates passed by the caller, marks and policies are not mutated.
  Actual UTC millisecond anchors, inherited `null`, explicit zero and performance durations
  remain protected. Proposing never writes estimates.

## Proposal fingerprint

The deterministic SHA-256 token binds algorithm version, event ID, event structural and
live versions, the original baseline, all mark details, explicit estimate inputs and policy
inputs. Canonical ordering prevents token changes caused only by DB enumeration order.
Even a change from inherited `null` to an equivalent explicit value changes the token.

**A token is not authorization and does not perform approval.** Future approval must:

1. Revalidate current DB identity, security stamp, session, event role and Japanese event day.
2. In one write transaction, load current structural/live versions, policies, baseline,
   marks and estimates, then calculate the proposal again.
3. Compare the presented token and both versions; reject stale/changed/empty proposals.
   Never accept client-submitted cuts or timestamps as authoritative.
4. Apply only server-recomputed cuts to the live estimates; preserve original and actuals.
5. Save a new live version plus the actor, time, request fingerprint/result and audit.
   Replayed requests must return the saved result after current authorization, never apply
   the cuts twice. Races must resolve to one success and one stale conflict.

These steps, policy persistence/migration, before-event and live policy editors, the
manager-only permanent proposal panel, one-click approval, mobile behavior and native UI
acceptance are **not implemented by this increment**.

## Verification scope

Run `dotnet run --project tests/Sem.ShorteningChecks -c Release`.
The dedicated workflow also runs existing progress/timetable/participation/domain/storage
safety suites, the offline-copy suite, a warnings-as-errors Release build and publish asset
checks. This is not the full Docker/native-passkey/SMTP/UI acceptance workflow and does not
establish DXP/iOS/physical-key acceptance.

No production configuration, keys, volumes, images, delivered packages or PR merges are
changed. No user specification decision is currently required to continue integration.