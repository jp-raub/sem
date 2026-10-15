# Authenticated CSV import sample download

This adds a read-only Core endpoint that lets an authenticated event manager download a starter CSV for an event. It does not import or modify event data.

## HTTP contract

- `GET /api/core/event-import-sample?id=<event-id>` returns UTF-8 CSV as `text/csv; charset=utf-8` with the constant attachment name `event-import-sample.csv`.
- The `id` query parameter must occur exactly once and be non-empty. The current cookie principal is authorized through the existing `ParticipantEvent` check; only an event manager or workspace admin can download it. No actor supplied in the request is trusted.
- `POST` and other non-GET methods return `405` before body handling. GET does not need an antiforgery token.
- Handler responses (GET and POST) are `Cache-Control: no-store` and include `X-Content-Type-Options: nosniff`.

## Template content and safety

The fixed columns are `Kind,Title,Artist,Duration,Prep,Parts`. The examples are benign fixed mock values: a song (4:30, 0:30 preparation), a transition (10:00), and another song (1:02:03, 0:00 preparation). If the event has a current, active local part definition that is representable and spreadsheet-formula-safe, the first eligible name in stable `(SortOrder, Id)` order is used in both song rows. The part cell is escaped as CSV, including commas and quotation marks.

A name is eligible only when it is trimmed, 1–40 UTF-16 code units long, starts with a letter or digit, and contains no pipe, carriage return, line feed, or control character. Selection examines at most 100 current definitions in stable order (the normal event-definition limit). This avoids formula-leading characters without rewriting a real definition name. If no definition qualifies, the response contains the fixed header and transition example only; it never invents a part name. The template does not export existing songs, events, tokens, users, or other records.

This endpoint performs no writes, audit logging, import, or history repair. It deliberately does not apply import eligibility/history checks: downloading a template is not an import. The generated CSV is only a convenience template: current import history, plan editability, duplicate keys, and other preview/apply eligibility checks are not evaluated, so downloading it does not promise that a later preview or import will be accepted. It uses only current event authorization and a read of active local part definitions; it has no external fetch, browser UI, or deployment side effect.
