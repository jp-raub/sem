# Manual notification device preflight (read-only, disconnected)

## Scope

Follows the explicit composer. `ManualNotificationDevicePreflight.ReadAsync` checks one immutable accepted recipient snapshot against one expected current device binding. It is a **read-only internal building block**, not an HTTP API, send authorization, recipient discovery operation, dispatcher or per-device outbox. It is not registered in DI or a worker. It adds no migration or application invocation; the existing composer/inbox/automatic Push behavior is unchanged.

Every result has `DispatchConnected=false`. Even an otherwise valid binding returns `PolicyPending`; no deadline, TTL, payload, lease, retry, delivery status or transport work is created. Manual Push remains dormant. An accepted inbox message is not rewritten when current eligibility changes.

## Coherent current checks

Within one fresh database transaction, each invocation checks:

1. The recipient exists in an accepted operation and its event binding matches. Inputs cannot add a recipient that joined later.
2. The deterministic inbox row still belongs to the snapshot's recipient.
3. Current enabled/confirmed account state; only ID/flags/stamp are projected from the identity row, never authentication email/profile/message content.
4. Current effective event visibility using the existing phase/current database manager logic.
5. Current spectator registration or a current assignment through nondeleted same-event song/definition rows. This is returned only as the nullable fact `CurrentlyRegistered`, not silently adopted as a post-acceptance removal/retention policy. Earlier failures leave it unknown (`null`).
6. The requested device currently belongs to the snapshot recipient, satisfies the existing endpoint/key/stamp/nonrevoked-session binding checks, and exactly matches the expected binding fingerprint. Re-registration/stamp/session changes invalidate the old expected binding.

Results are only a bounded state label, nullable registration fact and disconnected flag. No endpoint, subscription keys, stamp/session, identities, message text, profile or answers are returned or logged. The service does not acquire a dispatch lease; its facts are observations, **not** a guarantee valid for a later send. A future dispatcher must repeat current checks immediately before transport under its own owned durable claim and approved policy.

## Policy and implementation still missing

- Explicit manual Push expiry/TTL and treatment of delayed historical dormant intents.
- Post-acceptance participation-removal behavior (the factual current-registration result does not decide it).
- Durable per-device outbox/deduplication, claim/lease/retry/unknown external acceptance handling, final gates and transport connection.
- Generic lock-screen message, authenticated inbox navigation and physical-device/service-worker acceptance for the manual kind.

Do not feed these rows into the existing automatic event dispatcher or equate `PolicyPending` with eligible/sent/received/read. No DXP/.env/real data/keys/hostname, distribution or merge changes are made.

## Verification

`dotnet run --project tests/Sem.ManualNotificationDevicePreflightChecks -c Release` uses a real newly migrated synthetic SQLite DB and a real Core-accepted inbox/snapshot. It covers current account/stamp/session/endpoint/device ownership/fingerprint gates, hidden event and current manager behavior, retained snapshot vs changed registration, live/deleted performance assignment, late joiners, mismatched inbox owner, no queued transport/audit/state consumption, cancellation, no model change, secret-free results and a genuinely `Mode=ReadOnly` database connection. It does not test a dispatcher, external Web Push, native browser UI or deployment. The paired workflow runs warnings-as-errors and this plus all existing .NET suites; actual conclusions belong in PR/dashboard.
