# Event Import HTTP Boundary

The event-import HTTP boundary exposes the existing preview and apply services only. It adds no sample-download route, UI, external URL fetch, production configuration, or database migration.

## Routes

Both routes are POST-only under the existing `/api/core/{action}` map. GET returns `405 Method Not Allowed`.

- `POST /api/core/event-import-preview?id=<event-id>` — body: `{"csv":"..."}`
- `POST /api/core/event-import-apply?id=<event-id>` — body fields, all required: `requestId`, `rowVersion`, `previewFingerprint`, `csv`, `confirmed`, `confirmOverrun`

The `id` query parameter must occur exactly once and be nonempty. No actor identifier is read or trusted from the query or body: the existing authenticated cookie principal is passed directly to the core service. Preview is read-only; apply delegates authorization, stale-version checks, receipt creation, and replay behavior to the existing core service.

## Request protections and limits

- The Origin is checked first, then the event ID and a lightweight read-only preauthorization verify the current cookie principal, session/security stamp, and event-manager access before antiforgery validation or any body handling. Preview/apply repeat authorization inside their own core transactions; this guards against time-of-check/time-of-use changes. The preauthorization deliberately does not apply import-history or phase eligibility checks, so an authorized manager can still recover an existing receipt after a phase change.
- JSON is strict: all declared fields are required and unknown fields are rejected. Preview accepts only `csv`; apply uses the existing strict `EventImportApply` model.
- The CSV parser limit remains 1 MiB of UTF-8 CSV. The HTTP envelope is bounded at 7 MiB to accommodate JSON escaping of that CSV plus the apply fields. After authorization and antiforgery validation succeed, both declared-length and streamed/chunked over-limit requests return `413`; the reader buffers no more than the cap plus one byte. Other Core POST actions retain their 65,536-byte limit.
- Invalid JSON or content type returns `400`; authorization/origin failures return `403`; stale apply conflicts return `409`.
- Every response handled by the Core map carries `Cache-Control: no-store`.

## Verification

Run the isolated real-loopback boundary checks from the repository root:

```bash
dotnet build tests/Sem.EventImportHttpChecks/Sem.EventImportHttpChecks.csproj -c Release
dotnet run --project tests/Sem.EventImportHttpChecks/Sem.EventImportHttpChecks.csproj -c Release --no-build
```

The checks use an ephemeral loopback Kestrel listener, synthetic cookie identities and antiforgery tokens, a temporary SQLite database, and a no-op push transport. They do not contact external endpoints or change production configuration.
