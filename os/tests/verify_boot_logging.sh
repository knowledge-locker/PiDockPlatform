#!/bin/sh
# Run only on a trusted, freshly generated archive inside an isolated native builder.
# All mounts, device discovery and clocks are replaced inside a disposable chroot.
set -eu
[ "$#" -eq 1 ] || { echo "Usage: verify_boot_logging.sh TRUSTED_INITRAMFS.gz" >&2; exit 2; }
archive=$(readlink -f "$1")
root=$(mktemp -d)
trap 'rm -rf "$root"' EXIT
gzip -dc "$archive" | (cd "$root"; cpio -id --quiet --no-absolute-filenames --no-preserve-owner)
chroot "$root" /bin/sh -n /init
chroot "$root" /bin/sh -n /bin/pidock-boot-status
mkdir -p "$root/run/pidock-boot" "$root/proc"
: >"$root/dev/null"
printf '12.34 1.00\n' >"$root/proc/uptime"
printf 'previous attempt\n' >"$root/run/pidock-boot/pidock-early-boot.log"
printf 'expected diagnostic error\n' >"$root/run/pidock-init-output.log"
for cmd in mount umount sync; do rm -f "$root/bin/$cmd"; printf '#!/bin/sh\nexit 0\n' >"$root/bin/$cmd"; chmod 755 "$root/bin/$cmd"; done
rm -f "$root/bin/blkid"; printf '#!/bin/sh\nprintf "/dev/control\\n"\n' >"$root/bin/blkid"; chmod 755 "$root/bin/blkid"
rm -f "$root/bin/dmesg"; printf '#!/bin/sh\nprintf "expected kernel message\\n"\n' >"$root/bin/dmesg"; chmod 755 "$root/bin/dmesg"
chroot "$root" /bin/pidock-boot-status initramfs_started
grep -q 'previous attempt' "$root/run/pidock-boot/pidock-early-boot.previous.log"
grep -q 'uptime_seconds=12.34' "$root/run/pidock-boot/pidock-early-boot.log"
grep -q 'expected diagnostic error' "$root/run/pidock-boot/pidock-early-boot.log"
grep -q 'expected kernel message' "$root/run/pidock-boot/pidock-early-boot.log"
chroot "$root" /bin/pidock-boot-status init_failed 17
grep -q 'exit_code=17' "$root/run/pidock-boot/pidock-early-boot.log"
grep -q 'previous attempt' "$root/run/pidock-boot/pidock-early-boot.previous.log"
head -c 100000 /dev/zero >"$root/run/pidock-init-output.log"
chroot "$root" /bin/pidock-boot-status clock_check
[ "$(wc -c <"$root/run/pidock-boot/pidock-early-boot.log")" -lt 65536 ]
before=$(sha256sum "$root/run/pidock-boot/pidock-early-boot.log")
chroot "$root" /bin/pidock-boot-status invalid_stage
[ "$before" = "$(sha256sum "$root/run/pidock-boot/pidock-early-boot.log")" ]
# A missing/ambiguous control partition is diagnostic-only and cannot block startup.
printf '#!/bin/sh\nprintf "/dev/control\\n/dev/duplicate\\n"\n' >"$root/bin/blkid"
chroot "$root" /bin/pidock-boot-status console_ready
[ "$before" = "$(sha256sum "$root/run/pidock-boot/pidock-early-boot.log")" ]
printf 'Passed: stage export, uptime, errors, kernel output, exit code, previous attempt, size bound, invalid stage, ambiguous media.\n'

# Exercise actual startup: a failed or stalled clock helper cannot stop root discovery.
chroot "$root" /bin/boot-clock 946684800
if grep -q 'root_search' "$root/init"; then
    printf '#!/bin/sh\nprintf "/dev/control\\n"\n' >"$root/bin/blkid"
    # Missing signed descriptor deliberately stops the test before real root discovery.
    rm "$root/etc/root.env"
    rm -f "$root/usr/lib/pidock/boot-clock-set"
    cat >"$root/usr/lib/pidock/boot-clock-set" <<'CLOCK'
#!/bin/sh
echo 'injected clock failure' >&2
exit 42
CLOCK
    chmod 755 "$root/usr/lib/pidock/boot-clock-set"
    : >"$root/dev/console"
    : >"$root/dev/tty1"
    : >"$root/run/pidock-early.log"
    code=0
    chroot "$root" /init || code=$?
    [ "$code" -ne 0 ]
    grep -q 'stage=clock_deferred' "$root/run/pidock-boot/pidock-early-boot.log"
    grep -q 'exit_code=42' "$root/run/pidock-boot/pidock-early-boot.log"
    grep -q 'stage=root_descriptor' "$root/run/pidock-boot/pidock-early-boot.log"
    grep -q 'injected clock failure' "$root/run/pidock-boot/pidock-early-boot.log"
    ! grep -q 'stage=clock_ready' "$root/run/pidock-boot/pidock-early-boot.log"
    printf '#!/bin/sh\nexec /bin/sleep 30\n' >"$root/usr/lib/pidock/boot-clock-set"
    : >"$root/run/pidock-early.log"
    code=0
    chroot "$root" /init || code=$?
    [ "$code" -ne 0 ]
    grep -q 'stage=clock_deferred' "$root/run/pidock-boot/pidock-early-boot.log"
    grep -q 'stage=root_descriptor' "$root/run/pidock-boot/pidock-early-boot.log"
    ! grep -q 'stage=clock_ready' "$root/run/pidock-boot/pidock-early-boot.log"
    grep -q 'exit_code=124' "$root/run/pidock-boot/pidock-early-boot.log"
    grep -q 'exceeded five seconds' "$root/run/pidock-boot/pidock-early-boot.log"
    # Successful bootstrap follows its own path and is not reported as deferred.
    printf '#!/bin/sh\nexit 0\n' >"$root/usr/lib/pidock/boot-clock-set"
    : >"$root/run/pidock-early.log"
    chroot "$root" /init || true
    grep -q 'stage=clock_ready' "$root/run/pidock-boot/pidock-early-boot.log"
    ! grep -q 'stage=clock_deferred' "$root/run/pidock-boot/pidock-early-boot.log"
    rm "$root/dev/console"
    code=0
    chroot "$root" /init || code=$?
    [ "$code" -ne 0 ]
    grep -q 'stage=console_open' "$root/run/pidock-boot/pidock-early-boot.log"
    grep -q 'stage=init_failed' "$root/run/pidock-boot/pidock-early-boot.log"
    echo 'Passed: clock success, failure and actual five-second timeout; root discovery continues; console errors exported.'
fi
