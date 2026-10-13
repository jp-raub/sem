# Local camera QR intake

This adds optional camera intake to the existing event reception panel. It does not add a schema migration, automatically write a read, or complete reception.

## Operator flow

1. A currently authorized event manager explicitly starts the camera over HTTPS.
2. Grant video permission. Audio is never requested. Prefer the rear camera when available.
3. Local jsQR decodes frames at approximately four attempts per second with a maximum dimension of 640 pixels. Images are not uploaded and no CDN is used.
4. A valid current-origin/event QR link or 64-hex code stops all acquired tracks and fills the private password input. The operator explicitly records one read, checks the current participant and registration, and separately confirms reception.
5. Stop, hidden page, page exit and component disposal release tracks. Permission grants arriving after stop are discarded. Owner and attempt generations reject stale callbacks, including module loading and asynchronous decoder loading. Paste/link intake remains available when permission, device or playback fails.

The existing fresh server authorization, code validity, registration/version checks, immutable operation IDs and manual/QR mutual exclusion remain authoritative. Decoding alone does not prove a valid current registration.

## Dependency and privacy

Unmodified jsQR 1.4.0 is vendored in `wwwroot/vendor/jsqr-1.4.0.js`; the Apache-2.0 license is included beside it. Camera pixels and raw QR payloads stay in the browser. Do not log codes, upload images, or share test storage-state files.

## Verification boundaries

The lifecycle checks cover start/stop, delayed permission, invalid/successful decoding, denial and playback failure. The native test uses a synthetic PNG-backed continuously painted canvas stream and the actual local jsQR decoder, plus real localhost HTTPS/Blazor and synthetic passkey/SMTP fixtures. This is not physical camera acceptance.

User-approved local-only synthetic browser state snapshots may be used to debug isolated tests. Reject non-local origins/cookies, use mode 0600, never commit or disclose their contents, and delete after testing.

Physical iOS/Safari/Android camera behavior, background/lock, permission changes and the DXP reverse-proxy route still need acceptance before delivery. No DXP change, PR merge or new delivery bundle is performed by these checks.
