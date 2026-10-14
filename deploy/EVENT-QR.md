# Event participant code / scan ledger foundation

English is authoritative. This increment is an **authenticated API and storage foundation**. It does not render a QR image, open a camera, follow a pasted link, add an anonymous profile QR, or connect a successful read to QR-method reception completion. Manual receipts remain separate. Do not treat backend acceptance as completed camera/QR UX or live-device acceptance.

## Current scopes

GET `/api/core/event-qr?id=EVENT` reads the registered viewer's current code; `&user=USER` is available only for current global/assigned event managers. GET does not create/rotate anything. An enabled/confirmed current actor, valid stamp/session, currently viewable event (manager Hidden preview allowed) and current enabled/confirmed registered subject are required. Historical saved answers alone do not qualify. Responses are no-store; no email/private answers are included.

POST `/api/core/event-qr?id=EVENT` uses strict EventQrEdit(requestId,userId,tokenVersion,reissue). userId=null means self. Initial issuance is explicit with version0/reissue=false. Existing code must be read, or changed with current version and reissue=true. Reissue atomically replaces the old code and increases version. A current database role assignment, not a client claim, is authority. Codes are random 256-bit hexadecimal values, unique by SHA-256 hash. Raw code is persisted only with Data Protection purpose `SEM.EventReceptionQR.v1`; retain the DB and protection keys together. Do not paste codes, ciphertext, full code-bearing links or keys into chat/logs. There is no newly invented 24-hour/sliding expiry; profile QR requirements are separate.

POST `/api/core/event-qr-read?id=EVENT` uses strict EventQrReadEdit(requestId,token). Only current event managers may submit it. A current code for the exact event and current registered enabled/confirmed subject is necessary. Bad shape, unknown code, removed registration or reissued old code produces no successful read. Each intentional new read has a new request ID and stores subject, current token version, server UTC time and reading manager. **Reading is not reception completion.** Repeated reads are retained; manual receipt cancellation does not remove them.

GET `/api/core/event-qr-history?id=EVENT` is manager-only and returns newest-first 100 records and a continuation cursor. beforeAt/beforeId must be supplied together with strict UTC millisecond timestamp/GUID. It includes stable subject/actor IDs and current display names, not codes, hashes, email or questionnaire answers. Removed participants' historical records remain available to current managers. Timestamp/ID ordering prevents cursor ties from dropping rows.

## Idempotency / concurrency

One ledger binds event, actor, operation kind and complete request fingerprint. Identical authorized issuance retry reports the version committed by that operation; it does not roll back a newer reissued code and deliberately returns no secret code. Re-read current code separately and compare versions. Historical successful read retry reports that original recorded read, **not current code validity**, and does not make a new scan after reissue. Changed payload, actor, event or kind cannot reuse the ID. Current manager/session/visibility authorization still applies to retries. Receipt linkage must later check current code/version again before confirmation; historical read success must never bypass it.

Token version is an optimistic concurrency token and state is event×user keyed. Issuance, replacement, successful reads, ledger and audit are transactional. Existing SQLite busy/concurrency conflicts remain enabled; no automatic retry/submission is added by these APIs. Origin, CSRF, body limits and unknown-field rejection use the common protected endpoints. The later UI must capture operation IDs, retain uncertain outcomes and use only explicit identical retry.

Migration `20261013173823_AddEventParticipantQr` adds EventParticipantQrs/EventQrReads/EventQrOperations only. Existing receipts/cancellation/registration/identity/passkeys/notifications are not changed. Read history is not foreign-keyed to mutable current token state, so reissue cannot erase it. Restrictive event/user/actor FKs retain provenance; no raw token copy is stored in replay results or audit records.

## Verification / remaining work

Dedicated checks cover own/manager/Hidden/current membership scopes, old-token rejection, replay versus new reads, no implicit receipt, cancellation retention, key-backed restart, removed roles/session, secret storage/projections and additive model. Native authenticated acceptance uses the real API/native passkeys/legacy adoption and demonstrates spectator own issuance, manager scope, rotation, no implicit receipt and private history. Existing full regressions/fixed-source full CI are confirmed separately.

Remaining: actual QR images and camera/paste/direct-link interface, current-code recheck and read-linked QR-method receipt completion, per-person latest scan/whole roster reception state, full history UI, recovery/edit-mode integration and live device/network/DP restore acceptance. No DXP change, bundle, merge or automatic user submission is performed here.

Follow-up actual authenticated QR images and self-service issuance UI: [EVENT-QR-IMAGE.md](EVENT-QR-IMAGE.md). Camera/reader and QR-method receipt linkage remain separate.
