# Event notification schedule preview

English reference; [Japanese translation](NOTIFICATION-PLAN.ja.md).

## Scope and approved bases

This increment implements **read-only schedule calculation from formal event/timetable/live progress data**. It does not create a Push rule, recipient/default list, enabled setting, durable reservation, delivery, subscription or public send action. PR18's automatic daily email remains unchanged. Notification scheduling is not completed merely because this preview exists.

Only already-approved bases are used:

1. Opening reminder: opening time if present, otherwise event start, minus **120 minutes**.
2. Song preparation: the **immediately preceding timetable part's actual Start**, not its planned/predicted start and not a preceding song's PrepStart. Transition and generated Opening rows are timetable parts and are not skipped.
3. With **no preceding timetable part**, the latest predicted song PrepStart minus **10 minutes**, not performance Start minus ten.

A first song after an Opening row therefore waits for that Opening's Start actual. This is distinct from a song that is the very first timetable row. Missing preceding actual is `WaitingActual`, with null reference/due; no forecast is substituted. A due time can be in the past without being sent: `Due` is eligibility information only. `Planned` is before due, `Obsolete` means the target has already started (song PrepStart/Start/End, or event opening/first part started). Obsolete entries retain their basis for diagnostics, but are not live delivery instructions. These states are inputs for future reconciliation, not persisted cancellation or proof of send/receipt.

All calculation uses absolute instants with millisecond precision. UI explicitly displays Asia/Tokyo date/time and +09:00. Minute-display preferences do not round scheduling timestamps. Existing timetable rounding, inherited/explicit-zero preparation, order, live estimates and captured original progress remain authoritative. No performance shortening is introduced.

## Read consistency, recomputation and fingerprint

`CoreFoundationService.ReadNotificationPlanAsync` revalidates ordinary authenticated enabled/verified identity, security stamp/session and **current global or assigned-event management permission** inside a coherent read transaction. Ordinary participants cannot inspect this manager plan, even if they can view the event. Missing/unassigned hidden events are denied without data disclosure.

Before live progress begins, rows come from the current formal timetable. After capture, the original protected progress baseline plus current actuals/estimates supply the latest rows. The preview does not rewrite that baseline. A fresh read after saved event/order/estimate/progress changes recomputes entries and source identity. Unsaved UI drafts are deliberately not used. The read-only screen refreshes manually; it is not an automatically updating SignalR subscription and must not be mistaken for a background reconciliation worker.

A deterministic SHA-256 fingerprint includes event and progress versions and scheduling entries (basis, predecessor, exact reference/due, obsolete flag). Merely crossing due time changes display eligibility but not this source fingerprint. Stable logical IDs (`opening`, `song:<partId>`) identify future replacement/cancellation candidates. **No existing reservation is changed or cancelled by reading this API.** Future persistence must atomically reconcile changed fingerprints, revalidate rules/recipients/permissions immediately before dispatch, and implement per-device delivery/TTL/invalidation independently.

No recipient email, questionnaire answer, subscription endpoint/key, protected payload or secret is returned. Invalid dates/overflow, duplicate source parts or incomplete song timing refuse guessed schedules. Ordinary JSON/string fields are escaped by Blazor. No new schema or migration is required.

## Surfaces

- GET `/api/core/notification-plan?id=<eventId>`, no-store. POST is 405 after existing CSRF protection; there is no queue/send endpoint.
- InteractiveServer `/events/{EventId}/notification-plan`, linked only from manager timetable controls.
- Read-only refresh rechecks current permissions and clears the plan on failure. Direct member navigation returns a permission message without schedule data.
- Policy banner labels this as unreserved/unsent and explicitly describes Opening as a possible preceding timetable row. Mobile entries use normal wrapping text.

## Acceptance and deployment boundary

`Sem.NotificationPlanChecks` verifies exact 120/10-minute bases and milliseconds, opening precedence, preceding transition/performance/opening actuals, missing-actual waiting, exact due boundary, obsolete target, invalid clock/overflow/duplicates, input immutability, deterministic fingerprints, saved time/order/live changes, no writes, auth/role/revoked-session denial and unchanged EF/SQLite/FK. `notification-plan-checks.cjs` verifies actual authenticated API/no-POST, real Blazor refresh after event change, mobile waiting state, hidden member denial, navigation, private-data exclusion, no related SMTP and no runtime errors. Full prior daily-SMTP/business/native-passkey/Blazor/upgrade/restart regression and actual Docker workflows must pass as well.

No DXP change, `.env`/key/volume/hostname operation, delivery archive or merge. Keep PR18 verified source and current DXP intact. Next work is durable reservation/reconciliation/cancellation followed by actual event Push integration, without silently selecting unapproved target lists, defaults, TTL or rule candidates. Push request acceptance must remain distinct from device receipt.
