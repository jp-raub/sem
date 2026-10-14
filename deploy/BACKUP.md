# External-test backup and restore verification

This is the English reference for backup/restore of the initial DXP external-test deployment. The corresponding Japanese instructions are in README.ja.md, section 7. This documentation change does not update the deployed image or validate a restore.

## Safety boundaries

- Keep `/data/sem.db` and the entire `/data/keys` directory together. Back up all of `/data` while the application is stopped.
- Run from the existing `deploy` directory with its actual `.env` and Compose project. Do not change the public hostname/RP ID, VAPID keys, or Data Protection keys.
- The service runs as `app` with `cap_drop: ALL`. Using `--user root` does not guarantee access to files owned by the app user. Do not fix this by using chmod 777 or removing the application's restrictions.
- Read data as the normal app user and write the compressed stdout stream as the host user. Do not bind-mount a host backup directory into the container. `-T` is required for binary output.
- Backups contain personal data and authentication keys. Store them outside publicly served directories, restrict access, and retain a separate protected copy. Treat failed/partial archives as unusable.
- Never run `docker compose down -v` while data must be retained. Do not share `.env`, expanded `docker compose config`, key contents, SMTP credentials, or full Push subscription URLs.

## Stopped backup

The subshell leaves the caller's shell unchanged. Its exit trap attempts to restart the service on success or failure after stopping it. Check service health afterward; a restart attempt is not proof of availability. A failed archive retains the `.partial` suffix and must not be restored.

```sh
(
  set -eu
  mkdir -p backups
  umask 077
  backup="backups/sem-data-$(date +%Y%m%d-%H%M%S).tgz"
  test ! -e "$backup"
  test ! -e "$backup.partial"
  docker compose --env-file .env stop sem
  trap 'docker compose --env-file .env up -d sem' EXIT
  docker compose --env-file .env run \
    --rm --no-deps -T --entrypoint sh sem \
    -c 'tar czf - -C /data .' > "$backup.partial"
  gzip -t "$backup.partial"
  tar -tzf "$backup.partial"
  mv "$backup.partial" "$backup"
  sha256sum "$backup" > "$backup.sha256"
  printf 'Backup created: %s\n' "$backup"
)
docker compose --env-file .env ps sem
```

Check that the listing contains `./sem.db` and actual key files under `./keys/`, not just the directory. Keep the checksum beside the archive. This verifies readability and transport integrity, not SQLite consistency, data completeness, or successful recovery. A checksum stored beside an archive is not an independent authenticity guarantee.

## Read-only preparation for a restore

First select the exact successful timestamped archive. Do not automatically pick the newest file: it may be an earlier failed attempt. Existing archives can be checked with `gzip -t`, `tar -tzf`, and `sha256sum`; do not overwrite a previously recorded checksum merely to make verification pass.

Inspect the deployed container without printing its environment:

```sh
docker compose --env-file .env ps sem
cid=$(docker compose --env-file .env ps -q sem)
if [ -n "$cid" ]; then
  docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$cid"
  docker inspect --format '{{range .Mounts}}{{if eq .Destination "/data"}}{{.Type}} {{.Name}} {{.Destination}}{{end}}{{end}}' "$cid"
  docker compose --env-file .env exec -T sem sh -c 'id; ls -ldn /data /data/keys; ls -ln /data/sem.db'
else
  printf 'No running service container. Start/inspect the intended project before continuing.\n' >&2
fi
```

Confirm the actual project name, volume name, image and runtime UID/GID before writing a concrete restore command. Do not assume the effective volume name from the Compose file alone.

## Restore acceptance plan — not yet verified on DXP

1. Preserve the original volume and take a fresh stopped backup before the cutover. Identify a tested rollback route.
2. Restore the selected complete archive into a distinct empty volume. Restore the DB and keys as a set, with ownership matching the confirmed runtime user. Do not extract over the current volume.
3. Validate storage offline before starting an application against it. Starting the app may migrate/write data, send pending notifications, and interact with external providers.
4. Stop the original application before connecting the restored instance to the existing public route or network alias `sem`. Never run both under that alias. Keep NPM/DNS, the public hostname and VAPID keys unchanged.
5. At the same HTTPS hostname, verify existing users, invitations, inbox/history and logout/re-login with an existing passkey. Verify registered Push devices without re-registering, and test a newly scheduled notification. Record actual delivery separately from a server-side sent status.
6. Stop the restored instance before switching back to the preserved original volume. Changes made after the cutover exist only on the restored copy unless deliberately reconciled. Document the accepted active volume and retain the rollback copy until verification is complete.

Concrete volume creation/extraction/cutover commands are intentionally deferred until the runtime details and archive checks are confirmed. Archive creation alone is not restore success.
