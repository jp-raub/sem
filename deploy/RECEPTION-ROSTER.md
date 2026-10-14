# Reception foundation: current participant search (read-only increment)

English is authoritative; see RECEPTION-ROSTER.ja.md.

GET `/api/core/reception-roster?id=<event>&q=<query>` and the real Blazor
`/events/<event>/reception` page provide the **current** unique performer plus
spectator roster to current global/assigned event managers only. Regular users
cannot inspect this management surface even if they are registered or the event
is viewable. Hidden-event manager preview is permitted. DB role assignments,
enabled/confirmed identity, stamp and session are checked on each read; forged
role claims are not authority. The response is no-store, POST is rejected, and
all reads share one transaction snapshot. No reception records are written.

The roster includes opaque ID, encoded display name, stored public reading,
performance song titles (one per song despite multiple parts) and spectator
classification. It does not project email, private event answers, questionnaire,
contact/social details, credentials, tokens or other users' enrollment history.
A retained historical question answer alone is not current participation.
Removal of the spectator/final assigned parts is reflected on the next read.
This follows current registration storage, not an invented check-in status.

Name/reading partial search uses NFKC width normalization, case-insensitive
normalization, whitespace removal and Katakana-to-Hiragana matching. A query is
bounded to200 UTF-16 units. Exact participant/match counts are returned; at most
100 matching rows are displayed with an explicit more-matches indication.
Use a narrower search to find a target outside the default first100. Counts do
not mean every row was displayed. Managers get a workspace link; basic profiles
remain separately authenticated and independently validated.

## Explicitly not complete reception

This increment deliberately does **not** issue a QR, store a scan, mark someone
checked in, cancel a check-in, or simulate a successful receipt. The screen
states that no reception action is saved. No schema/migration is added.
The next reception increment must preserve the approved v28 split:

- user×event participant token, reissue/old-token invalidation and current
  registration/management checks; separate from anonymous profile QR;
- one successful valid scan records participant, read time and reading manager;
  invalid QR produces no successful read record;
- scan is not reception completion; cancellation/repeated reads preserve read
  history; final completion references the valid scan or explicit manual method;
- confirmation of identity/needed reception actions, optional note, receipt time
  and manager, duplicate/concurrency prevention; manual spectator support;
- no guessed legacy read time, actual camera/code images require distinct tests.

```bash
dotnet run --project tests/Sem.ReceptionRosterChecks -c Release
# Within isolated upgrade/native-key/real Blazor harness, using profile fixture:
SEM_MEMBER_PROFILE_CHECKS=1 SEM_RECEPTION_ROSTER_CHECKS=1 node scripts/e2e.cjs
```

The flags require the storage-upgrade harness. reception-roster.yml retains all
prior tests and adds privacy/normalization/no-write/member-denial/mobile checks.
Synthetic local DB, SMTP and sandbox browsers only. No real recipient/provider,
QR credential, camera, DXP .env/key/volume/hostname/image, bundle or merge changes.
