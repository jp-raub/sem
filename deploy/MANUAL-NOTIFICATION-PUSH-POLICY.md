# Manual notification Push policy and durable admission

## Scope

This foundation applies the three policies approved on 2026-10-14:

- Push expires exactly 600 seconds after original acceptance. Inbox retention/read state is independent.
- A recipient without a current live performance assignment or spectator registration is not eligible for subsequent Push. Their saved inbox is retained.
- Work accepted before Push activation must never be retrospectively dispatched.

There is **no production activation writer, HTTP route, DI/worker registration, device delivery queue or transport connection** in this change. Missing activation state means OFF. The UI continues to report Push disconnected. Do not enable by editing database rows; fixture-only writes in the checks are not an operational procedure.

## Durable boundary

The additive migration `AddManualNotificationPushAdmission` creates only `ManualNotificationPushActivations` and `ManualNotificationPushAdmissions`; it does not seed activation, rebuild existing tables, backfill old operations, alter recipient Pending state or touch existing inboxes, automatic Push, SMTP, settings, credentials or keys.

Only an original Core acceptance, while a valid activation is enabled, writes an admission alongside the operation, immutable audience, inboxes and audit in the same transaction. The admission pins the activation epoch, original AcceptedAt and ExpiresAt=AcceptedAt+600. Exact retries return the original result without inserting or updating admission. Old requests remain excluded even if accepted in the same second as activation.

A future audited activation writer must serialize changes with acceptance and use a fresh monotonically increasing epoch on every re-enable. Disabling blocks admitted work; a new epoch cannot revive it. This change deliberately does not implement that writer or claim an activation control is complete.

## Observational policy reader

`ManualNotificationPushPolicy.ReadAsync` uses one read transaction and one clock value for the admission, current activation, deadline, current accepted recipient/inbox owner, account, effective viewing rights, registration, device binding, stamp and session checks. It shares the existing fact reader without changing the standalone PR43 result contract.

At the exact deadline the result is Expired. Clock rollback before acceptance fails closed. Without original admission: NotAdmitted. Disabled or changed generation: Disabled/ActivationChanged. Removed current registration: NoLongerRegistered. A passing result is only EligibleForStaging with DispatchConnected=false, **not permission for a later external send**. No secrets or body content appear in results. No reads mutate or delete saved messages. A future worker must durably suppress rejected work and repeat all gates immediately before sending; rejoining must not revive a job already terminally suppressed.

## Verification and remaining work

`dotnet run --project tests/Sem.ManualNotificationPushPolicyChecks -c Release` performs 39 checks against real migrated synthetic SQLite: data-bearing old schema upgrade, default OFF, same-second cutover, exact replay, precise deadline, epoch changes, retained inbox on unregistration, late audience non-expansion, current account/stamp/session/view/binding, independent read-only reopen, failure-injected atomic rollback, constraints, integrity/foreign keys and cancellation. Existing suites must also pass.

Still required: audited activation transition, per-recipient/device durable preparation, claim/lease, bounded retries and ambiguous external outcomes, immediate send-time gates, generic authenticated inbox navigation, real Push/device acceptance. No Docker/DXP/deployment/merge claim is made.
