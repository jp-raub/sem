# Own-authored manual notification history (Core only)

`ReadEventManualNotificationHistoryAsync` is a read-only entry point for finding
previously accepted operations after leaving the composer. Every page validates
the current account, security stamp, unrevoked session, effective event viewing
and current event/global manager authority. Only operations authored by that
actor in that event are projected; global administrators cannot list other
authors' operations. This does not expand the PR53 status reader's authorization.

## Pagination and data boundary

- Newest acceptance Unix seconds first, then canonical operation GUID descending.
- 100 visible rows; an extra SQL row determines whether a continuation exists.
- Continuation consists of the original acceptance second and lowercase D-format
  operation ID. Both must match an existing operation in the current author's
  event. Invalid, unknown, other-event or other-author anchors are rejected.
- The caller can retain previous cursors in page memory for a previous-page
  button; requesting no cursor returns the latest page. No database-wide total
  or other-author existence is returned.
- Inserts newer than the current cursor appear on a latest-page refresh, not in
  an older continuation. Each call is a separate read transaction, not a frozen
  multi-page snapshot.
- Projection: operation ID, own-authored subject, original recipient count and
  acceptance time. Subject is plain text; future UI must encode it, not render
  it as markup. No body, audience/device IDs, endpoint/binding, request hash,
  comparison fingerprint, activation epoch or delivery state is returned.
- Original recipient count means inbox acceptance, not current membership,
  successful Push, handset arrival, read receipt or external exactly-once delivery.
  Use the existing independently authorized status reader for a selected operation.

## Verification and exclusions

The dedicated synthetic SQLite check uses real migrations and 105 same-second
operations, author/event isolation, cursor validation, current permission/session
revocation, insertion stability, exact projection, no-write snapshots and SQLite
integrity/foreign-key checks. CI builds Release with warnings as errors and runs
all .NET regression projects.

The sole additive migration creates a composite index on
`(EventId, ActorId, AcceptedAt, Id)`; it changes no table columns or business rows.
The populated previous schema is upgraded in the dedicated check, with an exact
business-state snapshot preserved. SQLite EXPLAIN confirms index use without a
temporary sort on the latest page. It is not a benchmark or constant-cost promise.

No HTTP endpoint, screen, JS, browser persistence, retry/send button, queue
preparation/sweep/claim, delivery, activation update, audit write,
production inspection, deployment or merge is added here. HTTP/UI integration
and reload entry routes are the next engineering step. Runtime worker composition
is inherited unchanged: missing activation defaults OFF, while an existing
persisted ON state is **not** forcibly reset. Future rollout still requires the
separately approved state inspection/explicit stop; this PR is not that approval.
