# Own-author manual notification history HTTP and screen

The notification-plan page links to `/events/{eventId}/manual-notification-history`.
The interactive screen first reauthorizes against the current browser cookie;
history is fetched only after an explicit action. Reload stores no private list
or selected operation in browser storage. The original author remains bound
throughout the page lifetime. Opening a new page is required after switching users.

`GET /api/core/manual-notification-history` requires one `id` and one `actor`
matching the current cookie actor. Continuation requires exactly one `beforeAt`
and one `beforeId` together. The Core history reader independently rechecks current
session/account, event access and manager/author scope on every page. `no-store`
applies; POST with otherwise valid CSRF is rejected. No new write action exists.

Latest/next/previous reads clear the old list and selected status before awaiting.
The current request gates overlapping list/status actions. Parameter changes and
component disposal invalidate late results and discard private display state.
Late module imports are disposed while stale, before assignment. Failed or
mismatched snapshots display no old list; 403 hides all private history until
explicit reauthorization, which never restores cached rows. The displayed list
is a retrieval-time snapshot, not a live permission or delivery monitor.

The own-author subject is rendered by Razor as literal text. The screen validates
the event, maximum 100 rows, canonical IDs, original positive recipient counts,
Unix timestamps, descending order, strict cursor boundary and continuation anchor.
Previous cursors exist only in circuit memory. Newer inserts are found by latest
refresh; there is no multi-page frozen snapshot or total-count promise.

An explicit per-row status read uses the existing independently author-bound
PR53 endpoint and PR54 ticket validator, including original operation/count/time
matching. People and per-device persisted counts are separate; current global
activation/time eligibility is not device eligibility. Provider acceptance is
not handset arrival/read confirmation. No polling, send/retry, mutation, browser
storage, deployment, activation change, real Push call or merge is introduced.

Dedicated JS checks cover one GET, encoded joint cursor, no-store/no redirect,
invalid contexts, failures, no automatic retry and no logging/storage/polling.
The isolated real HTTPS/virtual native passkey/Blazor check covers actual HTTP
binding/method/cursor/no-store, rediscovery after reload, literal rendering,
explicit read gating, selected status, stale-data clearing and 403 reauthorization.
100+ operation pagination is Core SQLite coverage; it is not a claim of 100+
rows traversed in a real browser. Disposal-during-import is statically reviewed,
not claimed as a native browser fault-injection test. CI additionally runs all
.NET regressions. Docker/DXP/physical devices and external Push remain separate.

The inherited worker remains registered, with default OFF when activation is
missing. Existing persisted ON is not reset; deployment still requires separately
approved state inspection and any explicit stop. This PR grants no rollout approval.
