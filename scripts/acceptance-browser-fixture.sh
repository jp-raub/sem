#!/usr/bin/env bash
# Disposable localhost-only fixture. Never use an existing application data directory.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
run=$(mktemp -d)
export SEM_FIXTURE_RUN="$run"
cleanup(){ for name in host smtp; do if [[ -f "$run/$name.pid" ]]; then kill "$(cat "$run/$name.pid")" 2>/dev/null || true; fi; done; rm -rf "$run"; }
trap cleanup EXIT
umask 077
cd "$root"
mkdir -p verification/acceptance-native
printf 'phase\tseconds\n' > verification/acceptance-native/fixture-phases.tsv
fixture_started=$SECONDS
for port in 18080 18443 18025 2525; do
 python3 - "$port" <<'PY'
import socket,sys
s=socket.socket(); s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1); s.bind(('127.0.0.1',int(sys.argv[1]))); s.close()
PY
done
dotnet publish src/Sem.Web/Sem.Web.csproj -c Release -o "$run/app" > "$run/publish.log"
printf 'publish\t%s\n' "$((SECONDS-fixture_started))" >> verification/acceptance-native/fixture-phases.tsv
fixture_started=$SECONDS
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$run/key.pem" -out "$run/cert.pem" -days 1 -subj /CN=localhost > /dev/null 2>&1
cat > "$run/start.sh" <<'START'
#!/usr/bin/env bash
set -euo pipefail
cd "$SEM_FIXTURE_RUN/app"
export ASPNETCORE_URLS=http://127.0.0.1:18080 ASPNETCORE_ENVIRONMENT=Development
export Sem__PublicUrl=https://localhost:18443/ Sem__DataDirectory="$SEM_FIXTURE_RUN/storage" Sem__TrustedProxies__0=127.0.0.1
export Sem__BootstrapSecret=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
export Smtp__Host=127.0.0.1 Smtp__Port=2525 Smtp__Tls=None Smtp__AllowInsecureLocalTest=true
exec dotnet Sem.Web.dll
START
cat > "$run/restore.sh" <<'RESTORE'
#!/usr/bin/env bash
set -euo pipefail
run="$SEM_FIXTURE_RUN"
kill "$(cat "$run/host.pid")"
for i in $(seq 1 60); do if ! kill -0 "$(cat "$run/host.pid")" 2>/dev/null; then break; fi; sleep 1; done
if curl -fsS http://127.0.0.1:18080/health/ready >/dev/null 2>&1; then echo 'Fixture host failed to stop'; exit 1; fi
# SQLite WAL, protected tokens, private images and Data Protection keys travel together.
stamp=$(date +%s%N)
tar -czf "$run/data-$stamp.tgz" -C "$run/storage" .
gzip -t "$run/data-$stamp.tgz"
mv "$run/storage" "$run/before-$stamp"
mkdir "$run/storage"
tar -xzf "$run/data-$stamp.tgz" -C "$run/storage"
diff -qr "$run/before-$stamp" "$run/storage" >/dev/null
nohup bash "$run/start.sh" >> "$run/host.log" 2>&1 & echo $! > "$run/host.pid"
for i in $(seq 1 60); do if curl -fsS http://127.0.0.1:18080/health/ready >/dev/null 2>&1; then echo 'Disposable whole-directory restore passed'; exit 0; fi; sleep 1; done
exit 1
RESTORE
chmod +x "$run/restore.sh"
python3 scripts/smtp-fixture.py > "$run/smtp.log" 2>&1 & echo $! > "$run/smtp.pid"
bash "$run/start.sh" > "$run/host.log" 2>&1 & echo $! > "$run/host.pid"
ready=false
for i in $(seq 1 60); do if curl -fsS http://127.0.0.1:18080/health/ready >/dev/null 2>&1; then ready=true; break; fi; sleep 1; done
[[ "$ready" == true ]]
printf 'fixture-startup\t%s\n' "$((SECONDS-fixture_started))" >> verification/acceptance-native/fixture-phases.tsv
export SEM_E2E_KEY="$run/key.pem" SEM_E2E_CERT="$run/cert.pem"
export SEM_STORAGE_UPGRADE_COMMAND="$run/restore.sh"
export SEM_PACED_API_CHECKS=1
case "${SEM_ACCEPTANCE_FOCUS:-full}" in
 full) export SEM_SETTINGS_NAVIGATION_CHECKS=1 SEM_NOTIFICATION_UI_CHECKS=1 SEM_THEME_NAVIGATION_CHECKS=1 SEM_PUBLIC_ANSWER_CHECKS=1 SEM_EVENT_HEADER_CHECKS=1 SEM_EVENT_DESCRIPTION_CHECKS=1 SEM_EVENT_LOCATION_CHECKS=1 SEM_ACCEPTANCE_RETENTION_COMMAND="$run/restore.sh" SEM_EVENT_MATRIX_CHECKS=1 SEM_COMMENT_CHECKS=1 SEM_ACCEPTANCE_WORKFLOW_CHECKS=1 ;;
 timetable-editor-ui) export SEM_TIMETABLE_EDITOR_UI_CHECKS=1 SEM_EDIT_GUARD_CHECKS=1 SEM_EVENT_MANAGEMENT_UI_CHECKS=1 SEM_DEFINITION_ORDER_UI_CHECKS=1 SEM_PROGRESS_UI_CHECKS=1 SEM_INVITATION_UI_CHECKS=1 SEM_SERVICE_MANAGEMENT_UI_CHECKS=1 ;;
 service-management-ui) export SEM_SERVICE_MANAGEMENT_UI_CHECKS=1 ;;
 invitation-ui) export SEM_INVITATION_UI_CHECKS=1 ;;
 profile-ui) export SEM_SETTINGS_NAVIGATION_CHECKS=1 ;;
 reception-ui) export SEM_MEMBER_PROFILE_CHECKS=1 SEM_RECEPTION_ROSTER_CHECKS=1 SEM_MANUAL_RECEPTION_CHECKS=1 ;;
 reception-regression) export SEM_MEMBER_PROFILE_CHECKS=1 SEM_RECEPTION_ROSTER_CHECKS=1 SEM_MANUAL_RECEPTION_CHECKS=1 SEM_EVENT_QR_CHECKS=1 SEM_EVENT_QR_IMAGE_CHECKS=1 SEM_QR_RECEPTION_CHECKS=1 SEM_QR_READER_UI_CHECKS=1 SEM_QR_CAMERA_CHECKS=1 SEM_RECEPTION_OVERVIEW_CHECKS=1 SEM_RECEPTION_HISTORY_CHECKS=1 ;;
 comment-ui) export SEM_COMMENT_CHECKS=1 ;;
 progress-ui) export SEM_PROGRESS_UI_CHECKS=1 ;;
 notification-ui) export SEM_NOTIFICATION_UI_CHECKS=1 ;;
 theme) export SEM_THEME_NAVIGATION_CHECKS=1 ;;
 definition-order-ui) export SEM_DEFINITION_ORDER_UI_CHECKS=1 ;;
 event-management-ui) export SEM_EVENT_MANAGEMENT_UI_CHECKS=1 ;;
 edit-guard) export SEM_EDIT_GUARD_CHECKS=1 ;;
 public-answers) export SEM_PUBLIC_ANSWER_CHECKS=1 ;;
 header-image) export SEM_EVENT_HEADER_CHECKS=1 ;;
 description) export SEM_EVENT_DESCRIPTION_CHECKS=1 ;;
 venue-url) export SEM_EVENT_LOCATION_CHECKS=1 ;;
 admin-search) export SEM_ADMIN_SEARCH_AUDIT_CHECKS=1 ;;
 *) echo 'Unknown acceptance focus; full verification was not run.' >&2; exit 1 ;;
esac
export SEM_E2E_RESULTS="$root/verification/acceptance-native"
browser_started=$SECONDS
status=0
node scripts/e2e.cjs || status=$?
printf 'browser-total\t%s\n' "$((SECONDS-browser_started))" >> verification/acceptance-native/fixture-phases.tsv
exit "$status"
