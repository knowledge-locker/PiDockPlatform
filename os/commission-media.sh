#!/bin/bash
# Run privately in your Mac Terminal. No SSH or network ownership reset is used.
set -euo pipefail
repo=$(cd -- "$(dirname -- "$0")/.." && pwd)
state="$repo/.state/image-build/media-owner"
companion="$repo/.state/image-build/companion/PiDock.Appliance.Web"
[ -x "$companion" ] || { echo 'Build the private Mac companion before owner setup.' >&2; exit 1; }
[ -t 0 ] && [ -t 1 ] || { echo 'Run this directly in your own interactive Mac Terminal.' >&2; exit 1; }
mkdir -p "$state"
chmod 700 "$state"
if [ -f "$state/accounts.protected" ]; then
    echo 'Owner setup is already saved for this SD card. It will not be replaced.'
    exit 0
fi
export ASPNETCORE_ENVIRONMENT=Production
export PiDock__StateDirectory="$state"
export PiDock__RequireTrustedClock=false
printf '\nPiDockOS headless SD setup\nThis creates the local owner for the new SD installation.\nYour current Pi installation and cloud account are unchanged.\nKeep the password and authenticator setup secret in this Terminal.\n\n'
cd -- "$(dirname -- "$companion")"
"$companion" --commission
[ -s "$state/accounts.protected" ] || { echo 'Owner setup is incomplete.' >&2; exit 1; }
printf 'Owner prepared for private SD personalization.\n'
