#!/bin/sh
# systemd attaches this process exclusively to the local keyboard/display, never a login shell.
set -eu
export PATH=/usr/bin:/usr/sbin:/bin:/sbin
export ASPNETCORE_ENVIRONMENT=Production
export PiDock__StateDirectory=/var/lib/pidock/appliance
export PiDock__RequireTrustedClock=true
while true; do
 printf '\nPiDockOS local console\n1. Set up the first appliance owner\n2. Set current UTC time for authenticated-time bootstrap\n3. Restart into physical recovery\n4. Show network addresses and local CA fingerprint\n5. Power off\nChoice: '
 choice=
 read -r choice || { sleep 1; continue; }
 case "$choice" in
  1)
   if [ -e /var/lib/pidock/appliance/accounts.protected ]; then echo 'An owner already exists. Use physical recovery to replace credentials.'; continue; fi
   cd /usr/lib/pidock/appliance
   setpriv --reuid=1654 --regid=1654 --clear-groups --bounding-set=-all --inh-caps=-all --ambient-caps=-all ./PiDock.Appliance.Web --commission || echo 'Setup is incomplete. Check authenticated time and try again.'
   ;;
  2)
   printf 'Current UTC time as YYYY-MM-DD HH:MM:SS: '
   read -r value || continue
   echo "$value" | grep -Eq '^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}$' || { echo 'Invalid format.'; continue; }
   date -u -s "$value" || echo 'Clock repair failed.'
   systemctl restart chrony.service pidock-time.service
   echo 'Protected access still waits for authenticated network time.'
   ;;
  3)
   printf 'Applications will stop. Type RECOVERY to restart into the local recovery environment: '
   read -r answer || continue
   [ "$answer" = RECOVERY ] || continue
   exec systemctl --reboot-argument=1 reboot
   ;;
  4) ip -brief address; openssl x509 -in /etc/pidock/appliance.crt -noout -fingerprint -sha256 || true;;
  5) exec systemctl poweroff;;
  *) echo 'Choose 1, 2, 3, 4 or 5.';;
 esac
 done
