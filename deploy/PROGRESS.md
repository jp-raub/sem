# Live progress: records, estimates and audience separation

English is authoritative; PROGRESS.ja.md accompanies this change. This incremental implementation follows approved v20–v26 state/chain/precision/audience rules. It does not finish buffer proposals, business notification linkage, reception or the fully styled v33 event workspace. No DXP operation, image delivery or PR merge is included.

## Storage and original-plan protection

AddEventProgress adds four tables over the populated timetable schema, without inventing actuals or copying account/answer contents:
- EventProgressStates: event PK, optimistic live RowVersion, SourceVersion, immutable PlanJson and CreatedAt. The original baseline is captured by the first successful live write (record or estimate), **never by GET**. It includes ordered opening/song/transition rows, rounding, default prep and original nullable prep inputs.
- ProgressMarks: event/part/point unique key, UTC millisecond At, real actor FK and same-event operation FK. Opening is a derived snapshot identity rather than a TimeParts FK. Part and point legality are validated against that immutable snapshot.
- ProgressEstimates: event/part PK and nullable preparation/transition seconds, nonnegative constraints. Presence of a song estimate with prep=null means current captured event-default inheritance; absence means the original song's prep. Null and zero remain distinct in the editor.
- ProgressOperations: UUID PK, event/actor/kind/time, request fingerprint and sanitized saved result. A record may own multiple adjacent marks. Fresh authorization happens before replay; identical requests return the saved result without new marks/audit/version increments. Reusing an ID for a different request/actor/event is rejected.

The structural event version protects fresh consent and the live version serializes record/estimate writes. Live writes do not increment the event structure version or registration participant/slot versions. Once the baseline exists, **normal song/timetable plan saves and event start/opening changes are blocked** to prevent a parallel original-plan edit from invalidating snapshot identities. Other event metadata/phase/manager edits remain possible and require refreshed structural consent. Change durations through live estimates instead. Fine-grained normal-plan amendments and live-plan resets are not exposed in this milestone; do not delete snapshots to work around the guard.

## State-dependent atomic record

Read-only state never advances because a planned time elapsed. The manager sees one next action: first preparation/start, performance start, or end. Song preparation and performance remain explicit v20 record steps, including zero-configured prep; no clock-based fake start is introduced. Opening/transition has only start/end, never a prep point.

POST `/api/core/progress-record?id=<event>` accepts requestId, current event/live versions, partId and point. It does **not** accept a timestamp, preview clock or arbitrary actual values. Server TimeProvider gives UTC time; normal production clock is not accelerated. All writes are limited to the Japanese calendar date of the event.

Ending a row writes its End plus the next Song.PrepStart or non-song Start in one transaction at the **same exact unrounded millisecond timestamp**. The final End does not create a next row. Wrong/stale next action, previous later actual, existing conflicting next marker, duplicate point and rapid fresh record (<650ms) reject without partial changes. Retry of the same completed operation is safe. Unknown input fields/Origin/CSRF/body limits retain the core API guards.

## Forecasts and estimates

Latest estimates walk the immutable original mixed order. Actual prep/start/end anchors are preserved exactly; only *unrecorded* song prep/performance starts are minute-ceiled when enabled. Ends and transition boundaries remain unrounded. Performance length always stays original and cannot be edited or shortened. Song prep may be changed until performance starts; transition duration may be changed until it ends. Opening and completed rows cannot be edited. Blank prep inherits the captured event default rather than storing a resolved value; explicit 0:00 is zero. Original and actual data are never overwritten.

Current health compares actual current anchors and the currently overdue original boundary against the original plan, not a future shortened end. Participant remaining time shows seconds below one minute, otherwise ceiling minutes; overdue time shows seconds or minutes/seconds. Planned times do not switch state. Date arithmetic overflow is a validation error.

GET `/api/core/progress?id=<event>` reads a coherent snapshot under a transaction and checks current DB identity/EmailConfirmed/Enabled/SecurityStamp/session revocation, event management and effective phase visibility. Hidden remains manager-only. Ordinary members get current state/forecast, next part and their own incomplete booked songs; manager comparison rows and next write action are not returned. No private answers, email or credentials enter the progress DTO.

POST `/api/core/progress-estimate?id=<event>` has the same fresh manager/day/version guards. Song duration is forbidden, transition preparation is forbidden, and completed/opening/foreign rows are rejected. A successful change updates only latest estimates and its audit operation.

## Actual Blazor screen

`/events/{id}/progress` shows a simple participant view, 1s local countdown and 15s authorized refresh. Manager controls: participant/manager preview, a large state-dependent single record button, row-grouped original/latest/actual comparison, and staged per-row estimate modal with save/cancel/navigation protection. While a local estimate draft is open, recording and automatic reload are disabled; cross-screen writes are protected by versions. PC/tablet default to visible comparison, phones show it on request. Lost authorization clears details and controls.

Read-only future/past plan preview is possible; server writes are event-day-only. Live snapshot creation is explicit through successful manager action, not reading a page.

## Verification / remaining scope

Sem.ProgressChecks starts at the populated timetable migration; checks retention/read-only GET, Japanese day, server milliseconds, stale/rapid/replayed/reversed/wrong-stage records, atomic boundaries, final end, blank/zero estimates, protected performance/original, current delay/countdown, authorization/stamps/revocation, concurrent commits, same-event operation FK, restart and SQLite integrity/model agreement. All previous regression suites remain required.

`progress-checks.cjs` runs after the existing native virtual-passkey/isolated real SMTP/actual Blazor modules. It uses the real current day, no browser clock override; covers public/private separation, anti-forgery fields, actual manager draft/cancel/estimate and single-button chain, exact adjacent actuals, original preservation, participant pending songs and restart. `.github/workflows/event-progress.yml` verifies both branch and stacked PR using Docker.

Remaining: pre-plan shortening permissions/minima and single stale-safe approval proposals; event notifications on prior-part starts and rescheduling/dedup; reception/QR; import; comment/aggregate/profile completion; full styled tab/live transport integration; DXP/physical passkey/Push acceptance. No new user decision is currently needed to continue those approved requirements.
