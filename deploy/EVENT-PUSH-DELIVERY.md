# Automatic event Web Push delivery

## Scope and activation

Opening and song-preparation reminders are connected to the real Web Push transport. Manager rules still default OFF. Existing personal arbitrary-part Start preferences are NOT connected by this change. Their expiry policy remains undecided; do not interpret the personal switch as a delivery promise.

Both VAPID keys must be configured. The UI separately reports missing keys. No worker claims or sends jobs when transport configuration is absent. Keep the same hostname, VAPID keys, Data Protection keys and current `.env`; this change does not deploy or alter a live DXP environment.

Opening recipients are current formal performers plus spectators. Preparation recipients are ONLY the target song's current performers. Every recipient must be enabled, email-confirmed and currently allowed to view the event. Device subscription permission alone does not grant event access. Effective automatic phases are evaluated at request time, not only from the phase-worker cache.

Activation and expiry remain the approved rule policy: no historical burst on ON/re-ON; opening expires at opening or event start; preparation expires at the earlier of due+10 minutes and latest preparation start. Actual preparation/start/end makes the target obsolete. A preparation based on the preceding actual Start must be newer than the arming progress watermark.

## Subscription security and upgrade

Migration `20261013151643_AddEventPushDelivery` adds nullable device security-stamp/session bindings and the durable per-device delivery ledger. Existing subscriptions are preserved but are NOT retroactively trusted: **after the first upgrade, sign in and register notifications once again on each desired device**. Registration stores the current authenticated user's security stamp and current session hash. Do not backfill old bindings or paste credentials/keys/subscription URLs into logs or Notion.

After authenticated registration, ordinary container restart preserves the binding. Logout removes current-session subscriptions; switching account cannot transfer queued notifications to the new owner. Disabled/unconfirmed users, changed stamps, revoked sessions, replaced or unsubscribed devices fail closed. Legacy message-box Push also respects these bindings.

Endpoints must be HTTPS:443 on exact supported Apple/Google/Mozilla hosts, without credentials/fragments. Redirects are disabled in the event transport. Subscription keys/endpoint/session data are never copied to the delivery ledger, payload or diagnostic logs; the ledger stores fingerprints instead.

## Durability and final gate

The scheduler scans at most 20 enabled events per batch with a rotating keyset cursor, isolating invalid events. Jobs are unique per event/logical reminder/user/device. Accepted jobs never reopen. The dispatcher atomically claims at most 20 due jobs, owns a two-minute lease, and retries transient failures at most five times. A dead process lease is recoverable; a dead fifth attempt becomes Failed.

Before sending, it re-reads current recipient membership, effective viewing permission, identity/session/device binding, rule generation, source fingerprint, target state and expiry. It renews its owned lease and repeats those gates immediately before transport. TTL is floored from the remaining expiry, and send timeout is capped at 15 seconds or the remaining expiry. Less than one second remaining is not sent. Provider 404/410 removes only an unchanged subscription; concurrent re-registration is preserved.

A provider-accepted job records Accepted and inserts one privacy-minimal inbox message per logical user notification, even with multiple devices. It does not recursively trigger generic Push/SMTP. Payload contains generic text, a reference timestamp, stable tag and an authenticated event timetable destination, not song/event titles, addresses or answers. Service worker click targets are revalidated against allowed same-origin event routes; legacy inbox destinations remain supported.

## Guarantees and limitations

**Accepted means provider acceptance, NOT device arrival or viewing.** Normal repeated reconciliation/restart/concurrent claims do not resend Accepted rows. Nevertheless, a timeout or crash between external acceptance and committing the local result can cause a retry and duplicate delivery. Stable provider Topic/browser tag coalesce where supported; they do not provide exactly-once delivery. An already accepted Push cannot be recalled, and a state change in the tiny interval after the final gate is unavoidable.

A payload arriving past expiry displays a generic expired notice rather than stale actionable text; it remains visible to respect browser user-visible Push requirements. Notification permission may need re-enabling after logout. Receiver operating-system restrictions, VAPID validity, NPM routing and physical-device arrival require separate deployment acceptance.

## Verification

Run the dedicated ledger/transport checks and service-worker checks:

```sh
dotnet run --project tests/Sem.EventPushDeliveryChecks -c Release
node scripts/event-push-service-worker-checks.cjs
```

The transport check uses the production encryption/request code with a fake HTTP handler, never a real provider/device. Prior rule tests now assert the connected boundary while VAPID is unset. The full workflow retains all prior data-preservation, SMTP, Docker, native-passkey, migration and interactive Blazor checks. No merge, new distribution or DXP operation is implied by CI success.
