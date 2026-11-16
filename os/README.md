# PiDockOS image and hardware integration

The Pi 5 development image pipeline builds a signed, flashable ARM64 image with a dm-verity system, separate A/B boot and root slots, independent signed recovery, and persistent managed data. The current prepared image is **1.0.0-alpha.6**. It includes the reconciled agent/API implementation from the rc.4 source. This is a hardware-acceptance image, not a claim that secure boot has been provisioned on the board.

## Headless card preparation

For a new device, use PiDockCloud’s **Create SD image** workflow to choose Main or Node, review all settings, and download a fresh configured image. The Main owner and authenticator entry belong to that download; old local commissioning bundles are not reused. Follow the [SD installation instructions](../docs/Factory-Provisioning.md#install-a-downloaded-image-on-an-sd-card). No separate formatting is needed; Raspberry Pi Imager writes the compressed custom image with OS customization off and verification enabled.

Legacy operator tooling can also prepare a local owner, Data Protection key ring, local TLS CA and Wi-Fi configuration in the managed-data filesystem without changing signed system/boot bytes. `commission-media.sh` and `configure-media-network.sh` collect secrets in the operator’s Terminal. Their private output must never enter shared images, source control or cloud uploads. Use this route only when deliberately preparing a separate operator-built image.

`tools/personalize_data.py` creates the private filesystem with UID/GID 1654 for appliance state and root-only Wi-Fi/CA-key access. `tools/personalize_image.py` places it into a copy of the public factory image. With `--card-bytes`, it expands the extended-partition and final data-entry/link ranges to the identified card while keeping the image a bounded prefix. Boot/root offsets and bytes remain unchanged. `pidock-data-grow.service` grows ext4 to the prepared partition on first boot. Use the identity-checked Mac writer for this capacity-specific private image, not a generic flash command or a different-sized card.

`tools/flash_macos.py` defaults to read-only validation. Writing requires an explicit `--write`, administrator authorization, a matching SD serial/size, a whole removable SD device and a recorded image checksum. It rechecks identity after unmount and after opening the device, writes only the prepared prefix, verifies a full raw-device readback and ejects the card. Unrelated disks are excluded; a removable SD card in the Mac’s built-in reader can be reported by macOS as internal and is accepted only with its matching physical card identity. Protect and remove private installation copies after acceptance; keep the original owner preparation/recovery material private.

After flashing, power off the Pi, preserve its old installation media, insert the new card, connect power and allow network/time setup to complete. Open `https://pidock.local`. Use the owner/password and authenticator entry created for the flashed Main image. The local certificate needs owner review. Signed-image boot supplies only a plausible initial time; authentication waits for authenticated NTS time. Without an RTC backup battery, cold offline authentication is not certified.

GPIO has no default input/output grants. Outputs stay disabled until individually approved through local owner MFA. Cloud enrollment, remote container control and remote-interface access start off unless explicitly selected in the new Main setup; cloud-managed OS updates remain separately approved locally. The local broker uses its bundled ARM64 executable; container lifecycle support is available, while new deployments remain disabled pending the container trust-policy gate.

## Signing, recovery and acceptance

Pinned build inputs are recorded in `build/sources.json`; Buildroot configuration, signed initramfs and systemd integration are under `buildroot`. `tools/verify_target.py` checks service executables, enabled links, empty public managed state, release provenance and default GPIO policy. `tools/image_components.py` verifies the dm-verity data before signing; `tools/sign_boot.py` signs and verifies the A/B and independent recovery boot images. Credential-free factory publication additionally uses the trusted P-256 release key; see [factory provisioning](../docs/Factory-Provisioning.md).

The prepared boot images use a **development RSA key**. No EEPROM/OTP has been programmed. Hardware-enforced verified boot requires a separately reviewed production-key/EEPROM provisioning procedure after successful physical boot, health/rollback and recovery tests. Never fuse the development key.

The signed recovery ramdisk supports an optional attached physical console and starts no network administration. This appliance is headless: its first acceptance/recovery route is physical SD access on the Mac and re-flashing the validated private image with the preserved owner bundle. Restoring that initial image resets later appliance data/registration to preparation time; preserve managed data separately before using it for recovery after enrollment. There is no SSH or remote ownership-reset path.

Physical acceptance still needs the actual Pi: Wi-Fi/Ethernet, trusted time, HTTPS sign-in, GPIO monitoring without output claims, signed A/B update health promotion, failed-update rollback, power loss and headless media recovery. The supplied image/tool checks do not substitute for those board tests. [Hardware procedure](../docs/Hardware-Testing.md).

The older `os/systemd` units are compatibility templates for the Linux-hosted service installation. Whole-image units are under `os/buildroot`; see [signed service updates](../docs/PiDockOS-Updates.md) for the older updater's separate bootstrap and limits.

Expanded setup uses schema 2, advertised in the signed factory manifest. Main supports separate Ethernet/Wi-Fi DHCP or static IPv4 settings, an optional HTTPS cloud URL and first-boot invitation, and independent remote container/interface consent. Node omits human ownership and external networking; it exposes an isolated USB ECM link for discovery/adoption in **Main → USB nodes**. The Main owner confirms adoption/configuration/release with fresh MFA. Device identity is pinned independently of interface names; no household addressing is required. Available node services are configuration and resource monitoring. USB image flashing from Main, workload placement and node I/O grants remain unavailable.

The signed base enables the USB-C peripheral controller; Main leaves it unbound and uses its USB host ports for nodes. The Node-only gadget binds an ECM function. Host links receive scoped IPv6 link-local addresses and explicit firewall forwarding blocks. Hardware acceptance must verify the exact boards, data cables and powered dock; no power compatibility or successful physical adoption is inferred from software tests.

## Supervised app recovery

The broker unit defaults `Broker__EnableAppRecovery=false`. Enable it together with managed app resources and lifecycle/deployment permissions to allow per-app opt-in recovery. The PiDockOS appliance host must run: it supplies current credentials and coordinates recovery without browser/cloud access. Persist and protect `/var/lib/pidock-broker` and its keys across OS updates. See [shared lifecycle rules](../docs/Application-Lifecycle.md) for durable Stop intent, retry limits, migration of existing apps and remaining health/hardware limits.

## Ordered app shutdown

`Broker__EnableAppOrchestration=false` remains the supplied default. Enabling it requires resources and lifecycle/deployment permission. A graceful broker stop journals and stops resource apps dependent-first; host-shutdown intent resumes in order after broker restart. Explicit maintenance pause remains paused until Owner Resume. The appliance unit orders after the broker and the broker after Docker; broker stop timeout is 220 seconds around a 180-second batch. Preserve private state/keys/journal across upgrades. See [orchestration boundaries](../docs/Application-Orchestration.md); forced shutdown and physical I/O safety remain separate concerns.

## App image update opt-in

`Broker__EnableAppUpdates=false` is the supplied default. Enabling it requires resources, orchestration and lifecycle permissions. Preserve the private `updates` journals and broker key ring across service/OS upgrades, including accepted operations. The appliance update worker uses current credentials; the broker integrates active-update interruption into coordinated shutdown. See [image-only compatibility and rollback limits](../docs/Application-Updates.md).
