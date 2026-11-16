#!/bin/sh
set -eu
umask 077
cd /var/lib/pidock/identity
[ ! -L ca ] || { echo 'Local CA directory link rejected.' >&2; exit 1; }
# Existing identities remain usable during time outages. New issuance requires trusted time.
if [ -f server/server.crt ] && [ -f server/appliance.pfx ] && openssl x509 -in server/server.crt -checkend 2592000 -noout >/dev/null 2>&1; then exit 0; fi
if [ -f server/appliance.pfx ]; then
    /usr/lib/pidock/updater/PiDock.Updater host-time-check || { echo 'Local certificate renewal deferred until authenticated time is available.'; exit 0; }
else
    while ! /usr/lib/pidock/updater/PiDock.Updater host-time-check; do sleep 2; done
fi
work=$(mktemp -d /var/lib/pidock/identity/.issue-XXXXXX)
trap 'rm -rf "$work"' EXIT
if [ ! -e ca ]; then
    mkdir "$work/ca"
    openssl req -x509 -newkey rsa:3072 -noenc -days 3650 -keyout "$work/ca/ca.key" -out "$work/ca/ca.crt" -subj /CN=PiDockOS-local-CA -addext basicConstraints=critical,CA:TRUE -addext keyUsage=critical,keyCertSign,cRLSign
    chmod 600 "$work/ca/ca.key"
    chmod 644 "$work/ca/ca.crt"
    chmod 755 "$work/ca"
    sync
    mv "$work/ca" ca
    sync
fi
[ -f ca/ca.key ] && [ -f ca/ca.crt ] || { echo 'Incomplete local CA identity; use physical recovery.' >&2; exit 1; }
mkdir "$work/leaf"
openssl req -new -newkey rsa:2048 -noenc -keyout "$work/server.key" -out "$work/server.csr" -subj /CN=pidock.local
serial=$(openssl rand -hex 16)
openssl x509 -req -in "$work/server.csr" -CA ca/ca.crt -CAkey ca/ca.key -set_serial "0x$serial" -days 365 -out "$work/leaf/server.crt" -extfile /usr/share/pidock/local-server.ext
openssl verify -CAfile ca/ca.crt -purpose sslserver "$work/leaf/server.crt"
openssl pkcs12 -export -out "$work/leaf/appliance.pfx" -inkey "$work/server.key" -in "$work/leaf/server.crt" -certfile ca/ca.crt -passout pass:
chown 1654:1654 "$work/leaf/appliance.pfx"
chmod 600 "$work/leaf/appliance.pfx"
chmod 644 "$work/leaf/server.crt"
chmod 755 "$work/leaf"
leaf="leaf-$serial"
sync
mv "$work/leaf" "$leaf"
ln -s "$leaf" server.next
mv -Tf server.next server
sync
# A new leaf retains the same local CA. Restart only the management UI, only on actual renewal.
/usr/bin/systemctl --no-block try-restart pidock-appliance.service
