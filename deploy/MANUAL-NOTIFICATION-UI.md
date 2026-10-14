# Manual notification composer and guarded acceptance

## Scope

Stacked on the atomic acceptance foundation. Adds a manager-only composer at `/events/{id}/manual-notification`, linked from the existing notification tab/plan, and two POST-only Core HTTP actions. The existing preview, current actor/account/session/stamp/manager/event visibility gates, immutable operation contract and atomic inbox/outbox/audit transaction are unchanged. No additional database migration, Push dispatcher, SMTP invocation, release, deployment or merge is introduced.

The explicit final action **saves inbox messages**. A successful result is acceptance, not device delivery/read receipt. The recipient-level Push intent remains dormant; expiry must be decided before dispatch is enabled. Automatic event Push and the reservation ledger are untouched.

## HTTP boundary

`manual-notification-preview` accepts `{subject,body}`; `manual-notification-accept` accepts the existing strict `ManualNotificationSubmit` DTO. Both require POST, exact configured Origin, antiforgery, current normal authentication and database management authority; GET returns 405. Every response and private composer HTML use no-store. Drafts are JSON bodies, never query strings. `id` selects the event; the required `actor` hint must equal the authenticated actor to prevent a cookie-account switch from silently changing the composer owner. It is not an authorization token. The Core service still independently checks all current permissions.

The preview only returns normalized plain text, event information, live distinct audience count, comparison fingerprint and generic Push preview body; no recipient identities, profiles, answers or subscriptions. Acceptance returns only operation ID/count/timestamp/replay state. The generic client error messages do not expose server exceptions or message content.

## Explicit flow and failure handling

1. Input plain-text subject (1–80 UTF-16 units) and body (1–2000); existing server normalization remains authoritative. Preview is explicit and writes nothing.
2. Display normalized subject/body and current audience count. A separate checkbox is required before saving. Editing invalidates preview and approval.
3. On explicit save, synchronously freeze one canonical operation ID plus immutable approved payload before the first await. Disable duplicate clicks, editing, discard and new-operation actions while pending.
4. A valid matching acceptance displays the original count/time and clears unsent input. A new message requires a separate explicit new-composer action.
5. A first definite 400/403/409 rejection requires a fresh preview; authorization failure hides the private composer. A transport failure, timeout, malformed/mismatched success, 429 or 5xx is **unknown**, never automatically retried. The pending request stays immutable; the user may explicitly retry the exact ID/payload. A later rejection does not disprove the earlier unknown commit and cannot discard that ID. Reauthorization cannot switch actor ownership.

Internal navigation is refused for unsent input/preview, processing or unknown result. Explicit unsent cancellation clears only local draft/approval, never accepted messages. External navigation/reload uses the browser's standard warning: a user can choose to leave, and browser/process termination cannot be prevented. Drafts and pending requests are circuit memory only: no local/session storage, saved authentication state or claim of automatic UI recovery after reload/restart. Durable Core replay requires the same request ID/payload; this page does not persist those across circuit loss. Accepted operation state remains in the database even when its composer is disposed.

The async component checks disposal/generation, does not reinitialize pending state on unrelated parent render, and hides draft fields after explicit authorization rejection. Keyboard focus, text escaping, multiline rendering and 44px controls support desktop and 390px layouts. Native browser/passkey tests are synthetic and do not replace actual mobile/device/DXP acceptance.

## Verification

- `dotnet run --project tests/Sem.ManualNotificationComposerChecks -c Release`: pure state lifecycle, explicit approval, duplicate prevention, frozen retry, definite/unknown rejection, matched acceptance and cancellation.
- `node scripts/manual-notification-transport-checks.mjs`: explicit single HTTP attempt, CSRF/no-store/context encoding, failures, no automatic retries/storage/background loops.
- `bash scripts/manual-notification-ui-check.sh`: builds an isolated publish, fresh runtime DB, localhost HTTPS proxy/SMTP fixture and real virtual-native-passkey administrator/member sessions. Runs the shared baseline plus manual HTTP/UI checks, including current spectator audience, manager/CSRF/actor restrictions, encoded native preview, real tab locks, **committed response loss**, rejected retry with hidden fields, reauthorization and exact durable replay into exactly one inbox message. Temporary runtime DB/keys/processes are cleaned up. Requires .NET 10, Python, OpenSSL, Node and Playwright/Chromium, and free localhost ports 18080/18443/18025/2525. `SEM_CHROMIUM` optionally selects a local browser executable. No browser auth state files are created.

The dedicated workflow builds warnings-as-errors, runs all .NET console suites, relevant JS regressions and the isolated native acceptance. This is not a Docker/old-image upgrade, DXP deployment, real camera/mobile-device or manual Push delivery test. Actual results belong in the PR/dashboard; the presence of these tests is not evidence of passing them.
