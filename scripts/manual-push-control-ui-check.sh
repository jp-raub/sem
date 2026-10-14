#!/usr/bin/env bash
set -euo pipefail
# Isolated current-source native-passkey acceptance, not Docker/DXP or external Push.
cd "$(dirname "$0")/.."
mkdir -p verification
ROOT=$(mktemp -d /tmp/sem-manual-ui.XXXXXXXX)
APP_PID= SMTP_PID=
cleanup() {
  if [[ -n "$APP_PID" ]]; then kill "$APP_PID" 2>/dev/null || true; wait "$APP_PID" 2>/dev/null || true; fi
  if [[ -n "$SMTP_PID" ]]; then kill "$SMTP_PID" 2>/dev/null || true; wait "$SMTP_PID" 2>/dev/null || true; fi
  rm -rf "$ROOT"
}
trap cleanup EXIT INT TERM
python3 - <<'PY'
import socket
for port in [18080,18443,18025,2525]:
    with socket.socket() as s:
        s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1)
        s.bind(('127.0.0.1',port))
PY
dotnet publish src/Sem.Web -c Release -o "$ROOT/publish" > verification/manual-ui-publish.log 2>&1
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$ROOT/key.pem" -out "$ROOT/cert.pem" -days 1 -subj '/CN=localhost' >/dev/null 2>&1
python3 scripts/smtp-fixture.py > verification/manual-ui-smtp.log 2>&1 &
SMTP_PID=$!
ASPNETCORE_URLS=http://127.0.0.1:18080 ASPNETCORE_ENVIRONMENT=Production Push__PublicKey= Push__PrivateKey= \
Sem__PublicUrl=https://localhost:18443/ Sem__TrustedProxies__0=127.0.0.1 Sem__DataDirectory="$ROOT/runtime" \
Sem__BootstrapSecret=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA \
Smtp__Host=127.0.0.1 Smtp__Port=2525 Smtp__From=fixture@example.test Smtp__Tls=None Smtp__AllowInsecureLocalTest=true \
dotnet "$ROOT/publish/Sem.Web.dll" --contentRoot "$ROOT/publish" > verification/manual-ui-server.log 2>&1 &
APP_PID=$!
for n in $(seq 1 60); do
 if curl -fsS http://127.0.0.1:18080/health/ready >/dev/null 2>&1 && curl -fsS http://127.0.0.1:18025/ >/dev/null 2>&1; then break; fi
 sleep 1
done
curl -fsS http://127.0.0.1:18080/health/ready >/dev/null
SEM_E2E_KEY="$ROOT/key.pem" SEM_E2E_CERT="$ROOT/cert.pem" SEM_PACED_API_CHECKS=1 SEM_MANUAL_NOTIFICATION_UI_CHECKS=1 SEM_MANUAL_PUSH_CONTROL_CHECKS=1 SEM_MANUAL_PUSH_STATUS_CHECKS=1 node scripts/e2e.cjs
