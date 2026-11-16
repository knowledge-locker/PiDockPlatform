#!/bin/sh
set -eu
flag=/proc/device-tree/chosen/bootloader/tryboot
[ -f "$flag" ] || exit 0
value=$(od -An -tx1 "$flag" | tr -d ' \n')
[ "$value" = 00000001 ] || exit 0
[ ! -f /run/pidock-image/healthy ] || exit 0
# A normal restart clears the firmware's one-shot trial flag. The default remains the previous slot.
exec /usr/bin/systemctl reboot
