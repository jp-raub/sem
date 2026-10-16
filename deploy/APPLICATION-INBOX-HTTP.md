# Application inbox HTTP contract

This slice exposes the current user's persisted messages in the main application. It is read-only except for an explicit mark-as-read operation. It does not send notifications, delete messages, or change the legacy `/api/read-message` contract. The parent application must register `InboxService` as scoped and call `MapApplicationInbox()`.

All responses use `Cache-Control: no-store`. Every operation revalidates the authenticated principal against the live enabled, email-confirmed user, current security stamp, and non-revoked session. Administrator status does not widen message ownership.

## List

`GET /api/core/inbox?page={page}`

- `page` is optional (default `1`); if supplied it must appear exactly once and be an integer from `1` through `10000`. No other query parameters are accepted.
- Returns at most 50 messages owned by the current actor, ordered by `Created` descending and `Id` ascending. The service probes 51 records to set `hasMore`.
- Response:

```json
{
  "actorId": "current-user-id",
  "page": 1,
  "pageSize": 50,
  "hasMore": false,
  "messages": [
    { "id": "message-id", "title": "Title", "created": 0, "readAt": null }
  ]
}
```

## Read a message body

`GET /api/core/inbox/message?id={id}&expectedActor={current-user-id}`

Exactly one `id` (1–64 characters, no control characters) and one `expectedActor` query value are required; unknown or repeated query values are rejected. `expectedActor` must match the live authenticated user. The service filters by both message ID and owner ID; another user's or nonexistent message returns `404`.

Response: `{ "actorId", "id", "title", "body", "created", "readAt" }`. The stored body is returned intact; no truncation or mutation occurs.

## Mark as read

`POST /api/core/inbox/read?expectedActor={current-user-id}`

- Exactly one `expectedActor` value is required and must match the live authenticated user.
- Requires one exact `Origin` matching the configured public origin, one `X-CSRF-TOKEN` header, JSON content type, and a body no larger than 4 KiB. Origin, actor, content envelope, and size are checked before antiforgery validation and body reading.
- The UTF-8 JSON body must be exactly `{ "id": "message-id" }`; duplicate, unknown, missing, wrong-typed, or control-containing ID input is rejected.
- An owner-filtered conditional update changes `ReadAt` only when it is `NULL`. Repeated requests return the original read timestamp and do not change it. Missing or non-owned IDs return `404`.
- Response: `{ "actorId", "id", "readAt" }`.

The list route allows only `GET`; the message route allows only `GET`; the read route allows only `POST`. Known routes answer other methods with `405` and an `Allow` header. Unknown action names return `404`.