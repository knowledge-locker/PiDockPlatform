#!/usr/bin/env python3
"""Run the disposable shared app inside the isolated PiDockOS review shell."""
import argparse
import datetime
import os
from pathlib import Path
import secrets
import signal
import subprocess
import sys
import time
import tempfile
import resource


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host-port", type=int, default=7355)
    parser.add_argument("--sample-port", type=int, default=7365)
    parser.add_argument("--services-port", type=int, default=7356)
    args = parser.parse_args()
    # macOS shells often start with 256 descriptors, below the two-listener ASP.NET review's needs.
    # Raise only this disposable launcher/children, bounded by the existing hard limit; no system setting changes.
    soft, hard = resource.getrlimit(resource.RLIMIT_NOFILE)
    if soft < 1024:
        resource.setrlimit(resource.RLIMIT_NOFILE, (1024 if hard == resource.RLIM_INFINITY else min(1024, hard), hard))
    if any(port < 1024 or port > 65535 for port in (args.host_port, args.sample_port, args.services_port)) or len({args.host_port, args.sample_port, args.services_port}) != 3:
        parser.error("Choose three different unprivileged ports.")
    root = Path(__file__).resolve().parents[2]
    projects = ["PiDock.AppUi.Sample", "PiDock.UiReview"]
    for project in projects:
        subprocess.run(["dotnet", "build", str(root / "tools" / project), "-c", "Release"], check=True)
    # Do not inherit real app registrations, credentials, broker paths or grants into a review fixture.
    env = {key: value for key, value in os.environ.items()
           if not key.lower().startswith(("pidock__", "pidock:", "broker__", "broker:", "sample__", "review__"))}
    env["ASPNETCORE_ENVIRONMENT"] = "Development"
    env["Logging__LogLevel__Default"] = "Warning"
    token = secrets.token_hex(32)
    expiry = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=1)).isoformat()
    delivery = tempfile.TemporaryDirectory(prefix="pidock-connected-review-", dir="/private/tmp" if sys.platform == "darwin" else None)
    connection_file = str(Path(delivery.name) / "connection.json")
    host = env | {"Review__Port": str(args.host_port), "Review__ConnectSharedServices": "true", "Review__ServicesPort": str(args.services_port), "Review__SamplePort": str(args.sample_port), "Review__SampleAssembly": str(root / "tools" / "PiDock.AppUi.Sample" / "bin" / "Release" / "net10.0" / "PiDock.AppUi.Sample.dll"), "Review__ConnectionFile": connection_file, "PiDock__ApplicationUi__Credentials__sample__Token": token,
                  "PiDock__ApplicationUi__Credentials__sample__ExpiresAt": expiry}
    prefix = "PiDock__ApplicationUi__Registrations__0__"
    fields = {"Contribution__SchemaVersion": "1", "Contribution__AppId": "ui-sample", "Contribution__Id": "main",
              "Contribution__Title": "Shared app sample", "Contribution__EntryPath": "index.html",
              "Contribution__RequiredUiVersion": "1.0", "Contribution__RequiredStyleVersion": "1.0",
              "ServiceOrigin": f"http://127.0.0.1:{args.sample_port}/", "Enabled": "true",
              "Bridge__Version": "1.0", "Bridge__CredentialId": "sample", "Bridge__CommandsPath": "commands",
              "Bridge__StreamPath": "events"}
    fields.update({f"PublicAssets__{index}": name for index, name in enumerate(
        ["assets/components.css", "assets/tokens.css", "assets/client.js"])})
    fields.update({f"Bridge__Commands__{index}": name for index, name in enumerate(
        ["counter.increment", "counter.reset", "stream.disconnect", "demo.start", "demo.stop", "demo.bank-toggle", "demo.reset", "engineering.force", "engineering.release", "engineering.release-all"])})
    host.update({prefix + key: value for key, value in fields.items()})
    children = []

    def stop(_signal, _frame):
        raise KeyboardInterrupt

    signal.signal(signal.SIGTERM, stop)
    try:
        for project, settings in [("PiDock.UiReview", host)]:
            folder = root / "tools" / project
            children.append(subprocess.Popen(["dotnet", str(folder / "bin" / "Release" / "net10.0" / f"{project}.dll")],
                                             cwd=folder, env=settings))
        print(f"Disposable sample: http://127.0.0.1:{args.host_port}/apps/ui-sample/main", flush=True)
        print("Connected shared simulation/engineering review; no appliance Docker broker or physical hardware. Lifecycle controls: /sample-lifecycle. Press Ctrl+C to stop the review and its sample process.", flush=True)
        while all(child.poll() is None for child in children):
            time.sleep(0.25)
        return 1
    except KeyboardInterrupt:
        return 0
    finally:
        for child in children:
            if child.poll() is None:
                child.terminate()
        for child in children:
            try:
                child.wait(timeout=10)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait()
        delivery.cleanup()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except subprocess.CalledProcessError:
        sys.exit("The review build failed; no sample was launched.")
