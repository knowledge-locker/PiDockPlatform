#!/bin/sh
# Fixed ECM management function only: no mass storage, serial shell, bridging or forwarding.
set -eu
[ -f /var/lib/pidock/node-mode ] || exit 0
state=/var/lib/pidock/node
mkdir -p "$state"
chmod 700 "$state"
if [ ! -f "$state/usb-id" ]; then
    umask 077
    cat /proc/sys/kernel/random/uuid | tr -d '\n-' > "$state/usb-id.tmp"
    mv "$state/usb-id.tmp" "$state/usb-id"
fi
serial=$(cat "$state/usb-id")
case "$serial" in *[!0-9a-f]*|'') exit 1;; esac
[ "${#serial}" -eq 32 ] || exit 1
modprobe libcomposite
modprobe usb_f_ecm
mountpoint -q /sys/kernel/config || mount -t configfs none /sys/kernel/config
gadget=/sys/kernel/config/usb_gadget/pidock
mkdir -p "$gadget"
cd "$gadget"
if [ -n "$(cat UDC)" ]; then exit 0; fi
printf '%s' 0x1d6b > idVendor
printf '%s' 0x0104 > idProduct
printf '%s' 0x0200 > bcdUSB
printf '%s' 0x0100 > bcdDevice
mkdir -p strings/0x409
printf '%s' "pidock-$serial" > strings/0x409/serialnumber
printf '%s' PiDock > strings/0x409/manufacturer
printf '%s' 'PiDockOS Node' > strings/0x409/product
mkdir -p configs/c.1/strings/0x409
printf '%s' 'Private PiDock management' > configs/c.1/strings/0x409/configuration
# Development descriptors do not qualify a dock or its power delivery.
printf '%s' 500 > configs/c.1/MaxPower
mkdir -p functions/ecm.usb0
printf '%s' usb0 > functions/ecm.usb0/ifname
suffix=$(printf '%s' "$serial" | cut -c 1-10 | sed 's/../:&/g')
printf '%s' "02$suffix" > functions/ecm.usb0/dev_addr
printf '%s' "06$suffix" > functions/ecm.usb0/host_addr
[ -L configs/c.1/ecm.usb0 ] || ln -s ../../functions/ecm.usb0 configs/c.1/ecm.usb0
set -- /sys/class/udc/*
[ "$#" -eq 1 ] && [ -d "$1" ] || { echo 'Exactly one USB peripheral controller is required.' >&2; exit 1; }
printf '%s' "${1##*/}" > UDC
