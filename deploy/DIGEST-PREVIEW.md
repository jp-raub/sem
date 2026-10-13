# Daily digest: read-only preview

English reference; [Japanese translation](DIGEST-PREVIEW.ja.md).

## Boundary

This increment implements **daily grouping and global-administrator mail-text preview only**. It does not enqueue, schedule, send or enable a digest. Automatic delivery policy has since been explicitly approved and is implemented separately in [DAILY-DIGEST.md](DAILY-DIGEST.md). The preview itself remains read-only and does not trigger that worker.

## Calendar and committed history

A selected `yyyy-MM-dd` denotes the Japanese calendar day: Asia/Tokyo 00:00 inclusive to the next 00:00 exclusive, represented as UTC 15:00-to-15:00 bounds. A missing API day defaults to the server's current Japanese day. Previewing today is not a delivery cutoff decision. The date input is strict and date-only, and impossible/extreme overflowing dates are rejected.

The source is PR16's encrypted business-notification snapshots, produced in the same transaction as successful formal performance registration/release. Partial success includes only committed slots; rejected, rolled-back, zero-success, spectator and answer-only operations have no such snapshot. Historical operations before PR16 are not backfilled. Counts do not depend on whether the original recipient email is Pending/Sending/Sent/Failed/Cancelled: this is committed business history, not a mail-delivery summary. Register and release of the same slot remain two operations, not a net-zero disappearance.

Operations sort by instant then ID. Registration and release slot totals are distinct. Frozen event/song/part/actor/release reason come from protected snapshots, not renamed live records. **Affected-user display name is explicitly current**, because PR16 did not freeze that name. Preview does not falsely label it historical. No questionnaire answers, recipient email list, protected payloads, SMTP errors or secrets are returned. Subject line breaks are removed from the service label.

## Authorization and surfaces

- GET `/api/core/digest-preview`, optional `?day=yyyy-MM-dd`.
- InteractiveServer page `/service/digest-preview`, linked from the global service menu.
- Current enabled/email-confirmed ordinary identity, valid security stamp/session and **current global-admin flag** required for every read. An event-scoped administrator cannot open the global mail-text preview, even for an assigned event; this follows v33's preview rule. Automatic scoped-email recipients are defined separately by the approved daily policy; receiving that email does not grant global preview access.
- The UI escapes text, is readable at mobile width, and selects another date without writes. Role/auth failure clears the displayed data. Validation failure removes stale text but leaves date controls for retry after a previously authorized request.
- GET is a read-only, no-store endpoint. There is no new POST/send/queue API. A read transaction keeps the query coherent; original histories, queue statuses/attempts/leases, participant/event versions, audit, auth mail and keys are unchanged.

The preview is bounded at **1000 operations per selected day**. Larger days fail explicitly instead of silently showing an incomplete digest. This is a preview resource limit, not a new participation limit or an approved automatic-delivery truncation policy. Undecryptable/corrupt/duplicate/invalid snapshots refuse a partial preview. The preview itself needs no new schema; automatic delivery adds separate day/mail tables.

## Acceptance

`Sem.DigestChecks` verifies exact Japanese midnight boundaries, ordering and offset instants, separate register/release totals, immutable inputs and frozen details, current-name labeling, strict day parsing, all five original-email statuses, read-only persisted state, auth/role/session denial, corrupt protected history, overflow refusal and model/SQLite/FK integrity. `digest-preview-checks.cjs` verifies actual authenticated API and InteractiveServer date selection, mobile layout, service-menu separation, denied member screen and absence of digest SMTP submission. Run the full existing business-email/native-passkey/Blazor/SMTP regression and fixed-source Docker CI as well.

Synthetic acceptance does not mean DXP device validation or production installation. Preserve all existing deployment configuration, public hostname, keys and volumes. No merge, new delivery or DXP operation is part of this increment.
