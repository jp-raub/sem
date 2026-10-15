# Member profiles, optional profile questions, and 24-hour QR sharing

English is authoritative; see MEMBER-PROFILE.ja.md for the paired translation.

The existing authenticated member profile remains at `/profiles/<opaque user ID>` and keeps its current profile and registration-history transports. Enabled, email-confirmed members can see the target's current display name, normalized reading, call name, emoji icon, explicit social links, and profile-question responses. Email, authentication/session data, invitation data, and registration history are not included in the profile QR projection. The existing registration history remains a separate authenticated request and is never reachable through a QR bearer.

## Profile questions and answers

Global administrators manage service-wide profile questions through the profile settings page. A question is free text (`Text`, at most 200 characters) or yes/no (`YesNo`); there is no required-answer flag. The type is immutable after creation. Text edits, stopping and reordering are allowed even after responses exist. Stopping a question hides it from future answer editing but retains existing answers. Deletion is refused while any answer exists. Member answers are optional and limited to 1000 characters for free text; yes/no values are constrained to `YES` or `NO`. Current answers are automatically visible to enabled, email-confirmed members on the existing profile page. Stopped questions and their retained responses are omitted from member and guest projections, matching the approved active-question display; stored answers still prevent deletion.

The profile page also accepts one emoji icon. Image upload is deliberately not implemented: the image-storage, retention, backup and recovery policy remains undecided. The icon is rendered as ordinary encoded text.

## Anonymous profile QR

The profile owner alone can issue or reissue a QR from `/profile/settings`. The read-only `GET /api/core/profile-qr` returns status only. Issuance/reissue is a CSRF- and Origin-checked `POST`, bound to the current cookie actor and a row version. Each issue creates a cryptographically random bearer with an exact 24-hour lifetime; reissue atomically replaces the stored hash/version and immediately invalidates the previous token. Reads do not extend expiry. The database stores only a SHA-256 hash, never the raw bearer.

The generated QR points to `/profile/share#token=…`. The fragment is not sent in the initial page request. The page removes it from browser history before sending a same-origin, CSRF-protected POST body to resolve the token. No token is accepted in an API query; the owner-only SVG GET receives the bearer in an HTTP header, not the request URL. QR and profile responses are no-store, use `Referrer-Policy: no-referrer`, and the share page loads no third-party assets. The owner UI keeps the raw token only in memory; after reload, an active QR can be replaced by explicitly reissuing it.

On each bearer read, the server checks the current expiry and that the target account is still enabled and email-confirmed, then reads the current profile and current answers. The QR grants only current basic-profile fields, answers and saved social links. It never grants email, invitation/authentication secrets, registration history, event access, check-in, or account actions. Deactivation or email-unconfirmation prevents further QR reads. Social links are not fetched or embedded.

## Persistence and verification scope

An additive EF migration adds the emoji field, question/answer records, and hash-only QR credential with uniqueness, foreign keys, and validation constraints. Existing profile rows receive an empty icon; existing social/profile data and event/authentication tables are not rewritten. `ApplicationDbContextModelSnapshot` and the migration designer are kept aligned with the model.

Standalone synthetic Core and HTTP checks use temporary SQLite databases, synthetic cookie principals, CSRF/Origin headers, and a no-op Push transport. They cover authorization, immutable types, answer limits/retention, QR expiry/reissue, owner-only SVG, fragment/body transport, and exclusion of email/history. These checks do not establish real-browser, multi-device, PostgreSQL, production-proxy access-log, real SMTP/Push, or deployment acceptance. Do not enable raw request-body logging on the QR resolver; infrastructure access logs must not record bearer headers or request bodies. Run the isolated synthetic suites with `dotnet run --project tests/Sem.ProfileExtensionChecks` and `dotnet run --project tests/Sem.ProfileExtensionHttpChecks` (with `PATH=/data/dotnet:$PATH`, `DOTNET_ROOT=/data/dotnet`, and the verified `NUGET_PACKAGES` setting in this workspace). No production endpoint, SMTP, Push, external hostname, key, volume, shared browser, or deployment is changed by this increment.

QR resolve responses carry a validated expiry header. The browser clears old content before a new fragment token, ignores stale responses, and clears displayed content at expiry; server expiry checks remain authoritative.

HTTP profile editing accepts up to 1 MiB of strict JSON after current-session authorization; other new profile writes retain a 64 KiB bounded reader. SNS JSON retains the existing 32,000-character cap. Browser JSON reads are strict-UTF8 streams capped at 2 MiB (CSRF 64 KiB) with 25-second request budgets; editor auth failures clear the prior profile and QR display.

Issuance/reissuance and their actor/target audit are saved in the same transaction; audit contains no bearer. Editor requests and callbacks are owner-generation scoped, aborted/ignored after teardown, and late module imports are disposed rather than initialized.
