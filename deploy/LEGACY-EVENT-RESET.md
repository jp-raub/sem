# One-time legacy test event reset (English source of truth)

## Scope and status

The owner approved discarding old external-test events rather than inventing formal phase settings or classifying simple participation as performer/spectator. This is the first prerequisite for the formal business foundation, **not** its implementation or a DXP deployment. No PR merge or production operation is implied.

The maintenance command deletes all `Events` and their matching `Participations` from the **exact supported external-test schema**, atomically. It appends one host-maintenance audit with counts. It preserves every other table, including Identity users/passkeys/security stamps, service state, invitation requests/uses, email proofs, inbox, deliveries, Push devices, existing audit rows and migration history. It never reads/changes the protection-key directory, environment, SMTP/VAPID credentials or RP hostname. A historical audit referring to a deleted event remains valid history; notification bodies must not be treated as structured foreign keys. `Kind=Reminder` uses `Subject` as an event ID; it is retained, but the existing worker sends nothing when that event is absent. Already sent history and unrelated scheduled-test/email/Push deliveries remain intact.

## Safety gates

- Default is read-only inspection. Explicit `--apply`, inspected SHA-256, event/participation counts and a **new** absolute DB-backup path are required to write.
- Only the frozen baseline schema without history, managed baseline, or baseline plus passkey-addition proof are supported. Extra/missing tables, columns, indexes, views, triggers and non-prefix/unknown history and a remaining EF migration lock are refused. Databases with formal-model/future migration history or schema are refused. A newer binary may retain this command, but it still accepts only the frozen legacy database model.
- Orphan participations and SQLite integrity/foreign-key errors are refused before deletion. The command does not silently broaden the deletion scope.
- The DB must be stopped/offline, non-symlink and use delete/truncate/persist journal mode. Nonempty WAL/SHM/journal sidecars and WAL mode are refused. A lock is **not** proof that the application is stopped: the operator must stop all writers. Never remove sidecars or change journal mode on a running DB to bypass validation.
- Apply acquires an immediate SQLite transaction with a bounded one-second lock wait, rechecks the inspected hash/counts, creates an exclusive 0600 DB backup, verifies its hash, then deletes participation and events. Existing backup files are never overwritten. Any exception before commit rolls back both deletes and audit. Keep a created backup even if execution later fails.
- Before commit, every preserved table's rows and existing audit rows are checked for changes, then integrity/foreign keys are checked again. A repeated reset of an already empty DB is a no-op: no second audit or backup.
- Nothing runs on normal application startup, in an EF migration or over HTTP. The CLI exits before deployment configuration, migrations, HTTP and notification workers. It prints counts and hashes, never raw personal rows or secrets.

## Operator sequence — copied volume only

1. Inventory the actual stopped application image, volume and project. Take the established stopped **whole `/data` archive**, including `sem.db` and `keys`, with restricted permissions; record archive checksum. The command's DB-only backup is supplemental, not a replacement for DB + keys disaster recovery.
2. Restore the archive into a separate empty volume using UID/GID 1654:1654, retain the original image/volume/archive, and verify restored DB/key hashes. No restored instance may run alongside the original on alias `sem` / the same public route.
3. EF-created SQLite files normally retain WAL mode even after a clean stop. On the **verified offline copy only**, run `scripts/prepare-offline-reset-copy.py --database /absolute/copied/sem.db --confirm-offline-copy` with Python 3. It refuses nonempty sidecars, missing files, symlinks and integrity/FK errors, switches the clean copy to DELETE mode and verifies all rows (including binary keys) unchanged. The DB file hash changes with its journal header; record the new hash and use the later inspection result, not the archive DB hash. This is a separate explicit preparation action, not read-only inspection. Never run it against the original or live DB. The whole DB+keys archive taken before preparation remains the rollback source.

For a copied Docker volume, mount only the helper script read-only and that copied volume; Python needs no secrets or network and must use app UID/GID. Replace placeholders with verified absolute script path and copied volume:

