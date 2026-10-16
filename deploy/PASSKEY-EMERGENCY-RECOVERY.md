# Host emergency recovery for the last administrator

This procedure is a narrowly scoped break-glass path for restoring access to a self-hosted SEM instance when the only enabled, confirmed global administrator cannot complete the ordinary administrator-approved recovery flow. It does not create an administrator identity, bypass native passkey registration, or provide an HTTP reset endpoint.

## Safety boundary

- The offline host command is the only entry point for preparation. It must validate the caller's effective operating-system UID against a fixed, root-controlled policy and hold the shared application runtime lease.
- Before preparation, the operator must create and verify a backup of both the database and the Data Protection key ring. The command passes the backup identity and the security stamp observed with that backup to the internal service method. The service reloads the target and rejects a changed stamp before writing.
- The target must be the sole enabled, confirmed global administrator. A disabled account is eligible only when a persisted HostEmergency operation proves that this recovery flow disabled it and its saved email and security-stamp bindings still match. A disabled account with no such record is not recoverable here.
- SMTP must be configured. If mail cannot be prepared, the service does not disable the account or remove credentials. Mail transport retry failure also leaves the account unchanged.
- The host operator is recorded as a HostOperator actor with operator ID and backup ID. No application-user or administrator identity is fabricated. The audit records the target, reason, action, and time, never a token, credential, security stamp, or session secret.

## Two-stage recovery

1. **Prepare (non-destructive).** The host command creates a `HostPrepared` operation and queues protected mail only to the account's existing confirmed address. It does not disable the account, rotate the security stamp, delete passkeys, invalidate enrollment tickets, or issue a grant. The response exposes only an operation ID, deadline, and masked recipient; it does not return the bearer token.
2. **Confirm the saved-email link (destructive).** The recipient opens the normal public `/recovery#op=…&token=…` link. Anonymous confirmation rechecks the live sole-admin or matching host-recovery-disabled condition, the saved email, and the security-stamp binding. It consumes the one-use token atomically with disabling the account, rotating the stamp, deleting all old passkeys, invalidating other credential flows and push bindings, and recording host-reset audit provenance. It returns only a short-lived passkey-registration grant; it does not issue a session cookie.
3. **Register a replacement key.** The existing native ASP.NET Core Identity passkey creation/attestation path is the only way to register a new credential. The account is re-enabled only after successful native key persistence. The user must then perform a fresh normal passkey login.

The saved email address is preserved; there is no alternate-address input. Old passkeys are irreversibly removed by the reset and cannot be restored through the public API. The host path does not bypass the public confirmation or native registration stages.

## Restart and throttling

- If the 15-minute host-prepared link expires, the account remains in its pre-prepare state. A new attempt requires a new verified backup and current stamp, and is subject to the durable account/purpose send and failure limits.
- If email confirmation already disabled the account but new-key registration was not completed before the grant expired, a new host preparation is permitted only when the persisted HostEmergency operation still proves that recovery disabled the account and current saved-email/stamp bindings match. The account remains disabled while the new saved-address confirmation is pending.
- Five invalid email proofs expire and invalidate the current preparation, clear the token/grant/challenge material, and cancel unsent protected mail. For a HostPrepared account this does not disable the account or rotate its stamp. Cooldown and send quotas persist across retries.
- Each email token is one-use and kept as a hash in the operation. Mail is held in the dedicated protected outbox. Outbox delivery retries do not constitute additional accepted sends.

## Deployment and acceptance

Apply the additive host-recovery migration together with the matching application build. The host command must refuse to run if the exact expected schema/model is not present, a migration is pending, the database is not in the required clean state, the backup is missing/unverified, the caller UID is not explicitly allowed, or another process holds the runtime lease. Never bypass these checks manually.

The migration intentionally refuses an automatic downgrade because converting host-operator audit provenance back into administrator identity fields would be lossy. Rollback requires a separately reviewed recovery plan and a verified backup.

Use disposable synthetic accounts, a temporary database, a test-only mail transport, and a synthetic host operator for tests. Do not use a production database, a real account reset, or live SMTP as a substitute for synthetic checks. Production enablement and real-device/mail acceptance require a separately approved operational procedure.
