# Manual Push pipeline composition

## Scope and activation boundary

`Program` calls `AddManualNotificationPushPipeline` after registering the existing typed `IEventPushTransport`. The reusable extension registers a scoped queue, dispatcher and bounded engine, one hosted worker, and runtime registration status. It does not write activation or initialize/backfill notifications. Durable activation remains OFF unless a current global administrator explicitly changes it with the existing epoch/confirmation/Origin/CSRF protected control. Registration status is not a health check, Push-key validity, provider acceptance, handset receipt or read status.

The existing WebPush typed client keeps its 15-second timeout and redirect refusal. The worker opens an independent DI scope every three seconds and preserves only a transient fairness hint. Each pass bounds preparation/dispatch/sweep to20; current account/session/device binding/registration/view/epoch/deadline are rechecked before external I/O. Inbox persistence is not delivery success. Original acceptance+600 seconds, no pre-enable backfill, five-attempt cap, provider429 Retry-After handling, Unknown/no-auto-retry and claim-token fencing are unchanged.

OFF prevents new preparation/sending; bounded housekeeping can terminalize expired/stopped work. Missing host Push keys prevent claims/transport invocation but do not disable housekeeping. Shutdown cancels the worker; an invoked send with uncertain outcome is not automatically replayed. Restart can process still-eligible original current-epoch acceptance but never resurrects pre-enable, old-epoch or terminal Unknown work. Multiple app instances still rely on durable queue fencing, not timer ownership.

## Verification

`tests/Sem.ManualPushCompositionChecks` uses this exact extension and actual hosted worker, scoped factory and migrated synthetic SQLite. All transport calls are an in-memory spy; no real Push service is contacted. Cases cover default OFF, idempotent DI registration, scoped resolution, eligible dispatch, missing transport, expiry, unknown response/no retry, OFF/epoch changes, late registration removal, shutdown and fresh-provider restart. Other queue/engine/dispatcher checks remain separate and retain fault/lease/concurrency coverage.

Exact-source Release/all.NET/client regressions and virtual-native-passkey/Blazor acceptance are recorded only after completion. The actual operational page reports worker registration separately from activation and host-key configuration; the composer no longer falsely says transport is unconnected. Per-operation delivery outcome UI is a later step, not implemented here.

No new schema, production activation, DXP/.env/real data/keys/public deployment, new distribution or merge. User approval covers code registration and isolated testing only. Docker/physical-device/public-origin acceptance and deployment remain separate.

## Future rollout caution

Registration deliberately does not force an existing persisted ON state OFF. If this code is later deployed to an already-ON database, valid current-epoch admissions can be processed. Before any future rollout, verify/explicitly stop the existing durable activation as a separately approved operation. No production state was inspected or modified during this implementation.
