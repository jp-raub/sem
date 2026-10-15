# Profile images (backend)

English is authoritative; `PROFILE-IMAGE.ja.md` is the paired Japanese translation.

Profile images are opt-in, owner-managed, and stored in a dedicated `ProfileImages` SQLite table keyed by user ID. They do not alter `UserProfiles`, basic profile edits, authentication/passkey data, or QR expiry/revocation state. The existing authorization paths for profile editor, member profile, and valid guest QR projection decide whether a normalized image is included in `ProfileView`; there is no public image-ID route.

## HTTP contract

- Upload: `POST /api/core/profile-image?expectedActor=<current-cookie-actor>&rowVersion=<image-version>`, raw request body, exact `Content-Type` of `image/png`, `image/jpeg`, or `image/webp`.
- Delete: `POST /api/core/profile-image-delete?expectedActor=<current-cookie-actor>`, strict JSON body `{ "rowVersion": n }`.
- Both require same-origin `Origin`, the existing antiforgery header/cookie, and a currently authenticated, enabled, email-confirmed actor with current security stamp, current session claim, and no revoked-session record. These checks (plus strict query validation) occur before reading request bodies. The actor must match the cookie subject; clients cannot choose another user.
- Upload accepts at most 10 MiB; delete JSON is capped at 64 KiB. Only two image requests may hold upload slots; a busy request returns 429 before body consumption. Kestrel's action-specific limit leaves 16 KiB of framing headroom; the streaming reader enforces the actual cap.
- Both operations compare the independent image row version and save the mutation plus a non-secret audit record transactionally. Responses are `{ "rowVersion": n, "width": n, "height": n }`; deletion returns zero dimensions. Basic profile PUT does not modify image state.

## Normalization and storage

The server checks MIME and magic bytes; it never accepts a path, URL, SVG, GIF, or other format. PNG APNG animation-control chunks and WebP animation chunks/flags are rejected before pixel decode; image-list length is also limited to one. ImageMagick is initialized with a restrictive coder/module policy (PNG/JPEG/WebP read, PNG write only; all delegates disabled), native memory/disk/area/dimension/list/thread/time/profile limits, a 24-megapixel pre-decode cap, and bounded non-queueing upload/decode semaphores.

A single validated raster is EXIF-auto-oriented, stripped of profiles/comments/metadata (including GPS), downscaled only when its long edge exceeds 512 px, aspect-preserved without upscaling, and encoded as PNG. The result must be at most 1 MiB. Only normalized PNG bytes and dimensions are persisted; original input is memory-only and is never written to a file or included in logs/audit. A deleted image remains an empty tombstone with zero dimensions and a strictly increased image row version, preventing delete/recreate ABA conflicts. `ProfileView` carries nullable `ImageDataUrl` (normalized PNG only) and independent `ImageRowVersion`; the base64 projection is bounded by the stored 1 MiB limit.

## Migration and runtime

The additive `20261015153000_AddProfileImages` migration creates only the new per-user table and restrictive user foreign key. The migration designer and EF model snapshot include the corresponding `ProfileImage` entity; no existing profile/passkey row is rewritten. Magick.NET-Q8-AnyCPU 14.17.2 supplies a Linux x64 native asset inside the NuGet package, so the current .NET 10 Linux Docker runtime does not need an extra ImageMagick OS package. This was verified from the restored package's `runtimes/linux-x64/native` asset; still run the Docker image smoke check before deployment. Runtime migrations/database backups retain the existing deployment procedures.

Dedicated checks: `dotnet run --project tests/Sem.ProfileImageChecks` (image formats, size, orientation/scale, output and animation/malformed rejection) and `dotnet run --project tests/Sem.ProfileImageHttpChecks` (synthetic authorization, pre-body denial, CSRF/Origin, version/rollback, profile projection and guest expiry). No real SMTP/Push, public image hosting, external URL ingestion, production operation, or deployment is part of these checks.
