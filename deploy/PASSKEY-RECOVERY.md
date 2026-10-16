# Passkey recovery (administrator-approved)

This feature is an account-recovery exception, not a password reset or a mail-only login. A global service administrator must select an account, provide an audit reason, explicitly confirm the irreversible reset, and prove possession of a registered administrator passkey. Event-scoped administrators cannot perform recovery.

## Flow and security properties

1. The administrator starts recovery from `/service/passkey-recovery`. The API rechecks the live global-admin role, confirmed/enabled status, security stamp, and authentication-session revocation state.
2. A native ASP.NET Core Identity passkey assertion is bound to the selected target, reason, and administrator session. A successful assertion is consumed in the same transaction that disables the target, rotates its security stamp, deletes every existing passkey, invalidates pending passkey-addition/email-change flows, removes push-device bindings, writes an audit record, and admits a protected recovery email.
3. The email goes only to the account's already-confirmed saved address. The link uses `/recovery#op=…&token=…`; the bearer token is in the URL fragment, never the query string. The token itself is stored only as a hash in the operation and inside a data-protected outbox payload. Recovery-link outbox rows also keep a separate SHA-256 `ProofHash` to verify that a queued message still matches the operation’s active token; after delivery, the protected body is erased while that digest remains. It is not the bearer token, but is treated as sensitive and is never logged or audited.
4. The anonymous confirmation consumes the email token once and returns a short-lived grant for one target and one recovery purpose. It does not create an application cookie or normal authenticated session.
5. The grant permits only native Identity passkey creation/attestation. The account remains disabled until the new passkey is saved successfully. Completion clears the grant/recovery state, rotates the security stamp, re-enables the account, queues a protected completion notice, and requires a fresh normal passkey login.

The original account email, internal user ID, roles, and application data are preserved. The recovery flow never accepts an alternate email address. The removed passkeys are not recoverable through the public API.

## Expiration and durable throttles

- Initial recovery mail and its operation expire 15 minutes after reset. A resend requires a fresh administrator passkey assertion, rotates the token, invalidates the old link/grant, and does not extend that deadline.
- A confirmed-email grant is valid for at most 5 minutes and is bound to the target account, saved email, security stamp, operation, and recovery purpose.
- Accepted recovery-email admissions are limited per account and purpose to 3 in a rolling hour, with at least 60 seconds between admissions. Provider retries do not count as new admissions.
- Failed administrator proofs and invalid email proofs are limited to 5 per account/purpose in a rolling 15-minute window. On the fifth invalid email proof for a pending operation, the operation is marked expired, its deadline is clamped to the failure time, all token/grant/challenge hashes are cleared, and unsent protected recovery-link mail is cancelled. The account remains disabled. Admin proof options, reset starts, and resends are blocked while the durable email-failure window is active; after it ages out, restart requires a fresh administrator proof and the same saved email. Rate state is not reset by an expired or recreated recovery operation.
- An account disabled outside a recovery operation cannot use this flow. If a recovery operation expires, the account remains disabled; a new administrator approval may start a new operation only when the persisted record still proves that recovery disabled the account.

## Deployment prerequisites

- Serve the application over HTTPS and set the deployed public origin and Identity passkey/RP configuration correctly. The email link is built from the configured public origin.
- Configure SMTP before enabling this flow. The dispatcher uses a dedicated, durable, data-protected outbox with bounded retries. Recovery links are cancelled when their operation, token, stamp, or account state is no longer current.
- Persist and back up the ASP.NET Core Data Protection key ring and the database on durable storage. Restoring the database without its matching Data Protection keys can make queued protected email payloads unreadable.
- Deploy the matching application migration. SQLite startup initialization first checks for the supported schema/history and then applies pending migrations; it refuses unknown schemas or migration histories instead of attempting an unsafe adoption.
- Do not use a real account, production SMTP, or production database for acceptance testing. Use synthetic users and a test-only mail transport.

## Verification and operator acceptance

Run the isolated checks with:

```bash
dotnet run --project tests/Sem.PasskeyRecoveryChecks/Sem.PasskeyRecoveryChecks.csproj
```

The checks use disposable synthetic SQLite databases and a fake passkey adapter/mail transport. They must not be pointed at the service database or a real SMTP server. Confirm the test harness records its pre-test empty-database backup under `/data/evidence/`.

Before enabling the route in a deployment, verify:

- the application reports storage ready after the approved migration;
- SMTP delivery and Data Protection key persistence work in the deployment environment;
- an administrator can reach the service UI, while an event-scoped administrator cannot;
- the confirmation route accepts the fragment token only through a POST body and never places it in a query string or logs;
- recovery completion displays the requirement to sign in again with the newly enrolled passkey;
- start, resend, email confirmation, and completion audits contain actor, target, reason, and time, but no token, credential, challenge, or session secret.

No real-account reset, production activation, live email send, or change to emergency host-level recovery is part of this implementation or acceptance checklist.
