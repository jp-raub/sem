# Event CSV import UI

## Scope

The event management list in `/workspace` now links managers to `/events/{id}/event-import`. The page accepts UTF-8 CSV by file upload or paste (maximum 1 MiB), and can fetch an anonymously published Google spreadsheet CSV directly from the browser. It retrieves the current event/actor/title context from the authenticated `event-import-context` endpoint, and pins that actor for all preview and apply requests. The server rechecks current authorization and the expected actor; browser state is not authorization.

Preview is read-only. It shows row dispositions, per-row and whole-preview errors, add/skip counts, projected end time, and any planned-end overrun. An apply is enabled only after a valid preview, an explicit confirmation, and (when overrun is positive) a separate overrun acknowledgment. Apply uses the existing atomic `event-import-apply` operation and its receipt/replay contract. Only added rows are appended; no existing row is updated.

## Safety and recovery

- File bytes and Google responses are capped at 1 MiB and decoded with fatal UTF-8 handling. Pasted text remains in the browser textarea; owner-scoped JavaScript sends only revision/input-validity metadata to .NET. On explicit preview, the browser validates Unicode and byte length, snapshots UTF-8 bytes, and transfers them through a bounded `IJSStreamReference`; the server applies a 25-second read timeout. Local file bytes use the same bounded stream path. The global SignalR receive-message limit is not raised.
- CSV text, preview, frozen operation, and receipt remain in transient browser/component memory only. No browser storage, raw-input logging, server-side spreadsheet proxy, or automatic retry is used. Google URLs are restricted to public CSV export routes on `docs.google.com`.
- A request ID is a canonical lowercase GUID, created once after confirmation. The event, actor, row version, fingerprint, CSV, and confirmation flags are frozen in the pending operation.
- Network errors, server errors, and malformed success responses are treated as unknown. The same frozen operation can be explicitly retried; a new operation cannot replace it. A definitive initial 400/409 clears the operation and requires a fresh preview. If an earlier attempt was unknown, later 400/409 responses do not clear it. A 403 hides the private composer and preserves the pending identity; reauthenticate as the same actor before recovery.
- Dirty input and unresolved operations block internal navigation and trigger external-navigation/reload warnings. Forced reload can lose memory-only input and operation data; do not reload or close when the result is uncertain.
- Rendered CSV values and errors are ordinary escaped text. The UI provides keyboard focus indicators and touch-sized controls, including narrow layouts.

## Verification and limitations

`tests/Sem.EventImportComposerChecks` covers preview/confirmation gating, separate overrun acknowledgment, frozen identity/payload, unknown-result retention across retries and later rejections, receipt validation, explicit new-operation reset, and fresh-preview recovery. `scripts/event-import-local-stream-checks.cjs` exercises compact callbacks, 64 KiB/1 MiB paste and file streams, over-limit input, invalid Unicode/UTF-8, and listener cleanup with synthetic browser controls. Real-browser acceptance and the .NET/CI build are handled separately; this change performs no shared build or production deployment.
