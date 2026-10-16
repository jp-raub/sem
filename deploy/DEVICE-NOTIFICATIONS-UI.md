# Current-browser Push notification settings

## Scope

`/notifications` is an authenticated, current-browser-only management screen. It is separate from the public/external diagnostics UI at `/`. It does not list or edit other devices, send test notifications, request email delivery, or change notification preferences.

The page disables prerendering and imports `wwwroot/device-notifications.js` after the interactive component starts. It uses the existing root-scope `/sem-sw.js` service worker. The navigation link uses a full-document boundary so a stale static-script instance cannot leave the page uninitialized.

## Read-only initialization

On entry the module reads browser capabilities and `Notification.permission`, then fetches `GET /api/core/device-notifications` for the current `actorId`, `pushConfigured`, and `pushPublicKey`. The public key must be canonical unpadded base64url for a 65-byte uncompressed P-256 point with prefix `0x04` that lies on the P-256 curve; invalid keys stop initialization before any permission prompt. Next it calls `getRegistration('/')` and `getSubscription()`. It does not call `Notification.requestPermission()`, register a service worker, subscribe, or rebind a subscription on page load. When a local subscription exists, it obtains a CSRF token from `GET /api/csrf` and performs a read-only status request:

- `POST /api/core/device-notifications/status?expectedActor=<exact actorId>`
- JSON body: `{ "endpoint": "..." }`
- Expected response: `{ "actorId": "...", "registered": true|false }`

A `false` result does not trigger registration. If no browser subscription exists, the API cannot identify a current browser endpoint, so the UI explicitly says the server-side state cannot be checked from this browser.

All API requests use same-origin credentials and mode, `redirect: "error"`, `cache: "no-store"`, a 25-second abort bound, and strict fatal-UTF-8 JSON parsing capped at 16 KiB. Mutating requests send the CSRF token in `X-CSRF-TOKEN` and the exact context actor once as the `expectedActor` query parameter. No endpoint, subscription key, CSRF token, or response secret is written to page text, attributes, browser storage, or logs.

## Explicit actions

Only the explicit **Register this browser** action can request notification permission, register `/sem-sw.js` with scope `/`, create/reuse a browser subscription, and submit it:

- `POST /api/core/device-notifications/register?expectedActor=<exact actorId>`
- JSON body: `{ "endpoint": "...", "p256dh": "...", "auth": "..." }`
- Expected response: `{ "actorId": "...", "registered": true }`

Only the explicit **Unregister this browser** action can remove a server binding and local browser subscription:

- `POST /api/core/device-notifications/unregister?expectedActor=<exact actorId>`
- JSON body: `{ "endpoint": "..." }`
- Expected response: `{ "actorId": "...", "registered": false }`

Unregister is server-first. The browser's `PushSubscription.unsubscribe()` is called only after the server confirms unbinding. If that unsubscribe and the follow-up `getSubscription()` both fail, the UI states that server unbinding succeeded but this browser's subscription state is unverified, disables further register/unregister actions, and offers manual read-only recheck. This is not treated as an unknown server write and does not auto-replay. Removing the subscription does not revoke browser/OS notification permission. Logout removes the server-side session binding; after signing in again, explicitly register the desired browser.

A failed or malformed mutation response, timeout, or lost connection is treated as an unknown outcome. The page locks further mutation and does not auto-replay. The user must explicitly request a read-only status refresh. That refresh re-reads the current actor and local subscription; it clears the lock only after a valid status response. If there is no local subscription, the server state cannot be reconciled from this endpoint and the page keeps the lock.

Browser-native mutations (`serviceWorker.register()`, `PushManager.subscribe()`, and `PushSubscription.unsubscribe()`) cannot be cancelled by the JavaScript timeout. If one exceeds the 25-second wait bound, the page keeps registration and unregistration locked while the original browser promise remains pending. A refresh during that time is read-only and cannot unlock the controls. Once the original promise settles, the user must explicitly refresh again; only a fresh local read (and server status read when a subscription exists) can reconcile the state. No native or server mutation is automatically replayed. Within the same loaded module, this pending-operation guard follows owner/root replacement so a late browser operation cannot silently enable a successor page. A full document reload creates a new JavaScript module and cannot inherit the old promise tracker; wait for any still-running browser operation before reloading or starting another change, then manually inspect the current browser state.

## Accessibility and lifecycle

The page is authorized and interactive-only. Controls start disabled. A visible `role="alert"` explains missing module failures and leaves actions disabled. Status and permission messages use accessible status text. All display uses `textContent`; API strings and secret values are never inserted as HTML. The module has a single owner/root initialization; disposal removes listeners, aborts active requests, cancels JavaScript waits (not the underlying browser-native mutation), increments its generation, and drops actor, key, subscription, and registration references. A pending native mutation remains tracked until the browser promise settles; late callbacks cannot update the old DOM, and a successor owner stays locked until an explicit manual reconciliation.

## Deliberately deferred

There is no test-push button: the existing test-send operation is account-wide and cannot be represented honestly as a current-browser-only action. Synthetic Node checks use fake timers and deferred native promises to exercise timed-out `ServiceWorker.register()`, subscribe, and unsubscribe operations; read-only refresh while each operation is pending; root/owner replacement; late settlement; and manual reconciliation without automatic writes. These tests do not perform an actual browser permission prompt, service-worker installation, Push subscription, or delivery acceptance. Production acceptance must separately verify an HTTPS browser and device, explicit permission, registration, status, unregister ordering, logout/re-register behavior, and actual delivery using an approved test procedure.
