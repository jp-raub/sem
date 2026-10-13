# Explicit event Push rule arming

English reference; [Japanese translation](EVENT-PUSH-RULES.ja.md).

## Boundary

This increment adds current manager-only rule switches and read-only eligibility/expiry decisions for the two approved automatic notification kinds, `OpeningReminder` and `SongPreparation`. **DeliveryConnected remains false: enabling a rule does not yet enqueue or send Push.** Personal arbitrary-part Start opt-ins remain independent. No recipient/device/message/transport queue is created by this increment. Do not interpret `Eligible` as accepted/delivered/viewed.

The next dispatcher must use current authorization, current recipients, valid subscriptions and a durable device-level dedupe/lease. It must revalidate rule generation, current source and expiry immediately before the external send, cancel obsolete work, invalidate revoked bindings, use minimized lock-screen content and an authenticated same-origin deep link. This arming UI is not completion of those delivery requirements or a DXP deployment.

## Activation and expiry

Rules default OFF/version0 without database writes. An OFF→ON transition stores the exact millisecond activation instant and the current progress version, with a generation/audit in one transaction. Identical ON is a no-op and does not move the activation fence. OFF retains its generation and clears activation fields. Re-enabling captures a new fence: existing due entries/actuals are not released as historical notifications. Restart retains the fence.

Only due times **strictly after** activation are considered. A previous-part actual basis additionally needs progress newer than the captured progress watermark. Waiting actuals have no guessed time; already-started targets are ineligible. Planned future instants are separate from due eligibility. Expiry is exclusive: no delivery at/after the boundary.

- Opening: expires at opening, or event start when there is no opening.
- Song preparation: expires at the earlier of due+10min and latest preparation start; actual preparation/start/end already makes the target obsolete. Expiry arithmetic is bounded even near the representable maximum date.
- Missing/malformed rule or source/expiry fails closed. Merely reading eligibility never writes a queue or ledger.

Migration `20261013134737_AddEventPushRules` introduces event/kind primary keys, restrictive event foreign key, allowed-kind/version/activation-null checks and optimistic concurrency. Existing PR21 identities, personal settings, protected data and outboxes are retained.

## Surfaces

No-store GET and authenticated Origin/CSRF POST `/api/core/event-push-rules?id=<eventId>`; strict required body `kind`, `enabled`, `version`, `eventVersion` and no arbitrary subject. Current global/scoped manager access, active identity, SecurityStamp/session and latest event version are rechecked. Rule generation protects stale tabs and OFF/ON ABA updates. Invalid source cannot be armed. Changes and audit roll back together.

InteractiveServer `/events/{EventId}/event-push-rules`, linked from manager plan preview, has accessible switches, Japan-time activation/expiry, disabled controls before the live circuit is ready, and explicit no-send text. Failed/unauthorized reads clear saved-looking UI. EN/JA documentation is paired. No DXP/.env/keys/volumes/hostname, bundle or merge changes.
