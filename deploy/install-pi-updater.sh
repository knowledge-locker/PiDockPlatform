#!/bin/bash
# One-time local installation of the independent updater and a public release-signing trust anchor.
# Usage: sudo ./install-pi-updater.sh /path/to/published-updater release-key-id /path/to/public-key.pem
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo 'Run with sudo on the Pi.' >&2; exit 1; }
[ "$#" = 3 ] || { echo 'Usage: install-pi-updater.sh UPDATER_DIRECTORY KEY_ID PUBLIC_KEY_PEM' >&2; exit 1; }
source_dir=$(realpath -- "$1")
key_id=$2
public_key=$(realpath -- "$3")
[[ "$key_id" =~ ^[a-zA-Z0-9-]{1,64}$ ]] || { echo 'Invalid key ID.' >&2; exit 1; }
[ -f "$source_dir/PiDock.Updater.dll" ]
[ -d /usr/lib/pidock/appliance ] && [ -d /usr/lib/pidock/broker ]
# Refuse a private key: no signing secret belongs on an appliance.
! grep -q 'PRIVATE KEY' "$public_key"
openssl pkey -pubin -in "$public_key" -text -noout | grep -q 'prime256v1'
if [ -f "/etc/pidock/update-trust/$key_id.pem" ]; then
    cmp -s "$public_key" "/etc/pidock/update-trust/$key_id.pem" || { echo 'Key ID already has a different trust anchor.' >&2; exit 1; }
fi
systemctl is-active --quiet pidock-appliance.service pidock-broker.service
if systemctl is-active --quiet pidock-updater.service; then
    echo 'The updater is already running. Upgrade its bootstrap installation separately during local maintenance.' >&2; exit 1
fi
install -d -m 755 /usr/lib/pidock/updater /etc/pidock/update-trust
cp -R "$source_dir/." /usr/lib/pidock/updater/
chown -R root:root /usr/lib/pidock/updater
chmod -R go-w /usr/lib/pidock/updater
install -m 644 "$public_key" "/etc/pidock/update-trust/$key_id.pem"
script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
install -m 644 "$script_dir/../os/systemd/pidock-updater.service" /etc/systemd/system/pidock-updater.service
install -d -m 755 /etc/systemd/system/pidock-appliance.service.d
printf '%s\n' '[Unit]' 'After=pidock-updater.service' '[Service]' 'Environment=Updates__SocketPath=/run/pidock-updater/update.sock' > /etc/systemd/system/pidock-appliance.service.d/updater.conf
chmod 644 /etc/systemd/system/pidock-appliance.service.d/updater.conf
systemctl daemon-reload
systemctl enable --now pidock-updater.service
systemctl restart pidock-appliance.service
systemctl is-active --quiet pidock-updater.service pidock-appliance.service pidock-broker.service
echo 'Independent updater installed. Cloud-managed PiDockOS updates default to off.'
echo 'Install a web/broker build that includes the Updates page once; subsequent signed service updates can use local upload or PiDockCloud.'
