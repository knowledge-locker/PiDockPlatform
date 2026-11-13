# PiDockOS image and hardware integration

The Pi 5 development image pipeline builds a signed, flashable ARM64 image with a dm-verity system, separate A/B boot and root slots, independent signed recovery, and persistent managed data. The current prepared image is **1.0.0-alpha.2**. It includes the reconciled agent/API implementation from the rc.4 source. This is a hardware-acceptance image, not a claim that secure boot has been provisioned on the board.

## Headless card preparation

A privately prepared local owner, Data Protection key ring, local TLS CA and Wi-Fi configuration can be copied into the managed-data filesystem without changing signed system/boot bytes. `commission-media.sh` and `configure-media-network.sh` collect secrets in the operator's own Terminal. Preserve their private output; never put it in shared images, source control or a cloud upload. The current card reuses the owner already prepared: do not commission another account or replace its authenticator entry.

`tools/personalize_data.py` creates the private filesystem with UID/GID 1654 for appliance state and root-only Wi-Fi/CA-key access. `tools/personalize_image.py` places it into a copy of the public factory image. With `--card-bytes`, it expands the extended-partition and final data-entry/link ranges to the identified card while keeping the image a bounded prefix. Boot/root offsets and bytes remain unchanged. `pidock-data-grow.service` grows ext4 to the prepared partition on first boot. Use the identity-checked Mac writer for this capacity-specific private image, not a generic flash command or a different-sized card.

`tools/flash_macos.py` defaults to read-only validation. Writing requires an explicit `--write`, administrator authorization, a matching SD serial/size, a whole removable SD device and a recorded image checksum. It rechecks identity after unmount and after opening the device, writes only the prepared prefix, verifies a full raw-device readback and ejects the card. Internal and unrelated external disks are excluded. Protect and remove private installation copies after acceptance; keep the original owner preparation/recovery material private.

After flashing, power off the Pi, preserve its old installation media, insert the new card, connect power and allow network/time setup to complete. Open `https://pidock.local`. Use the prepared owner/password and its existing new-SD authenticator entry. The local certificate needs owner review. Signed-image boot supplies only a plausible initial time; authentication waits for authenticated NTS time. Without an RTC backup battery, cold offline authentication is not certified.

GPIO has no default input/output grants. Outputs stay disabled until individually approved through local owner MFA. Cloud enrollment and cloud container, update and remote-interface permissions also start off. The local broker uses its bundled ARM64 executable; container lifecycle support is available, while new deployments remain disabled pending the container trust-policy gate.

## Signing, recovery and acceptance

Pinned build inputs are recorded in `build/sources.json`; Buildroot configuration, signed initramfs and systemd integration are under `buildroot`. `tools/verify_target.py` checks service executables, enabled links, empty public managed state, release provenance and default GPIO policy. `tools/image_components.py` verifies the dm-verity data before signing; `tools/sign_boot.py` signs and verifies the A/B and independent recovery boot images. Credential-free factory publication additionally uses the trusted P-256 release key; see [factory provisioning](../docs/Factory-Provisioning.md).

The prepared boot images use a **development RSA key**. No EEPROM/OTP has been programmed. Hardware-enforced verified boot requires a separately reviewed production-key/EEPROM provisioning procedure after successful physical boot, health/rollback and recovery tests. Never fuse the development key.

The signed recovery ramdisk supports an optional attached physical console and starts no network administration. This appliance is headless: its first acceptance/recovery route is physical SD access on the Mac and re-flashing the validated private image with the preserved owner bundle. Restoring that initial image resets later appliance data/registration to preparation time; preserve managed data separately before using it for recovery after enrollment. There is no SSH or remote ownership-reset path.

Physical acceptance still needs the actual Pi: Wi-Fi/Ethernet, trusted time, HTTPS sign-in, GPIO monitoring without output claims, signed A/B update health promotion, failed-update rollback, power loss and headless media recovery. The supplied image/tool checks do not substitute for those board tests. [Hardware procedure](../docs/Hardware-Testing.md).

The older `os/systemd` units are compatibility templates for the Linux-hosted service installation. Whole-image units are under `os/buildroot`; see [signed service updates](../docs/PiDockOS-Updates.md) for the older updater's separate bootstrap and limits.
