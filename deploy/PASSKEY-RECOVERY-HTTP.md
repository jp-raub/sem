# Passkey recovery HTTP integration

This is a development contract, not evidence of a deployed recovery, real email delivery, or real-device acceptance. The application has no operational environment at the time this increment was authorized.

## Routes and authority

All routes are under `/api/`. The existing router handles GET/POST and sets `Cache-Control: no-store`; other HTTP methods may receive routing-level 405 without that handler header. Unknown recovery actions return 404. No `/Account` route is enabled.

* GET `passkey-recovery-admin-context`: live global admin only; no query returns `{actorId}`. Exactly one `id` query returns `{actorId,target}`; a missing target is null. Target contains only ID/display name/masked saved email/enabled/recovering/operation ID/expiry. The recovering flag is bound to an operation proving this flow disabled the account, including current stamp/email checks.
* POST `passkey-recovery-admin-options`: `{targetUserId,reason}` → `{publicKey}` (native Identity assertion options).
* POST `passkey-recovery-start`: `{targetUserId,reason,credential,explicitlyConfirmed:true}` → `{operationId,expiresAt,maskedRecipient}`.
* POST `passkey-recovery-resend-options`: `{operationId}` → `{publicKey}`.
* POST `passkey-recovery-resend`: `{operationId,credential}` → the start-result shape, preserving the initial deadline.

Every admin POST requires exactly one query `expectedActor` matching the current cookie actor. Query values are nonempty, control-free, maximum 128 characters. Origin, strict query, live admin/session/stamp and expected actor are checked before antiforgery and body reads. The service rechecks live authority and the native proof is bound to target, reason/operation, admin and session. An admin cannot reset their own account through this flow.

Anonymous POSTs have no query parameters:

* `passkey-recovery-confirm`: `{operationId,token}` → `{userId,grant,expiresAt}`.
* `passkey-recovery-options`: `{userId,grant}` → `{publicKey}` (native Identity creation options).
* `passkey-recovery-complete`: `{userId,grant,credential}` → `{message}`; no normal sign-in cookie is issued. The user must perform ordinary passkey login after completion.

All POSTs, including anonymous ones, require exact same-origin Origin and the existing CSRF cookie/header. The email link is `/recovery#op=...&token=...`; the browser removes the fragment before network requests and sends bearer values only in JSON. Never put tokens/grants/credentials in query strings, audit records or diagnostic output. Initial mail is addressed only to the saved confirmed email.

## Bounds and failures

The endpoint applies transport and declared-length limits and rejects non-JSON content before antiforgery validation. Exactly one nonempty X-CSRF-TOKEN header is required; form-token fallback is not supported.

Bodies must be UTF-8 JSON objects with exactly the documented property names; unknown/duplicate properties, null/wrong types and missing fields are rejected. Explicit confirmation is a JSON boolean true, not a string. Credential requests (start, resend, complete) have a 65,536-byte cap; other recovery requests have a 4,096-byte cap. The reader checks Content-Length and probes at most one byte past the cap for chunked bodies. Kestrel transport headroom does not increase the application cap. IDs/tokens/grants have a 128-character cap, reasons 500 and serialized native credentials 50,000. Service validation further normalizes and bounds the reason.

400 covers input/CSRF/native proof errors, 403 authority or expired/invalid grant, 409 conflicting writes, 413 oversized bodies, 429 durable send/failure limits with Retry-After. A failed or lost POST response must not be replayed automatically. Admin UI refresh uses only the read-only context route before permitting another explicit action; an uncertain anonymous completion requires a fresh recovery flow or ordinary login rather than automatic credential replay.

The limited grant is not an application session. Disabled accounts remain disabled until native key persistence and grant consumption commit together. Recovery tables, mail purpose/protection, quotas and audits are separate from email-change state.

## Verification

`tests/Sem.PasskeyRecoveryHttpChecks` uses a disposable migrated SQLite database and loopback Kestrel with synthetic identity proof only in test DI. `tests/Sem.PasskeyRecoveryChecks` covers service/migration/dispatcher behavior. The cumulative integration script discovers both .NET projects and runs the recovery UI Node checks. These synthetic checks do not prove native WebAuthn interoperability; the separate local HTTPS browser fixture exercises the production adapter with a virtual authenticator and must be reported separately from physical-device acceptance. No provider email delivery follows from an outbox assertion.

## Live session invalidation

The HTTP live-session middleware checks current enabled/confirmed account state, security stamp, session claim and revocation on every authenticated request. Invalid application cookies are signed out and the request principal is cleared. Stamp rotation rejects pre-recovery cookies even after the account is re-enabled. The Blazor circuit provider revalidates display authentication state every 30 seconds; this is not immediate circuit termination. Core service authorization checks live account/stamp/session on each operation, independently of that display interval; it is not an authorization grace period. The legacy SEM.Enrollment options and completion paths reject recovery-disabled accounts, including a transaction-time recheck.
