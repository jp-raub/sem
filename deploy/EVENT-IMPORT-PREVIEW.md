# CSV import eligibility and read-only preview

## Boundary

This stage adds a strict UTF-8 CSV parser, durable pre-registration history gates, and an authenticated Core-only validation preview. It does **not** add a write/apply operation, HTTP route, browser UI, external spreadsheet fetch, deployment, or production activation. A valid preview is not permission to mutate data and its comparison fingerprint is not approval, an operation identity, or a replay guarantee.

The source is the approved UI mock at `9cf8b031258fa1c9afe9668f54e61f002c4bce17` (`mock/enhancements.js`, `mock/domain.js`, `mock/sample-import.csv`). Blank preparation inherits the event default; explicit `0:00` means no preparation.

## Durable history and access

Every preview independently validates the current authenticated account, security stamp and non-revoked session, current event access, and current global/event-manager permission in a read transaction. Effective phase must be Hidden. An event created through the new server creation path has known initial history. Pre-existing rows migrate with unknown history and are refused even if currently Hidden and apparently empty. No administrative override or client-editable history fields are provided.

Observed Custom registration-phase entry is irreversible for import eligibility. Phase/event changes observe the old state before overwriting it. Potentially missed past automatic windows are treated conservatively as uncertainty, not asserted as proven entry; removing or rescheduling that evidence must not restore eligibility. Current registrations, registration operations and reception records independently refuse import, but their absence is never used as proof of no history. Progress-started plans retain the existing immutability guard.

## CSV contract

- At most 1,048,576 UTF-8 bytes, including BOM. Invalid UTF-8 and invalid UTF-16 input strings are rejected.
- Required headers: `Kind`, `Title`, `Duration`. Optional: `Artist`, `Prep`, `Parts`. Headers are case-insensitive and unique; unknown headers are rejected except recognized assignment-like headers.
- Exact row kinds: `Song` and `Transition`. Durations use `m:ss` or `h:mm:ss` with bounded subordinate minute/second segments and Int32 total seconds. Song performance is at least one second; transitions and preparation allow zero.
- Titles/artists are trimmed and follow the existing Core 200 UTF-16-code-unit limit and control-character refusal. Song parts are pipe-separated, nonempty, case-insensitively unique and must match active event-local definitions. Transition rows need no parts.
- Structurally valid rows with valid Kind, Title, Artist and Duration and nonempty assignment-like columns are skipped before optional preparation/parts validation; assignment values are never returned. Invalid optional Artist text is still a blocking input error, not waived by assignment presence. No imported registration is created.
- Existing active song `(title, artist)` and transition-name keys, and earlier valid CSV keys, are skipped. Existing rows are not updated or resurrected. Comparison is case-sensitive for song/transition keys; part-name resolution is case-insensitive.
- Semantic row errors invalidate the prospective batch; syntax/header/encoding errors reject the input. Limits include existing active rows: 300 songs, 300 transitions, 600 time parts. There is no unrelated small raw CSV row limit. Ambiguous active definition names or an inconsistent existing song/time-part mapping are refused, not inferred or repaired.

## Preview semantics

`ReadEventImportPreviewAsync` returns per-row dispositions (`Ready`, `DuplicateSkipped`, `AssignedSkipped`, `Invalid`), normalized source fields, aggregate counts, validation errors, event version and a SHA-256 comparison. When valid it calculates the prospective append-only mixed timetable using local synthetic keys. It reports proposed end and overrun; it does not approve overrun or store those keys.

The comparison binds the actor, event, version, sanitized parsed input (assignment presence only, never its value), relevant event timing settings, active definitions and active plan. It is a change detector only. Preview performs no saves, audits, registration writes, imports, notification sends or history-marker repairs. A later apply stage must revalidate all gates and current plan in an atomic transaction and define explicit confirmation, stale-preview protection and unknown-result replay before enabling mutation.

## Verification and remaining work

Dedicated parser, durable eligibility and Core-preview checks use synthetic SQLite only. Existing .NET regressions and the Release model/migration check are separate evidence. There is no claim of browser, external CSV, actual write/apply, SMTP/Push, Docker/DXP or production acceptance at this stage.

Remaining: atomic append/apply with confirmation and replay handling; authenticated CSRF-protected route and UI; cancellation/stale-response/navigation protection; real browser acceptance; separately scoped public spreadsheet retrieval with redirect/SSRF/size/timeout controls; separately approved integration/deployment. Existing persisted Push ON state is not silently turned OFF by this work; no production state is inspected or changed.

## Next-stage implementation plan (not implemented)

1. Extract preview construction into a transaction-local private function so an apply request can recompute the same sanitized comparison against the very same database snapshot; do not invoke the public preview through a second context/transaction.
2. Require a canonical lowercase D-format operation GUID, explicit confirmation, event version, exact preview comparison, normalized source input, and explicit acknowledgment when the prospective plan exceeds its planned end. None of these substitutes for current authorization.
3. Validate current actor/session/event-manager access before looking up an existing import operation. A matching saved operation is returned only to that author for that event and the identical normalized request; it does not rerun import. Another actor, event or request cannot reuse the identity. Rechecking a completed operation must not be misrepresented as a new import after the event phase changes.
4. For a new operation, recheck Hidden/history/contrary business data/progress protection, active event-local definitions, existing keys, limits, event version, comparison and prospective timetable inside the write transaction. Generate new row IDs server-side. Append mixed time parts in CSV order after the existing plan; create songs and unassigned definition-linked song parts only for Ready rows. Do not update existing rows, baseline, registration, answers, receptions or notification records.
5. Atomically store appended rows, one event-version advance, one import receipt with original counts/time and one audit. A uniqueness constraint on the durable operation identity must fence concurrent retries; any failure rolls back everything. An uncertain response may be checked using exactly the same operation/request, never silently assigned a new ID.
6. Test stale versions/definitions/phase/permission, concurrent identical and conflicting identities, process restart replay, fault-injected audit failure, mixed ordering, no overwrites, null-versus-zero prep, overrun acknowledgment and whole-database integrity. Add route/UI only after these invariants pass, with explicit preview/confirmation, no-store/CSRF, memory-only draft/operation state and stale-response/navigation protection.

This is an implementation plan, not a claim that atomic apply, receipt storage or its UI already exists.
