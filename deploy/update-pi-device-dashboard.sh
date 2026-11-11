#!/bin/bash
# Install the dashboard agent and narrow broker lifecycle routes. Remote control remains locally opt-in.
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo 'Run with sudo on the Pi.' >&2; exit 1; }
[ "$(dpkg --print-architecture)" = arm64 ]
stage=$(cd -- "$(dirname -- "$0")" && pwd)
for part in appliance broker; do (cd "$stage/$part" && sha256sum --check "$stage/$part.sha256" >/dev/null); done
systemctl is-active --quiet pidock-appliance.service pidock-broker.service
backup=$(mktemp -d /usr/lib/pidock/dashboard-backup-XXXXXXXX)
candidate=$(mktemp -d /usr/lib/pidock/dashboard-next-XXXXXXXX)
dropin=/etc/systemd/system/pidock-broker.service.d/lifecycle.conf
setting=/etc/pidock/broker-lifecycle.env
for file in "$dropin" "$setting"; do if [ -f "$file" ]; then cp -p "$file" "$backup/$(basename "$file")"; fi; done
stopped=0; config_changed=0
cleanup() {
    result=$?; trap - EXIT
    if [ "$result" != 0 ] && [ "$stopped" = 1 ]; then
        systemctl stop pidock-appliance.service pidock-broker.service || true
        for part in appliance broker; do
            if [ -d "$backup/$part" ]; then
                if [ -d "/usr/lib/pidock/$part" ]; then mv "/usr/lib/pidock/$part" "$backup/failed-$part"; fi
                mv "$backup/$part" "/usr/lib/pidock/$part"
            fi
        done
        if [ "$config_changed" = 1 ]; then
            for file in "$dropin" "$setting"; do
                if [ -f "$backup/$(basename "$file")" ]; then cp -p "$backup/$(basename "$file")" "$file"; else rm -f "$file"; fi
            done
        fi
        systemctl daemon-reload
        systemctl start pidock-broker.service pidock-appliance.service || true
        echo 'Update failed; previous program files and broker settings restored.' >&2
    fi
    if [ -d "$candidate" ]; then rm -rf "$candidate"; fi
    exit "$result"
}
trap cleanup EXIT
for part in appliance broker; do
    mkdir "$candidate/$part"; cp -R "$stage/$part/." "$candidate/$part/"
    chown -R root:root "$candidate/$part"; chmod -R go-w "$candidate/$part"; chmod 755 "$candidate/$part"
done
stopped=1; systemctl stop pidock-appliance.service pidock-broker.service
for part in appliance broker; do mv "/usr/lib/pidock/$part" "$backup/$part"; mv "$candidate/$part" "/usr/lib/pidock/$part"; done
config_changed=1
install -d -m 755 "$(dirname "$dropin")"
printf '%s\n' 'Broker__EnableLifecycle=true' > "$setting"; chmod 600 "$setting"
printf '%s\n' '[Service]' "EnvironmentFile=$setting" > "$dropin"; chmod 644 "$dropin"
systemctl daemon-reload
systemctl start pidock-broker.service pidock-appliance.service
for attempt in $(seq 1 20); do
    if systemctl is-active --quiet pidock-broker.service && curl -fsS --cacert /etc/pidock/appliance.crt https://localhost:7241/health/live >/dev/null 2>&1; then
        echo 'PiDockOS device dashboard update installed. Account, registration and queued data preserved.'
        echo 'Remote container control remains off until the local owner enables it under Cloud connection.'
        echo "Previous program files: $backup"
        exit 0
    fi
    sleep 1
done
exit 1
