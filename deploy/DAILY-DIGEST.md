# Automatic daily administrator digest

English reference; [Japanese translation](DAILY-DIGEST.ja.md). This follows the four explicitly approved delivery settings, not a preview-only feature.

## Approved policy

- At **00:05 Asia/Tokyo**, send the previous Japanese calendar day: 00:00 inclusive to next 00:00 exclusive. The worker checks every three seconds; 00:05 is eligibility, not a guarantee of second-exact SMTP arrival.
- One message per administrator per day. Current global administrators receive all changed events; current event administrators receive only assigned changed events, combined into one message. A global administrator also assigned to events is not duplicated.
- No recorded changes or no currently authorized changes means no email. Disabled, unverified or unavailable recipients are not sent mail.
- After downtime, catch up every unscheduled recorded closed day, oldest first. Discovery is limited to seven days per tick and dispatch to twenty messages per tick, not an overall history cutoff.

Source: encrypted immutable successful registration/release snapshots introduced by PR16. Rejected/rolled-back/zero-success, spectator and answer-only changes have no source entry. No unavailable pre-PR16 history is invented. Counts are independent of the original immediate-email delivery status. Register and release are separate operations, not netted away. Frozen event/song/part/actor/release reason are preserved; affected-user display name is explicitly current. Questionnaire answers and recipient email lists are excluded.

## Persistence and authorization

Migration `20261013100847_AddDailyDigest` adds `DailyDigestRuns` (one day marker) and `DailyDigestMails` (unique day/user). Day generation, marker and recipient rows commit atomically. Existing identities, passkeys, auth outbox, business history, keys and event data are retained.

Recipients are selected with current roles at generation. Each dispatch/retry decrypts history within a coherent read transaction, scopes it to current roles, and rechecks enabled/verified email and role signature immediately before SMTP. Revocation narrows the body or cancels it; changing roles during preparation defers/rebuilds it. New administrators added **after generation** are not retroactively added to that day's recipient queue. Revoked permissions are never frozen into a previously prepared persisted mail body: this queue stores no body or plaintext business snapshot.

Automatic aggregation uses keyset batches of 500 and includes the whole authorized day in one email; it does **not** inherit the preview's 1000-operation limit. Large days therefore consume resources proportionate to that day, including a full in-memory message. Invalid/undecryptable history fails the message instead of sending incomplete information. No public accelerated clock, send-now API or new UI send button exists.

## Delivery and limits

`Pending -> Sending -> Sent`; unavailable authorized content becomes `Cancelled`. Atomic owner claims and 120-second leases prevent competing workers from routinely sending the same queued item. A live lease is renewed and ownership checked before SMTP; crashed leases can be recovered after restart. SMTP has the existing 15-second timeout. Five attempts maximum, with 30/60/120/240-second retry delays; the fifth failure or an expired fifth lease becomes `Failed`, not `Sent`. Diagnostic error values contain exception types, not email/body/SMTP secrets. Automatic catch-up does **not** promise infinite retry of terminal failures.

The existing production SMTP transport preserves configured authentication username independently from From and sender display name. Daily message identities are stable and distinct from immediate business messages. **SMTP acceptance followed by process failure before database completion can still duplicate a message**; stable Message-ID helps downstream deduplication but is not an exactly-once guarantee. Role changes after the final database check cannot be made atomic with external SMTP. Normal sent rows are not requeued or replayed.

Generation closes recorded snapshots visible at the time the day transaction runs. There is no indefinite late-write rescan or second email for a day already generated/sent. Normal business snapshots use the operation's commit-time clock and the five-minute grace; manually backdated/late-committing history after generation requires investigation, not silent extra mail. No destructive reset or automatic repair is performed.

The global read-only preview remains global-only and GET/no-store, and reading it never schedules or sends mail. Its UI now states the approved automatic policy, while date controls remain preview-only. Event administrators receiving their scoped email does not authorize global preview access.

## Acceptance and deployment boundary

`Sem.DailyDigestChecks` covers populated migration preservation, exact time cutoff, no-zero recipients, atomic rollback/generation deduplication, concurrent workers, current email/role scoping, bounded SMTP retries, expired leases/restart, multi-day catch-up, over-1000 full aggregation and SQLite/FK/model integrity. `--smtp` additionally exercises production MIME transport against the isolated local SMTP receiver, scoped received bodies, configured sender/Message-ID and no replay. The daily-digest workflow also runs every existing suite plus native passkey/real Blazor/isolated SMTP, old populated database upgrades/restart and actual Docker builds.

This branch activates the worker when the candidate application is installed and run; a preview request is not required. **No DXP installation, `.env` change, new delivery, public hostname/key/volume change or PR merge is performed here.** Keep PR17's verified fixed source and existing DXP version intact until a separate controlled upgrade.
