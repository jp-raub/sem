# Member profile: current registration history

The member profile continues to read its basic profile through the same-origin `/api/core/profile?id=...` endpoint. Registration history is opt-in: the viewer must press **Registration history**; it is not loaded with the profile, refreshed in the background, or retried automatically.

## Privacy and scope

- Both reads use the browser's current same-origin authentication cookie. The target user ID selects only the profile subject; it never selects or overrides the viewer. Core validates the live viewer session, target eligibility, and current event visibility on each request.
- `GET /api/core/profile-registration-history?id=<targetUserId>` accepts exactly one nonempty `id` of at most 128 characters without control characters. POST receives a handler-generated `405`; handled GET/POST responses are `Cache-Control: no-store`. Other unsupported methods receive a routing-generated `405`, whose headers are outside this handler contract.
- History is a snapshot of current registrations, not an audit log. Core first chooses at most the latest 100 current candidate events, then filters by the viewer's current event permissions. The result can contain fewer than 100 or zero events even when older visible events exist. It is not a complete history, attendance record, or deleted-log view.
- The response contains only the target ID and current event title/date, spectator state, and current song/part display fields. It does not include email, answers, authentication/session values, hidden-event counts, totals, or cursors.
- The client accepts at most 128 KiB for a basic profile and 4 MiB for history, decodes UTF-8 strictly, aborts a request after 25 seconds including stream reading, rejects redirects, and fails closed on malformed or oversized responses. A failed/oversized history read is not presented as a complete snapshot.
- A `403` clears both profile and history from the UI. The user can explicitly refresh, which uses the current cookie again. Route changes and profile refreshes clear old history and stale responses cannot update the new profile.

## Local checks

Run the browser transport checks with `node scripts/profile-history-transport-checks.mjs` and the loopback-only HTTP checks with `dotnet run --project tests/Sem.ProfileRegistrationHistoryHttpChecks/Sem.ProfileRegistrationHistoryHttpChecks.csproj`. The HTTP checks use a temporary migrated SQLite database, synthetic cookies, and a loopback ephemeral port; they do not call production or external services.

Before release, independently verify the rendered profile at 390px, public and hidden event scope, actor-cookie changes returning `403`, target-ID changes with delayed responses, escaped profile/event/song text, and the explicit-only load behavior in a real Blazor browser.
