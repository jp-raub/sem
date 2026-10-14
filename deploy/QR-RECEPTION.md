# Read-linked QR reception API

This increment adds **confirmed QR-method reception**, not a camera or reader page. The existing reception page remains the manual workflow, but accurately labels both methods in current receipt and history. Scan/reading alone never marks a participant received.

## Strict confirmed write

POST `/api/core/qr-reception?id=EVENT` requires current management and a valid current session plus the existing origin, CSRF and request guards. The strict body is `requestId`, `userId`, `readId`, `token`, `tokenVersion`, `eventVersion`, `participantVersion`, `receptionVersion`, `receptionDone`, `note`. Unknown fields are rejected. Confirm identity, current registration and required reception actions before setting `receptionDone=true`; note is optional, max 500 characters.

A committed successful read must match event and user. At final confirmation the raw token hash and current code version must still match the read version; reissue during confirmation makes the old read unusable for a new receipt. Current event, registration and receipt versions are checked again in one transaction. Disabled/unconfirmed/unregistered users and an already-active receipt are rejected. A current second manager can confirm a genuine read by another manager; read provenance and receipt actor remain independently recorded.

The receipt stores method `QR`, original `ReadId`, server UTC/actor and encoded note. Manual receipts retain null `ReadId`. Composite database FK binds event, user and read; the method check prevents a fabricated manual/read combination. Receipt, optimistic state, retry ledger and audit commit together. Raw code is not persisted in the receipt or retry result; keep the code out of logs and error messages.

## Retries, cancellation and retention

Capture full payload and GUID before writing. An identical authorized retry reports the original commit without making another receipt; any changed payload under the same ID is rejected. Manual and QR payloads cannot reuse the same operation ID. Historical success is not evidence of current valid code or current received state. Reissue or cancellation never resurrects a cancelled receipt. Existing explicit cancellation supports QR too and retains original receipt/read/actor/time. A new receipt requires a new confirmation and ID with fresh versions.

## Populated SQLite upgrade

Migration `20261013181107_LinkQrReceptions` adds nullable read linkage and the composite key/FK/check. SQLite table rebuilds need a temporary parent unique index before the child rebuild; it disappears with the old read table. Custom SQL ends in a semicolon because startup replays the migration script to verify the supported schema. New DB, populated prior schema, repeated startup and final schema/model checks are tested.

The populated fixture retains every old column in users, events, protected codes, reads, and active/cancelled manual receipts, then checks integrity and foreign keys. No existing data is deleted or treated as a newly confirmed QR receipt. **Do not downgrade a database that contains QR receipts**: old method constraints cannot represent them. Rollback uses the preserved old image plus its own pre-upgrade volume/backup, never an old image on the upgraded live DB. Continue the existing stopped-copy backup/restore acceptance process before any DXP update.

## Verification and remaining scope

Dedicated checks exercise authorization, strict/versioned confirmation, actual read and current token requirements, reissue mid-confirmation, exact retries, cancellation/history, manual compatibility, independent composite FK rejection and populated migration/restart integrity. Native HTTPS/passkey tests exercise actual API and method display with synthetic participants. Full regressions and CI results are recorded separately; no physical-device or DXP acceptance is implied.

Remaining: camera/paste/direct-link read UI, explicit QR confirmation page, complete roster/history UI and final recovery/edit-mode integration. No merge, delivery bundle or DXP change is performed by this increment. English is canonical; Japanese companion is maintained in the same change.
