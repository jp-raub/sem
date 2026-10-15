# Self-service email-change UI

This UI lets an authenticated member request a new email address, prove control of the existing account with a passkey, and confirm the change from a single-purpose email link. This change is limited to the web UI; the backend must provide the API contract below. The UI does not change SMTP configuration or send real email by itself.

## Profile flow

- The profile settings page reads `GET /api/email-change-status`, expecting `{ "actorId": "...", "pending": null | { "operationId": "...", "expiresAt": "<ISO-8601>", "lastSentAt": null | "<ISO-8601>", "confirmationSent": true | false } }`. It retains `actorId` in memory for request binding but does not display it or any pending address.
- Starting a request sends `POST /api/email-change-begin?expectedActor=<actorId>` with `{ "newEmail": "..." }`. The response is `{ "operationId": "...", "publicKey": <native WebAuthn request options> }`. The browser uses the existing WebAuthn request-options and credential serialization conventions, then sends `POST /api/email-change-reauth?expectedActor=<actorId>` with `{ "operationId": "...", "credential": "<serialized credential JSON>" }`.
- Resending always obtains fresh options using `POST /api/email-change-reauth-options?expectedActor=<actorId>` with `{ "operationId": "..." }`. Its response is `{ "operationId": "...", "publicKey": <native WebAuthn request options> }`. Only after passkey proof does the UI call `POST /api/email-change-reauth?expectedActor=<actorId>` to send/resend the confirmation mail.
- Cancellation sends `POST /api/email-change-cancel?expectedActor=<actorId>` with `{ "operationId": "..." }`. Every authenticated POST has exactly one `expectedActor` query parameter. The backend must compare it with the current live actor before checking CSRF or reading the body. Successful reauthentication and cancellation responses contain a bounded `message` string; the UI uses fixed local status text rather than rendering server-provided HTML or exposing the pending address.
- The pending address is never requested from the status endpoint or displayed. The address field is cleared after a begin response; no address, token, credential, or operation state is saved in local storage.
- The UI locks authenticated actions until a successful status read establishes the current actor. If the actor changes, stale requests and UI generations are invalidated; HTTP `401`/`403` clears the actor and pending state and requires an explicit status refresh.
- A failed/uncertain POST is never replayed. The UI may reconcile with a read-only status GET; until that succeeds, it keeps actions locked. On `429`/`Retry-After`, it tells the user to wait, refresh status, then reauthenticate for a remaining request or start again if no request remains.

## Public confirmation link

The public route is `/profile/email-change-confirm#op=<operationId>&token=<secret>`. Both values are read only from the fragment; the token is never accepted from a query string or path. The script immediately removes the fragment (and query) from browser history with `history.replaceState` before it requests CSRF data or makes a POST. It sends the values only in the JSON body of anonymous, queryless `POST /api/email-change-confirm`: `{ "operationId": "...", "token": "..." }`. This endpoint does not receive `expectedActor`.

The confirmation endpoint must accept the single-purpose token without requiring an authenticated session, consume it for this email-change operation only, and must not issue a normal sign-in/session. The UI does not create or navigate into an authenticated session after confirmation. The token is not logged, displayed, or stored in browser storage. The POST uses same-origin fetch, a CSRF token, no-cache behavior, and rejects redirects.

## Transport and privacy

- Every POST obtains a fresh token from `GET /api/csrf`, sends `X-CSRF-TOKEN`, uses `credentials: same-origin`, `mode: same-origin`, `redirect: error`, and `cache: no-store`. The browser supplies the `Origin` header for these same-origin POST requests.
- JSON responses and outgoing request bodies are bounded. Requests time out after 25 seconds. The client does not read or display server error bodies.
- The status endpoint is scoped to the current actor on the server. The client retains its `actorId` only in memory for authenticated POST binding and never renders a pending address.
- Backend ownership, CSRF/Origin enforcement, strict JSON parsing, token validation/consumption, email delivery, and transactional email change are server responsibilities and are not implemented by these UI files.