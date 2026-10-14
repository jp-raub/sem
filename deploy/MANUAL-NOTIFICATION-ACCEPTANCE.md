# Manual notification: atomic acceptance foundation

## Scope and boundary

This follows the read-only preview foundation. It adds a core-only explicit acceptance method, two new tables, an additive EF Core migration and synthetic SQLite tests. No HTTP route, composer, automatic invocation, Push dispatcher, SMTP path, release or deployment is added. Nothing consumes the new recipient-level Push outbox yet. `Accepted` means that inbox messages and durable intent were committed, not that any device received or read a notification.

## Durable operation contract

`AcceptEventManualNotificationAsync` requires a canonical lowercase D-format GUID, explicit confirmation, normalized plain-text draft, and the preview fingerprint. It independently revalidates the current account/session/security stamp and database global/assigned-event manager permission inside a transaction. A fingerprint never replaces authorization.

An unaccepted request rebuilds the same preview with the current live performers/spectators, account eligibility and effective event visibility. Changed actor, event revision/title, content or recipient set requires a fresh preview. Zero eligible recipients cannot be accepted. All gates and writes use the same SQLite transaction.

An accepted operation ID is permanently bound to actor, event and normalized request hash. Retrying that exact request returns its original count and timestamp without adding rows. Different content/fingerprint/actor/event cannot reuse it. Current management and session authorization is still required even for replay. Later membership changes do not rewrite accepted history or add recipients. A **new** operation ID represents a deliberately separate send; equal text alone is not globally deduplicated.

## Atomic persistence

One transaction commits:

1. `ManualNotificationOperations`: immutable accepted draft, actor/event binding, request hash, preview provenance, acceptance timestamp and recipient count.
2. One ordinary unread `Messages` row per distinct eligible recipient, with deterministic operation/event/user-derived ID.
3. `ManualNotificationRecipients`: event-bound recipient snapshot, the same message ID and dormant `PushState=Pending` intent. Unique operation/user and restrictive foreign keys prevent duplicate/orphan snapshot rows. It contains no subscription secrets.
4. One `ManualNotificationAccepted` audit referencing the operation ID, without body or recipient secrets in diagnostic text.

Failure at any step rolls back all four. A lost response after commit is recovered by retrying the **same** request ID and payload. The method returns no recipient identities. It does not call the legacy notification helper, SMTP, existing automatic Push worker or reservation ledger. Existing Identity/key stores, inbox rows, scheduled deliveries and service configuration are not rewritten.

The `AddManualNotificationAcceptance` migration only creates these two new tables and indexes. Its down path drops only the new tables and is not an endorsed rollback after live acceptance: restoring the old application/database requires an explicit data-preserving operational plan. This development change does not run any migration on DXP.

## Verification

The dedicated console suite checks input/confirmation/authentication, stale preview rejection, rollback via an injected audit failure, exact replay and cross-request rejection, three simultaneous identical requests, real separate-process database reopen/replay, populated previous-schema migration, preserved account stamp/inbox/scheduled payload/settings, unchanged unrelated operations, matching EF snapshot and final SQLite integrity/foreign keys. The separate-process check is not a deployed web-container restart or native-browser test.

Run `dotnet run --project tests/Sem.ManualNotificationAcceptanceChecks -c Release`. The dedicated workflow builds Release with warnings as errors and runs this plus all existing .NET console suites. Local results and CI conclusions are recorded in the PR/dashboard, not assumed from the presence of this document.

## Next work

Add explicit preview/confirm UI and protection for unsent/processing/uncertain requests, then per-device delivery state and final current account/event/device/session gates with generic Push body and message-box navigation. Define expiry behavior before enabling dispatch. This dormant outbox does not establish an expiry policy, device delivery guarantee or exactly-once external transport. No full v33 completion claim.
