# Event comments and combined participation

English is authoritative; the paired translation is EVENT-COMMENTS.ja.md.

## Implemented behavior

Authenticated viewers may read and post comments for a currently viewable event,
without performance/spectator registration or required questionnaire answers.
Ordinary posting is permitted **before midnight following the event date in
Asia/Tokyo**; the date is derived from the scheduled instant, not its UTC date.
At midnight it is closed. Current global/event managers may post afterwards and
preview Hidden events. Membership/session/security stamp and actual manual or
automatic phase are checked inside the transaction. An invalid/unrepresentable
date fails closed for ordinary posting.

Text is trimmed and must contain at least three Unicode scalar characters;
supplementary characters are not counted as two letters. HTML is ordinary
encoded text, not executable markup. The existing request-size guard and a
65,536 UTF-16-unit validation bound protect transport/storage, rather than
introducing a small business-message limit. No email, authentication data or
operation hashes appear in the public list. Body and display name are visible
to event viewers; not to unauthenticated internet users.

Only a **current** global or assigned event manager may delete. The ordinary
author cannot delete even their own comment. Deletion requires explicit UI
confirmation, physically removes the active comment body and records an audit
identity. Backups can retain earlier content; physical deletion is not a promise
to erase backups or a body already copied by another viewer.

## Storage and replay

Migration `20261013164240_AddEventComments` adds EventComments and
CommentOperations, with restricted user/event FKs and list indexing. It does not
alter or delete existing users, keys, answers, assignments, registration replay
records, mail/Push history or settings. CommentOperations intentionally has no
FK to the deleted comment: it retains operation/event/actor/target identity,
hash and timestamp, **not another plaintext copy of the body**.

A standalone post uses its request UUID as the comment identity. Exact same
operation+actor+event+payload replays the result; reused IDs with changed payload
or actor are rejected. A post replay after admin deletion does not resurrect the
body. Deletion is likewise replayable, but still requires current admin rights.
Current event visibility and authentication apply even to a historical replay.
A committed pre-cutoff operation can be checked after cutoff without posting
again. Hashes are integrity/replay keys, not encryption or guaranteed forgetting.

POST `/api/core/comments?id=<event>` uses
`{requestId,eventVersion,body}`. POST `/api/core/comment-delete?id=<event>` uses
`{requestId,commentId}`. Both retain strict JSON field checks, Origin, CSRF and
size guards. GET `/api/core/comments?id=<event>` is no-store, newest first with
stable ID tie-breaking. It returns up to100 rows and a `{at,id}` continuation;
pass both `beforeAt` and `beforeId` for older rows. Deleting the boundary row does
not invalidate a timestamp/ID cursor. Refresh replaces the list; the UI does not
claim live synchronization or recall of previously viewed data.

The `/events/<event>/comments` real Blazor page links from workspace/participation,
provides mobile input, deadline, public-scope notice, explicit delete confirmation
and bounded older-list loading. An uncertain write keeps its original UUID and
payload for a manual retry; it does not automatically repeat submission. Do not
reload or start a new UUID to resolve an uncertain result, because a new request
is a new intentional operation.

## Combined participation without breaking old replay

The existing ParticipationEdit adds optional `comment`, omitted from
serialization when null. The other serialized fields and their order remain
unchanged, so old request fingerprints replay byte-for-byte. No new property is
added to old result JSON. The resulting CommentCreated item shares the existing
registration request UUID, which is reserved against standalone operation reuse.
The actor, not a proxy registration subject, authors the comment.

- Register: one independent comment may commit even if all selected slots fail
  individually. Validation failures (phase, required answers, capacity, etc.)
  roll back the entire operation; they are not per-slot partial outcomes.
- Spectate/Answers: comment posts only after that operation succeeds. An
  AlreadySpectator zero-success response does not post another comment.
- Release/RemoveSpectator: submit comments separately.
- Expired ordinary comments block the combined request before modifying storage;
  the final gate rechecks time and records the admitted instant. Crossing the
  cutoff rolls back registration/answer state and comment together.
- Answers, participant version and business-mail enqueueing are decided from
  participation success **before** appending the comment result. A comment-only
  partial result never updates answers or creates a registration email.
- Same registration UUID replays all results without another comment/mail.
  Replay after comment deletion is a historical result, not recreation.

The participation UI clears the comment draft only after CommentCreated success.
Uncertain submissions retain their original captured request and lock edits until
same-ID result checking; the existing normal/proxy permissions are unchanged.

## Checks and limits

```bash
dotnet run --project tests/Sem.CommentChecks -c Release
# Inside the isolated storage-upgrade/native-key/browser harness:
SEM_COMMENT_CHECKS=1 node scripts/e2e.cjs
```

The native flag needs the existing SEM_STORAGE_UPGRADE_COMMAND harness; alone it
does not run the module. event-comments.yml retains all earlier regression suites
and adds the comment suite/native module to isolated Docker acceptance. Tests use
synthetic identities and local SMTP, not live recipients or Push providers.

Mock-v33 graph/popups/profile links and the final integrated event screen remain
separate UI work. QR reception and imports are not implemented by this increment.
This does not deploy/merge/create a delivery bundle or touch the DXP .env, keys,
volume, hostname or running image. Do not run a destructive Down migration on
state to be retained; restore a validated compatible copy/backup instead.
