# Application inbox UI

## Scope

The authorized, interactive-only `/inbox` page is the first-party, read-only message viewer for the signed-in user. It replaces the navigation link to `/?messages=1`; the shared navigation already forces a full-document boundary. The page can list messages, open a selected message, and explicitly mark that message as read. It does not send or edit messages, automatically mark a message read on opening, or claim to implement the former external inbox's other behavior.

Titles and bodies are rendered as plain text with `textContent`. HTML-looking content, links, and markup are not executed or converted into anchors. The body is not shortened by the UI. Message content is not persisted in browser storage or logs; it necessarily exists in the rendered DOM and JavaScript memory while displayed or processed. Message content and CSRF tokens are not placed in URL state or HTML attributes. A CSRF token is temporarily held in a JavaScript variable while the explicit read request is prepared and sent; neither it nor message content is persisted or logged.

## List contract

On initialization, the page makes a read-only request:

`GET /api/core/inbox?page=N`

The exact response shape is:

```json
{"actorId":"…","page":1,"pageSize":50,"hasMore":false,"messages":[{"id":"…","title":"…","created":1700000000,"readAt":null}]}
```

The UI validates the exact top-level and message property sets, actor identity, requested page, page size, `hasMore`, and at most 50 messages. IDs are 1–64 characters without control characters. `created` and non-null `readAt` are Unix-second integer values. The UI requests pages 1 through 10000 only. It displays an explicit API-limit notice and disables Next at page 10000 when `hasMore` is true. It does not imply that messages beyond the API page limit have been retrieved.

## Opening and reading a message

Opening a message is a read-only detail request and does not mark it read:

`GET /api/core/inbox/message?id=<messageId>&expectedActor=<exact actorId>`

The exact response shape is:

```json
{"actorId":"…","id":"…","title":"…","body":"…","created":1700000000,"readAt":null}
```

The returned actor and message ID must match the current page snapshot and selected message. The full title and body are displayed as text, subject only to the 1 MiB response-byte bound; the UI does not truncate message content.

Only a separate, explicit **Mark as read** button sends:

`POST /api/core/inbox/read?expectedActor=<exact actorId>`

```json
{"id":"<messageId>"}
```

The page obtains a fresh CSRF token from `GET /api/csrf` and sends it in `X-CSRF-TOKEN`. The success response must be exactly `{"actorId":"…","id":"…","readAt":1700000000}`. No read mutation is sent while opening, refreshing, paginating, or restoring the page.

The shared navigation obtains its unread count separately from the inbox page. After marking a message read, the count may remain stale until the user reloads the page; the inbox status and guidance say so rather than modifying the navigation module.

## Failure handling and lifecycle

The inbox list and detail responses are each limited to 1 MiB; CSRF responses to 16 KiB; read-mutation responses and JSON request bodies to 4 KiB. The complete response is read within the byte limit and decoded with fatal UTF-8 before JSON parsing. Oversized or malformed data is rejected as a whole; partial data is never applied. Requests use same-origin credentials and mode, `redirect: "error"`, `cache: "no-store"`, a 25-second abort bound, and no-store semantics.

After a read POST may have begun, a timeout, connection failure, or malformed response is treated as an unknown outcome. The page does not retry automatically and locks further read mutations. The operator can explicitly open or refresh that message's read-only detail; the lock is cleared only when the returned detail confirms a non-null `readAt`. An unread response keeps the lock in place. List/detail GETs are safe to repeat; no POST is replayed.

Every asynchronous action is bound to the current owner, actor, selected message, and operation generation. A 401/403 or actor mismatch clears actor-bound list/detail state and fails closed. Pagehide clears displayed content and aborts active requests; a BFCache pageshow performs only a fresh read-only page-1 request. Disposal removes listeners, aborts requests, invalidates pending work, and clears message content from the DOM. Message content and CSRF values are held only transiently as described above, not persisted in browser storage or logs.

## UI hooks

- `inbox-root`, `inbox-status`, `inbox-error`, `inbox-module-error`
- `inbox-refresh`, `inbox-list-body`, `inbox-previous`, `inbox-page`, `inbox-next`
- `inbox-unknown`
- `inbox-detail`, `inbox-detail-status`, `inbox-detail-error`, `inbox-detail-title`
- `inbox-detail-created`, `inbox-detail-read-state`, `inbox-detail-body`
- `inbox-detail-refresh`, `inbox-mark-read`

## Verification

`scripts/application-inbox-ui-checks.mjs` provides synthetic coverage for initial read-only behavior, safe text rendering, explicit read, unread unknown-write reconciliation without replay, pagination limits, and pagehide/pageshow cleanup. It also models a read committed before response loss: the UI remains locked until explicit detail GET reconciliation, with the original timestamp retained and no second POST. Deferred mutation tests dispatch pagehide/pageshow, verify the POST signal is aborted and content is cleared, then resolve or reject the old request after restoration to verify that stale success/catch/finally cannot repopulate or change the restored UI.

These are synthetic transport/lifecycle checks, not proof of an actual BFCache cache hit. Local browser acceptance uses real application UI/API and Identity cookies with synthetic accounts/messages; it is distinct from real-user, SMTP/Push, or production acceptance. Browser-level response-loss and in-flight pagehide scenarios must be recorded separately from the synthetic suite. The synthetic suite does not replace endpoint/CSRF tests or a .NET build.