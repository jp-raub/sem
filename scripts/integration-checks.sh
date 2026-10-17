#!/usr/bin/env bash
set -euo pipefail
mkdir -p verification
printf 'phase\tseconds\n' > verification/timings.tsv
phase_started=$SECONDS
bash -n scripts/integration-checks.sh scripts/build-integration-projects.sh
python3 tests/ci-build-graph-checks.py
printf 'Checked commit: %s\nTree: %s\n' "$(git rev-parse HEAD)" "$(git rev-parse HEAD^{tree})" | tee verification/source.txt
if [[ ! -f src/Sem.Web/Sem.Web.csproj ]]; then
  if [[ -n $(git log -1 --format=%H -- src/Sem.Web/Sem.Web.csproj) ]]; then
    echo 'Application project was removed from a product tree.' >&2; exit 1
  fi
  echo 'CI bootstrap only: no application exists at this tree; no application acceptance claimed.' | tee verification/bootstrap.txt
  exit 0
fi
node --check src/Sem.Web/wwwroot/passkey-recovery-admin.js
node --check src/Sem.Web/wwwroot/passkey-recovery.js
node scripts/passkey-recovery-ui-checks.mjs 2>&1 | tee verification/passkey-recovery-ui.log
node --check src/Sem.Web/wwwroot/auth.js
node --check scripts/application-auth-checks.mjs
node scripts/application-auth-checks.mjs 2>&1 | tee verification/application-auth-ui.log
node --check src/Sem.Web/wwwroot/device-notifications.js
node --check scripts/device-notification-ui-checks.mjs
node scripts/device-notification-ui-checks.mjs 2>&1 | tee verification/device-notification-ui.log
node --check src/Sem.Web/wwwroot/invitation-management.js
node --check scripts/invitation-management-ui-checks.mjs
node scripts/invitation-management-ui-checks.mjs 2>&1 | tee verification/invitation-management-ui.log
node --check src/Sem.Web/wwwroot/admin-accounts.js
node --check scripts/admin-accounts-checks.cjs
node scripts/admin-accounts-ui-checks.mjs 2>&1 | tee verification/admin-accounts-ui.log
node --check src/Sem.Web/wwwroot/account-passkeys.js
node --check scripts/account-passkeys-native-checks.cjs
node scripts/account-passkeys-ui-checks.mjs 2>&1 | tee verification/account-passkeys-ui.log
node scripts/legacy-passkey-url-checks.mjs 2>&1 | tee verification/legacy-passkey-url.log
node scripts/dialog-lifecycle-checks.mjs 2>&1 | tee verification/dialog-lifecycle-ui.log
node --check src/Sem.Web/wwwroot/field-help.js
node scripts/field-help-ui-checks.mjs 2>&1 | tee verification/field-help-ui.log
node --check scripts/participant-profile-checks.cjs
node --check scripts/acceptance-navigation-checks.cjs
node --check scripts/acceptance-retention-checks.cjs
node --check scripts/comment-checks.cjs
bash -n scripts/acceptance-browser-fixture.sh
node --check scripts/event-matrix-checks.cjs
node --check src/Sem.Web/wwwroot/join.js
node --check scripts/application-join-ui-checks.mjs
node scripts/application-join-ui-checks.mjs 2>&1 | tee verification/application-join-ui.log
node --check src/Sem.Web/wwwroot/inbox.js
node --check scripts/application-inbox-ui-checks.mjs
node scripts/application-inbox-ui-checks.mjs 2>&1 | tee verification/application-inbox-ui.log
# Use default NuGet auditing. Historical dependency warnings are recorded, not suppressed.
printf 'synthetic-ui\t%s\n' "$((SECONDS-phase_started))" >> verification/timings.tsv
phase_started=$SECONDS
bash scripts/build-integration-projects.sh 2>&1 | tee verification/build.log
printf 'restore-build-all\t%s\n' "$((SECONDS-phase_started))" >> verification/timings.tsv
phase_started=$SECONDS
suites=0
shopt -s nullglob
for project in tests/*/*.csproj; do
  name=$(basename "$(dirname "$project")")
  suite_started=$SECONDS
  # Tests may use synthetic loopback fixtures but cannot reach an external provider.
  output_type=$(dotnet msbuild "$project" -getProperty:OutputType -nologo)
  command=run; project_flag=--project
  if [[ $output_type != Exe && $output_type != WinExe ]]; then command=test; project_flag=; fi
  # Drop root/capabilities/supplementary groups and forbid privilege escalation.
  sudo -E unshare --net bash -c 'set -euo pipefail; ip link set lo up; exec setpriv --reuid="$2" --regid=65534 --clear-groups --no-new-privs --bounding-set=-all --inh-caps=-all --ambient-caps=-all "$3" "$4" ${5:+"$5"} "$1" -c Release --no-build --no-restore' bash "$project" "$(id -u)" "$(command -v dotnet)" "$command" "$project_flag" 2>&1 | tee "verification/${name}.log"
  printf 'suite:%s\t%s\n' "$name" "$((SECONDS-suite_started))" >> verification/timings.tsv
  suites=$((suites+1))
done
if [[ $suites -eq 0 ]]; then
 if [[ -n $(git log -1 --format=%H -- tests) ]]; then
  echo 'Previously present test suites disappeared.' >&2; exit 1
 fi
 echo 'Historical tree has no dedicated .NET suite; build and Docker smoke only.' | tee verification/suites.txt
else
 printf 'All %s .NET suites passed.\n' "$suites" | tee verification/suites.txt
fi
printf 'all-suites\t%s\n' "$((SECONDS-phase_started))" >> verification/timings.tsv
phase_started=$SECONDS
dotnet publish src/Sem.Web/Sem.Web.csproj -c Release --no-build -o /tmp/sem-integration-publish 2>&1 | tee verification/publish.log
docker build --platform linux/amd64 -t sem-integration:check -f deploy/Dockerfile . 2>&1 | tee verification/docker-build.log
# Verify the final image as its configured non-root user, before starting it.
# No data volume, secrets, network, or application startup is required here.
docker run --rm --network none --read-only --cap-drop ALL \
 --security-opt no-new-privileges:true --entrypoint /bin/sh sem-integration:check -ec '
 test "$(id -u)" != 0
 test -r /app/appsettings.json
 test -r /app/Sem.Web.dll
 test ! -w /app/appsettings.json
 test ! -w /app/Sem.Web.dll
 test ! -w /app
 test -x /app
 echo "Non-root application files: readable and not writable."
' 2>&1 | tee verification/docker-file-permissions.log
printf 'publish-docker-build\t%s\n' "$((SECONDS-phase_started))" >> verification/timings.tsv
phase_started=$SECONDS
docker volume create sem-integration-data
cleanup() {
 docker logs sem-integration > verification/storage.log 2>&1 || true
 docker rm -f sem-integration >/dev/null 2>&1 || true
 docker volume rm sem-integration-data >/dev/null 2>&1 || true
}
trap cleanup EXIT
# Disposable empty storage only: no host secrets, SMTP provider, Push keys or external data.
docker run -d --name sem-integration --network none --cap-drop ALL --security-opt no-new-privileges:true \
 -v sem-integration-data:/data sem-integration:check
ready() {
 for n in $(seq 1 60); do
  if timeout 5s docker exec sem-integration bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080; printf "GET /health/ready HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" >&3; cat <&3' > verification/ready.json && grep -q '^HTTP/1.1 200' verification/ready.json && grep -q storage-ready verification/ready.json; then
   return 0
  fi
  sleep 1
 done
 return 1
}
ready
cp verification/ready.json verification/initial-ready.json
if [[ -f src/Sem.Web/Core/ProfileImages.cs ]]; then
 timeout 20s docker exec sem-integration dotnet /app/Sem.Web.dll --profile-image-self-test 2>&1 | tee verification/profile-image-native.log
fi
docker restart sem-integration >/dev/null
sleep 2
ready
cp verification/ready.json verification/restart-ready.json
echo 'Isolated Docker empty storage initial start/restart passed; no browser/device/production acceptance.' | tee verification/smoke.txt

printf 'docker-smoke\t%s\n' "$((SECONDS-phase_started))" >> verification/timings.tsv
