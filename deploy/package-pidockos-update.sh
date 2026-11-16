#!/bin/bash
# Build and sign a PiDockOS service package. Run only on a trusted release-signing workstation.
# Usage: package-pidockos-update.sh VERSION RUNTIME KEY_ID PRIVATE_KEY_FILE OUTPUT_ZIP
set -euo pipefail
[ "$#" = 5 ] || { echo 'Usage: package-pidockos-update.sh VERSION RUNTIME KEY_ID PRIVATE_KEY_FILE OUTPUT_ZIP' >&2; exit 1; }
version=$1
runtime=$2
key_id=$3
key_file=$(realpath -- "$4")
case "$runtime" in linux-arm64|linux-x64) ;; *) echo 'Supported runtimes: linux-arm64, linux-x64' >&2; exit 1;; esac
repo=$(cd -- "$(dirname -- "$0")/.." && pwd)
output_dir=$(cd -- "$(dirname -- "$5")" && pwd)
output="$output_dir/$(basename -- "$5")"
[ ! -e "$output" ] || { echo 'Output already exists.' >&2; exit 1; }
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
for part in appliance broker; do
    if [ "$part" = appliance ]; then project=PiDock.Appliance.Web; else project=PiDock.Appliance.Broker; fi
    dotnet publish "$repo/src/$project/$project.csproj" -c Release -r "$runtime" --self-contained false /p:UseAppHost=false -o "$work/$part"
done
dotnet run --project "$repo/src/PiDock.Updater" -- pack "$work" "$output" "$version" "$runtime" "$key_id" "$key_file"
echo "Signed PiDockOS package: $output"
