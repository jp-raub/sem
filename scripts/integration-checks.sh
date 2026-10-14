#!/usr/bin/env bash
set -euo pipefail
mkdir -p verification
bash -n scripts/integration-checks.sh
printf 'Checked commit: %s\nTree: %s\n' "$(git rev-parse HEAD)" "$(git rev-parse HEAD^{tree})" | tee verification/source.txt
if [[ ! -f src/Sem.Web/Sem.Web.csproj ]]; then
  echo 'CI bootstrap only: no application exists at this tree; no application acceptance claimed.' | tee verification/bootstrap.txt
  exit 0
fi
# Use default NuGet auditing. Historical dependency warnings are recorded, not suppressed.
dotnet build src/Sem.Web/Sem.Web.csproj -c Release 2>&1 | tee verification/build.log
suites=0
shopt -s nullglob
for project in tests/*/*.csproj; do
  name=$(basename "$(dirname "$project")")
  dotnet run --project "$project" -c Release 2>&1 | tee "verification/${name}.log"
  suites=$((suites+1))
done
printf 'All %s .NET suites passed.\n' "$suites" | tee verification/suites.txt
dotnet publish src/Sem.Web/Sem.Web.csproj -c Release --no-build -o /tmp/sem-integration-publish 2>&1 | tee verification/publish.log
docker build --platform linux/amd64 -t sem-integration:check -f deploy/Dockerfile . 2>&1 | tee verification/docker-build.log
docker volume create sem-integration-data
cleanup() {
 docker logs sem-integration > verification/storage.log 2>&1 || true
 docker rm -f sem-integration >/dev/null 2>&1 || true
 docker volume rm sem-integration-data >/dev/null 2>&1 || true
}
trap cleanup EXIT
# Disposable empty storage only: no host secrets, SMTP provider, Push keys or external data.
docker run -d --name sem-integration --cap-drop ALL --security-opt no-new-privileges:true \
 -p 127.0.0.1:18080:8080 -v sem-integration-data:/data sem-integration:check
ready() {
 for n in $(seq 1 60); do
  if curl -fsS http://127.0.0.1:18080/health/ready > verification/ready.json; then
   grep -q storage-ready verification/ready.json; return 0
  fi
  sleep 1
 done
 return 1
}
ready
cp verification/ready.json verification/initial-ready.json
docker restart sem-integration >/dev/null
sleep 2
ready
cp verification/ready.json verification/restart-ready.json
echo 'Isolated Docker empty storage initial start/restart passed; no browser/device/production acceptance.' | tee verification/smoke.txt
