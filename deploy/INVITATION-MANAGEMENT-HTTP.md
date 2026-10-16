# Invitation inventory and revocation API

This API adds a safe inventory read and administrator-only reasoned revocation. It does not issue invitations, create or decide requests, consume tokens, or change registration behavior. No schema change is required.

## `GET /api/core/invitations`

The optional query is one `page` integer from 1 to 10000; omitted means page 1. Each list has a fixed page size of 50. The service probes 51 rows for invitations and requests independently to compute their respective `hasMore` flags. Ordering is `Created` descending, then `Id` ascending. Results are `Cache-Control: no-store`.

Response shape:

```json
{
  "actorId": "…",
  "isAdmin": true,
  "page": 1,
  "pageSize": 50,
  "hasMoreInvitations": false,
  "hasMoreRequests": false,
  "invitations": [],
  "requests": []
}
```

Invitation rows contain only `id`, `ownerId`, `ownerName`, `issuerId`, `issuerName`, `requestId`, `limit`, `uses`, `created`, `expires`, `disabled`, `revokedAt`, `revokedBy`, `revokerName`, and `revocationReason`. Request rows contain only `id`, `userId`, `userName`, `reason`, `count`, `days`, `status`, `created`, `decidedAt`, `decidedBy`, and `deciderName`. Timestamp values are existing Unix-second numbers or `null`. Missing/blank display names are shown as `不明`.

An administrator sees all rows. A non-administrator sees only invitations where they are the owner and requests they created. Token hashes, protected tokens, invitation links, email values, and inferred historical purpose/source are not returned.

## `POST /api/core/invitations/revoke?expectedActor=<actorId>`

The body must contain exactly these properties, with no duplicate or unknown fields:

```json
{"id":"…","reason":"…","explicitlyConfirmed":true}
```

Only a live administrator may revoke. The expected actor ID must exactly match the authenticated actor, and the request must use the exact configured Origin and one non-empty `X-CSRF-TOKEN` header. Content length is capped before antiforgery validation; unknown-length streams are also bounded to 4 KiB. JSON is fatal-UTF-8, shallow, and type-checked. Form fallback is not accepted.

The reason is trimmed, 1–500 characters, and control-free. The ID is 1–128 characters and control-free. The service independently revalidates the live user, enabled/confirmed state, security stamp, session, administrator status, and explicit confirmation. A successful transition sets `disabled`, `revokedBy`, `revokedAt`, and `revocationReason` using server-derived values, and writes `InvitationRevoked` audit with the invitation ID. Update and audit are transactional.

If already disabled, the operation succeeds idempotently and preserves the original reason, actor, and time without another audit. Existing registration consumption and `Uses` accounting are untouched.

Known route patterns explicitly accept common HTTP methods so unsupported methods return 405 with `Allow`; unknown sub-actions return 404. All responses are `Cache-Control: no-store`.
