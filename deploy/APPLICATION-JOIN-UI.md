# Application invitation join UI

The `/join` page is an anonymous, static-SSR entry point for the existing invitation and passkey-enrollment flow. The page does not issue invitations, display confirmation links, or create an authenticated session until the existing passkey-completion endpoint succeeds.

## User flow

1. The registration form accepts an invitation code, display name, and email address. JavaScript sends an explicit `POST /api/join/register` with exactly `{ invite, name, email }`. A successful `{ message }` response is shown as a queued confirmation-email request; the UI does not expose the token or the email link.
2. An email confirmation link may open `/join#token=...`. Before any request, the page removes the query and fragment from the address bar with `history.replaceState`. The token stays in memory and is submitted only after the user presses the confirmation button, as exactly `{ token }` to `POST /api/join/confirm`.
3. After a successful confirmation, the UI checks `GET /api/status`. Only an unsigned-in session with `enrolling: true` can expose the next step. The page does not receive or display the pending user's identity. A confirmation checkbox and a separate button are required before requesting passkey options or invoking `navigator.credentials.create`.
4. `POST /api/passkey-options` sends `{}` and returns the native `PublicKeyCredentialCreationOptions` object directly. After explicit native registration, `POST /api/passkey-complete` sends exactly `{ credential: "<serialized credential JSON>" }`. The existing server endpoint issues the normal login cookie. The UI navigates to `/workspace` only after a subsequent status read confirms the current browser is signed in.

## Security and failure behavior

- The forms are disabled in server-rendered HTML and have no input `name` attributes. Without the script, they cannot submit registration values through a browser form request. Script initialization errors remain visible and fail closed.
- The `/auth` registration link disables enhanced navigation and causes a full document load. `join.js` is a one-shot deferred script, not an enhanced-navigation module.
- Requests use fixed same-origin API paths, `credentials: same-origin`, `mode: same-origin`, `cache: no-store`, `redirect: error`, and a fresh `/api/csrf` token in `X-CSRF-TOKEN` for each POST. The browser supplies the same-origin `Origin` header; server-side validation remains authoritative.
- The current `/api/status` category is rechecked after CSRF retrieval immediately before each POST. A change between anonymous and enrollment/authenticated state stops the send. The status contract has no enrollment actor identifier, so the UI cannot distinguish two enrollment-cookie identities; the server-side cookie binding is authoritative and the UI never displays or claims a pending identity.
- Join request bodies are limited to 4 KiB; passkey completion is limited to 64 KiB. Responses are bounded before parsing, require JSON, and use fatal UTF-8 decoding. Network operations have a 25-second timeout.
- Tokens and credentials are not written to storage, query strings, logs, or visible page text. Fragment values are removed before network access. DOM updates use text nodes and input values, not HTML parsing.
- An uncertain POST locks all mutation controls. The user can perform a read-only status check, but that result does not prove that the earlier request succeeded, identify its intended target, unlock submission, or trigger a retry. A status-only `enrolling` result does not reveal which person owns the enrollment session; the user must explicitly confirm that the current browser session is the one they intend to continue.
- An authenticated user is directed to the workspace path in the UI; registration and confirmation actions stop. Server authorization and enrollment-cookie binding remain the security boundary.
- Page disposal aborts outstanding requests and native credential prompts where supported. Every asynchronous continuation checks the active page/operation before updating UI or sending another request.

## Scope limits

This UI does not implement invitation issuance/approval, email delivery, resend flows, token display, account recovery, password authentication, or a new enrollment schema. It uses the existing `/api/status`, `/api/csrf`, `/api/passkey-options`, and `/api/passkey-complete` contracts alongside the join endpoints.

`scripts/application-join-ui-checks.mjs` provides synthetic contract and lifecycle checks. It does not replace integration testing of the server endpoints or a real-browser/native-authenticator acceptance test.