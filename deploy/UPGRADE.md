# Verified upgrade candidate: SQLite migrations and passkey-addition proof

English reference; [Japanese version](UPGRADE.ja.md).

**This is not the completed event-management application.** It adds safe SQLite migration adoption and verified passkey addition to the existing external-test application. Songs/parts/phases/timetables/day-of-event/QR workflows remain separate implementation work. No DXP update or PR merge is performed by packaging this candidate.

## 1. Inspect and stage without changing the running deployment

The delivery contains an immutable image, exact-commit source ZIP, English/Japanese guide, VERSION.json/VERSION.txt, verification.json and SHA256SUMS. CI builds linux/amd64, runs 18 storage + 17 proof/state + 45 HTTPS upgrade/auth checks, exports the tested image, reloads the export and verifies its image ID and all package checksums. Checks use synthetic data and virtual authenticators; physical devices and the current DXP database remain untested by CI.

Unpack the **delivery artifact** into a new staging directory, not over the existing deployment. From that directory:

```sh
sha256sum -c SHA256SUMS
python3 -c 'import json; v=json.load(open("VERSION.json")); print(v["sourceCommit"], v["image"], v["imageId"], v["platform"])'
gunzip -c sem-dxp-image.tar.gz | docker load
```

Verify the displayed commit against the successful delivery CI and inspect the tag from VERSION.json:

```sh
docker image inspect sem-dxp:proof-REPLACE_WITH_COMMIT_PREFIX --format '{{.Id}} {{.Os}}/{{.Architecture}}'
```

It must match VERSION.json. Loading this unique tag **does not replace** `sem-dxp:external-test` or restart an application. The source ZIP is for reference/building; do not overlay it onto the live directory, invoke `prepare-source.py`, copy `.env.example` onto `.env`, or regenerate VAPID/Data Protection keys.

## 2. Preserve the current configuration and prepare a fresh stopped snapshot

Run deployment commands from the existing directory (`/volume2/Docker/sem-dxp-delivery/deploy` in the confirmed installation). Keep its Compose, `.env`, SMTP authentication ID, SMTP_FROM/display name, NPM trusted IP, br alias, RP hostname and Push keys. Do not add Cloudflare Access or change NPM/DNS as part of this update.

Read-only inventory (never print container environment):

```sh
cid=$(docker compose --env-file .env ps -q sem)
test -n "$cid"
docker inspect --format 'project={{index .Config.Labels "com.docker.compose.project"}} image={{.Image}}' "$cid"
docker inspect --format '{{range .Mounts}}{{if eq .Destination "/data"}}{{.Type}} {{.Name}} {{.Destination}}{{end}}{{end}}' "$cid"
docker compose --env-file .env exec -T sem sh -c 'id; ls -ldn /data /data/keys; ls -ln /data/sem.db'
old_image_id=$(docker inspect --format '{{.Image}}' "$cid")
```

Expected confirmed original volume: `sem-external-test_sem-data`; UID/GID: 1654:1654. Stop if actual values differ; do not infer them from a file name. Record the old image ID and retain that image. Take a **new** stopped full `/data` backup using [BACKUP.md](BACKUP.md), which restarts the original service afterward. Verify gzip, existing checksum, DB and actual key files. Keep the backup, its checksum and a protected separate copy. Earlier restore-test archives are not automatically the latest state. Nothing should consume the original volume as a candidate.

## 3. Restore a distinct empty candidate volume, offline

Before writing, choose the exact successful fresh backup, captured old image ID, and a new unused volume name. All commands here are examples to tailor and confirm before execution. Never remove the original volume or use `down -v`.

```sh
backup='backups/REPLACE_WITH_CONFIRMED_FRESH_ARCHIVE.tgz'
candidate_volume='sem-proof-check-REPLACE_WITH_UNIQUE_SUFFIX'
(
  set -eu
  gzip -t "$backup"
  sha256sum -c "$backup.sha256"
  if docker volume inspect "$candidate_volume" >/dev/null 2>&1; then
    printf 'Volume already exists; choose a new unused name.\n' >&2
    exit 1
  fi
  docker volume create "$candidate_volume"
  docker run --rm -i --network none --cap-drop ALL \
    --security-opt no-new-privileges:true --entrypoint sh \
    -v "$candidate_volume:/data" "$old_image_id" \
    -c 'test ! -e /data/sem.db; test -z "$(find /data -type f -print -quit)"; tar xzf - -C /data; id; ls -ldn /data /data/keys; ls -ln /data/sem.db' < "$backup"
)
```

The old image's empty `/data` directory seeds app ownership in the new Docker volume. Extract as the normal app user; do not add `--user root`, chmod 777, host backup mounts or extra capabilities. If extraction fails, stop and do not start the partial volume. Never rerun extraction on a nonempty volume.

Compare the restored DB and each key byte/hash with the archive, and run SQLite `integrity_check` / `foreign_key_check` on a read-only **temporary DB copy**, as in the prior restore test. Do not upload DB or key contents. Matching hashes and SQL integrity do not yet prove migrated-app login. Starting the candidate can write migrations and send restored pending notifications, so acceptance testing must account for those effects.

