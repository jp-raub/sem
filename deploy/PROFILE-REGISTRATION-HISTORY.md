# Profile registration history (Core read-only)

`CoreFoundationService.ReadProfileRegistrationHistoryAsync(ClaimsPrincipal principal, string userId, CancellationToken ct)` returns a minimal current-state view for the requested enabled, email-confirmed member. The caller must have a valid current authenticated session. The response contains the target user ID and visible events with event ID, title, scheduled date, spectator state, and current song/part title, name, and category. It does not include email, profile answers, tokens, device details, or administrative identities.

## Scope and visibility

- This is a bounded recent view, not complete history. It first selects at most the 100 most recent candidate events by scheduled date descending, then event ID ascending for deterministic ties. The 100-event limit is applied **before** viewer-permission filtering; hidden or misconfigured candidates can therefore leave fewer than 100 results, including an empty result even when older visible registrations exist.
- Only after that window is fixed does the service apply the caller's current `ParticipantEvent` access rules, including current session validation, exactly one active phase, the effective manual or automatic phase, and current event-manager/global-admin privileges. A target's manager role does not authorize the viewer. Unauthorized and misconfigured candidates are omitted without returning their IDs, titles, counts, totals, or other metadata.
- The response deliberately has no total, `hasMore`, cursor, or hidden-candidate count. Clients must not present it as a complete registration history.
- Returned registrations are current state only: current `SongParts.AssignedUserId` assignments joined to non-deleted songs and event-local non-deleted part definitions, plus current spectator rows. The read does not reconstruct released registrations, deleted songs/definitions, attendance, or past phase visibility.
- A spectator and performer in the same event are represented by one event item with `Spectator: true` and the current part list.

## Read-only guarantees

The operation uses a database read transaction and no-tracking projections. It does not call `SaveChanges`, repair stale phase caches, observe automatic-phase transitions, or alter registration or audit state. It does not use the broader participation DTO, which could include unrelated/private form data.

This adds a Core API and dedicated synthetic SQLite checks only. It does not add HTTP endpoints, UI, schema, or migrations.
