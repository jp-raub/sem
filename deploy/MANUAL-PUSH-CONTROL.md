# Manual Push operational control (worker not registered)

## Current-cookie boundary

Global administrators access `/service/manual-push` from service management. The interactive page imports a single-attempt browser transport, re-reading current cookie authority independently of the long-lived Blazor circuit. Context contains only actor ID, current activation/epoch, runtime-connected flag and a configured boolean; no account email, recipient, subscription, keys or body. Both HTML and API are no-store. Current Core actor validation still checks enabled/confirmed account, stamp, unique live session and DB global-admin flag. Event managers/forged roles do not suffice.

GET `/api/core/manual-push-control` is read-only; mutation is POST `/api/core/manual-push-control-change?actor=...` only. POST requires exact configured Origin, antiforgery and an exact current-cookie actor match before Core independently authorizes. Input rejects unmapped properties; canonical GUID, expected epoch and explicit confirmation remain mandatory. Status403/400/409 are definite application rejections for a first attempt; control-server429/5xx/network/malformed success are unknown. This differs from a received WebPush provider429.

## Frozen operation / truthful current state

Stage is not confirmation. The request ID, desired state, expected epoch and confirmation freeze before the first await. Busy/unknown disallow alternate operation, discard and internal navigation; external unload warns. The service-return link uses an explicit circuit guard and NavigationManager rather than unguarded static-SSR enhanced navigation. A lost response preserves the same ID and request. A later rejection cannot prove an earlier attempt did not commit. Authority loss hides private controls without deleting the ID; a different current-cookie actor cannot reveal/retry the original operation. Reauthorization must match original actor. Browser storage/auth-state files and automatic submit retries are not used.

A successful response must match ID/target/expected resulting epoch. Historical replay is shown separately and NEVER assigned as current state; a new authorized context read displays current ON/OFF. Thus old ON replay cannot undo or visually overwrite newer OFF. First definite rejection requires fresh current context and approval. Explicit current-state refresh invalidates an unsent approval. During an outstanding context read, stage/confirmation/submit are blocked both in DOM and handlers; a delayed pre-mutation snapshot cannot overwrite a successful newer operation. Cancel discards only unsent stage, not durable state/audit. The state/audit/ledger transaction and accept serialization remain in the existing Core.

Runtime marker is explicitly false in Program and the manual worker is not registered. UI clearly says ON governs future admissions but this version does not send actual Push; missing-key status is separate. Startup/migrations never enable activation. Synthetic test ON/OFF operations are not production enablement. No migration, real provider, DXP, distribution or merge.

## Verification and remaining

Dedicated pure state29 checks cover first context/actor pin, approval, frozen IDs, concurrency guards, unknown/rejection retention, actor switch, mismatched result and overflow, replayed ON/current OFF, no-op and cancellation of unsent stage. JS transport tests and isolated virtual-passkey/Blazor HTTP/UI cover Origin/CSRF/roles/methods/no-store, actual lost committed response, stale replay/current OFF, conflicts, navigation/44px/mobile/focus. All40 .NET regressions and exact-source CI are separate evidence; report final counts only when runs complete.

Service/worker composition and truthful composer delivery status still remain. Runtime integration must preserve default OFF, no pre-enable backfill, original600s deadline and removed-recipient inbox retention/no subsequent Push. Real Docker/provider/device acceptance/deployment requires a separate stage and explicit approval.
