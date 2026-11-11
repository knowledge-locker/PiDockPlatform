#!/bin/bash
# Update only PiDockOS program files. Accounts, certificates and telemetry stay in place.
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo 'Run with sudo on the Pi.' >&2; exit 1; }
[ "$(dpkg --print-architecture)" = arm64 ]
stage=$(cd -- "$(dirname -- "$0")" && pwd)
(cd "$stage/appliance" && sha256sum --check ../appliance.sha256 >/dev/null)
current=/usr/lib/pidock/appliance
backup=/usr/lib/pidock/appliance-backup-$(date -u +%Y%m%dT%H%M%SZ)
candidate=$(mktemp -d /usr/lib/pidock/appliance-next-XXXXXXXX)
dropin=/etc/systemd/system/pidock-appliance.service.d/cloud-default.conf
old_dropin=$(mktemp)
had_dropin=0
if [ -f "$dropin" ]; then cp -p "$dropin" "$old_dropin"; had_dropin=1; fi
swapped=0
service_stopped=0
rollback() {
    result=$?
    trap - EXIT
    if [ "$result" != 0 ] && [ "$swapped" = 1 ]; then
        systemctl stop pidock-appliance.service || true
        if [ -d "$current" ]; then mv "$current" "$candidate-failed"; fi
        mv "$backup" "$current"
        if [ "$had_dropin" = 1 ]; then cp -p "$old_dropin" "$dropin"; else rm -f "$dropin"; fi
        systemctl daemon-reload
        systemctl start pidock-appliance.service || true
        echo 'Update failed; previous program files restored.' >&2
    fi
    if [ "$result" != 0 ] && [ "$service_stopped" = 1 ]; then systemctl start pidock-appliance.service || true; fi
    rm -f "$old_dropin"
    if [ -d "$candidate" ]; then rmdir "$candidate" 2>/dev/null || true; fi
    exit "$result"
}
trap rollback EXIT
cp -R "$stage/appliance/." "$candidate/"
chown -R root:root "$candidate"
chmod -R go-w "$candidate"
chmod 755 "$candidate"
systemctl stop pidock-appliance.service
service_stopped=1
mv "$current" "$backup"
swapped=1
mv "$candidate" "$current"
install -d -m 755 "$(dirname "$dropin")"
printf '%s\n' '[Service]' 'Environment=Enrollment__DefaultCloudUrl=https://pidock-dev.knowledgelocker.ai' > "$dropin"
chmod 644 "$dropin"
systemctl daemon-reload
systemctl start pidock-appliance.service
for attempt in $(seq 1 20); do
    if curl -fsS --cacert /etc/pidock/appliance.crt https://localhost:7241/health/live >/dev/null 2>&1; then
        echo 'PiDockOS enrollment UI updated. Existing owner, registration, keys and queued telemetry retained.'
        echo "Previous program files: $backup"
        exit 0
    fi
    sleep 1
done
echo 'PiDockOS did not pass its HTTPS health check.' >&2
exit 1
