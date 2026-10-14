# Atomic CSV append acceptance (Core only)

## Boundary

`ApplyEventImportAsync` implements the confirmed mutation following `EVENT-IMPORT-PREVIEW.md`. This stage adds a Core method and one additive receipt-table migration, not an HTTP route, browser UI, external spreadsheet retrieval, deployment or production activation. The existing read-only preview remains read-only. Import never sends mail/Push or creates participation.

## Request and current authorization

`EventImportApply` requires a canonical lowercase D-format operation GUID, explicit `Confirmed=true`, event row version, exact lowercase preview fingerprint, CSV, and a separate `ConfirmOverrun` flag. Unknown JSON members are refused. A fingerprint or operation ID is not authorization.

Every attempt, including recovery of a completed operation, revalidates the current authenticated account, security stamp, non-revoked session, effective event access and current global/event-manager permission. A receipt belongs to its original author and event: even another global administrator cannot recover it.

## New append

The SQLite write transaction checks the current version and recomputes the preview through the same context and transaction. Hidden phase, durable known pre-registration history, contrary business records, progress immutability, active local definitions, existing keys, limits, input validity and the current timetable are checked again. A changed comparison refuses the request. A positive planned-end overrun requires the explicit acknowledgment.

Only `Ready` rows are appended. New songs and mixed time parts follow the current active maximum sort orders; mixed parts retain CSV order. IDs are generated on the server. Song parts reference current event-local definitions and remain unassigned. Blank preparation stays null (inherits the default); explicit zero stays zero. Existing rows, including deleted rows, are not updated or resurrected. Version and sort-order overflow are refused.

Appended rows, exactly one event-version advance, one immutable receipt and one audit are saved atomically. Existing planned-policy validation runs before commit. A failure, including an audit insert failure, rolls back every change. No original progress baseline, registration, answer, reception, message, notification reservation or Push queue is written.

## Retry and privacy

The globally unique operation primary key fences concurrent requests. A matching author/event/request retry returns the original counts, result version and acceptance time with `Replayed=true`; it does not append, audit or advance the version again. Authorization is still current, but a successful receipt is not treated as a new import when the event phase or plan has since changed. A different author, event or normalized request cannot reuse the ID. Different IDs with the same stale preview cannot both append successfully.

Request identity is the **privacy-filtered parsed representation**, protocol version, event, submitted version/fingerprint and both confirmation flags, not literal CSV bytes. The parser retains only assignment presence, never its value. Changing only an ignored assignment value therefore produces the same request identity; normalized equivalent syntax can also replay. Changing meaningful parsed fields, flags or preview identity does not. This is intentional effect-equivalent replay, not a byte-for-byte payload guarantee. The receipt stores only the SHA-256 request hash and result metadata, never raw CSV, assignment values or message content.

Callers must retain the same operation and request while a result is uncertain. They must not silently create a new ID, automatically resubmit a modified request, or label receipt recovery as a new import. A later route/UI must provide explicit preview/confirmation, CSRF/no-store controls, memory-only source/operation state and stale-response/navigation protection.

## Migration and verification

`20261014181715_AddEventImportOperations` creates only `EventImportOperations`, its primary key, actor/event indexes, restrictive foreign keys and receipt-shape/result constraints. Its down migration drops only that new table. Existing data and configuration are not rewritten. Applying the migration is not permission to deploy or enable production behavior.

`Sem.EventImportApplyChecks` uses actual migrations and synthetic SQLite. It covers old-schema preservation, current authorization, stale preview/version/definitions/settings/history/phase, author-scoped replay, ignored assignment-value privacy, real separate-process replay, concurrent same/different identities, audit fault rollback, mixed append ordering, null/zero preparation, overrun acknowledgment, whole-batch rejection and SQLite integrity/foreign keys. Local results and fresh CI are recorded separately; static review is not runtime acceptance. No browser, real spreadsheet, real SMTP/Push, DXP or physical-device acceptance is claimed.

Remaining: authenticated route and UI; explicit preview/apply/unknown-result lifecycle and real browser verification; separately scoped external public spreadsheet retrieval with redirect/SSRF/size/timeout controls. No production rollout or existing persisted Push state is inspected or changed.
