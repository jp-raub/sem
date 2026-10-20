# Event participation, answers and spectators

Incremental actual-DB/Blazor unit, stacked on event-songs. No merge, distribution or DXP application is part of this change.

## Preservation and concurrent writes

`20261013003202_AddParticipation` adds nullable Capacity to formal events, RowVersion to song slots, EventParticipantStates, EventSpectators and RegistrationOperations. Upgrade from populated song schema retains event structural versions, slot identities/assignments/originating phases, questions/answers, managers, definitions and identities. Capacity starts unlimited; previous slots start version0. Existing migrations are unchanged. Use stopped DB+protection-key backup/copy acceptance, never original-volume deletion.

Event RowVersion represents structural consent (phase configuration, songs, questions, capacity). Booking does not increment it: another participant's booking must not invalidate every independent selected slot. Each slot has a version to prevent stale selection and release/reclaim ABA; each event/user participation state has a version to prevent stale answers or conflicting actions by the same subject. SQLite transactions serialize admission, unique-person capacity and phase counts. Current database authentication/session/rights and current effective phase are checked inside the transaction, not from an old page/role claim or a 15-second worker cache.

## Registration and partial success

Authenticated visible-event members can register only in Custom phases with MaxPicks !=0. One occupied slot cannot receive a second user. Unavailable, occupied and stale items return explicit failure codes while independent eligible slots commit. However, if the union of already registered songs in the current phase and the eligible batch exceeds MaxPicks, the whole request fails validation before any slot, answer or comment is persisted. There is no selection-order-based partial admission for phase quota overflow. See [the current contract](PARTICIPATION-CONDITIONS.ja.md). Normal compatibility is one slot or exactly one Sing plus one Play per song, including the user's already assigned slots from any phase. Incompatible combinations fail before writes. Per-phase MaxPicks counts DISTINCT songs, not slots; the same song's voice+instrument is one. Originating phases are retained, independent counts are used in a new phase, and -1 is unlimited.

Managers may register a named enabled/email-confirmed subject and authorize compatibility exceptions only with an explicit flag and nonblank reason (maximum500 characters). This does not bypass phase acceptance, phase song limit, capacity or required questions. Ordinary members cannot choose another subject or authorize exceptions. Self cancellation is NOT permitted for ordinary members: release is manager-only with reason, exact subject and current slot versions. A stale release changes nothing. User and phase are cleared together and slot versions increase; historical answers remain.

## Spectators, capacity and answers

Custom-phase nonperformers can spectate, including phases whose part MaxPicks is0. Performer and spectator identities form one unique-person set. Nullable capacity is unlimited;0 admits no new people. Existing people can add parts at full capacity. Managers cannot lower capacity below current participants or bypass it. Successful performance signup removes the subject's spectator row atomically without double counting. Spectator removal is manager-only with reason and retains historical answers.

Active question IDs, type, requiredness and 2000-character length are revalidated under structural consent. Required YesNo accepts both YES and NO; blank optional values are null. Unknown, stopped or deleted question IDs are rejected; stopped history is retained. Omitted active answers preserve prior values (especially delegated signup); explicit blank/null clears only optional answers. Required new questions still require a valid answer. Answers are stored only after at least one registration/spectator item succeeds; zero successful slots do not update answers or participant state. Answer-only updates require an existing performer/spectator and a current Custom phase and participant version.

The participant plan returns ONLY the authenticated user's answers. Manager subject plans contain no subject answer values; delegation cannot read private stored answers. This unit does not add public answer aggregates/respondent lists/text publication. Plans expose event-visible roster identities/display names, not email or account secrets.

## API, UI and audit

GET/POST `/api/core/participation?id=<event>`; optional GET `subject` is manager-only and still hides answers. POST `/api/core/capacity?id=<event>` is manager-only. Existing Origin/CSRF/64KiB bounds and nested unknown-field rejection apply. Operation kinds are Register, Spectate, Answers, Release and RemoveSpectator. Every mutation supplies a UUID RequestId and current event/phase/subject/slot versions. Retrying an identical authorized request returns its persisted result without another mutation/audit/state increment; reuse with different payload is rejected. Use a new request ID for a new action.

RegistrationOperations records actor, subject, event, effective phase, kind, reason, UTC timestamp, request fingerprint and sanitized item results. It contains no plaintext answers; values live only in EventQuestionAnswers. The ordinary audit points to the operation ID, never logs answer text. Current rights are rechecked even for a replay.

Actual `/events/<event>/participation` provides slot selection, required questions, one primary action for parts/spectator/answer update, per-item results, current quota/capacity and manager target/compatibility/reason/release/capacity/spectator controls. Generic transmission failure instructs users to reload/check results before resending. Lost authentication clears private state. Full v33 unified styling/draft admin layouts and live multi-browser updates remain separate.

## Verification and remaining work

Local participation65 checks cover populated-song upgrade, partial success, phases/-1, combinations, current identities/manager grants, private/delegated answer protection, omission/null/NO semantics, idempotency, stale subject/slot versions, capacity, spectator conversion/removal, concurrent claiming/answer edits, last-seat spectator capacity, stopped-question history/forged input rejection, changed security stamp, automatic phase expiry, restart, EF model and SQLite integrity/foreign keys. Run all previous regressions as well. SEM_PARTICIPATION_CHECKS=1 adds native-key, isolated-SMTP, actual member/manager Blazor and restart acceptance. CI builds legacy/foundation/current Docker images and runs all modules; record actual results separately, not predicted counts.

Still not implemented: business registration/release mail/Push and digest rules, public question aggregation, comments, complete roster/profile history UI, TimeParts/transitions/timetable/rounding, on-day progress, reception, PostgreSQL and full recovery/session reset. Real DXP/NPM/iOS/physical-key acceptance is separate from synthetic local/container tests.
