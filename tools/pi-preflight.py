#!/usr/bin/env python3
"""Read-only Raspberry Pi test-host inventory; no secrets or configuration changes."""
import argparse
import json
import platform
import shutil
import struct
import subprocess
from pathlib import Path


def command(args):
    if shutil.which(args[0]) is None:
        return {"available": False, "reason": "tool not installed"}
    try:
        result = subprocess.run(args, capture_output=True, text=True, timeout=10, check=False)
        return {"available": result.returncode == 0,
                "value": result.stdout.strip()[:4096] if result.returncode == 0 else "unavailable or permission denied"}
    except (OSError, subprocess.TimeoutExpired):
        return {"available": False, "reason": "command unavailable or timed out"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-path", default=".", help="existing filesystem path to measure")
    args = parser.parse_args()
    model_path = Path("/proc/device-tree/model")
    model = model_path.read_text().rstrip("\x00\n") if model_path.exists() else "Not detected"
    memory = None
    if Path("/proc/meminfo").exists():
        for line in Path("/proc/meminfo").read_text().splitlines():
            if line.startswith("MemTotal:"):
                memory = int(line.split()[1]) // 1024
                break
    machine = platform.machine()
    bits = struct.calcsize("P") * 8
    eligible = platform.system() == "Linux" and machine in ("aarch64", "arm64") and bits == 64 and any(
        board in model for board in ("Raspberry Pi 4 Model B", "Raspberry Pi 3 Model B", "Raspberry Pi 5"))
    usage = shutil.disk_usage(args.data_path)
    report = {
        "board": model, "system": platform.system(), "architecture": machine, "userspaceBits": bits,
        "memoryMiB": memory, "eligibleArm64TestBoard": eligible,
        "storage": {"path": str(Path(args.data_path).resolve()), "capacityBytes": usage.total, "freeBytes": usage.free},
        "dotnetRuntimes": command(["dotnet", "--list-runtimes"]),
        "dockerServer": command(["docker", "info", "--format", "{{.OSType}}/{{.Architecture}}"]),
        "ntpSynchronized": command(["timedatectl", "show", "--property=NTPSynchronized", "--value"]),
        "ntsAuthentication": command(["chronyc", "authdata"]),
        "clockNote": "Network synchronization and authenticated time must be checked after boot. No RTC holdover is assumed.",
        "certification": "Inventory only; production security and physical GPIO behavior are not validated."
    }
    print(json.dumps(report, indent=2))
    return 0 if eligible else 2


if __name__ == "__main__":
    raise SystemExit(main())
