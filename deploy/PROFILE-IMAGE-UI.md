# Profile image UI

The profile editor supports an optional image alongside the existing emoji icon. The emoji remains editable and stored as profile text; a saved image takes visual precedence only while one exists.

## Selection and storage

- The file picker advertises PNG, JPEG, and WebP. The client accepts only those declared MIME types and files from 1 byte through 10 MiB; the server remains responsible for validating actual file contents and rejecting SVG, GIF, and animated images.
- Selecting a file never uploads it and never decodes or previews it. The UI shows only the selected filename and byte size. Upload requires an explicit button press.
- The server stores only its normalized PNG in the database: no dimension above 512 pixels, no more than 1 MiB, and metadata removed. Original uploads are not retained. The client sends the selected `File` directly with `fetch`; it does not copy the file through a Blazor/SignalR circuit.
- Saved images are rendered only from the existing authorized profile DTO's `imageDataUrl`, which must be an exact `data:image/png;base64,` URL with canonical strict Base64, a PNG signature, and no more than 1 MiB decoded. No public image URL, remote fetch, SVG, or HTML rendering is used. A saved image wins over the emoji for display; when no image is present, the emoji (or a neutral emoji placeholder) is shown.

## API and lifecycle

- Existing editor, basic-profile, and guest-QR DTOs expose `imageDataUrl` and `imageRowVersion`; the row version defaults to `0` when no image exists.
- Upload: `POST /api/core/profile-image?expectedActor=<cookie actor>&rowVersion=<imageRowVersion>`, with the selected file as the binary request body, its validated `image/png`, `image/jpeg`, or `image/webp` content type, and the current same-origin CSRF token.
- Delete: `POST /api/core/profile-image-delete?expectedActor=<cookie actor>`, with JSON `{ "rowVersion": <imageRowVersion> }` and the current same-origin CSRF token.
- A successful mutation returns `{ "rowVersion": number, "width": number, "height": number }`; the editor then reloads its existing profile DTO to display the server-normalized image or emoji fallback.
- Requests use the current same-origin authentication cookie, reject redirects, and have a 25-second bound. Component disposal aborts in-flight requests; owner-generation and current-root checks prevent stale responses from changing a remounted page.
- If a write's outcome cannot be confirmed, the UI does not replay the request or re-upload the selected file. It clears the selected file and locks profile writes until the user explicitly refreshes the profile to inspect current server state. A `401`/`403` clears the editor identity and image.
- The existing guest QR profile response carries the normalized image; no new QR endpoint or public image URL is added. QR expiry clears the entire guest profile DOM, including the image.

The editor, basic-profile, and guest-QR JSON response bounds are 4 MiB. The registration-history bound remains 4 MiB, and the CSRF response bound remains 64 KiB. Blazor profile/history transfers remain bounded JS streams; the global SignalR receive limit is unchanged.

## Checks

Run the synthetic browser-transport checks with:

```sh
node scripts/profile-image-transport-checks.mjs
node scripts/profile-history-transport-checks.mjs
```

Before release, verify in the approved browser fixture that a normalized 512×256 PNG renders at native dimensions, deleting it restores the emoji fallback, a changed authentication cookie returning `403` leaves no image visible, and guest-QR expiry removes the image along with the rest of the profile. No `dotnet`, shared-host, or production validation is part of these UI checks.
Images with excessive decoded dimensions or resource requirements can be refused even when their compressed size is within 10 MiB; the backend safety limits remain authoritative.
