# Invitation issuance UI

## Scope

Direct invitation issuance is integrated into the existing authorized `/invitations` management page. The UI only enables the issue controls after the server-returned actor is confirmed as an administrator. The backend remains authoritative for authorization, limits, expiry, purpose validation, and issuance.

Request creation and approval, request-driven issuance, and automated link distribution are not implemented in this slice. Existing inventory pagination and administrator revocation remain in place; see [Invitation management UI](INVITATION-MANAGEMENT-UI.md).

## Issuance request

The page sends the following only after the administrator checks the explicit confirmation box:

`POST /api/core/invitations/issue?expectedActor=<exact actorId>`

It obtains a current CSRF token from `GET /api/csrf` and sends it in `X-CSRF-TOKEN`. The JSON body contains exactly:

```json
{"purpose":"…","count":1,"days":1}
```

The purpose is trimmed and must contain 1–500 characters without C0/C1 control characters. The usage limit is an integer from 1 through 5, and expiry is an integer from 1 through 7 days. Both numeric fields default to 1. Changing purpose, count, or days clears confirmation and any previously displayed link. The request snapshots the confirmed values and actor; stale owner/root/generation results are discarded.

The success response must have exactly these fields: `actorId`, `id`, `purpose`, `issuanceSource`, `limit`, `uses`, `created`, `expires`, and `link`. The UI verifies the actor, identifier, normalized purpose, `issuanceSource: "Direct"`, requested limit, zero uses, Unix-second timestamps, and a same-origin `/join#invite=…` link with no query string. Invalid or oversized replies are treated as uncertain write outcomes, not as a successful issuance.

## Temporary secret-link handling

After a fully validated success response, the secret link is held in page memory and displayed only in a temporary read-only field. It is not rendered as an anchor, added to an inventory row, copied automatically, written to browser storage/logs, or put in the current page's query, fragment, or history. The administrator must explicitly click Copy; if clipboard access is unavailable, the field is selected for manual copying. Reload, actor change, root disposal, and the synchronous `pagehide` handler clear the link. `pagehide` also clears the rendered actor/list and confirmations, invalidates in-flight UI operations, and aborts active requests. A BFCache `pageshow` does not restore the link or confirmation: it performs only a fresh read-only page-1 actor/list request before writes can be enabled. Stale POST or clipboard completions cannot repopulate the restored UI. A clipboard write already explicitly handed to the browser API cannot be recalled, but its late completion is ignored. Do not treat the displayed value as a permanent record; the invitation record itself does not contain the secret link.

The invitation list displays the API's nullable `purpose` and `issuanceSource` fields. A null or blank value is labelled “未記録”; the UI does not infer historical values. The list never displays invite tokens or links.

## Uncertain outcomes and limits

The issue endpoint has no idempotency guarantee. Once the issue POST may have started, a timeout, connection loss, malformed/oversized response, or other uncertain result must not trigger an automatic replay. The UI clears any displayed link, locks administrator mutations for the current page instance, and leaves only explicit read-only inventory refresh available. A refreshed list cannot conclusively identify an issue with no returned ID, so the lock remains and the UI does not claim to reconcile or retry it. A BFCache hide/restore preserves that unknown-outcome lock and performs no write; a full document reload starts a new page instance and clears page-memory state, so an operator must manually review the inventory and reconcile before issuing again. A 401/403 clears actor-bound state and the temporary link; it never causes a retry.

All requests use same-origin credentials/mode, `redirect: "error"`, `cache: "no-store"`, bounded timeouts, fatal UTF-8 JSON parsing, and byte limits. The server remains responsible for CSRF/origin checks, actor matching, authorization, and request validation. User/API text is rendered with `textContent`, never raw HTML.

## Stable UI hooks

- `invitation-issue`
- `invitation-issue-heading`
- `invitation-issue-form`
- `invitation-issue-fields`
- `invitation-issue-purpose`
- `invitation-issue-count`
- `invitation-issue-days`
- `invitation-issue-confirm`
- `invitation-issue-submit`
- `invitation-issue-result`
- `invitation-issue-result-heading`
- `invitation-issue-link`
- `invitation-issue-copy`
- `invitation-issue-status`
- `invitation-issue-error`
- `invitation-issue-copy-status`

The general management status/error and unknown-outcome alert remain the existing page hooks.

## Checks

The synthetic UI suite includes issuance parameter/confirmation validation, exact request shape and actor binding, temporary-link rendering/copy behavior, root/actor cleanup, unknown-outcome read-only reconciliation without replay, and `pagehide`/BFCache `pageshow` checks for secret cleanup, stale deferred POST/clipboard completion, listener disposal, and read-only restoration. The parent integration must run:

```sh
node --check src/Sem.Web/wwwroot/invitation-management.js
node --check scripts/invitation-management-ui-checks.mjs
node scripts/invitation-management-ui-checks.mjs
```

These checks do not replace endpoint/CSRF tests, a .NET build, or host/browser acceptance.