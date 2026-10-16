# Application passkey-login HTTP boundary

This note documents the existing public login contract and its strict HTTP input boundary. It does not change authentication policy, Identity's WebAuthn behavior, the database schema, or the global rate limiter.

## Existing public contract

- `GET /api/csrf` continues to return the antiforgery request token.
- `POST /api/login-options` accepts exactly `{"email":"…"}` and returns the native ASP.NET Core Identity passkey request-options JSON unchanged.
- `POST /api/login` accepts exactly `{"credential":"…"}` and returns `{"message":"ログインしました。"}` on success.
- Both login actions are queryless POST requests. Each request requires the exact configured `Origin`, `Content-Type: application/json` (optionally `charset=utf-8`), and the `X-CSRF-TOKEN` header plus antiforgery cookie. Form-token fallback is not supported.
- Login options are generated only for an enabled, email-confirmed account. Successful authentication still uses `SignInManager.PasskeySignInAsync`; the normal application cookie remains non-persistent and receives the existing random `sem.authentication-session` claim.

## Request validation

The early login router validates the method, query string, declared body size, content type/encoding, origin, and mandatory antiforgery header before calling antiforgery validation or reading JSON. It does not require the user to be authenticated before these anonymous login request checks.

- `login-options`: maximum request body 4 KiB.
- `login`: maximum request body 64 KiB; the credential field retains its 50,000-character limit.
- The handler enforces the size limit for both declared-length and streamed/chunked bodies.
- JSON must be strict UTF-8 without a BOM, a single object property of the exact expected name, and at most four levels deep. Duplicate, unknown, wrong-type, empty, oversized, or control-character values are rejected.
- Form content types, compressed request bodies, and any query string are rejected. No request body is parsed by the generic endpoint helper before these checks.

Malformed requests do not call Identity passkey methods or issue an authentication cookie/session claim. Native passkey options and sign-in continue to be generated and verified by Identity; no password fallback or new authentication policy is introduced.