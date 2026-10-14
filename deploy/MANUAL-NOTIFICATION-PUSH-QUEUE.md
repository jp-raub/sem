# Manual notification per-device queue (transport disconnected)

## Scope and safety boundary

This adds durable preparation, fenced ownership and completion primitives on PR44. There is no DI/HTTP/worker registration, activation writer, scanner or external transport invocation. Migration creates only `ManualNotificationPushPreparations` and `ManualNotificationPushDeliveries`; it neither backfills nor enables anything. Existing accepted snapshots, inboxes/read state, recipient Pending state, automatic Push, SMTP and credentials remain unchanged.

## Once-only preparation

`PrepareAsync(recipientId)` starts only from an accepted recipient, evaluates original durable admission + current activation epoch + acceptance-based 600-second deadline + current account/view/registration, then captures valid currently bound devices once in one transaction. Delivery identity hashes recipient ID + device ID; a unique index enforces that pair. Only binding digests are stored, never endpoints/keys/stamps/sessions or message content.

The durable preparation is also written when policy rejects or no valid devices exist. Repeating preparation returns its stored outcome/count without appending late devices, discovering late participants or reviving suppressed work. An insertion failure rolls back the preparation and every device row. There is deliberately no device foreign key: removal must not delete historical delivery intent; the current owner/binding is checked before claim and again before send. Adding a device after preparation is not a new delivery of the old notification.

## Ownership and outcomes

- Only due Pending work may be claimed; current policy, owner/binding and pinned deadline are checked in the same transaction. At most one concurrent caller obtains a fresh token. Lease is 30 seconds capped at expiry; at most five attempts.
- A claim alone is not send authorization. `RecheckAsync` requires the exact token, active lease and all current gates. Rejection terminally suppresses/expires the row; rejoining cannot revive it. The future transport must call this immediately before I/O and cap network timeout/TTL within the remaining lease and deadline.
- An elapsed claimed lease becomes **Unknown**, never automatically Pending. A crashed worker may already have sent. Unknown jobs have no automatic retry/reconciliation API in this foundation.
- ProviderAccepted records only provider acceptance time, not handset receipt/read. Completion requires the current exact token; stale tokens cannot complete later attempts or overwrite terminal outcomes. A definitive late result may still finalize an unchanged owned claim; once another caller fences it Unknown, stale completion is rejected.
- Only an explicit RetryableNotAccepted result schedules retry: 15/30/60/120 seconds, never beyond the original deadline and no more than five attempts. Timeouts, lost responses and ambiguous exceptions must instead be AcceptanceUnknown. SubscriptionGone is terminal; queue primitives do not delete live subscriptions. PermanentFailure is terminal.
- Results/reasons are bounded codes; no raw exception messages or subscription content are persisted. No exactly-once external-delivery guarantee is made. The authorization-to-I/O race remains and must be minimized by the future dispatcher, not claimed eliminated by a database lease.

## Verification

`dotnet run --project tests/Sem.ManualNotificationPushQueueChecks -c Release` performs 67 synthetic SQLite checks. Includes populated previous-schema upgrade/admission preservation, default OFF, once-only current-device snapshot, no late expansion, concurrent preparation/claim, fenced completion, provider/unknown/expired/gone/permanent outcomes, backoff and attempt limit, registration/view/session/binding/epoch changes, inbox preservation, deadline-capped lease, fault-injected second-device rollback, real separate-process reopen/simulated completion, constraints/integrity/foreign keys and cancellation. No actual provider, physical device, Docker or DXP acceptance is implied.

## Remaining

Audited activation management serialized with acceptance; a bounded scanner/dispatcher with timeout and response classification; final preparation of a generic authenticated inbox URL; binding-safe subscription invalidation; service-worker and real-device acceptance. Deployment/activation/PR merge remain separate approvals. Existing PR44 policy documentation describes its original boundary; this queue change extends it without changing the approved policies.
