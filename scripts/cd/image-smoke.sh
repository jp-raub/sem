#!/usr/bin/env bash
# Synthetic container only. No production configuration or external mail/Push.
set -euo pipefail
image=${1:?Expected local image}
container="sem-cd-smoke-${GITHUB_RUN_ID:-$$}-${GITHUB_RUN_ATTEMPT:-1}"
volume="$container-data"
cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null 2>&1 || true
}
trap cleanup EXIT
[ "$(docker image inspect "$image" --format '{{.Os}}/{{.Architecture}}')" = linux/amd64 ]
[ "$(docker image inspect "$image" --format '{{.Config.User}}')" = app ]
docker run --rm --network none --entrypoint sh "$image" -c \
  'test "$(id -u)" != 0 && test -r /app/Sem.Web.dll && test -r /app/appsettings.json'
docker volume create "$volume" >/dev/null
docker run -d --name "$container" --cap-drop ALL --security-opt no-new-privileges \
  -p 127.0.0.1::8080 -v "$volume:/data" "$image" >/dev/null
port=$(docker port "$container" 8080/tcp | sed -n 's/^127\.0\.0\.1://p')
[[ "$port" =~ ^[0-9]+$ ]]
ready() {
  local n ok
  ok=0
  for n in $(seq 1 90); do
    if curl --max-time 3 -fsS "http://127.0.0.1:$port/health/live" >/dev/null 2>&1 \
      && curl --max-time 3 -fsS "http://127.0.0.1:$port/health/ready" >/dev/null 2>&1; then
      ok=$((ok + 1)); [ "$ok" -lt 3 ] || return 0
    else ok=0; fi
    sleep 2
  done
  echo 'Synthetic container health failed (raw logs not published)' >&2
  return 1
}
ready
docker restart "$container" >/dev/null
ready
echo 'linux/amd64, non-root reads, empty storage, restart health: passed'
