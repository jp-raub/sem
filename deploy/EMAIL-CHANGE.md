# Email address change (English canonical)

This increment adds a purpose-specific email-change flow. It does not implement account recovery or host emergency reset, and is not a production deployment acceptance claim.

## Proof and timing
- The current enabled, email-confirmed cookie actor, security stamp and session are checked before authenticated POST bodies. `expectedActor` must occur exactly once in the query and match that actor. Origin and antiforgery are required. No caller-selected user is accepted.
- Begin fixes a new address and starts a five-minute native Identity passkey challenge. The target cannot be replaced while pending; complete, cancel or expire first.
- A successful native assertion queues a confirmation to that new address and starts the original fifteen-minute mail deadline. Every resend requires a fresh native assertion; challenges expire within five minutes and never beyond the original mail deadline. Resends rotate the token, invalidate older links, and do not extend that deadline.
- Confirmation is anonymous and purpose-limited: a one-use bearer from the fragment is immediately removed from the address bar, then POSTed in a same-origin JSON body with Origin/antiforgery. It changes only the email/login name, rotates the security stamp, revokes the initiating session and queues a notice to the snapshotted old address. It does not create a normal session. Existing passkeys are retained; re-login is required.

## Persistent limits
Accepted user-initiated send/enqueue requests are limited per account and purpose to three in the preceding rolling sixty minutes, at least sixty seconds apart. SMTP delivery retries do not count again. Five failed passkey proofs or five failed email confirmations in rolling fifteen minutes throttle this flow, not normal login. The fifth email failure invalidates the pending operation. Cancel/recreate/resend cannot reset counters. Clock rollback retains future counter entries rather than bypassing limits. HTTP 429 includes Retry-After; expired proof stages must restart.

## Storage and delivery
The additive migration adds operations, persistent rate state and a dedicated outbox. Targets and explicit mail payloads use application Data Protection; bearer tokens and original/target identities are hashed for comparison. Audits exclude addresses, tokens, assertions and mail content. Data Protection keys must be durably protected under the existing deployment policy; losing them prevents pending payload recovery. No production keys or email delivery are exercised by synthetic checks.

The separate dispatcher snapshots the recipient rather than looking up the current email at send time. It claims with a lease, retries at most five times, clears encrypted payloads after terminal delivery/cancellation, and rejects expired or security-stamp-invalid verification work. SMTP is at-least-once under an ambiguous transport outcome, not exactly-once. Delivery failure does not undo a completed email change. Old-address notices have a seven-day delivery retention deadline. There is no public mail payload or token-query endpoint.

## API and verification scope
GET `/api/email-change-status` returns `{actorId,pending}` (ISO timestamps; no pending address). Authenticated POSTs: `email-change-begin` (`newEmail`), `email-change-reauth-options` (`operationId`), `email-change-reauth` (`operationId,credential` serialized native assertion), and `email-change-cancel` (`operationId`). Each has query-only `expectedActor`. POST `email-change-confirm` accepts only `operationId,token`, with no query or actor selector. Strict property/type validation, fatal UTF-8 and bounded streaming apply: assertion body 64 KiB, others 4 KiB. Handled GET/POST responses are no-store; other methods may be rejected by routing before that handler.

Dedicated service and loopback HTTP checks use migrated temporary SQLite and synthetic actors/proof adapters. They are not evidence of native WebAuthn acceptance; that requires the separate virtual-authenticator browser fixture using the production native proof implementation. PostgreSQL, production SMTP/proxy logging and real-device acceptance remain separate deployment checks. Do not log confirmation bodies or URL fragments at infrastructure boundaries.

Registered and unused target addresses follow the same begin/challenge/send path; begin does not query target membership. Self-address rejection reveals only the actor's own known address. A registered target cannot be claimed: uniqueness is checked after the one-use mail proof before mutation. A concurrent pending target reservation can still cause a generic conflict; this does not identify account membership.
