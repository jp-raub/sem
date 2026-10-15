# Theme appearance UI

The appearance editor at `/appearance` uses the Core theme-appearance API. On other authenticated pages the same module loads the current user's resolved appearance and applies the four validated palette values to the document. An event-specific read is made on `/events/{id}` and on `/appearance?eventId={id}`.

## Preferences and permissions

- Personal, event, and site appearance preferences are persisted in the Core database. Personal overrides event, which overrides site; theme and mode inherit independently. System mode follows the device color-scheme preference.
- The editor exposes event controls only to an authorized event manager and site/catalog controls only to a site administrator. The API remains authoritative and validates actor, target, and permission on every request.
- A draft changes only the in-memory preview. “Apply” is the only save action; “Cancel” restores the committed state. The browser does not store appearance preferences in localStorage or use a client-selected actor identity.
- Catalog names are inserted as text, and colors are accepted only as six-digit `#RRGGBB`. Built-in themes are not deletable through the UI.
- Requests use the current same-origin authentication cookie, a freshly fetched CSRF token for writes, no-store caching, redirect rejection, bounded response sizes, and a 25-second timeout. A write is attempted once. If its outcome is uncertain, the UI blocks further writes until the user explicitly reloads and inspects the current server state; it never claims exactly-once delivery.
- The dark palette follows the approved mock defaults (`#15201c`, `#202d27`, `#e1ebe5`) and brightens the selected accent. Forced-colors, keyboard focus, responsive layout, and reduced-motion preferences are supported.

## Verification

Run `node scripts/theme-appearance-checks.mjs`. This is a frontend source/mock contract check only; it does not replace the required parent-run .NET build, API tests, CI, or browser acceptance.
