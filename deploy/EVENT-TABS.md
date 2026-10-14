# Authorized event tab routing

English is canonical; the Japanese file is paired. This is isolated development, not a DXP upgrade or a delivery.

## Scope

The five approved labels are Event, Participant roster, Check-in, Day progress and Notifications. The event overview is `/events/{id}`. Existing independently authorized route components are reused instead of embedding all panes and reinitializing mutation state on a parent render. Current session and effective event visibility are checked on initial read and immediately before a requested switch. Destinations independently authorize all data and writes. Off-day standalone progress preview remains available, but it is not a restorable event tab. Check-in and the current notification-plan surface require current global or assigned management; progress tab availability uses the Japanese event calendar day.

The notification tab currently opens the existing notification planning surface; it does NOT implement the mock's manual participant-message composer or claim the entire application is complete. Participation, timetable, own QR, comments, public answers and own start-notification settings remain explicit links in the main overview.

## Selection and accessibility

Only a permitted tab identifier is stored in `sessionStorage`, under an opaque current-user/current-event scoped key. No account credentials, event payloads, QR tokens, drafts or pending operation IDs are saved to browser storage. Different users and events are isolated; unavailable or malformed state falls back to main. Entry through plain overview restores saved selection. Explicit main selection uses `?tab=main` to avoid restore loops; direct subroute links are honored. Storage denial leaves manual controls usable. Requested selection is NOT saved until the destination mounts, so blocked/failed switches leave the current stored selection unchanged.

Tabs use a horizontal scroll strip, 44px minimum controls, roving tab stops, selected styling and tab/panel accessibility labels. Left/Right wrap and Home/End request the adjacent permitted route. Native route handling runs existing Blazor navigation locks before disposal; unauthorized panes are not eagerly instantiated by the host.

## Pending operations and drafts

Manual receipt navigation is blocked during work, uncertain completion/cancellation IDs, a locked QR camera/workflow, unsent note, confirmation or completion checkbox. QR navigation blocks active camera, work, pending read/receipt IDs and unsent input/note/confirmation. Explicit discard clears only unsent inputs, not persisted receipt/read history, and is disabled during work or pending IDs. Progress navigation blocks current work and existing estimate/shortening drafts. Requests are not automatically resubmitted or regenerated on a tab switch. Existing business APIs, CSRF and operation semantics are unchanged; no schema migration.

## Verification boundaries

Dedicated current-authority/visibility/Japan-midnight checks:17. Synthetic-browser state/keyboard/lifecycle checks:17; pure selection module:26. All30 .NET check suites passed in the isolated worktree. Release build:0 warnings/errors. New native virtual-passkey/HTTPS/Blazor tests are in progress: real route switches, reload/return restore, per-event scope, on/off-day tab sets, QR and manual unsent-draft blocking/discard, literal title escaping and mobile/desktop screenshots. Do not call the full host acceptance complete before those tests and full CI succeed. Docker/new delivery/DXP/PR merge remain separate.
