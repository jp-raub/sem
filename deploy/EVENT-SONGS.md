# Event songs and slots

Incremental management unit, stacked on the verified event-questionnaire branch. This is not complete participant registration or timetable delivery.

## Data and preservation

The additive `AddSongsAndSlots` migration adds Songs/SongParts and event DefaultPrepSeconds (600 for existing and new formal events, following the refined v33 mock). Existing identities, native passkeys, protection keys, invitations, messages, notifications, phases, manager grants, definitions and question/answer rows remain intact. Composite principal keys on phases/definitions and songs keep every slot in its own event. SQLite may rebuild existing tables to add constraints; use the stopped-copy upgrade procedure and backup, not a destructive reset. Never delete the original data volume.

- Song: immutable identity/event, title and artist up to 200 characters, positive performance seconds, nullable preparation seconds, order and deletion tombstone. Null preparation inherits the event default; zero explicitly means no preparation. The event default accepts explicit zero.
- Each newly managed definition has one slot per song. At least one slot is required. Management preserves existing slot IDs and registrations when renaming or reordering.
- Slots have real restricted FKs to song, event definition, retained user and originating phase. User and phase must both be null or both present. Assignment storage is a foundation only; this unit exposes no registration/assignment/clear endpoint. Unit fixtures deliberately insert synthetic assignments, not successful participant signups.
- A used definition cannot be deleted or change Sing/Play category, even when its slots are unassigned. Rename on the same definition ID is allowed. Service presets stay independent of copied event definitions.
- An assigned slot or song cannot be removed. Legacy same-definition multiple assigned slots are rejected for editing rather than silently merged. Unassigned duplicates may be consolidated to one slot; a single assigned slot keeps its identity. Unused removed songs receive a tombstone, their unassigned slots are removed, and old song IDs cannot be reused.

## API and actual UI

Manager-only GET/POST `/api/core/songs?id=<eventId>` and actual Blazor `/events/<eventId>/songs`. Every operation rechecks current database identity, enabled/email/stamp/session and global/event-management rights. Origin, CSRF, 64 KiB payload bounds, unknown-field rejection, shared event RowVersion and atomic transactions are retained. Management DTOs cannot change assignments; plans show assignment presence but not assigned user identities. Technical guardrails are 300 songs / 100 definitions per draft, not a new product signup limit.

Title-click edit panel, add, stable-ID up/down ordering, draft apply, final save and cancel. Input uses m:ss / h:mm:ss and preserves seconds. Inherited prep disables specific prep input; explicit 0:00 is accepted. Modal disables background operations. Assigned controls are disabled in the UI but the server independently enforces protection. Error/competition preserves the draft; lost authentication clears private management state.

## Acceptance

Run `dotnet run --project tests/Sem.SongChecks -c Release` plus existing questionnaire/definition/phase/foundation/storage/proof/reset and offline-copy regressions. Song checks start from populated questionnaire schema and verify identity/reference preservation, 600-second initial value, null versus zero, immutable IDs, cross-event restrictions, referenced-definition/assigned-slot protections, duplicate legacy protection, concurrent/stale managers, revoked session, restart, model consistency and SQLite integrity.

Set `SEM_SONG_CHECKS=1` in the native-key/isolated-SMTP/actual-Blazor test. The event-songs workflow builds current/fixed-legacy/foundation containers, checks published Blazor assets and fresh/restart/rate-limit behavior, then upgrades populated data and runs all modules. Record actual results, not planned counts; local no-Docker results do not imply container CI or DXP acceptance.

## Still separate

TimeParts/transitions, start/end computation and rounding, icons, drag ordering, live shared updates, actual participant signup/release and phase-song MaxPicks, spectator and required answers, capacities, partial success, business notifications, reception and on-day progress. Management song order is a song-only sequence, not a complete mixed timetable. No image delivery, PR merge or DXP changes are included.