## 4. Verify an override before the planned cutover

Copy only `deploy/upgrade-candidate.override.yaml` from the staged source into the existing deploy directory. Set nonsecret shell variables from VERSION.json and the confirmed candidate volume; do not change `.env`:

```sh
export SEM_CANDIDATE_IMAGE='sem-dxp:proof-REPLACE_WITH_COMMIT_PREFIX'
export SEM_CANDIDATE_VOLUME="$candidate_volume"
# Verify settings without displaying expanded secrets:
docker compose --env-file .env -f compose.yaml -f upgrade-candidate.override.yaml config --format json |
python3 -c 'import json,os,sys; c=json.load(sys.stdin); s=c["services"]["sem"]; v=c["volumes"]["sem-data"]; assert s["image"]==os.environ["SEM_CANDIDATE_IMAGE"]; assert v["external"] and v["name"]==os.environ["SEM_CANDIDATE_VOLUME"]; assert v["name"]!="sem-external-test_sem-data"; assert any(m["source"]=="sem-data" and m["target"]=="/data" for m in s["volumes"]); assert not s.get("ports"); assert not s["environment"].get("Sem__BootstrapSecret"); print("Candidate image/volume, private port and disabled bootstrap: OK")'
```

Do not share raw `docker compose config`, which includes private values. The override keeps the same project/service/network route; it changes **only image and data volume**. The distinct candidate volume is external so Compose will not create a silently empty one. This template is specific to the confirmed `sem-data` service mapping; revise it after review if the deployment differs.

## 5. Planned, single-instance acceptance window

Perform this only when ready for a brief interruption and the rollback route is confirmed. **Stop the original before starting the candidate**; they must not both use the br alias `sem`.

```sh
test "$(docker image inspect sem-dxp:external-test --format '{{.Id}}')" = "$old_image_id"
docker compose --env-file .env stop sem
docker compose --env-file .env -f compose.yaml -f upgrade-candidate.override.yaml \
  up -d --no-build --pull never --force-recreate sem
cid=$(docker compose --env-file .env -f compose.yaml -f upgrade-candidate.override.yaml ps -q sem)
docker inspect --format 'image={{.Image}} {{range .Mounts}}{{if eq .Destination "/data"}}volume={{.Name}}{{end}}{{end}}' "$cid"
```

Confirm actual image ID and candidate mount. Check readiness and startup status; schema mismatch is a failure requiring investigation, not permission to delete/relabel the DB. If startup fails, return to the original as below rather than forcing migration.

At the **same existing HTTPS hostname**, test:

1. Existing admin/member logout → passkey login; profile, inbox, invitation capacity/deadlines/decisions/revocation and event participation retained.
2. Previously registered device receives Push without re-registration; a new 30-second scheduled notification; actual SMTP delivery with the chosen sender/display name. Provider receipt is distinct from server sent status.
3. Add-key operation: existing key reauthentication → saved-email link in the same browser → **different new key/authenticator** registration → logout/login with the new key. An authenticator already holding an excluded credential may correctly refuse another creation; iCloud synchronization is not a separate key.
4. Resend invalidates the earlier link; cancellation and logout invalidate pending addition. Expired operation starts from reauthentication. Whole deadline 15 minutes; fresh-mail creation grant at most 5 minutes, capped by whole deadline. Do not share proof URLs.
5. Restart candidate with the same override; repeat existing/new-key login, data and notification tests. Check registration on intended physical browsers/devices separately.

Record both actual mount and image. No startup result alone establishes all acceptance items. During this test, writes—including added keys, notifications and invitation changes—exist in the candidate copy only.

## 6. Return to the original; decide persistence explicitly

For a **temporary candidate test**, stop candidate, then start original using **only the original Compose** (the external-test tag must still refer to the recorded old image):

```sh
test "$(docker image inspect sem-dxp:external-test --format '{{.Id}}')" = "$old_image_id"
docker compose --env-file .env -f compose.yaml -f upgrade-candidate.override.yaml stop sem
docker compose --env-file .env -f compose.yaml up -d --no-build --pull never --force-recreate sem
cid=$(docker compose --env-file .env -f compose.yaml ps -q sem)
docker inspect --format 'image={{.Image}} {{range .Mounts}}{{if eq .Destination "/data"}}volume={{.Name}}{{end}}{{end}}' "$cid"
```

Verify recorded old image ID and `sem-external-test_sem-data`, then existing original-key login/data/SMTP/Push/scheduling. A key added **only to the candidate** will not log into the original; retain the original key. Keep both volumes/backups and the candidate image. Do not delete keys to remove a warning.

The upgraded database has new migration history/schema. Do not run an older migration-aware binary against it or drop tables/edit history to make it pass. Return to the retained original volume/full backup; explicitly account for post-cutover changes. If keeping the candidate later, record it as the active volume/image and ensure subsequent maintenance always uses the correct override. Do not silently return to the old DB on the next restart. Permanent adoption is a separate acceptance decision.
