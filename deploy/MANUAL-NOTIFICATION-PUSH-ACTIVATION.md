# Manual Push activation control (internal API only)

## Boundary

Adds a current-global-administrator-only Core read/write API and additive operation-ledger migration. No HTTP route, UI, background worker or transport is registered; startup/migration never enables Push. Actual operational enablement/deployment still requires separate approval. Do not use direct database edits as an operational procedure; direct writes in synthetic checks are test fixtures only.

`ReadManualPushActivationAsync` returns only Enabled/Epoch after current authentication and database IsAdmin checks in one read transaction. Missing state is OFF, epoch0, without inserting it. An event manager or forged Admin role cannot use global controls.

## Confirmed, replay-safe transitions

`SetManualPushActivationAsync` requires explicit confirmation, canonical lowercase D-format GUID operation ID and expected epoch. Current enabled/confirmed account, security stamp, nonrevoked authenticated session and database global-admin status are checked for both new controls and replay.

- One SQLite non-deferred transaction serializes the transition with original notification acceptance. It does not infer ordering from timestamps.
- Real OFF/ON transitions advance the epoch, including disable and every re-enable. Same-state confirmation is a no-op and does not rotate an enabled generation or reset notification expiry.
- A distinct request with stale expected epoch conflicts. An exact repeat returns its original accepted result without executing another transition; that historical result is not necessarily the current state. Replaying an old enable after disable cannot re-enable anything.
- Operation IDs cannot be reused for another actor/decision/expected epoch. Each new confirmed control, including a no-op, has exactly one audit; replay has none. State + operation + audit commit atomically; fault rolls all back. Epoch overflow is rejected.
- Existing admissions/inboxes/recipient state/queues are never backfilled, rewritten or consumed. Disabling immediately makes policy reads fail; a later new epoch cannot revive prior admitted work. Original acceptance records admission only for the current enabled epoch in its own serialized transaction.

A future UI/HTTP route still needs independently applied current-cookie actor matching, Origin/CSRF, no-store, explicit current-state display and pending/unknown operation recovery. These Core methods alone do not claim those protections are implemented.

## Verification

`dotnet run --project tests/Sem.ManualNotificationPushActivationChecks -c Release` performs 42 real synthetic SQLite checks: previous-schema/inbox retention, default OFF, global-vs-event authority, confirmation/input/version checks, same-second acceptance boundary, idempotent/no-op/disable/re-enable behavior, replay current-auth and actor checks, audit rollback, concurrent same/distinct requests, acceptance blocked behind uncommitted activation then observing its committed epoch, overflow, no dispatch mutation, audits, integrity/foreign keys and cancellation. Some concurrency checks directly hold synthetic DB transactions to prove serialization; they are not operational activation instructions.

Remaining: operational UI/HTTP controls, bounded scan/dispatch and transport response classification, final send-time snapshot and lease-bound timeout/TTL, binding-safe invalidation, service-worker/physical-device acceptance. Existing queue/control observations cannot guarantee handset receipt or eliminate the external-I/O race.
