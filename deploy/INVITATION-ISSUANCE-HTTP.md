# Administrator invitation issuance

`POST /api/core/invitations/issue?expectedActor=<current-user-id>` adds a purpose-bearing direct invitation without changing the existing legacy issuance or request-approval routes.

The request uses `application/json`, exactly one configured `Origin`, one non-empty `X-CSRF-TOKEN` header, the current live administrator session, and an exact `expectedActor` match. The request body is capped at 4 KiB before antiforgery validation and during reads, including unknown-length bodies. It is strict UTF-8 JSON with exactly these fields and types:

```json
{"purpose":"Project team","count":3,"days":7}
```

Purpose is trimmed and must contain 1–500 characters without control characters. `count` remains limited to 1–5 and `days` to 1–7, matching the existing invitation policy.

On success, the response is:

```json
{"actorId":"…","id":"…","purpose":"Project team","issuanceSource":"Direct","limit":3,"uses":0,"created":0,"expires":0,"link":"https://example.invalid/join#invite=…"}
```

The URL is built from the configured public origin and always targets `/join#invite=…`; the invitation token is in the fragment, not the query. The response is `Cache-Control: no-store`. The raw token/link is not written to the audit record or the invitation-list DTO. The database continues to store the token hash and its existing protected form.

The invitation row and `InvitationIssued` audit row are committed in one transaction. The issuer and owner are the current administrator; `RequestId` remains null, `Uses` starts at zero, and expiry uses the existing 1–7 day policy. Consumption is unchanged: the native passkey completion path rechecks the invitation and increments `Uses` only when registration completes.

`Purpose` and `IssuanceSource` are nullable columns. Existing rows remain null; their purpose/source is not inferred from `RequestId` or audit history. Existing legacy issuance and request-approval routes remain unchanged and continue to create rows without these fields.