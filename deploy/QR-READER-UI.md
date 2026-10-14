# Explicit pasted/direct-link QR reading and reception confirmation

This UI connects the authenticated read and confirmed receipt APIs to `/events/EVENT/reception`. The manual search/receipt controls remain separate. **Camera decoding remains a follow-up.** English is canonical and the Japanese companion is paired.

## Private input, no automatic operation

The client-only input module accepts a 64-hex code or an absolute URL for the current origin and exact current event reception path, with only `#code=CODE`. Different origin/event, credentials, query strings, malformed code and extra fragment fields are rejected. Code comparison normalizes hex case. No external generation/validation service or CDN receives it.

A direct link's fragment is immediately removed from visible URL/history while retaining browser history state. It is copied to a password-type input only after current manager authorization succeeds. Opening a link does **not** write a read or receipt. Neither module nor navigation initiates a server write. Invalid/inaccessible route, authorization error or route change clears private state. Raw code is never printed as page text, result message or secret-bearing HTTP query; input has autocomplete off, and clears after explicit read.

## Two deliberate operations

1. Press the explicit one-read button. The UI captures the code and operation GUID before submitting. The server resolves the current registered participant, records a single successful read, and the UI fetches current identity, registration and reception versions.
2. Review display name, reading, spectator/songs and successful read time/version. Separately check identity/current registration/required reception actions; then explicitly complete QR reception with optional 500-character note. Server rechecks current code/read and optimistic versions. Reissue during this confirmation rejects and clears the stale target; nothing is inferred or auto-confirmed.

Current receipt displays method/actor/time. Cancellation and detailed per-person history remain in the manual/search surface below, supporting both receipt methods. A currently active receipt does not expose another completion action.

## Uncertain result discipline

Read and receipt each capture a full immutable payload + GUID. Unknown result disables competing controls and offers only explicit identical-ID retry. A historical read replay still undergoes final current-code checks at a new receipt. A historical receipt result is followed by fresh current-state retrieval and cannot resurrect cancellation. Known authorization/validation/conflict errors clear pending sensitive state and require a fresh explicit read; raw input is not shown in errors. Route epochs ignore stale responses; disposal clears secrets and releases the JS module.

## Verification and remaining acceptance

Pure client validation tests cover own/wrong origin and event, raw code, query/credential/extra fragment rejection and fragment removal without write. Synthetic native HTTPS/passkey/mobile Blazor tests cover direct-link no-op, masked input, explicit read versus unchecked completion, linked QR receipt and encoded note, invalid input, reissue race, member denial and no runtime errors. Existing full regression and CI results are recorded separately. These are not physical camera, iOS/Safari, NPM/WebSocket or DXP acceptance.

No schema change beyond the prior read-linked receipt increment. No merge, delivery or DXP update. Remaining: camera permission/decoding/lifecycle, complete roster/history surfaces, final recovery/edit-mode integration and physical-device acceptance.


Read locks are set before async client validation to reject rapid repeated clicks. Manual and QR controls mutually disable while another write has an unknown/pending result. Parent rerender does not clear a pending operation: private state resets only when event route changes.
