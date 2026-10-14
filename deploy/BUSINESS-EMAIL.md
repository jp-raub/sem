# Business registration/release email

English reference; [Japanese translation](BUSINESS-EMAIL.ja.md).

## Implemented scope

Successful formal-event **performance-slot registration and manager release** enqueue one consolidated recipient email per operation. The affected performer, not a proxy manager, is the recipient. Only successful slots are included; occupied/stale/quota failures and zero-success operations are excluded. Spectator/answer-only operations do not acquire new email semantics in this increment. No historical operations are retroactively notified.

An encrypted, immutable snapshot includes event/title/scheduled date, successful song/part names, actor, release reason, subject and body. Absolute dates are presented with Japan's UTC+09:00 offset; private questionnaire answers, other recipients' addresses and secrets are excluded. This frozen record can support future authorized digest aggregation; **daily digest, Push rules, preparation triggers and notification-management UI are not implemented here**.

## Transaction and migration

`AddBusinessNotifications` creates a separate `BusinessNotifications` table, retaining original identity/passkeys/registration states/answers/progress/old delivery and message rows. Restrictive foreign keys bind the notification to the saved registration operation, event and recipient. Operation ID is the primary key, so one committed operation has at most one email record.

The operation, participant/slot changes, audit and snapshot queue insert share the existing transaction. No notification is visible to the worker before commit; a rolled-back operation has no future email. Identical request replay returns the saved result before queue creation. Notifications already generated are not recreated on restart. A protection or database failure **before commit** rejects the operation atomically; SMTP failure **after commit** never rolls back the business result.

Data Protection uses `SEM.BusinessNotifications.v1`; encrypted payloads require the same application/key ring on restore. Back up the DB and existing Data Protection keys together. No new production secret or key rotation is introduced.

## Delivery semantics

A dedicated worker checks the queue every three seconds (server scheduling, not an arrival guarantee). Atomic conditional SQL claims prevent two workers from normally sending the same queued row. Claims persist a unique owner and 120-second lease; expired claims can be recovered after crash/restart. The existing SMTP client has a 15-second timeout. Only the claim owner can complete or reschedule its row. Error diagnostics record exception **type**, never the SMTP text or protected body.

- `Pending`: due now or waiting for retry.
- `Sending`: claimed; lease/owner required.
- `Sent`: SMTP submission accepted and saved; `SentAt` present. This is **not** recipient delivery/read confirmation.
- `Failed`: five attempts exhausted, or last-attempt abandoned lease expired. No automatic infinite retry or public replay endpoint.
- `Cancelled`: recipient missing/disabled/email-unconfirmed/without an email at dispatch. `SentAt` stays null.

Retry delays are 30/60/120/240 seconds before the next bounded attempt. Cancellation during sending preserves the lease for later recovery. Authentication/recovery messages remain on the pre-existing notification path and are not subject to this business-recipient cancellation rule.

The SMTP `Message-ID` is stable per operation. This helps identify retries; it **does not guarantee receiver deduplication**. The external SMTP side effect and database completion cannot be atomic: a crash or completion-record failure after SMTP acceptance can result in a repeated submission. Lease and idempotent operation handling suppress avoidable app-side duplication but do not promise exactly-once delivery.

SMTP authentication ID, configured From (including supported display-name syntax), credentials and TLS are inherited from the existing `NotificationService`. No SMTP, public hostname, deployment `.env`, volume or Push key changes are required. Do not expose the queue, encrypted payload or recipient details through an unapproved public API.

## Acceptance and limits

`Sem.BusinessNotificationChecks` covers populated PR15 migration, immutable encrypted snapshots, partial/zero success, reasoned proxy release, authorization and stale failures, replay, injected atomic rollback, bounded retry, multiple dispatchers, cancelled users, key reload/expired lease recovery, original auth queue preservation and SQLite/FK/model integrity. `business-email-checks.cjs` exercises actual authenticated operations against isolated SMTP, configured From, stable Message-ID, successful slot details, affected-user recipient, replay/restart and denied/zero-success non-delivery.

Run these with the entire existing native-passkey/Blazor/SMTP and unit regression suite. CI must be green at the exact submitted source before claiming full acceptance. These synthetic checks are not DXP device/real-mail-provider acceptance. No merge, delivery or production deployment is part of this change.
