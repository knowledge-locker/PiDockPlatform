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
chroot "$root" /bin/boot-clock 946684800
chroot "$root" /bin/blkid --version
if [ "$2" = normal ]; then
    chroot "$root" /bin/pidock-boot-services
    grep -q '^Environment=Updates__SocketPath=/run/pidock-updater/update.sock$' "$root/run/systemd/system/pidock-appliance.service.d/90-pidock-updater.conf"
    grep -q '^Before=pidock-updater.service$' "$root/run/systemd/system/pidock-first-boot-diagnostics.service"
    ! grep '^Before=' "$root/run/systemd/system/pidock-first-boot-diagnostics.service" | grep -q 'pidock-appliance.service'
    grep -q '^RuntimeWatchdogSec=10s$' "$root/run/systemd/system.conf.d/zz-pidock-watchdog.conf"
    grep -q '^RebootWatchdogSec=10s$' "$root/run/systemd/system.conf.d/zz-pidock-watchdog.conf"
    grep -q '^SyncIntervalSec=1s$' "$root/run/systemd/journald.conf.d/90-pidock-startup.conf"
    grep -q '^ReadWritePaths=/run/pidock-time /run/chrony$' "$root/run/systemd/system/pidock-time.service"
    grep -q '^ProtectClock=true$' "$root/run/systemd/system/pidock-time.service"
    grep -q '^CapabilityBoundingSet=CAP_DAC_OVERRIDE$' "$root/run/systemd/system/pidock-time.service"
    chroot "$root" /bin/veritysetup --version
    # Exercise the exact shipped open command, not just the executable's version.
    # A nonexistent input prevents any host device mapping; CLI parsing must pass.
    command=$(sed -n '/^veritysetup open /s/ >>.*//p' "$root/init")
    [ -n "$command" ]
    result=0
    chroot "$root" /bin/sh -c 'export PATH=/bin:/sbin; . /etc/root.env; root_device=/nonexistent-pidock-test; '"$command" >"$root/open-check.log" 2>&1 || result=$?
    [ "$result" -ne 0 ]
    if grep -Eiq 'unknown option|unrecognized option|invalid action' "$root/open-check.log"; then
        cat "$root/open-check.log" >&2
        exit 1
    fi
    grep -q 'nonexistent-pidock-test' "$root/open-check.log"
    echo 'Signed verity open command parses successfully; nonexistent device rejected.'
else
    chroot "$root" /bin/sh -n /bin/pidock-recovery
    chroot "$root" /bin/setpriv --version
fi
# Starting the actual shell catches lost loader search-directory links, even when
# every DT_NEEDED library exists elsewhere in the archive.
chroot "$root" /bin/sh -c 'printf "%s\n" "Early startup shell executes successfully."'
