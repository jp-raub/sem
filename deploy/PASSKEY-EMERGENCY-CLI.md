# Offline last-administrator recovery CLI

This is a host-only entry, not an HTTP route, bootstrap account or an authentication bypass. It uses the dedicated HostEmergency recovery service described in [PASSKEY-EMERGENCY-RECOVERY.md](PASSKEY-EMERGENCY-RECOVERY.md). It does not impersonate an application administrator. The deployment/run steps below are instructions, not a report that any real reset or SMTP delivery has occurred.

## Authorization and prerequisites

Linux only. A root-controlled fixed file `/etc/sem/emergency-recovery.json` contains exactly one property:

```json
{"allowedEffectiveUid":1654}
```

Use the actual effective UID of the single permitted host operator (1654 is an example, not an assigned deployment identity). The file and each parent must be root-owned, real files/directories, and not group/world writable. The file must be a regular file, at most 4096 bytes. The CLI checks the opened file's Linux `statx` UID/mode as well as its ancestors. There is no command-line or environment override for the policy path, and no `--operator-id` option. Missing configuration, UID mismatch, unsupported OS or inability to verify ownership fails closed. Container deployments must deliberately mount this root-controlled file read-only and establish the intended host/container UID mapping; do not assume the image's non-root user is automatically approved.

Stop **all** application instances and workers accessing this storage. Current-version web hosts and this CLI hold the same `sem.runtime.lock` exclusive file lease. The inode remains after close; do not delete it to override a busy lock. This cooperative lease cannot stop older programs or arbitrary SQLite clients which do not honor it. An immediate SQLite transaction separately protects backup verification against writers. No online maintenance or force-unlock option exists.

Use an existing absolute `Sem:DataDirectory` containing `sem.db` and existing `keys/`. The database must have exactly the current migration chain/schema, no pending model changes, successful integrity/FK checks and a clean non-WAL journal. This command never creates a database, applies migrations, checkpoints WAL, changes journal mode or starts HTTP/workers. A nonempty WAL/SHM/journal causes refusal; perform approved offline storage maintenance separately, never remove sidecars to make this check pass. Symlinks in the storage/key/backup paths are rejected.

Configuration is loaded from the deployed application's `appsettings.json` at its assembly directory and environment variables, including `Sem__DataDirectory`, `Sem__PublicUrl`, `Smtp__Host` and `Smtp__From`. Keep these consistent with the stopped application. PublicUrl must be an HTTPS origin. SMTP configuration is required for preparation but is not a delivery test. CLI Data Protection uses the application's existing `SEM.ExternalTest` key ring and disables automatic key generation; an unusable key ring fails rather than silently generating a replacement.

## Two-stage operation

1. Inspect the target and current DB hash. This uses a read-only database connection. It may open/create only the cooperative lease file. The target must be the sole enabled, confirmed global administrator, or the matching target previously disabled by this HostEmergency flow. A disabled account of unknown origin is never made eligible. No alternate recipient can be supplied.
2. With the inspected hash and explicit target confirmation, apply preparation. Before the service writes an operation or protected mail, the CLI creates a **new** backup directory outside live storage, copies `sem.db` plus all Data Protection key-ring files, flushes and verifies every SHA-256, rechecks source keys/DB, and writes a private manifest. It never overwrites a backup. The backup ID is the SHA-256 of the manifest's ordered `{path,sha256}` entries and is passed into the operation/audit. The saved security stamp is rechecked by the service before preparation. Partial backups on failure are preserved; they are not automatically considered valid.
3. A successful result is `mail-prepared-no-reset`, with operation ID, expiry, masked recipient and backup ID. **The CLI has not deleted a key, disabled the account, rotated its stamp or sent SMTP.** Restart the application promptly to dispatch the protected mail. The original 15-minute email deadline is not extended by waiting or worker retries. If the mail cannot be received/confirmed, the initial account remains unchanged. A restart of an already recovery-disabled account remains disabled; this does not silently re-enable it.
4. Only successful one-use confirmation at the saved confirmed email address initiates the atomic credential reset and short-lived registration grant. Eligibility/email/stamp are rechecked at that point. Native Identity passkey registration must complete within the grant deadline; completion does not issue an ordinary application cookie. Log in normally using the replacement passkey. See the service document for durable attempt/send limits and restart behavior.

Example commands (placeholders, not run by documentation):

```sh
# Application is stopped. Environment points to the intended existing storage.
dotnet /app/Sem.Web.dll --passkey-emergency-recovery \
  --target-user-id '<exact-user-id>' --reason 'Lost all administrator passkeys'

# Supply the inspection's DatabaseSha256, exact same target, and a NEW backup directory.
dotnet /app/Sem.Web.dll --passkey-emergency-recovery \
  --target-user-id '<exact-user-id>' --reason 'Lost all administrator passkeys' \
  --apply --confirm-target '<exact-user-id>' \
  --expected-sha256 '<64-hex-inspected-database-hash>' \
  --backup-directory '/secure-backups/sem-emergency-unique'
```

Arguments are case-sensitive, bounded and reject duplicates/unknown names; target is 1–128 control-free nonblank characters, reason is 3–500 trimmed control-free characters. No email, bearer, grant or credential argument exists. Inspection is not approval to run against a real account. Actual operational recovery must follow the operator's own incident authorization.

## Backup, audit and rollback limits

Backup directory is created exclusively with mode 0700 and files with 0600. It contains sensitive DB and Data Protection keys: restrict access and retain together. The JSON manifest contains relative paths and hashes, not key contents, email addresses or tokens. Capture the successful result/manifest as evidence; error output intentionally omits underlying SQL, secrets and address details. Service audit distinguishes the OS actor from an app administrator and records target, reason, UTC time, mode and backup digest.

Preparation is transactional but database and SMTP are not a distributed transaction. This design avoids resetting an initial account before mail ownership is proved; it does not promise SMTP availability or delivery. After mail proof or registration, **do not automatically restore the old DB**: restoration can resurrect compromised keys, cookies and consumed capabilities. Preserve the verified backup and incident evidence, and obtain a deliberate rollback decision with a security plan. Neither this CLI nor the worker deletes/replaces a backup or performs automatic rollback.

## Acceptance boundary

Synthetic tests cover policy parsing, lease/backup/preconditions and service state transitions. They do not establish host mount/UID configuration, a real operator identity, SMTP receipt, physical-device WebAuthn or production recovery. A deployed operator must separately verify the policy mount, correct stopped storage/key ring, backup access, mail delivery and replacement-key login on disposable accounts before enabling this runbook for incidents.

### Protected parents and uncertain command completion

Live storage, the key directory, and the backup destination's immediate parent must be owned by root or the permitted effective UID and must not be group/world writable. The CLI also checks every ancestor; a root-owned sticky ancestor such as `/tmp` is permitted only above the immediate parent, because it prevents other users from replacing an operator-owned child. Pointing the backup directly into a shared writable parent is rejected. Key-ring inputs must be regular files (not FIFO/socket/device); links and special files are not hashed/copied. Keep these paths protected after completion as well.

An output or disposal failure can occur after preparation commits. A nonzero exit therefore does **not** prove that no operation/mail exists. Do not automatically replay apply. Run the read-only inspection for the same target and compare `OperationId`, `OperationStatus`, expiry and `BackupId` with the preserved backup manifest; inspect protected storage/audit offline if still ambiguous. Inspection may report a pre-existing operation and does not consume it, send mail, or reset credentials. If inspection itself cannot complete, preserve evidence and resolve that blocker instead of guessing the write outcome.
