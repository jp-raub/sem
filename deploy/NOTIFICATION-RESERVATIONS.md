# Dormant notification reservation ledger

English reference; [Japanese translation](NOTIFICATION-RESERVATIONS.ja.md).

## Boundary

This increment persists and reconciles PR19's approved schedule bases. **It is a dormant plan ledger, not a connected delivery outbox.** No recipient, enabled Push rule, TTL, transport, send permission, Sent status, delivery claim or send API is introduced. A `Due` row is never proof of submission or device receipt. Existing PR18 daily email and immediate business/authentication mail are independent and unchanged.

Migration `20261013115902_AddNotificationReservations` adds event-keyed `NotificationScheduleStates` and event/logical-ID keyed `NotificationReservations`, with restrictive parent foreign keys, state/time/reason/kind/version constraints and optimistic header-version concurrency. Millisecond Unix timestamps retain PR19's precise instants. Existing identities, passkeys, protected keys, answers, progress baselines, email outboxes and subscriptions are retained.

## Reconciliation and cancellation

The hosted worker ticks every five seconds, scans at most twenty event IDs in ascending keyset order, retains its cursor, then wraps after a complete pass. Twenty is a per-tick throughput bound, not an overall event limit or a promise that every event reconciles within five seconds. Restart begins scanning again without regenerating duplicate logical rows. A bad event is isolated; other events in the batch continue and the failed event is retried on a later pass. Logging includes exception type only, never a body, key or subscription.

Each event reads the current formal plan/captured baseline/actuals/estimates and updates its source header plus all affected rows and one `system` audit (`NotificationReservationsReconciled`) in **one transaction**. Concurrent writers either apply one generation or conflict/roll back and retry on a later pass. Failed inserts/updates do not leave a new header pointing to old or partial rows. Reconciliation never changes source event/progress versions, originals, participant assignments or transport queues.

- `Planned`: approved computed time is still in the future.
- `Due`: computed time reached, **still unsent/dormant**.
- `WaitingActual`: missing preceding Start actual; reference/due remain null instead of guessed forecasts.
- `Cancelled / TargetStarted`: target already began, including its preparation.
- `Cancelled / PartRemoved`: logical song notification no longer appears in current source. The row remains as a tombstone, not a physical deletion.

Timing/order/opening changes update the same stable logical ID and clear an old due when its new basis requires an actual. Deleted-part cancellations retain old timing fields for diagnostics, not future delivery instructions. A fresh valid source can revise a still-existing logical row; this does not add an undo/deleted-song restoration API. Formal protection of started plans remains unchanged.

Header generation increments on a source-version/fingerprint change or effective row change (including clock eligibility); changed row versions identify their most recent generation. Identical reads/reconciliation do **not** change version, ChangedAt or add duplicate audits. ChangedAt is the last material change, not a heartbeat. PR19's source fingerprint remains independent of mere time passage, while `IsCurrent` additionally compares effective row states and current event/progress versions.

## Read surfaces and stale-source handling

- no-store GET `/api/core/notification-reservations?id=<eventId>`; POST is405 after existing CSRF protection, with no reconcile/send command exposed.
- InteractiveServer `/events/{EventId}/notification-reservations`, linked from the manager plan preview.
- Current ordinary authentication, enabled/verified identity, security stamp/session, global or assigned-event management permission rechecked for each read inside a coherent transaction. Member/unassigned/expired access is denied and the UI clears prior data.
- GET reads the stored ledger and a fresh PR19 plan but never generates/reconciles one. An unseen event returns an explicit uninitialized dormant view. A source change before the worker runs returns `IsCurrent=false`, not a falsely current old schedule. Invalid source fails closed instead of returning an apparently usable old ledger.
- The UI labels dormant/unsent, cancellation reasons, stale/uninitialized state, Japan dates and manual refresh. It is not a SignalR auto-refresh subscription. No recipient emails, answers, subscription secrets or protected bodies are returned or stored here.

## Acceptance / remaining work

`Sem.ReservationChecks` includes PR19 calculation foundations plus populated-PR19 migration preservation, exact persistent milliseconds, read-only initial GET, idempotency/no duplicate audits, clock-current comparison, atomic injected update rollback, opening-basis replacement, real-thread concurrent generation, removed-part tombstones, restart, target-start cancellation, bad-source rollback/read failure, twenty-plus-event fairness/isolation, existing outbox preservation, SQL constraints and EF/SQLite/FK integrity. `reservation-checks.cjs` exercises the actual hosted worker, manager API/Blazor/mobile refresh, real formal song deletion/cancellation, restart preserving generation, denied member UI, no POST and no related SMTP. Run the entire prior native-passkey/daily-production-SMTP/business/Blazor/upgrades/restart suites and actual Docker CI.

**Actual Push remains unfinished.** Before connecting any ledger to dispatch, establish explicit rule/recipient/default/TTL policy, atomically reconcile applicable enabled rules, revalidate current source and recipients/permissions immediately before sending, protect per-device subscriptions and credentials, implement delivery retries/deduplication/expiry/logout reset and distinguish acceptance from device receipt. Never send dormant historical Due rows merely because this ledger exists. No DXP, `.env`, public hostname, keys, volume, distribution archive or merge is changed by this work.

## Personal opt-in settings

The separate [part-start preferences increment](PART-START-PREFERENCES.md) adds per-user switches for arbitrary viewable time parts. It does not connect this dormant ledger or activate delivery.
