# Manual participant notification: read-only preview foundation

## Scope

This change adds `PreviewEventManualNotificationAsync` and a dedicated synthetic SQLite console suite. It does not add an HTTP endpoint, composer UI, send operation, migration, worker, distribution or deployment. A preview never sends mail or Push and never creates inbox, outbox, audit or registration rows.

The approved v33 mock (`9cf8b031258fa1c9afe9668f54e61f002c4bce17`, `mock/event-operations.js`) provides a manager-initiated message with a required title (80) and body (2000), deduplicated current performers and spectators, explicit submit, and generic Push body. This backend foundation is not a claim of full v33 parity.

## Current read contract

- Revalidate the authenticated enabled/email-confirmed account, current security stamp and non-revoked session, current event and effective phase, and database-backed global/assigned-event management permission in a read transaction. Forged role claims do not grant management. No event-day restriction is introduced.
- Normalize surrounding whitespace; subject length 1–80 and body length 1–2000 use .NET UTF-16 length, consistently with existing core text validation. Reject subject control characters including CR/LF/tab; permit internal CR/LF/tab only in body. Preserve internal line endings. Values remain plain text, never evaluated as markup.
- Resolve current assignments on live songs and live event part definitions plus current spectators, confined to this event. Deduplicate identities. Do not infer current membership from old answers or reception history.
- Recipients must currently be enabled, email-confirmed and allowed to view the event under existing effective-phase/global-or-assigned-manager gates. Hidden-event ordinary participants are excluded. An assigned/global manager is not automatically a recipient: current participation is still required. Zero eligible recipients is rejected.
- Return public event identity/version, normalized draft, eligible inbox-recipient count, opaque snapshot fingerprint and the generic Push preview body `新しいメッセージがあります`. No recipient IDs, emails, profiles, answers, security stamps, subscriptions or transport counts are returned. The recipient query projects only ID and account eligibility flags.
- The SHA-256 fingerprint includes protocol, actor, event identity/title/version, normalized text and sorted eligible recipient IDs. It is only a change-comparison value. It is **not** authorization, explicit approval, an operation ID, a lock, a send guarantee or proof that recipients have devices. A future submit must independently revalidate all gates and compare the current snapshot inside its write transaction.

## Remaining send boundary

A later change must add explicit preview/confirm and unsent-input navigation protection, a durable actor/event-bound operation ID and request hash, atomic inbox/recipient snapshot/outbox/audit persistence, safe retry/uncertain-result recovery, event-aware transport gates and dispatch-time account/device/session validation. Do not wire this preview directly to the legacy `NotificationService.Message` helper or overload automatic notification reservations. Transport acceptance must not be described as terminal receipt/read or exactly-once delivery. No arbitrary new manual-Push expiry policy is established here.

## Verification status

The editing sandbox was unavailable while this change was authored. The dedicated GitHub workflow runs Release build (warnings as errors), this suite and all existing .NET console regression projects against synthetic data; its result must be checked separately. No local build, native-browser, Docker, real SMTP/Push, DXP or full send acceptance is claimed by this document.
