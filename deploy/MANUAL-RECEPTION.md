# Manual reception confirmation and cancellation

This increment is stacked on the manager roster/search and basic profile view. It adds **manual receipt writes**, not event QR issuance, scanning or scan records. Reading a roster/target never completes reception. Anonymous profile QR is also separate.

## Current manager and participant gates

`GET /api/core/reception-target?id=EVENT&user=USER` returns the current registered person's display name/reading, unique song titles, spectator flag, event/registration/reception versions, current receipt and latest 100 receipt records. Only a current global or assigned event manager may read this information. Current enabled/confirmed actor, security stamp and revoked-session checks apply on every read/write/replay. Forged role claims do not grant access. The projection excludes email, questionnaire answers, social/authentication details. Responses are no-store; route/reload/access errors clear prior target data. Older history overflow is explicitly reported, not hidden as a complete result.

New completion requires current performance or spectator registration and an enabled, confirmed target. It checks displayed event, per-user registration and reception versions in one transaction. The server does not require a QR or invent an event-day cutoff. Browser edit drafts are not persisted; the reception confirmation panel is separate from event/registration editing. Authentication recovery mode and its eventual UI need a separate acceptance pass when implemented.

## Explicit confirmation and persistence

`POST /api/core/manual-reception?id=EVENT` accepts only `ManualReceptionEdit`: requestId, userId, eventVersion, participantVersion, receptionVersion, receptionDone and note. `receptionDone=true` certifies the administrator has checked identity/application and required reception actions. Note is optional, trimmed, at most 500 characters. The server records its own UTC timestamp, actor and method `Manual`; client-supplied times/actors/methods are rejected as unknown fields. The actual Blazor panel shows name/reading/application before a checkbox and explicit save button. Notes/names use ordinary Razor encoding, never raw HTML.

`POST /api/core/reception-cancel?id=EVENT` binds requestId, userId, receptionId, receptionVersion and confirmed=true. Cancellation marks the original receipt with timestamp/actor; it does not delete the history. Backing out of the confirmation makes no write. A new explicit confirmation after cancellation creates a new receipt, leaving the previous one intact.

Migration `20261013172337_AddManualReceptions` adds EventReceptions, EventReceptionStates and ReceptionOperations only. Existing users/keys/events/registrations/answers/comments/notifications and migration history remain unchanged. Restrictive foreign keys keep historical actors/subjects; a filtered unique index permits at most one uncancelled receipt per event/user. Per-user receipt state has an optimistic concurrency token. SQLite transactions and common busy/concurrency conflict handling prevent silent overwrites. Do not delete live data or call down -v to test it.

## Result uncertainty and exact retries

The operation ledger binds request fingerprint, actor, event, kind and saved receipt ID. An identical authorized retry returns the original result without another receipt, audit or cancellation. Changed payload/actor/event/kind cannot reuse an ID. A replay of completion after cancellation **does not resurrect** it; a replay of an old cancellation never cancels a newer receipt. Historical retries still require the actor's current event management/session, but do not execute new participant writes.

The UI captures the request ID and full payload before sending and disables competing edits/selections while outcome is unknown. It offers only explicit same-ID retry, never automatic submission. A historical success means that operation committed, not that its receipt is still current; the UI re-reads current state. Validation/conflict clears the confirmation and asks for current selection; access failure removes sensitive data.

## Boundaries and acceptance

Dedicated checks cover permissions, current registrations, spectator/performer, required confirmation, note bounds, version conflicts, duplicate active receipt, exact retries, cancellation retention, cancellation/recompletion ordering, current revocation, restart and unchanged unrelated business data. Native acceptance covers mobile actual Blazor actions, API permissions/confirmation, retained history, encoded notes and historical retry semantics. Full existing regressions and fixed-source CI must be separately confirmed before marking review-ready.

Remaining: user×event QR generation/reissue/current-token validation, valid scan records and complete history, scan-linked receipt confirmation, camera/input UX, global reception summary/history, final integrated v33 UI, recovery/edit-mode integration, live DXP/network/physical-device acceptance and delivery. No deployment, merge, new bundle or live data change is performed by this increment.
