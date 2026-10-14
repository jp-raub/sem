# Event-local part definitions and service presets

English source of truth. This is a staged business feature, not a DXP image delivery.

## Data and management

`PartPresets` is a service-admin-only ordered list. Initial defaults are Vo/Cho (Sing), Gt/Ba/Dr/Key (Play), matching the approved mock. `PartPresetStates` provides a singleton concurrency version independent of display-name settings. `EventPartDefinitions` contains event-local identities, values, ordering and optional source-preset provenance; provenance is not a live relationship or cascading FK.

The additive `AddPartDefinitions` migration seeds the six defaults and creates separate snapshots for all pre-existing formal events. The previous formal schema contains no Songs/SongParts, so there are no formal assignments to infer or erase. It leaves accounts, passkeys, invitation/notification data, formal events, managers, phases and event versions unchanged. This migration does not import/reset obsolete test Events. Existing event IDs and copied definition IDs survive restart.

New events copy the currently committed preset **inside the event-creation transaction**. Empty presets are allowed, producing empty event definitions. Later preset edits never change existing events; event edits never change the preset. Names are trimmed, 1–40 characters, reject controls and `<>&"'|`, and are unique ignoring invariant case within the catalogue/event. Categories are Sing or Play. API body limit is the existing 64 KiB; lists are defensively bounded to 100 definitions. Identifiers from other events/presets, duplicate IDs and deleted identities cannot be reused. Removal retains a tombstone, and rename/reorder retains identity. Atomic name swaps are supported under DB unique indexes.

`/service/part-presets` and `/events/{id}/definitions` are actual Blazor Server management pages with name-click editing panels, add/edit/delete, keyboard-usable up/down ordering, final save and cancellation. `/api/core/definitions` GET/POST uses absent `id` for the service preset and an event `id` for the event-local plan. Do not send `sourcePresetId` in an edit: it is server-controlled provenance. Write DTOs reject unknown properties. Existing Origin/CSRF/current session/role/revocation guards apply. Event saves share `SessionEvents.RowVersion` with phases/overview, while preset saves use their own version. Conflicts preserve the client draft for review rather than silently overwriting newer data.

## Verification / boundaries

The new tests exercise populated-phase-schema adoption, independent identities/copies, names/categories, role isolation, stale and concurrent edits, ID forgery/tombstones, atomic swaps, shared event versions, revoked sessions and restart. Storage adoption compares every **pre-existing** business row and separately verifies the explicit newly seeded rows; adding a new catalogue is not mistaken for loss or mutation of old data. Existing phase/core/auth/storage/reset/offline-copy checks remain enabled.

Native virtual-passkey/isolated-SMTP HTTPS acceptance additionally exercises populated adoption, guarded API saves, copy-at-creation, existing snapshot isolation, real Blazor modal save/cancel, route scopes and restart. CI retains the prior Docker asset/regression safeguards; results must be read before declaring CI success.

Not complete here: editing preset selections directly in the initial new-event modal (all current presets copy first; adjust afterwards in event definition management), drag ordering, full v33 styling/focus-trapped modal polish, questions/answers, Songs/SongParts, signup, capacity and business notifications. **Before introducing SongParts, add FK-backed checks refusing deletion/category changes of referenced definitions while allowing same-ID renames.** No pretend usage counter exists in this pre-song unit; do not claim assignment preservation has been tested against a model that does not yet exist.

No DXP, `.env`, keys, public RP, proxy/network, original volume, image delivery or PR merge is changed. **Continuation:** under existing authorization, finish verification and proceed to questions / song-model reference protection without user confirmation; surface new specification contradictions or destructive-operation decisions explicitly.
