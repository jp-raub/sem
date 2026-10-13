# Actual participant QR image and self-service page

This increment follows the event code/scan ledger APIs. It adds a real PNG QR image, authenticated self-service page and workspace link. **Camera, paste/direct-link reading UI and read-linked QR-method reception completion are still unfinished.** A QR image can be decoded externally, but the current reception route does not automatically consume its fragment or mark anyone received. Manual receipt remains separately confirmed.

## Payload and authenticated image

QRCoder is pinned to1.7.0. Its Linux-safe PngByteQRCode renderer (no System.Drawing) encodes the configured public origin and `/events/EVENT/reception#code=CODE` with ECC M and quiet zone. Event ID is URL-encoded. The code is in the fragment, never the server query, so following a link does not put the code into the HTTP request/access log or make a scan write. No external QR generation service receives it. The generated image itself is sensitive and must not be publicly shared.

GET `/api/core/event-qr-image?id=EVENT&version=VERSION` uses the same current authorized participant/manager code read and mandatory matching positive version. An image read never creates/reissues a code. Unknown/inaccessible/unregistered targets cannot fetch one; another-user query requires current event management. Stale version produces conflict instead of a mismatched image. PNG response is no-store and contains no script/raw HTML. Previously viewed/saved images cannot be recalled; reissue invalidates their code for future new scans.

## Explicit issuance UI

`/events/EVENT/qr` displays only the current authenticated registered person's image. An initial visit loads state without issuing anything; explicit first-issue button generates the code. Reissue opens an explicit old-code invalidation confirmation, and backing out does nothing. The page never renders the raw code/link as text, avoids secret-bearing image URLs and uses an accessible alt label with a mobile-scaled white QR area.

The full request and ID are captured before submission. While result is uncertain, competing actions are disabled and no QR image is shown; only explicit identical-ID retry is offered. A historical issuance result is not a rollback: the UI re-reads current code/version. Route/reload/authorization or validation conflict clears the private view and requires current-state retrieval; stale responses are ignored using a route epoch. No automatic submission is added.

## Verification and boundaries

Five dedicated renderer/payload checks and independent jsQR1.4.0/pngjs7.0.0 PNG roundtrip verify actual bytes decode to the exact configured link. Native acceptance verifies authorized no-store PNG, fragment-only decoded payload, owner/manager scope, stale versions, actual mobile first issuance/reissue/back-out, no raw code text, unknown-route clearing and no runtime errors. These are synthetic local/native and CI checks, not physical camera/iOS/Safari/DXP acceptance. Full current regressions and fixed-source CI are confirmed separately.

No new DB migration is needed. Existing protected tokens, read ledger, manual receipt history, registration/identity/notifications and origin/CSRF/session/rate guards are preserved. Source package versions are explicit; no remote CDN or third-party image endpoint is added. Remaining: real camera/paste/direct-link management surface, current-code recheck/read-linked QR receipt, full receipt/read history/roster UI and recovery/edit-mode integration. No DXP deployment, new bundle or merge.

The page also identifies the current event and registered owner with encoded text to avoid wrong-event/account confusion.


The next API increment is documented in [QR-RECEPTION.md](QR-RECEPTION.md). It adds confirmed read-linked receipts; camera/reader and QR confirmation UI remain separate unfinished work.
