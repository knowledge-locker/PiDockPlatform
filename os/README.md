# Raspberry Pi hardware and image integration

There is no flashable PiDock image yet. The runnable .NET services are the input to the compatibility milestone, not evidence that the OS trust chain is complete.

Target: Raspberry Pi 5 ARM64, fitted RTC battery, Buildroot with glibc/systemd/udev, a Pi 5-supported kernel and firmware, Docker, .NET 10 runtime, chrony with NTS, managed data ext4, verified system A/B slots and signed recovery.

Available test boards: Pi 4 (primary functional testing) and Pi 3 Model B (constrained-resource testing). Start on 64-bit Raspberry Pi OS Lite as a temporary test host; keep cloud services off-board. Network synchronization can establish time after boot during powered development, with physical time bootstrap if needed for TLS. These tests do not certify the Pi 5 production image or battery-backed offline behavior. See [the hardware test procedure](../docs/Hardware-Testing.md).

Before selecting the production defconfig:

- Boot the pinned firmware/kernel on the real Pi 5 and validate Ethernet, Wi-Fi, USB, SD/SSD, GPIO character devices, UART, PWM, camera and HDMI.
- Cross-publish each service for `linux-arm64` and resolve native runtime dependencies against the chosen libc.
- Validate RTC power-off retention, NTS/TLS initial clock bootstrap, clock rollback and dead-battery behavior.
- Prove secure boot, verified root filesystem, recovery and A/B promotion/rollback on the board before irreversible provisioning.
- Add separate OS users, the privileged broker group and data-volume quotas; verify application containers cannot reach management sockets or secrets.
- Validate power loss, full volume, application storage abuse, invalid updates and recovery without internet.

The systemd units are integration templates. They do not create users/groups, install .NET, set trusted HTTPS certificates, provision secrets, configure the firewall or implement secure boot. Do not enable them on an unattended device without completing those steps. Production RTC/NTS gating and physical recovery are required before release.
