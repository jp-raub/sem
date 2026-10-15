# Event CSV import UI

## Scope

The event management list in `/workspace` now links managers to `/events/{id}/event-import`. The page accepts UTF-8 CSV by file upload or paste (maximum 1 MiB), retrieves the current event/actor/title context from the authenticated `event-import-context` endpoint, and pins that actor for all preview and apply requests. The server rechecks current authorization and the expected actor; browser state is not authorization.

Preview is read-only. It shows row dispositions, per-row and whole-preview errors, add/skip counts, projected end time, and any planned-end overrun. An apply is enabled only after a valid preview, an explicit confirmation, and (when overrun is positive) a separate overrun acknowledgment. Apply uses the existing atomic `event-import-apply` operation and its receipt/replay contract. Only added rows are appended; no existing row is updated.

## Safety and recovery

- File bytes are capped before decoding and decoded with fatal UTF-8 handling. Pasted text is checked for valid UTF-16/UTF-8 conversion and the same byte cap.
- CSV text, preview, frozen operation, and receipt remain in component memory only. No browser storage, raw-input logging, external-URL fetch, or automatic retry is used.
- A request ID is a canonical lowercase GUID, created once after confirmation. The event, actor, row version, fingerprint, CSV, and confirmation flags are frozen in the pending operation.
- Network errors, server errors, and malformed success responses are treated as unknown. The same frozen operation can be explicitly retried; a new operation cannot replace it. A definitive initial 400/409 clears the operation and requires a fresh preview. If an earlier attempt was unknown, later 400/409 responses do not clear it. A 403 hides the private composer and preserves the pending identity; reauthenticate as the same actor before recovery.
- Dirty input and unresolved operations block internal navigation and trigger external-navigation/reload warnings. Forced reload can lose memory-only input and operation data; do not reload or close when the result is uncertain.
- Rendered CSV values and errors are ordinary escaped text. The UI provides keyboard focus indicators and touch-sized controls, including narrow layouts.

## Verification and limitations

`tests/Sem.EventImportComposerChecks` covers preview/confirmation gating, separate overrun acknowledgment, frozen identity/payload, unknown-result retention across retries and later rejections, receipt validation, explicit new-operation reset, and fresh-preview recovery. This change does not claim a completed .NET build, CI run, or real-browser acceptance; run the dedicated checks and parent integration/build after the shared HTTP work is stopped. No production deployment is performed. External spreadsheet URLs are not fetched; users must provide a local CSV file or paste its contents.
