# Mixed timetable: staged implementation

This adds the approved v33 **planned** timetable, not live progress, actual timestamps, buffer-shortening approvals, reception, CSV import or business notification scheduling. English is authoritative; TIMETABLE.ja.md accompanies it. No DXP change, image distribution or PR merge is implied.

## Persistence and compatibility

`AddTimetable` is an additive migration over the populated participation schema. `TimeParts` reference real events and, for songs, the same-event `(EventId, SongId)` key with restrictive deletion. One time part per song is unique. Existing song IDs/order/tombstones seed stable Song time parts without changing registrations, originating phases, participant versions, question answers, spectators or event versions. Transition names are 1–200 trimmed characters and their durations are nonnegative integer seconds. Kind/song reference cannot change and tombstone IDs cannot resurrect.

Event defaults: `RoundStarts=true`, `ClockFormat=seconds`, `DurationFormat=units`, `SongIcon=🎵`, `PlannedEndAt=null`. Explicit false survives the true database default. Song overrides allow 🎵/💿/🎤 or null inheritance. No `ManualStartAt`, integer-minute conversion, wall-clock acceleration or invented actual data is introduced.

The existing song editor continues to own titles, artists, durations, prep inheritance and part toggles. It synchronizes the corresponding time parts, appends new songs, and tombstones unused deleted song rows. In events with transitions it preserves existing mixed positions and rejects reordering the retained song subsequence; use the timetable editor for mixed order. Registered song/slot deletion remains protected by the previous checks. Mixed reorder updates only the Song.SortOrder subsequence for registration display, never assignments.

## Calculation

- Required event start is the first preparation/transition baseline.
- Optional opening produces a **derived fixed** Opening row: opening to main event start. No opening means no row. Its order/duration/title cannot be posted as ordinary parts. Opening is managed in the event overview.
- Walk mixed parts in stable order. With rounding enabled, ceiling **song prep start** to a minute, add configured prep, then separately ceiling **performance start** to a minute. Do not round performance end or either transition boundary.
- With rounding disabled all seconds remain exact. Null song prep follows the current event default; explicit zero is no prep. Performance ≥1 second; transition ≥0 seconds. Zero-duration transitions remain real rows.
- Keep UTC instants in storage and Japanese calendar dates in display. Midnight and month/year changes are not wrapped to ambiguous times. Minute-only display never changes calculation precision. Date arithmetic overflow is a validation error, not wrapped data.
- An optional absolute planned end must be no earlier than main start. Server returns exact overrun seconds. Timetable save requires explicit acknowledgement if overrun >0; stale event version still rejects acknowledgement. Other screens' song/event edits remain separate transactions; their durations/start changes recalculate the next timetable read. Full unified overview/song draft with cross-screen warning is a later UI integration, not completed here.

## Authorization and real UI

GET/POST `/api/core/timetable?id=<event>` uses live DB identity, security stamp/session revocation, current event management and effective phase visibility. Hidden events are manager-only; visible events are authenticated-member-readable. Ordinary members cannot save. No answer values, account/email secrets or assignment mutation fields enter this DTO. POST keeps existing strict Origin/CSRF/body-size/unknown-field checks; plannedEndAt must include a timezone.

Save compares the event RowVersion inside a transaction and requires every active song exactly once. Missing songs, duplicate IDs, event moves, kind/curve reference changes, forged opening, negative seconds and deleted-ID reuse reject atomically. Transition removal is a tombstone. Existing restrictive same-event FKs protect storage integrity.

`/events/{id}/timetable` provides authenticated read-only calculated rows and manager staging: transition add/edit/delete in modal, per-song icon, drag or up/down mixed order, display settings, planned-end input, draft preview and overrun acknowledgement, save/cancel and navigation protection. Opening has no management controls. Japanese date input is yyyy/MM/dd HH:mm; unchanged existing planned-end seconds are preserved. Changing forecast settings/order resets acknowledgement. The screen does not yet consolidate song-content editing and registration into the full styled v33 event tab.

## Verification

`tests/Sem.TimetableChecks` starts at the populated participation migration, upgrades and checks retained real references, opening/rounding/inheritance/zero transitions/day rollover/overflow, settings persistence, mixed ordering/song-sync, authorization, stale/concurrent saves, tombstones, foreign IDs, private-answer retention, restart and SQLite integrity/FKs. `scripts/timetable-checks.cjs` runs with native virtual passkeys and isolated real SMTP after all previous modules, tests real HTTP plus actual Blazor staging/cancel/warnings/reordering/restart. These are synthetic server/browser checks, not DXP or physical passkey/Push acceptance.

Run:

```bash
dotnet run --project tests/Sem.TimetableChecks -c Release
# Native harness requires the existing synthetic HTTPS, SMTP, upgrade/restart fixtures.
SEM_TIMETABLE_CHECKS=1 SEM_PARTICIPATION_CHECKS=1 SEM_SONG_CHECKS=1 \
SEM_QUESTION_CHECKS=1 SEM_DEFINITION_CHECKS=1 SEM_PHASE_CHECKS=1 \
SEM_CORE_FOUNDATION_CHECKS=1 SEM_PASSKEY_ADDITION_CHECKS=1 node scripts/e2e.cjs
```

Use `.github/workflows/event-timetable.yml` for both branch and stacked PR checks. No database downgrade as rollback: retain the original volume/image and switch the public endpoint only after user-operated backup/clone verification.