```bash
docker run --rm --network none \
  --cap-drop ALL --security-opt no-new-privileges:true --user 1654:1654 \
  --mount 'type=volume,source=<verified-offline-copy-volume>,target=/data' \
  --mount 'type=bind,source=<absolute-source-path>/scripts/prepare-offline-reset-copy.py,target=/prepare.py,readonly' \
  python:3.13-alpine python /prepare.py --database /data/sem.db --confirm-offline-copy
```

The Python image must be available locally; an image pull may require Docker-host Internet access. Do not grant the preparation container network access.

4. Use the separately verified maintenance image (this change is not in the previously delivered `proof-6c5e396d788a` image). Do **not** use these examples until that exact image is available. Both shell variables below are operator-verified placeholders; do not point `COPY_VOLUME` at the original or currently running volume.

```bash
# Example only: substitute the verified offline copy and maintenance image.
COPY_VOLUME='<verified-offline-copy-volume>'
RESET_IMAGE='<verified-maintenance-image-tag>'

docker run --rm --network none \
  --cap-drop ALL --security-opt no-new-privileges:true \
  --mount "type=volume,source=$COPY_VOLUME,target=/data" \
  "$RESET_IMAGE" --legacy-event-reset --database /data/sem.db
```

5. Review `Status=inspection-only`, `Events`, `Participations` and `DatabaseSha256`. If anything is unexpected, stop. Approval is specifically for old test events, not future formal events. Retain the JSON result privately.
6. Run the same image/volume, with the **exact inspected values**, and choose a new backup filename. There is intentionally no automatic application update in this command.

```bash
# Replace all placeholders with the preceding inspected values.
docker run --rm --network none \
  --cap-drop ALL --security-opt no-new-privileges:true \
  --mount "type=volume,source=$COPY_VOLUME,target=/data" \
  "$RESET_IMAGE" --legacy-event-reset --database /data/sem.db \
  --apply --expected-sha256 '<inspected-database-sha256>' \
  --expected-events '<inspected-count>' \
  --expected-participations '<inspected-count>' \
  --backup '/data/sem-before-event-reset-UNIQUE.db'
```

7. Inspect again: zero events/participations; keep the DB-only backup and original whole-volume archive. Under the established single-instance/same-origin acceptance procedure, verify login with an original passkey, accounts/roles, invitation use counts, existing messages, Push without re-registering and a newly scheduled notification. Actual transport/DP-key preservation is a separate acceptance test, not inferred from SQLite row comparison.
8. Formal event creation, profiles, multiple administrators and business migrations remain the next implementation unit. Do not call the legacy test screen a finished business UI. A formal event must get a new identifier; don't reuse deleted event IDs.

## Recovery

On failure, do not retry against a guessed target or delete data/keys. Keep logs (redact secrets), backup and original volume. The original image plus untouched original volume is the primary rollback path. Restoring the supplemental DB-only backup requires a stopped instance with the corresponding preserved keys. Never use an old binary against a DB containing unsupported newer migrations. Stop/senders first, and do not concurrently run restored notification workers. This reset does not provide transactional guarantees for notifications already sent externally.

## Verification

`dotnet run --project tests/Sem.LegacyResetChecks -c Release` exercises synthetic legacy/managed/proof schemas, no-write inspection/refusals, exact backups/0600, preserved rows/passkey bytes, atomic target removal, audit/idempotence, stale hash/counts, extra schema/history, orphans, FK errors, sidecars, WAL, backup collisions, competing writer locks and CLI validation. Existing storage/proof/HTTPS integration suites are retained in the dedicated CI. Tests run only on isolated synthetic DBs, never DXP data.

`python3 tests/offline-reset-copy-checks.py` also checks explicit copy confirmation, missing/symlink paths, pending sidecars, stopped WAL conversion, unchanged binary rows, integrity and byte-idempotence. HTTPS acceptance creates and joins a disposable event, explicitly prepares and resets the stopped synthetic volume, then checks existing native keys/accounts/invites/inbox, new scheduling and the full addition-proof flow.
