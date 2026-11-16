#!/bin/sh
# Run as root inside the isolated native-architecture Linux image-builder container.
# Input must be a freshly generated, trusted build artifact, not an uploaded archive.
set -eu
[ "$#" -eq 2 ] || { echo 'Usage: verify_initramfs_runtime.sh INITRAMFS.gz normal|recovery' >&2; exit 2; }
case "$2" in normal|recovery) ;; *) exit 2;; esac
[ "$(id -u)" -eq 0 ] || { echo 'An isolated container with chroot capability is required.' >&2; exit 2; }
archive=$(readlink -f "$1")
root=$(mktemp -d)
trap 'rm -rf -- "$root"' EXIT HUP INT TERM
gzip -dc "$archive" | (cd "$root"; cpio -id --quiet --no-absolute-filenames --no-preserve-owner)
chroot "$root" /bin/sh -n /init
chroot "$root" /bin/sh -n /bin/pidock-boot-status
chroot "$root" /bin/pidock-boot-status initramfs_started
grep -q '^stage=initramfs_started$' "$root/run/pidock-early.log"
chroot "$root" /bin/blkid --version
if [ "$2" = normal ]; then
    chroot "$root" /bin/veritysetup --version
else
    chroot "$root" /bin/sh -n /bin/pidock-recovery
    chroot "$root" /bin/setpriv --version
fi
# Starting the actual shell catches lost loader search-directory links, even when
# every DT_NEEDED library exists elsewhere in the archive.
chroot "$root" /bin/sh -c 'printf "%s\n" "Early startup shell executes successfully."'
