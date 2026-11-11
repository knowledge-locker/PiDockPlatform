# Raspberry Pi hardware testing

## Available boards and scope

| Board | Test role | Constraints |
| --- | --- | --- |
| Raspberry Pi 4 | Primary application, Docker, storage, network and I/O tests | Record actual RAM; use a 64-bit OS and ARM64 images |
| Raspberry Pi 3 Model B | Constrained-memory compatibility and recovery-from-pressure tests | 1 GB RAM; one small workload initially; run cloud services on another machine |
| Raspberry Pi 5 with RTC battery | Production reference and later certification | Required for Pi 5 firmware, RP1 GPIO, RTC battery, secure boot and final image validation |

Pi 3B has a 64-bit CPU and 1 GB RAM, as listed in its [official specification](https://www.raspberrypi.com/products/raspberry-pi-3-model-b/). The Pi 4 is also 64-bit, and the [official Pi 4 specification](https://www.raspberrypi.com/products/raspberry-pi-4-model-b/) describes its memory variants and shared 40-pin header. CPU capability does not prove the installed OS is 64-bit: check both before deployment.

Start with a maintained 64-bit Raspberry Pi OS Lite installation as a temporary test host, using a spare card or an existing installation the owner has approved for testing. Do not reflash a working board, change credentials, enable remote services, or alter connected equipment without an explicit test arrangement. The host must have .NET 10 runtime and an ARM64-capable Docker installation; use [Docker's Debian/ARM64 instructions](https://docs.docker.com/engine/install/debian/) for a compatible Raspberry Pi OS version. Building on the development computer avoids putting the SDK workload on the Pi 3B.

## Read-only discovery

Run this on the selected Pi, from the project folder or after copying only the script:

```sh
python3 tools/pi-preflight.py
# To report the filesystem intended for PiDock data:
python3 tools/pi-preflight.py --data-path /var/lib
```

It reports board model, CPU/OS bitness, RAM, storage capacity, installed .NET runtimes, Docker availability, and time-service status. It installs nothing, does not use sudo, does not read credentials, and does not change the clock, GPIO, network or files. Missing tools and denied Docker access are reported as unavailable. A zero exit code indicates an eligible ARM64 test board, not security certification or successful deployment.

## Battery-free development clock policy

The Pi 4 and Pi 3B test setup does not require an added RTC battery while network time is available. Continuous power reduces interruptions but does not establish correct time after every reboot, kernel reset or unexpected outage. Establish a plausible clock locally if necessary, then synchronize through the chosen trusted time service before testing TOTP. A synchronized NTP status alone does not prove that transport authentication used NTS. Do not disable certificate checks or TOTP to work around incorrect time.

The current engineering preview labels clock trust unverified and does not yet enforce the production trusted-clock login gate. Test the normal powered/networked path now; battery holdover, offline cold-start authentication, clock repair and fail-closed behavior remain explicit production gates.

## Initial test sequence

1. Record model, RAM, architecture, OS version and card/SSD capacity using the read-only preflight. Establish the board's hostname/IP and authorized access method.
2. Confirm 64-bit OS/runtime, network time and Docker availability. Back up any existing application data before deployment changes.
3. Publish the appliance and broker for Linux ARM64 on the development machine. Transfer binaries into a separate test directory; do not replace host services or firmware.
4. Provision an appliance owner through the trusted local console. Use HTTPS with a physically verifiable certificate. Any temporary development remote access is distinct from production physical recovery.
5. Start monitoring and validate CPU, memory, temperature, storage and network counters against OS measurements. Keep cloud ingestion on the existing separate server.
6. Deploy one small stateless ARM64 app. Measure platform RAM, startup time, CPU, temperature and storage behavior on Pi 4, then Pi 3B. The specification's 512 MiB platform budget is a target to measure, not proof that a 1 GB board has room for arbitrary workloads.
7. Exercise cleanup using disposable monitoring records within a bounded test budget. Avoid filling the board's actual boot filesystem. Confirm application files survive.
8. Validate the physical GPIO driver and pin ownership before enabling any real output override. The shared header diagram does not make Pi 5 and Pi 3/4 register access interchangeable. Start with an unconnected pin or an LED/resistor fixture; do not force attached equipment by default.
9. Test service restart, network loss and reconnection. Schedule reboot/power-loss tests separately so existing workloads are not interrupted unexpectedly.

No Pi has been contacted, flashed or modified by documenting this plan. Hardware access details are still needed before on-device testing.
