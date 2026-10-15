# Event import: public Google Spreadsheet CSV

## Behavior and scope

The existing event-import page can explicitly fetch a publicly accessible Google Spreadsheet as CSV and place the result into its existing CSV input. This is a browser-only, read-only source step: it does not preview or append anything. The manager must inspect the loaded text, run the existing preview, and separately confirm an append. The URL and CSV remain in page/component memory; neither is put in browser storage, the address bar, logs, nor a server request.

Only HTTPS URLs on the exact `docs.google.com` host are accepted, using spreadsheet edit/export URLs or published `pubhtml`/`pub` URLs. Regular sheets are normalized to `/spreadsheets/d/{id}/export?format=csv&gid={gid}`; published sheets are normalized to `/spreadsheets/d/e/{id}/pub?output=csv&gid={gid}`. A missing tab identifier defaults to `0`. A decimal `gid` up to `2147483647`, including one in a `#gid=...` fragment, is supported. Known `usp=sharing` and `usp=drive_link` decorations are discarded. Duplicate parameters, unknown query parameters/fragments, credentials, non-default ports, encoded/traversal paths, other hosts and other schemes are rejected. Arbitrary query parameters are never forwarded.

The browser fetch uses CORS, omitted credentials, no referrer, no cache, `redirect: error`, and a 15-second abort limit. The response must be a successful CSV/text response; redirects and HTML/login pages fail closed. Both declared and streamed bytes are bounded to 1 MiB and UTF-8 decoding is fatal. The submitted URL is never fetched as-is. There is no server-side fetch or proxy, so this change introduces no server-side URL-fetch/SSRF path.

## Limitations and fallback

This works only when Google makes the selected tab anonymously exportable and the browser permits the cross-origin response. A sheet requiring Google sign-in, OAuth, or an authenticated session is unsupported; CORS restrictions or provider redirects may also prevent retrieval. No credentials, API keys, cookies, or auth parameters are requested or sent to Google. On failure, the existing local CSV file selection or paste input remains available, and an earlier CSV value is preserved. Fetching a new source immediately invalidates any old preview/confirmation, but never starts preview or append automatically.

## Checks

Run `node scripts/event-import-spreadsheet-checks.cjs` for deterministic URL-normalization, rejection, mocked-fetch policy, byte-limit, content-type, HTML, UTF-8, redirect, and timeout checks. Tests use mocked responses only; they do not contact Google or claim live-sheet compatibility. Parent integration is responsible for the actual browser run and fresh .NET/CI validation.