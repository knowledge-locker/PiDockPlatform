#!/bin/sh
set -eu
target=$1
: "${PIDOCK_PAYLOAD:?Publish the appliance payload before building the image}"
for part in appliance broker updater; do
    case "$part" in appliance) program=PiDock.Appliance.Web;; broker) program=PiDock.Appliance.Broker;; updater) program=PiDock.Updater;; esac
    test -f "$PIDOCK_PAYLOAD/$part/$program.dll"
    rm -rf -- "$target/usr/lib/pidock/$part"
    mkdir -p "$target/usr/lib/pidock/$part"
    cp -a "$PIDOCK_PAYLOAD/$part/." "$target/usr/lib/pidock/$part/"
done
# No signing key, cloud account, owner secret or enrolled device identity is embedded.
mkdir -p "$target/etc/pidock" "$target/var/lib/pidock" "$target/data" "$target/boot/control"
for public in "$target/etc/pidock/update-trust/"*.pub; do
    test -f "$public"
    cp "$public" "${public%.pub}.pem"
    chmod 644 "${public%.pub}.pem"
    rm "$public"
done
rm -f "$target/etc/machine-id"
touch "$target/etc/machine-id"
board=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
"$HOST_DIR/bin/aarch64-buildroot-linux-gnu-gcc" -O2 -Wall -Wextra -Werror -o "$target/usr/lib/pidock/boot-reboot" "$board/boot-reboot.c"
python3 - "$target" "$board/image-profile.json" <<'PY'
import json,os,re,sys
from pathlib import Path
root=Path(sys.argv[1]); profile=json.loads(Path(sys.argv[2]).read_text())
version=os.environ.get('PIDOCK_IMAGE_VERSION',profile['version'])
sequence=int(os.environ.get('PIDOCK_IMAGE_SEQUENCE',profile['sequence']))
minimum=int(os.environ.get('PIDOCK_IMAGE_MINIMUM',profile['minimumSequence']))
if not re.fullmatch(r'[A-Za-z0-9.+-]{1,64}',version) or sequence<=0 or not 0<=minimum<=sequence:
    raise SystemExit('Invalid image version policy')
(root/'etc/pidock/image-profile.json').write_text(json.dumps(dict(schema=1,version=version,sequence=sequence,minimumSequence=minimum,development=True,setupSchema=profile.get('setupSchema',1),usbNodeProtocol=profile.get('usbNodeProtocol',0),sourceCommit=os.environ.get('PIDOCK_SOURCE_COMMIT','unknown')),separators=(',',':'))+'\n')
PY
