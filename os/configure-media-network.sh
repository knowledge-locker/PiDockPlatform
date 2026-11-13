#!/bin/bash
set -euo pipefail
repo=$(cd -- "$(dirname -- "$0")/.." && pwd)
[ -t 0 ] && [ -t 1 ] || { echo 'Run this privately in your own Mac Terminal.' >&2; exit 1; }
python3 "$repo/os/tools/configure_wifi.py" --output "$repo/.state/image-build/media-network/wpa_supplicant.conf"
