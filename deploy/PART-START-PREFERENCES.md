# Personal time-part start notification preferences

English reference; [Japanese translation](PART-START-PREFERENCES.ja.md).

## This increment

A signed-in user can opt in independently to **any currently viewable time part**, not only songs they perform: song, transition and the derived opening row. The personal setting is initially OFF and is independent of the automatic opening/song-preparation recipient groups. The timetable links to `/events/{EventId}/part-start-preferences`; each choice is an accessible `role=switch` with `aria-checked` and immediate save. The setting belongs to the authenticated actor; even a global manager only edits their own choices here.

**Actual Start delivery is now connected** by the later [personal delivery increment](PART-START-DELIVERY.md). `DeliveryConnected=true`; missing host VAPID configuration is separately reported. Saving settings alone still does not request/override browser permission, create subscriptions or send a notification. Delivery follows newly recorded actual Start, never forecast/preparation or an implicit historical replay.

## Persistence and safety

Migration `20261013130605_AddPartStartPreferences` adds event/user/part-keyed preferences with restrictive event/user foreign keys, boolean/version/part-ID checks and optimistic row-version concurrency. Opening is derived, and part membership is validated against the current timetable rather than inventing a mutable persisted opening part. Current timetable rows alone are returned; deleted parts are omitted even if historical opt-in is ON. This does not restore removed parts or bypass hidden phases.

- No-store GET `/api/core/part-start-preferences?id=<eventId>` reads only the current actor's settings, with OFF/version0 for absent rows. Reads write nothing.
- Existing authenticated Origin/CSRF-protected POST takes `partId`, `enabled`, `version`, `eventVersion`; all fields are required and unknown fields (including a subject user ID) are rejected.
- Current user activation, email confirmation, security stamp/session revocation and effective event visibility are checked for each operation. Event and preference versions reject stale tabs, changed plans and deleted parts. No subscription/address/other-user details are returned.
- Identical state is a no-op; a material change and its audit commit atomically. OFF rows retain their generation to prevent absent-row ABA overwrites. Unique-insert/lock races conflict rather than silently losing another update. Failed saves do not show optimistic success; unauthorized/read failures clear the screen.
- New contexts/restarts retain selections. Saving does not change event/progress versions, performance assignments, protected baselines or transport queues.

## Approved delivery policy / next increment

The user approved automatic opening recipients = formal performers + spectators, song preparation recipients = that song's performers, manager-created rules initially disabled, opening expiry at opening (or event start), and preparation expiry at the earlier of due+10min and latest preparation start, cancelling after actual preparation begins. These are adopted design decisions, **not activated by this settings-only PR**. Optional personal part-start recipients form a separate opt-in group. Sender-side authorization/current opt-in/current valid devices, privacy-minimized content, expiry, idempotent durable dispatch and logout/recovery invalidation still require the delivery increment. Submission is not proof of device receipt. No DXP update, .env/key/volume change, delivery bundle or merge is performed.
