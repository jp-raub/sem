# Current-device push notification API

This slice manages the push subscription associated with the authenticated browser only. It does not send a test push or contact a push provider. There is no schema change; the service uses the existing `PushDevices` records and binds them to the current security stamp and authentication-session hash.

All responses use `Cache-Control: no-store`.

## `GET /api/core/device-notifications`

No query parameters are accepted. A live, enabled, email-confirmed session is required. The configured public VAPID key is returned only when push is configured.

```json
{"actorId":"…","pushConfigured":true,"pushPublicKey":"…"}
```

The response does not expose a private key or stored endpoint credentials.

## `POST /api/core/device-notifications/status?expectedActor=<actorId>`

Body: `{"endpoint":"https://…"}`. Returns `{"actorId":"…","registered":true|false}`. `registered` is true only when the stored endpoint is bound to this actor, current security stamp, and exact authenticated browser session.

## `POST /api/core/device-notifications/register?expectedActor=<actorId>`

Body:

```json
{"endpoint":"https://…","p256dh":"…","auth":"…"}
```

The `endpoint` must be HTTPS and on the existing push-service allowlist. `p256dh` must be canonical, unpadded base64url for a 65-byte uncompressed P-256 public point; the point is imported and validated on P-256, so off-curve points are rejected. `auth` must be canonical, unpadded base64url for exactly 16 bytes. Registration requires push configuration and binds the row to the current security stamp and hashed session. It does not send a push.

When registration would change an existing endpoint's user, stamp, or session binding, the request must present both exact currently stored `p256dh` and `auth` values. Both comparisons are fixed-time. Missing or mismatched key material rejects the request without modifying the existing record. Thus an explicit rebind requires possession of the actual subscription secrets; an endpoint URL alone cannot transfer ownership.

## `POST /api/core/device-notifications/unregister?expectedActor=<actorId>`

Body: `{"endpoint":"https://…"}`. The endpoint is removed only when actor, current security stamp, and exact session hash all match. A different user or another session of the same user cannot remove the row. A no-op unregister returns `registered:false`.

## Request protections

Action routes are POST-only; unsupported methods return 405 with `Allow: POST`. The root route is GET-only and unsupported methods return 405 with `Allow: GET`. Unknown actions return 404. Action requests require exactly one `expectedActor` query value matching the live actor, the exact configured `Origin`, one non-empty `X-CSRF-TOKEN` header, and JSON content. Form fallback is not accepted. The body is limited to 16 KiB before antiforgery processing and while reading unknown-length streams; UTF-8 decoding is strict and JSON has a shallow depth limit, exact fields, no duplicate or unknown properties, and string-only values.

The synthetic `Sem.DeviceNotificationChecks` project covers request guards, body limits, live actor/stamp/session validation, endpoint/key validation, on-curve P-256 checks, explicit same-user and cross-user rebind with matching secrets, transfer attempts without matching secrets, cross-session unregister rejection, and owner-only unregister. It uses synthetic data and does not contact a push provider.
