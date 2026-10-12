# Verified passkey addition

English reference; [Japanese version](PASSKEY-ADDITION.ja.md).

This feature is stacked on the SQLite migration foundation. It does **not** authorize a DXP update or implement authentication recovery, email change, key deletion or global session revocation.

## User flow and approved deadlines

1. Log in with an existing passkey. Sessions issued before this feature retain normal access but must log out and log in again before adding a key.
2. Choose **Add passkey** and reauthenticate with an existing key belonging to that account. Native ASP.NET Core Identity validates the assertion and persists its updated state.
3. A new one-time confirmation link is queued to the account's **saved email**, not an address supplied by the caller. Open it in the same authenticated browser/profile that started the operation.
4. Register the new passkey after email confirmation. Native Identity validates and stores the attestation. Completion queues an inbox notification and an email.

The whole operation expires **15 minutes after successful existing-key reauthentication**. The email link cannot outlive that deadline. After fresh email confirmation, creation permission lasts **five minutes or until the whole deadline, whichever comes first**. Equality at either deadline is expired. Restart from reauthentication after expiry; resend cannot extend the whole deadline or revive an expired creation grant.

Resend invalidates the preceding email token, email proof and pending creation challenge. Cancel and logout invalidate the unfinished operation for the current session. A new operation cancels any earlier unfinished one in that session. Another user/session, changed saved email/security stamp, disabled account, initial-enrollment token, replay and direct creation/completion requests are rejected. A normal login or a historical `EmailConfirmed` value alone is insufficient.

## Implementation boundaries

- `PasskeyAdditions` is introduced by `20261012093941_AddPasskeyAdditionProof`; retain the baseline migration unchanged.
- Operation, user, authentication-session hash, saved email and security stamp are bound together. Mail tokens and WebAuthn challenge bindings are stored as hashes. Existing queued mail content is encrypted by the notification service's Data Protection protector; do not publish DB/backups or protection keys.
- Purpose binding supplements, **never replaces**, Identity's native challenge, origin/RP, signature and attestation checks. No custom cryptographic verification is added.
- Proof consumption and completion use immediate SQLite transactions. Concurrent submissions can succeed only once. Audit events contain operation IDs, never raw mail tokens, challenge material or credentials.
- The browser removes the proof query parameter before its same-origin, authenticated, CSRF-protected POST. Fetching the HTML alone does not consume the token; an unauthenticated scanner cannot confirm it. Do not share or log confirmation URLs.
- Existing Origin/CSRF checks and the global API rate limiter remain active. Dedicated per-account resend throttles, queue cleanup/retention, hardware coverage and broader abuse testing remain follow-up work; this is not a claim of complete security certification.
- SMTP authentication ID, SMTP_FROM address/display name, public hostname/RP ID, VAPID keys and Data Protection identity are unchanged. Do not overwrite a deployed `.env` with the example.

## Validation

```sh
dotnet build src/Sem.Web -c Release
dotnet run --project tests/Sem.StorageChecks -c Release
dotnet run --project tests/Sem.PasskeyProofChecks -c Release
```

The `Passkey addition proof` workflow runs those checks and builds linux/amd64 legacy/candidate images. The HTTPS fixture enrolls synthetic accounts in fixed legacy `78b9a6c`, upgrades the same DB and protection-key volume, verifies existing login/invitation/inbox/scheduled processing, then exercises native reauthentication, SMTP proof/resend, concurrent confirmation and attestation, completion notifications, cancellation and login with only the newly added virtual key. Its additional cases use `SEM_PASSKEY_ADDITION_CHECKS=1` and isolated SMTP. No real account, SMTP credential or device key is used.

Fake-clock service checks exercise exact deadlines, identity/session binding, changed account state, one-use and cancellation. Chromium virtual passkeys do not establish Safari/iCloud/iPadOS behavior or actual SMTP/Push arrival.

## Future deployment and rollback

Follow [migration readiness](MIGRATIONS.md): fresh stopped backup of the complete `/data`, preserve the original volume and private `.env`, validate a separate copy first, stop the old instance, and never run two instances against the same published alias. DXP has not been updated by this feature.

The upgraded schema contains a new table and migration history. A baseline-only migration-aware binary will intentionally reject it. **Do not downgrade simply by loading an older image**, edit history or drop the new table. Return to the retained pre-upgrade volume/backup and explicitly account for any changes made after cutover. Keep the same public hostname/RP ID and protection keys.
