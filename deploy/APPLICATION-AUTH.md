# Application passkey authentication UI

## Scope

`/auth` is the production-oriented, passkey-only sign-in page. It is separate from the `/` external diagnostics/test UI (`Home.razor` and `sem.js`). No password, recovery-code, or alternate identity-provider flow is introduced. The sign-in page does not start WebAuthn until the user explicitly submits the form. Server-rendered controls remain disabled until `auth.js` has initialized and obtained an antiforgery token. If the script is missing or initialization fails, the accessible status/error copy explains that the form is unavailable.

The page uses these existing endpoints without changing their contract:

1. `GET /api/csrf` returns `{ "token": "..." }` and establishes the antiforgery cookie.
2. `POST /api/login-options` with `{ "email": "..." }` returns the native `PublicKeyCredentialRequestOptions` JSON directly. The request uses `X-CSRF-TOKEN`.
3. After an explicit user click, the browser calls `navigator.credentials.get({ publicKey })` and serializes the assertion.
4. `POST /api/login` with `{ "credential": "<serialized assertion JSON>" }` verifies it. A successful response contains a `message`; the browser then navigates to the validated local return path.

Both POSTs use same-origin fetch, the browser cookie jar, `cache: no-store`, `redirect: error`, a 25-second request timeout, and bounded UTF-8 JSON parsing. Responses are capped at 1 MiB, decoded with fatal UTF-8, and must have a JSON content type. The credential payload is capped at the endpoint's 50,000-character contract and is kept only in transient local variables. Neither email, CSRF token, nor credential is written to a URL, browser storage, or logs.

## Navigation and session safety

Unauthenticated protected-route navigation goes to `/auth` with a local path-only `returnUrl`; the existing redirect component uses `forceLoad: true`. The Workspace sign-in anchor sets `data-enhance-nav="false"` as well. Both entry paths therefore perform a full document navigation: `auth.js` is a one-time static-page initializer and is not reinitialized by Blazor enhanced navigation. The redirect component intentionally drops the source query and fragment. The client accepts exactly one return URL, rejects non-local paths, protocol-relative forms, encoded or literal slash/backslash/control tricks, malformed escapes, credentials, fragments, and canonicalized reserved first segments (`/auth`, `/api`, and `/Account`, including percent-encoded and nested-encoded aliases), and verifies same origin. It then discards any query and fragment and returns only the approved local pathname; invalid input defaults to `/workspace`. This avoids login loops, API returns, open redirects, and carrying source URL secrets through sign-in.

The existing server endpoint owns authentication and session issuance. It creates a non-persistent application cookie with its random session claim; live-session validation remains server-side. The client does not store or create an authentication session.

If the final login POST has an unknown transport/protocol outcome, the page locks the form and never replays that POST automatically. The user can explicitly request a read-only `GET /api/status` reconciliation. A detected authenticated user is sent to the safe return path; otherwise the page stays locked and advises waiting and manually reloading before a new attempt.

On page disposal, the active request/WebAuthn operation is aborted, the generation is invalidated, and the in-memory CSRF token is cleared. Stale results are ignored. No conditional/automatic WebAuthn mediation is used.

## Owned UI files

- `src/Sem.Web/Components/Pages/Auth.razor`
- `src/Sem.Web/Components/Account/Shared/RedirectToLogin.razor` (the existing redirect component; it resides under `Shared` in this checkout)
- `src/Sem.Web/Components/Pages/Workspace.razor` (sign-in link and removal of stale pending-feature copy)
- `src/Sem.Web/wwwroot/auth.js`
- `scripts/application-auth-checks.mjs`
- `deploy/APPLICATION-AUTH.md` and `deploy/APPLICATION-AUTH.ja.md`

The `/Account` security block and the external diagnostic Home page are intentionally untouched.

## Verification

Run only the focused UI checks:

```sh
node --check src/Sem.Web/wwwroot/auth.js
node --check scripts/application-auth-checks.mjs
node scripts/application-auth-checks.mjs
```

The synthetic checks cover SSR-inert markup, explicit-submit behavior, the current CSRF/options/login contract, WebAuthn assertion serialization, timeout and malformed response handling, unknown-outcome lock/read-only reconciliation, and return URL rejection. A later HTTPS browser acceptance should use a real enrolled passkey and verify the workspace return, diagnostics separation, and unchanged `/Account` 404. No .NET build/host is part of this UI-only verification scope.
