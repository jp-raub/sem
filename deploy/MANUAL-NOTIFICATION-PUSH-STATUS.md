# Manual notification Push status (Core read only)

## Scope and safety boundary

This addition exposes `CoreFoundationService.ReadEventManualNotificationPushStatusAsync` for one canonical, lower-case D-format request ID. It is a read-only Core capability for an event manager to inspect **their own** accepted manual-notification operation. A second event manager or global administrator cannot inspect another author's operation. Each read revalidates the actor's enabled account, email confirmation, current security stamp and non-revoked session, then applies the current effective event-view and manager checks in a read transaction.

There is no HTTP route, API, UI, JavaScript, worker, sender, audit write, data migration or production activation change in this increment. It must not be treated as an operational deployment or permission to enable Push. Parent PR verification and GitHub publishing remain separate.

## Returned data and interpretation

The aggregate-only result contains the request/event IDs, original accepted recipient count and timestamp, optional original admission expiry, a gate category, recipient preparation counts, and counts of persisted per-device delivery rows:

- `NotAdmitted`: the original acceptance had no admission. Later activation does not backfill it.
- `Stopped`: current activation is OFF, its generation no longer matches, the operation is not yet within its admitted time window, or the persisted admission is invalid. Global epoch values are not returned.
- `Expired`: the current time is at or beyond the original expiry.
- `Eligible`: the original admission is currently enabled, generation-matched, and unexpired. This is only the admission/activation/time gate; it does **not** mean a recipient or device currently passes registration, account, binding, or final-send checks.

Preparation counts classify the immutable accepted-recipient snapshot: prepared recipient rows, recipients without a preparation row, durable `NoEligibleDevices` outcomes, and other known non-prepared outcomes. Delivery counts are persisted queue rows from that same snapshot, counted per device—not per recipient. They do not rescan current devices or add recipients from replies, inbox history, or later registrations. Unknown persisted preparation or delivery states fail closed with an error instead of disappearing into a category.

`Accepted` means only that the provider reported accepting a request. It is not proof of handset delivery, display, read, or exactly-once external delivery. `Unknown` remains ambiguous. Counts are a snapshot read and may change after this method returns.

The result excludes message subject/body, recipient names/emails/user IDs, device IDs/endpoints/keys/fingerprints, session/security-stamp material, claims, and activation epoch. SQL access is limited to scalar counts and narrow operation/admission/activation projections; recipient and device records are not loaded.

## Synthetic verification

Run only in the isolated development worktree, with the local SDK available:

```sh
PATH=/data/dotnet:$PATH dotnet run --project tests/Sem.ManualNotificationPushStatusChecks -c Release
```

The test project migrates a temporary synthetic SQLite database and checks authorization, admission/expiry/stop behavior, snapshot-versus-device counts, serialization privacy, no-write behavior, strict unknown-state handling, and SQLite integrity/foreign keys. Fixtures use synthetic identities and credentials; this is not production verification.
