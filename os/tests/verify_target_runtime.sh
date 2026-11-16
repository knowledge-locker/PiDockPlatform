#!/bin/sh
# Native-architecture Linux check; run in an isolated builder with chroot capability.
set -eu
[ "$#" -eq 1 ] || { echo 'Usage: verify_target_runtime.sh TARGET_ROOT' >&2; exit 2; }
root=$(readlink -f "$1")
chroot "$root" /usr/bin/kmod --version
for name in brcmfmac usb_f_ecm; do
    modules=$(find "$root/lib/modules" -type f -name "$name.ko*" | wc -l)
    [ "$modules" -eq 1 ] || { echo "Expected one installed $name driver" >&2; exit 1; }
    file=$(find "$root/lib/modules" -type f -name "$name.ko*")
    file=${file#"$root"}
    actual=$(chroot "$root" /usr/sbin/modinfo -F name "$file")
    [ "$actual" = "$name" ] || { echo "Cannot read packaged $name driver" >&2; exit 1; }
    echo "Packaged driver metadata decoded: $name"
done
