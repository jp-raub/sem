# Theme appearance HTTP API

The HTTP boundary is implemented inside the shared `/api/core/{action}` map. It does not add a standalone route or bypass the common origin and antiforgery checks.

## Read appearance

`GET /api/core/theme-appearance` returns the current authenticated actor's appearance. An optional single `id` query parameter scopes the read to an event:

```text
GET /api/core/theme-appearance
GET /api/core/theme-appearance?id=<eventId>
```

When present, `id` must be nonempty, at most 128 characters, contain no control characters, and occur exactly once. Event visibility and access use the existing `ParticipantEvent` checks. The response is `{ "eventId": string|null, "appearance": ThemeAppearanceView }`; the appearance includes the current `actorId`, permission flags, safe catalog projections, the caller's personal layer, optional event layer, site layer, and resolved theme. There is no account/user selector: personal preferences always belong to the authenticated actor. `GET` has `Cache-Control: no-store`.

## Write appearance and catalog

All writes are `POST` requests to the shared core map and require the exact current actor ID in one query parameter named `expectedActor`. It is a consent/context guard, not an authentication token or actor override:

```text
POST /api/core/theme-personal?expectedActor=<actorId>
POST /api/core/theme-event?id=<eventId>&expectedActor=<actorId>
POST /api/core/theme-site?expectedActor=<actorId>
POST /api/core/theme-catalog-save?expectedActor=<actorId>
POST /api/core/theme-catalog-save?id=<catalogId>&expectedActor=<actorId>
POST /api/core/theme-catalog-delete?id=<catalogId>&expectedActor=<actorId>
```

The IDs and `expectedActor` must each be a single, nonempty query value, no longer than 128 characters, with no control characters. `theme-catalog-save` omits `id` to create an entry; an empty, duplicated, or malformed `id` is invalid. Catalog IDs are additionally validated by the existing catalog service and database constraints.

JSON bodies do not include `expectedActor` or any actor identifier:

| Action | JSON body | Result |
| --- | --- | --- |
| `theme-personal`, `theme-event`, `theme-site` | `ThemePreferenceEdit`: `themeId`, `mode`, `rowVersion` | `ThemePreferenceView` |
| `theme-catalog-save` | `ThemeCatalogEdit`: `name`, `accent`, `background`, `surface`, `text`, `rowVersion` | `ThemeCatalogEntryView` |
| `theme-catalog-delete` | `{ "rowVersion": number }` | `{ "message": string }` |

Bodies reject unmapped fields, missing required fields, and incorrect JSON types. Modes use the existing `ThemeMode` numeric values (`0` System, `1` Light, `2` Dark). Preference layers can be cleared by sending `themeId: null` and `mode: null`. New catalog entries use a server-generated ID and row version 1; updates and deletes require the current row version. Built-in catalog entries may be edited but cannot be deleted; referenced entries cannot be deleted.

Before antiforgery validation or reading the JSON body, a read-only preauthorization transaction validates the current actor and the relevant scope. Personal changes require a current authenticated actor; event changes require an event manager (including a global administrator); site and catalog changes require a global administrator. The endpoint then compares `expectedActor` with the current cookie principal. Existing core write methods repeat authorization and concurrency checks in their own transactions.

All handled `GET` and `POST` responses are `no-store`. `POST` also retains the shared exact-origin check, antiforgery validation, and an effective 65,536-byte body limit. The HTTP layer does not mutate persistence during reads or preauthorization.

## Errors and verification

- `400`: malformed or duplicate identifiers, invalid JSON or body values, antiforgery failure, or a protected catalog entry.
- `403`: missing, disabled, stale-stamp, revoked-session, wrong-actor, or insufficiently authorized caller.
- `405`: a read action used with `POST`, or a write action used with `GET`.
- `409`: stale row version or a database concurrency/constraint conflict.
- `413`: a request body above 65,536 bytes.

`tests/Sem.ThemeAppearanceHttpChecks` hosts the real core map on synthetic Kestrel with a temporary migrated SQLite database and Identity schema V3. It covers cookie/session authorization, event visibility and manager scopes, strict query/body handling, origin/antiforgery, preference persistence and clearing, catalog create/edit/delete protection, no-store responses, read-only behavior, and the request-size boundary. Run it after the concurrent UI changes have frozen; it has not been run during concurrent editing.