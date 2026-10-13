# Personal actual part-Start Push delivery

## Approved behavior

Each user can opt into actual Start notifications for any currently viewable opening, song or transition part, not only their own performance. Choices default OFF and are user-owned; managers cannot select another recipient's preference. This is **not** a forecast-time or preparation reminder.

Expiry is the explicitly approved earlier of **actual Start + 10 minutes** and **actual End**. No End means the ten-minute bound remains. At or past expiry, do not send. The worker does not replay Starts that occurred before opt-in or re-enablement. Missing actual Start means waiting, never substituting planned or estimated time. An End that atomically records the next transition Start can trigger that transition's own opt-in; cascading song PrepStart does NOT trigger song Start.

## Persistence and migration

Migration `20261013160039_AddPartStartDelivery` adds personal activation clock/progress bounds and extends the existing per-device ledger to `PartStart`. Existing ON preferences retain Enabled, Version and ChangedAt; migration captures the adoption-time UTC clock and current event progress watermark. This preserves the selection while excluding already recorded historical Starts. OFF preferences remain OFF, with no fabricated historical delivery. Identical ON is a no-op; OFF retains the version and clears bounds; re-ON captures new bounds.

The original legacy device re-registration requirement from [automatic delivery](EVENT-PUSH-DELIVERY.md) still applies. Registered devices survive ordinary restart; personal opt-in does not grant browser permission or invent a device binding. Missing host VAPID keys are separately reported and prevent worker dispatch. No live `.env`, hostname, key or volume is changed by this implementation.

The source must match both current part existence/kind and validated frozen progress records. Immutable actual Start/operation identity supplies the source fingerprint; unrelated estimate/progress changes do not recreate or falsely cancel that Start. Current End still shortens expiry at every send gate. A removed current part, invalid progress, missing activation bound or future actual fails closed.

## Delivery and security

The shared scheduler scans automatic rules and personal preferences with one fair event cursor. Personal recipient is exactly the preference owner; event membership/performance assignment is not required, but current event viewing permission is. Before sending it checks current opt-in/version, validated actual source, expiry, enabled/confirmed user, effective phase/viewing permission, security stamp, unrevoked session and unchanged device binding. The second final gate repeats this after lease renewal.

Personal notifications reuse durable device claims, two-minute leases, bounded five-attempt retry, provider404/410 cleanup that preserves re-registration, stable Topic/browser tag, and one inbox message per logical user Start across devices. Payload contains generic text and actual Start reference time, not private event/song/transition titles, addresses or answers. Click revalidation opens the authenticated event timetable. TTL and timeout never intentionally extend beyond remaining expiry.

Provider Accepted still does not prove device arrival. Crash/timeout after external acceptance but before durable local commit can cause duplicate retry; stable Topic/tag is coalescing, not exactly-once. An already accepted Push cannot be recalled. Expired arrival displays only the generic expired notice. These limitations are the same as automatic delivery.

## Verification / deployment boundary

```sh
dotnet run --project tests/Sem.PartStartDeliveryChecks -c Release
dotnet run --project tests/Sem.EventPushDeliveryChecks -c Release
node scripts/event-push-service-worker-checks.cjs
```

The dedicated checks include populated PR23 upgrade, preserved ON/accepted history, all part types, no membership requirement, no scheduled/preparation substitution, exact expiry, history/re-ON suppression, opt-out/hidden/removed/revoked gates, second final gate, estimate identity stability and shared restart/retry. Production provider requests remain fake in transport tests; no physical-device arrival is inferred. All previous regression, SMTP, native passkey, data-preserving migration, mobile/Blazor and Docker checks remain in the workflow.

Prefer forward migration and tested backup/copy rollback. A downgrade of the kind constraint cannot safely accommodate already-created PartStart ledger rows; do not delete history to force it. No deployment, bundle or merge is implied by CI success.
