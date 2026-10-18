#!/usr/bin/env bash
# Run from an inspected/fixed GitHub revision. No Docker socket chmod; no downloads.
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo 'Use sudo for initial installation.' >&2; exit 1; }
for command in docker python3 systemctl install; do command -v "$command" >/dev/null; done
python3 -c 'import sys; assert sys.version_info >= (3,9), "Python 3.9+ required"'
docker compose version >/dev/null
systemctl --version >/dev/null
if [ -f /etc/sem-cd/config.json ]; then
  # Acquiring the existing control lock fails immediately if deployment is active.
  /usr/local/sbin/sem-cd pause >/dev/null
fi
source_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
# Install only fixed code files, never arbitrary data from the checkout.
install -d -o root -g root -m 0755 /opt/sem-cd
for file in updater.py release_gate.py initialize.py; do
  install -o root -g root -m 0644 "$source_dir/$file" "/opt/sem-cd/$file"
done
install -o root -g root -m 0755 "$source_dir/sem-cd" /usr/local/sbin/sem-cd
install -d -o root -g root -m 0700 /etc/sem-cd /var/lib/sem-cd
# Do not override an active configuration. Running this a second time only upgrades
# root-owned updater code/units, not secrets, pause/failure status or the app itself.
if [ ! -f /etc/sem-cd/config.json ]; then
  python3 -I -c 'import runpy,sys; sys.path.insert(0,"/opt/sem-cd"); runpy.run_module("initialize",run_name="__main__")' "$@"
fi
install -o root -g root -m 0644 "$source_dir/sem-cd.service" /etc/systemd/system/sem-cd.service
install -o root -g root -m 0644 "$source_dir/sem-cd.timer" /etc/systemd/system/sem-cd.timer
systemctl daemon-reload
# Timer deliberately waits for explicit resume. This is initial installation only;
# subsequent successful main updates do not require any user action.
systemctl enable --now sem-cd.timer
/usr/local/sbin/sem-cd status
