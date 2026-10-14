# Completed and cancelled receipt history

A read-only event-manager panel pages all retained manual/QR receipts and their cancellations, independently of QR read history. No migration or change to reception write/replay rules is introduced.

## Scope and current meaning

Every page requires a CURRENT valid session and event management permission. Removed managers, forged roles and revoked sessions are denied. A withdrawn participant's existing receipts remain manager-readable as historical records and are labelled with the current registration snapshot; this does not restore registration or contribute to current received counts. The panel has no mutation action. Use the existing fresh participant confirmation for cancellation/reception operations.

## Paging and filters

Rows are ordered by original receipt time and ID descending, not cancellation time. Pages contain at most 100 rows, with timestamp/ID keyset continuation. Next/back/latest and an optional subject filter reset or preserve cursor stacks explicitly; event changes/disposal invalidate late results. Current active receipt on the original target is still fetched independently of bounded history.

A historical row displays subject, method, receipt server time/actor, encoded note, cancellation time/actor when present, and whether a source QR read association exists. It does not include raw QR tokens, hashes, email, private answers, social details or authentication material. Server responses are no-store; POST and incomplete/invalid cursors are rejected.

## Acceptance

Dedicated checks cover equal-time pagination, exact complete coverage with no duplicate IDs, current membership vs withdrawn histories, manager/session boundaries, user filtering, privacy and no read-side writes/model changes. Native localhost tests intentionally create completed/cancelled synthetic receipts and exercise actual mobile first/next/back, subject/all filters and encoded notes without changing current completion totals. Full-chain CI timeout is 40 minutes to include paced real mutation fixtures; rate limits are not relaxed.

Physical DXP/browser acceptance, delivery and merge remain separate. No real database, .env, data/key volume or deployment is changed.
