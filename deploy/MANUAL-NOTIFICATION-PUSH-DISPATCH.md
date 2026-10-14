# Manual notification single-device dispatcher (not registered)

## Scope

Extends the durable queue with `DispatchOneAsync(deliveryId)`. No DI/HTTP/background worker/scanner is registered and nothing enables Push at startup. This branch includes the separately reviewed PKCS10.0.13 security pin through its base. No migration, inbox creation, read-state mutation, SMTP invocation or automatic Push behavior change.

The production `IEventPushTransport` is injectable. Verification uses synthetic spies and an in-memory HttpMessageHandler; no actual Push endpoint is contacted. It is not real-provider or handset acceptance.

## Immediately before I/O

An unavailable transport does not claim. After an exclusive queue claim, one transaction projects only the accepted subject and a current valid device while rechecking original recipient/inbox, admission, current activation/epoch, account, effective view, registration, binding/stamp/session, exact owner, active lease and pinned deadline. It never loads multiline message body for Push.

After preparation, transport availability and the full queue recheck run again immediately before I/O. Rejected work is terminal; rejoining/re-registering cannot revive it. Payload uses the reviewed accepted subject, generic `新しいメッセージがあります`, authenticated existing `/?messages=1` inbox navigation, stable recipient ID tag and millisecond expiry for the existing Service Worker. TTL floors remaining original lifetime; less than one second does not send. Network cancellation is capped at15 seconds and remaining lease/deadline. The unavoidable authorization-to-I/O race is minimized, not claimed eliminated.

## Result classification and recovery

- Success records ProviderAccepted only if current owned completion persists. It is not handset receipt/read. Unrecorded results are not reported accepted.
- Explicit400/401/403/413 provider rejection is terminal failure.
- 404/410 is terminal SubscriptionGone. Delete only a current subscription whose entire binding still equals the queued digest; an in-flight replacement survives. No other device is removed.
- Timeout/network/unknown exceptions and408/429/5xx are conservatively AcceptanceUnknown, not automatically retried. No raw provider/exception text is persisted.
- A definite pre-invocation failure/unavailability may schedule queue-bounded RetryableNotAccepted. Parent cancellation after invocation saves Unknown using an independent, bounded5-second cleanup token before propagating cancellation. Pre-cancelled calls do not claim.
- Outcome persistence failure remains conservative; elapsed claimed leases become Unknown and cannot be automatically resent. Fenced stale completion cannot overwrite newer/terminal work. No externally exactly-once guarantee or Unknown reconciliation API is added.

## Verification

`dotnet run --project tests/Sem.ManualNotificationPushDispatchChecks -c Release` performs45 checks using real synthetic SQLite and no external network. Covers current preparation/final gates, concurrent dispatch, status classes, replaced subscription retention, no duplicate inboxes, TTL/deadline and short timeout, cancellation before/after I/O, Unknown nonretry and raw-error exclusion. Two checks run the actual WebPush encryption adapter against an in-memory HTTP handler, verifying encrypted non-plaintext payload, TTL and stable Topic.

Existing full regression, existing Service Worker checks, isolated virtual-passkey/Blazor acceptance and exact-source CI remain distinct evidence. None is physical-device acceptance. Still required: bounded scan/worker integration, operational current-cookie/Origin/CSRF/no-store activation UI/HTTP, real-provider/physical-device reception and deployment approval.
