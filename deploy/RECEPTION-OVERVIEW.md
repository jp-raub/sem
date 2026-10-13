# Current reception overview and QR read-history pages

This builds on the optional camera intake. It adds no schema migration and does not change reception or registration policy.

## Current roster

The manager-only roster reports the full unique current registration population, received and unreceived counts, and the number matching the name/reading search. Search and the first-100 display limit never change the full denominators. A current active receipt contributes once, regardless of multiple same-song parts. Cancelled receipts and retained receipts of withdrawn participants do not inflate current completion. QR reads alone never count as reception.

Rows show unreceived or received, method, server receipt time and actor display name. Email, private answers, raw QR code and receipt notes are excluded from this projection. The target uses the same minimal current summary alongside its independently authorized detailed receipt.

Successful manual completion/cancellation refreshes the roster. Use explicit refresh for QR operations and other managers' changes; this is a current authorized snapshot, not an automatic realtime feed.

## QR read history

The separate manager-only read-history panel displays timestamp, target and reader names, and code version, with a maximum of 100 rows per page. Next/back use the existing server's descending timestamp/ID keyset cursor. Latest resets cursor navigation. Empty and bounded pages are labelled honestly. Every page read checks current session and event management; failure clears the visible projection. Event changes/disposal invalidate late results. This is QR read history, not completed reception history.

Individual completed/cancelled receipt history remains in the target confirmation panel and is still bounded to the latest 100. Full receipt-history pagination is a separate remaining task.

## Verification and boundaries

Dedicated tests cover cancellation, withdrawn registrations, duplicate song assignments, search and participants outside the first display page, authorization and privacy. Native synthetic localhost tests create deliberate repeat QR read records, verify actual mobile cursor navigation and manual receipt totals/refresh, and deny member access. Physical DXP/camera acceptance and new delivery remain separate. No real data, .env, deployment, merge or delivery changes are made.
