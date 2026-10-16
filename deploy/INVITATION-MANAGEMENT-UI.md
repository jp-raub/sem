# Invitation management UI

## Scope and access

`/invitations` is an authorized, interactive-only page with static prerendering disabled. The navigation link uses a full-document load so the module is initialized for each page visit. It shows only the invitation/request records returned for the signed-in user. Administrators may directly issue an invitation with a purpose, usage limit, expiry, and explicit confirmation, and revoke an existing invitation with a reason and explicit confirmation; the server remains authoritative for authorization. An issued invite link is shown only in a temporary read-only field after a successful response, and copying requires an explicit click.

Creating or approving requests and automated link distribution are not integrated. Secret links are not placed in anchors, list rows, query strings, browser storage, or logs. The UI does not guess historical purpose/source information absent from the API; null values are labelled as unrecorded. It does not claim that a page contains every record: invitations and requests are fetched independently, 50 of each per page, and share one page number. The endpoint accepts page numbers only through 10000. Previous/next navigation is available while either `hasMoreInvitations` or `hasMoreRequests` is true, except that next-page navigation is disabled at page 10000; if either flag is still true there, the UI explicitly states that additional records remain beyond this screen's API page limit.

For issuance-specific endpoint, confirmation, link-handling, and uncertainty details, see [Invitation issuance UI](INVITATION-ISSUANCE-UI.md).

## Read-only inventory

The page calls `GET /api/core/invitations?page=N` (the UI explicitly requests page 1 initially), and requests only pages 1–10000. It validates the returned actor against subsequent page responses, `pageSize: 50`, both `hasMore` flags, and the two bounded arrays. At page 10000, a true `hasMore` flag disables Next and produces a clear API-limit notice; it does not request page 10001 or imply the remaining records were retrieved. Timestamps are Unix seconds and are displayed in Japan Standard Time. Missing or blank display names appear as `不明`. Rows contain only documented IDs, names, nullable purpose/source, counts, dates, states, and recorded reasons; null purpose/source is shown as unrecorded. Rows never contain email values, tokens, or invite links.

All API calls use same-origin credentials and mode, `redirect: "error"`, `cache: "no-store"`, a 25-second abort bound, and fatal UTF-8 JSON parsing. The inventory response is capped at 512 KiB; the CSRF response at 16 KiB; mutation responses at 4 KiB; and JSON request bodies at 4 KiB. Limits are checked on bytes before parsing. If an inventory response exceeds its cap or is malformed, no partial data is parsed or applied, a safe error is shown, and row actions are locked until a successful explicit reload. Oversized or malformed mutation responses are treated as unknown outcomes and are never replayed automatically.

## Administrator revocation

The UI sends `POST /api/core/invitations/revoke?expectedActor=<exact actorId>` with exactly:

```json
{"id":"…","reason":"…","explicitlyConfirmed":true}
```

The reason is trimmed and must contain 1–500 control-free characters; the identifier must contain 1–128 control-free characters. The target row is snapshotted before the confirmation step. Changing the reason clears the checkbox, and selecting another row or refreshing the inventory clears the prior confirmation. The request uses a CSRF token from `GET /api/csrf` in `X-CSRF-TOKEN`. The UI presents the existing revocation reason and metadata as returned by the server; it does not fabricate server timestamps or actors from the mutation response.

Only the explicit confirmation submits the mutation. A 401/403 clears actor-bound state. A timeout, connection loss, malformed/oversized response, or other uncertain write outcome locks further administrator actions and is not automatically retried. Use the read-only page reload to inspect the selected ID: if the row is present, its server-returned `disabled` state reconciles the outcome; otherwise the lock remains until the relevant page is located and manually checked. An already-disabled invitation remains subject to the backend's idempotent behavior and its original reason is not overwritten.

## Rendering and checks

Untrusted names, reasons, IDs, and other API strings are inserted with `textContent` via DOM element creation, never raw HTML. The issued secret link is held only in the live page and its temporary read-only input value; it is not written to browser storage, the current URL/query/fragment/history, or logs. Copying is explicit. Module initialization is bound to a page owner/root, and disposal removes listeners and aborts in-flight requests; generation checks prevent stale responses from updating a successor page.

Run the focused synthetic UI checks with:

```sh
node --check src/Sem.Web/wwwroot/invitation-management.js
node --check scripts/invitation-management-ui-checks.mjs
node scripts/invitation-management-ui-checks.mjs
```

These checks exercise bounded list sizes, safe text rendering, both independent `hasMore` pagination boundaries at page 10000, explicit reason/confirmation, and unknown-write reconciliation. They do not replace the parent integration's .NET build, endpoint/CSRF tests, or host/browser acceptance.
