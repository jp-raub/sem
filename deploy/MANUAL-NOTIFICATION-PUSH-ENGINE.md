# Manual Push bounded engine and opt-in worker definition

## Boundary

Adds a bounded `RunOnceAsync` orchestrator, bounded expired-lease/expiry housekeeping, and a BackgroundService definition for future explicit composition. **None is registered in production DI/HTTP/hosted services.** No startup enablement, migration, HTTP control, deployment or actual provider acceptance is introduced. The default remains OFF; older accepted requests never acquire admission retroactively.

## One pass

1. Sweep at most20 expired Pending or elapsed Claimed rows, even when transport keys are absent. Pending expires; Claimed becomes Unknown because a crashed worker might already have sent. Unknown is never selected for automatic retry.
2. When current activation is ON, select at most20 accepted recipients with original admission in the current epoch, not before acceptance/not expired, and no preparation. Order by original acceptance then recipient ID. `PrepareAsync` independently rechecks everything in its own serialized transaction; scanning is not authorization. Disabled/old/expired/unadmitted requests are not discovered for retrospective delivery.
3. Process at most20 due, unexpired Pending deliveries. Dispatcher independently claims and rechecks current authority/registration/binding/lease/deadline immediately before I/O. Processed counters count calls, not provider acceptance or handset receipt.

No cursor or in-memory ownership is needed: durable preparation and terminal/fenced states provide progress and concurrency safety. One bad item increments FailedItems while independent items continue; cancellation propagates. Transport unavailability does not increment attempts and housekeeping still progresses. A later valid device/join cannot revive a recorded empty/suppressed preparation.

The worker definition creates a scope every3 seconds, logs bounded failure counts/exception type only and stops cleanly on cancellation. It is deliberately not AddHostedService'd and will not run in the application until explicitly integrated. The dedicated test explicitly composes it with an in-memory fake transport; that is not production registration or operational enablement.

## Verification

`dotnet run --project tests/Sem.ManualNotificationPushEngineChecks -c Release` performs29 synthetic SQLite checks: default OFF/no-backfill,25-recipient/20-item progress, no duplicate idle pass, absent-transport staging with zero attempts,20+5 expiry cleanup, late expiry exclusion, empty-device nonrevival, one-recipient fault isolation/recovery, crash lease Unknown, current-registration rejection, rejoin nonrevival, overlapping full batches with one I/O/device, actual test-composed BackgroundService execution/stop, cancellation, retained inboxes, automatic/legacy isolation, model parity/integrity/foreign keys.

All39 .NET regressions, exact-source CI and separate isolated virtual-passkey/Blazor compatibility acceptance are distinct from real provider/physical-device testing. Existing Service Worker and inbox authentication behavior are retained, not newly certified on real devices.

## Remaining integration

Explicit service registration and operational activation UI/HTTP (current-cookie actor match, independent global-admin checks, Origin/CSRF/no-store, confirmed expected epoch and unknown request recovery) remain. Only then can a separately approved deployment/enablement and physical-device reception be accepted. Runtime/image security verification is separate from the included PKCS package pin. No DXP/data/keys/public-route/distribution/PR merge occurs here.
