# Application join and email confirmation

This slice adds a dedicated anonymous application-join HTTP surface while reusing the existing invitation, pending-user, `EmailTicket`, and native Identity passkey-completion policies. It does not change the external test Home page, `/api/register`, `/verify`, passkey options/completion, or the existing invitation-consumption boundary. No schema, new quota, or ticket-purpose field is added.

## `POST /api/join/register`

Queryless JSON request with exactly one each of `invite`, `name`, and `email`:

```json
{"invite":"…","name":"…","email":"…"}
```

Only anonymous callers are accepted. The request must have the exact configured `Origin`, exactly one non-empty `X-CSRF-TOKEN` header, and `application/json`. The raw body is limited to 4 KiB before antiforgery validation and while reading unknown-length bodies. JSON must be strict UTF-8, shallow, an object, contain no duplicate or unknown fields, and use string values only. Unsupported methods return 405 with `Allow: POST`; query parameters are rejected.

The service applies the existing registration rules: email delivery must be configured; the site must be initialized; the invite must be enabled, unexpired, and have remaining uses; a pending user may only resume with the same invite. A one-hour `EmailTicket` is stored by hash, previous unused tickets for that pending user are invalidated, and the confirmation email is queued to the saved account email through the existing protected delivery queue. The response is `{ "message": "確認メールを送信待ちに登録しました。到着後、リンクを開いてパスキーを登録してください。" }`.

The new message link is a fixed same-origin `/join#token=<token>` URL. The credential is in the URL fragment, so it is not sent in the initial page request. The join UI must explicitly POST it to the confirmation endpoint; it must not confirm solely by loading the page.

## `POST /api/join/confirm`

Queryless JSON body with exactly one string field:

```json
{"token":"…"}
```

It has the same anonymous, Origin, CSRF, 4 KiB body, UTF-8, JSON, and method requirements as the registration endpoint. A valid unused ticket is atomically marked used and confirms the email. The response issues the existing non-persistent `SEM.Enrollment` cookie (20-minute expiry) and returns `{ "message": "メール確認が完了しました。この画面で期限内にパスキーを登録してください。" }`. The new UI can then use `/api/status` to observe the enrollment state. A consumed, expired, or invalid ticket is rejected. The endpoint accepts existing tickets too; ticket purpose is not persisted.

## Existing flow compatibility and completion boundary

Legacy `POST /api/register` continues to use the same registration service and its existing response. Legacy `GET /verify?token=...` still consumes the same one-time ticket, issues the same enrollment cookie, and redirects to `/?enroll=1`; old `/?invite=...` links remain unchanged. The existing external Home page remains separate from `/join`.

After confirmation, native enrollment continues through `POST /api/passkey-options` and `POST /api/passkey-complete`. The latter validates the native attestation and matching user, then in its existing transaction rechecks the invite and increments `Uses`, enables the user, and saves the passkey. Only after successful completion does it clear the enrollment cookie and issue the normal application cookie. These routes and their consumption logic are not changed by this slice.

No SMTP delivery or live registration is exercised by this implementation scope.
