# PiDockOS factory provisioning

PiDockCloud's **Create SD image** page (`/provisioning`) is available to workspace Owners and Administrators. It publishes credential-free, signed Raspberry Pi 5 factory packages and prepares private, headless installation downloads in the browser. It does not build arbitrary operating-system source on the cloud server or receive release-signing private keys.

Choose a published image, configure a separate appliance owner and Ethernet or Wi-Fi, add the new authenticator entry, and verify two consecutive codes. Download authorization uses the shared cloud MFA dialog. Flash the resulting `.img.gz` with Raspberry Pi Imager's custom-image option, without Imager OS customization. Flashing erases the target media. After boot, connect to `https://pidock.local`, review the local certificate, and sign in. Cloud enrollment and remote permissions remain separate local actions. GPIO outputs start disabled.

Device passwords, authenticator seeds and wireless credentials stay in the browser. The browser derives an ASP.NET Identity v3 PBKDF2-SHA512 password hash and WPA2 PSK, then streams personalization into the factory image's reserved 1 MiB setup file. Signed system, boot and recovery bytes are preserved. The download contains private provisioning material: keep it secure, never publish it as a factory release, and delete it after flashing. SD cards are not encrypted; deleting the consumed setup file is not secure erasure of flash media.

The signed first-boot importer accepts only bounded, typed owner/network settings at a fixed boot-partition location. It protects the imported account using the appliance's own key ring, writes Wi-Fi settings with root-only permissions, and removes the consumed boot file. It never replaces an existing owner or enables cloud permissions. An interrupted import can resume without resetting an existing account. Initial provisioning is not an ownership-recovery API.

## Install a downloaded image on an SD card

Use a separate SD card of at least 16 GB for each Main or Node. **No pre-formatting or filesystem choice is required.** The image supplies its own partition table, boot filesystems and Linux filesystems. Do not format it as APFS, exFAT or FAT32 first, and do not copy the `.img.gz` file onto a mounted card.

1. Open [Raspberry Pi Imager](https://www.raspberrypi.com/software/) on macOS, Windows or Linux and insert the intended card. Check its name/capacity carefully; writing erases that card.
2. Choose **Use custom** and select the configured `-main-private.img.gz` or `-node-private.img.gz`. The compressed image can be selected directly.
3. Choose the intended card as storage. Skip OS customization if offered: PiDockCloud has already included the appliance settings. Do not apply Imager owner, Wi-Fi or SSH settings.
4. Write and leave verification enabled. Wait for both operations to finish, then safely eject. If macOS offers to initialize an unreadable Linux partition, dismiss that prompt without initializing it.
5. Power off the Pi before inserting the card. Preserve the old installation media until hardware acceptance succeeds.

For **Main**, connect the selected network and power on. Allow network/trusted-time initialization, then open `https://pidock.local` or the configured static address. Review the appliance certificate and use the local owner/authenticator entry created for this particular download. Optional first-boot cloud registration follows the choices in that image.

For **Node**, flash its separate download to another card, boot with suitable power, connect its supported USB-C data interface to the Main using a data-capable connection, and adopt it under **Main → USB nodes**. It has no standalone owner or cloud sign-in. Physical USB/power acceptance remains required for the supported hardware.

GPIO outputs remain disabled. Configured images contain private setup; keep them secure and remove unneeded copies after successful installation. The cloud’s **Clear temporary image** control removes the browser preparation copy, not the saved download. See also [Raspberry Pi’s official image-writing instructions](https://www.raspberrypi.com/documentation/computers/getting-started.html).

## Release preparation

The Buildroot image pipeline is under `os/buildroot`, with pinned sources in `os/build/sources.json`. Build and sign boot/root components offline. No private signing key or prepared owner state belongs in a shared factory image. Development boot keys must not be programmed into OTP.

Locate the empty setup file in the final raw image:

```sh
python3 os/tools/factory_extents.py /absolute/path/pidockos.img /absolute/path/extents.json
```

Create a signed package using the updater tool (all file paths should be absolute):

```text
PiDock.Updater pack-factory IMAGE EXTENTS_JSON OUTPUT VERSION SEQUENCE DEVELOPMENT KEY_ID PRIVATE_KEY
PiDock.Updater verify-factory PACKAGE TRUST_DIRECTORY
```

`DEVELOPMENT` is `true` or `false`; trust files are named `KEY_ID.pem`. Packages use a domain-separated P-256 signature over the exact manifest, containing raw/compressed SHA-256 hashes, sizes and allowed personalization extents. Cloud publication verifies the signature, bounded decompression, both hashes and the empty setup region. Versions are immutable per workspace. Uploaded archives and compressed payloads are limited to 2 GiB; raw images to 16 GiB; the catalog reserves free disk space and limits retained artifacts.

Publication and download authorization require action-specific MFA. A download ticket expires after five minutes, is consumed once, and is bound to the cloud user, workspace and artifact. It travels in a request header, never a URL. The audit retains actor, workspace, image digest, action and timestamp, without local setup values or download tokens. Personalized images are not stored on the cloud.

Large downloads require a current desktop browser with Origin Private File System (OPFS) support and sufficient temporary disk space. A dedicated worker streams the signed compressed base into an OPFS file while incrementally checking its SHA-256, then decompresses, patches and recompresses through bounded streams into a second disk-backed file. It never accumulates a whole image in a JavaScript ArrayBuffer or Blob. The preflight checks available space for the compressed base plus the raw image size and 64 MiB margin (about 8.5 GiB for alpha.4); actual use is normally smaller. Progress shows received bytes, transfer speed, elapsed time and the separate local preparation stage. A two-minute network idle timeout restores retry controls. Safari’s Downloads list remains empty until the configured disk-backed file is ready to save.

Temporary configured files contain the same unencrypted private setup as the download, including the authenticator seed and derived Wi-Fi key; the owner password is present only as its salted hash. Nothing in this private setup is uploaded to PiDockCloud. Users can explicitly clear the temporary image after saving. Open tabs expire it after 30 minutes; interrupted files older than 24 hours are removed on the next provisioning visit. Error and cancellation paths remove their attempt directory. Cleanup is best-effort if the browser has crashed or storage is unavailable; browser site-data controls can remove remaining local files. The bundled incremental SHA-256 implementation is pinned to @noble/hashes 2.4.0 with MIT license and registry integrity recorded alongside its source. The cloud distributes a prebuilt signed base; personalization runs locally. New-card hardware boot, Wi-Fi, trusted time, health rollback and recovery must be accepted on a physical Pi before claiming production image readiness. Firmware OTP provisioning is a separate irreversible step; a signed development image does not establish hardware-enforced verified boot by itself.


## Alpha.2 headless SD acceptance preparation — November 13, 2026

The credential-free **1.0.0-alpha.2** factory package, SHA-256 `79f9f770edce573d4337b0c01840ccb181be0220a29be4f62a8fe05f1d636974`, is prepared and verifies against the production release public key. The signed system was built from `104d1e350f04470497d16a63e6b1f3b4d6fd3d93`, including the reconciled rc.4 agent/API source and image-specific broker/data-growth fixes. The boot signature uses the existing development RSA key; no hardware trust/OTP provisioning has occurred.

A private copy was personalized for the identified new 64 GB SDXC card. Encrypted owner state/key-ring bytes and local CA were preserved exactly; privately entered Wi-Fi was included with root-only permissions. Signed system/boot ranges matched the public image. Only data/container partition metadata and private data changed. The new data range is prepared for approximately 59.18 GB, with ext4 expansion at first boot; a separate filesystem growth/check preserved the owner bundle. GPIO output grants and cloud permissions remain off.

The operator authorized the raw write privately in macOS. The 8,392,802,304-byte prepared prefix was written, the complete raw readback matched its recorded checksum, and the card was safely ejected. Private image/checksum/flash receipts remain in protected operator state, not in the shared factory package. Physical Pi boot, Wi-Fi/NTS, local sign-in, A/B health/rollback and headless recovery acceptance remain pending. Preserve the old Pi media during those tests.

The alpha.2 card passed full readback but was withdrawn before hardware acceptance: its time guard dropped the permission required to query chronyd’s private Unix socket. Alpha.3 grants only `CAP_DAC_OVERRIDE` to that root-owned guard while retaining `ProtectClock=true`, read-only system protection and no clock-setting capability. A Linux ARM64 runtime probe reproduced the failure with an empty capability set and verified local queries with this correction; it ran without network access or changing the host clock. The prepared private owner/network bundle is reused unchanged.

## Expanded setup and USB nodes

Factory manifests with `setupSchema: 2` support **Main** and **Node**. Older signed manifests default to schema 1 and keep the existing basic owner/Wi-Fi form; new options must never be written into an older importer. Use `pack-factory-v2` with the same arguments as `pack-factory` for an image whose signed profile declares schema-2 import and USB node protocol 1. This is a release-authority assertion about the built image.

Main configures its existing local owner and separate Ethernet/Wi-Fi interfaces. Each interface can be disabled, use DHCP or use a static IPv4 address/prefix, gateway and DNS servers. At least one external interface must remain enabled. Ensure a working route/DNS for authenticated time at first boot. Settings persist on the data partition and are rendered into runtime networkd files before networking starts; immutable system bytes stay unchanged.

Optional cloud setup captures an HTTPS origin and single-use invitation. The invitation remains private and is redeemed by the Pi after network/trusted-time readiness, with the device key generated on the Pi. Invitations may expire before a delayed first boot; the owner can enter a replacement in the existing local Cloud connection flow. Initial remote container control and remote UI choices default off and require explicit separate consent. The protected intent is bound to the imported owner stamp, cannot replace an existing cloud registration, is consumed before permission grants, and cannot replay after a restart, local revocation or ownership recovery. Cloud-update permission stays separate/off. Initial settings commit under the enrollment gate to avoid overlapping cloud migration.

Node requires no human owner or external network configuration. It advertises a private USB ECM interface, with no shell, mass-storage function, bridging or forwarding. A Main identifies the physical USB interface and discovers the node’s persistent TLS key. Main owner MFA authorizes adoption; subsequent settings/measurements require the owning Main’s pinned TLS client key. Unplugging does not reset membership. Release must be acknowledged by the connected node before another Main adopts it; stale release/configuration requests cannot affect a later owner or newer configuration. The Main records actor/device/action/result audits without keys. The node uses service identity, with no TOTP/login or direct cloud access.

The Main UI currently exposes node name, resource-monitoring enable/interval and CPU/memory/uptime observations. Scheduling, storage, node GPIO grants and automatic USB media provisioning are separate capabilities. Physical USB/power/reattachment acceptance remains required. Development USB descriptors use Linux Foundation example IDs and must be replaced or appropriately authorized before production hardware distribution. See the [Raspberry Pi OTG guide](https://pip-assets.raspberrypi.com/categories/685-app-notes-guides-whitepapers/documents/RP-009276-WP-1-Using%20OTG%20mode%20on%20Raspberry%20Pi%20SBCs) and [Linux ConfigFS gadget documentation](https://docs.kernel.org/usb/gadget_configfs.html).

### Alpha.4 build evidence — November 13, 2026

The credential-free alpha.4 factory package verifies with the production P-256 release key; its complete package SHA-256 is `b25251fff2162ba73cc5d683b3dcb840860dee8c881be15bc87a07bd1139c2b6`. The signed OS was built from `740d34f0e8b979847fc6b89d8e2d033aa650a3f7` (merged in PR #4), and declares setup schema 2 and USB node protocol 1. All 230 application tests, browser provisioning checks and six Linux image-tool tests pass. Linux ARM64 runtime, broker health, media/network startup and chrony socket access also pass. Required USB gadget/host modules are present, with the kernel’s 4 KiB page profile confirmed.

A private Main card image preserves the prepared owner/key ring, local CA and Wi-Fi byte for byte. GPIO grants remain absent. Signed boot/system partition ranges match the public factory image. Writing and physical acceptance are separate steps; no hardware boot, USB power/attachment or OTP result is inferred from these checks. Downloads include the selected `main` or `node` role in their names, and setup controls are locked from submission through authorization/compression so the approved choices cannot drift during preparation.

### Factory publication transport

Factory packages stream over an authenticated HTTP request instead of the interactive page connection. Fresh action MFA mints a single-use, user/workspace-bound publication ticket that expires after five minutes; the browser sends it in a custom header, never a URL. The upload has a 2 GiB limit and a two-hour deadline, with visible upload progress and a separate verification status. Failed/interrupted uploads release the page for retry; reconnecting users should first check the image catalog in case verification completed. Signature, blank-setup, immutable-version, quota and current-account checks still run before publication commits. Setup credentials remain browser-only and are not part of factory publication.

Publication progress is displayed in a prominent panel above image selection, with percentage, uploaded/total MiB, a progress bar and an explanation of temporarily disabled controls. The publication button changes to “Uploading…” and “Verifying…”; verification uses an indeterminate bar. Success and error states explain when controls become available and what to do next. Progress updates use a polite live region without marking that region busy, so assistive technology can announce changes.

Operators may stage credential-free release packages under `factory-images/prepared`: `<full-package-sha256>.pidockfactory` with a matching `<digest>.json` containing `digest`, `version`, and `bytes`. The owner/administrator selects the prepared package and completes the same fresh action MFA. The server verifies its signature, complete content, exact digest and version before normal workspace-scoped publication. Symlinks and invalid metadata are ignored; staging alone never publishes an image. This supports recovery of a slow browser transfer without bypassing local setup privacy or cloud publication approval.

The provisioning page reveals steps progressively: before any image is published it shows publication choices; setup appears only after an image is configured. During upload or verification only the status panel remains visible. Other sections are hidden in place so browser-owned setup is not destroyed. Main/Node choices include short role descriptions. The optional cloud section states the disconnected default and displays the current portal origin as the suggested URL only when cloud setup is enabled; no registration or remote permission is implied by that suggestion.

Owner setup pairs the password/confirmation and current/next authenticator fields in responsive columns. Confirmation is enabled only after the 14–256 character password requirement is met; both entries have independent show/hide controls and exact-match validation. The next authenticator field is enabled only after successful first-code verification, and editing the first code invalidates that verification. Native validation and submit-time checks both enforce password length. Appliance passwords are never sent to PiDockCloud: the browser writes an Identity v3 salted PBKDF2-SHA512 hash (210,000 iterations, 128-bit random salt) into the private image. The image still carries the authenticator secret and optional derived Wi-Fi key in its unencrypted setup area, which is stated beside the fields.

Authenticator setup has explicit first- and second-code verification buttons. The second code must match the next 30-second step after the first. Successful verification is retained for that generated setup secret while other fields are completed; submission writes the verified second step into the image rather than requiring the code to remain current. Editing the first code resets both proofs, and unverified or expired sequences cannot reach cloud download approval.

Code verification observes actual input values on both input/change events. Clearing verified code boxes updates those observed values before focus changes, so delayed browser change events cannot erase successful proof. Real edits still invalidate proof, including autofill detected at final submission. Automated regressions simulate change-on-blur and change-on-disable; native Safari reaches approval after both codes, and a synthetic image-generation test verifies the retained step, password-hash format, Wi-Fi payload and unchanged bytes outside the setup area after a five-minute delay. All wireless controls, including credentials and country, are nested inside the Wi-Fi interface section.

## Alpha.5 early-loader correction

The alpha.4 hardware attempt did not reach managed-data mounting or media setup. Executing its extracted initramfs in an isolated native ARM64 chroot reproduced a loader failure before `/init`: `libresolv.so.2` could not be found. The required library was present, but this Buildroot glibc searches `/lib64` and `/usr/lib64`; the minimal archive omitted the sysroot’s `lib64 → lib` directory aliases. Alpha.5 preserves these aliases in both normal and recovery ramdisks. This does not change verification policy, boot keys, GPIO permissions or owner setup.

Before signing, run `os/tests/verify_initramfs_runtime.sh` against each trusted, freshly generated A/B/recovery initramfs inside a native ARM64 Linux builder with only the chroot capability and temporary writable storage. The check executes the actual startup shell and verifier tools; file-presence checks alone do not detect this failure. Physical boot and recovery acceptance are still required after rebuilding. Configured alpha.4 Main and Node downloads both need replacement boot artifacts; preserve the private setup when preparing the replacement card.

## Alpha.6 compressed-driver correction

The kernel installs XZ-compressed modules, but the alpha.5 target kmod reported `-XZ`. Native `modinfo` could not decode the installed `brcmfmac.ko.xz`, blocking Wi-Fi; USB expansion modules use the same format. Alpha.6 enables XZ support in both target kmod and the host depmod tool that builds the driver dependency/alias indexes. Run `os/tests/verify_target_runtime.sh TARGET_ROOT` in the isolated native builder before signing; it reads the actual compressed Wi-Fi and USB ECM driver metadata without inserting modules into the build host’s kernel. The target-profile gate reads the checked-in release profile rather than duplicating its version. Alpha.5 was not published to production and must not be used for replacement Node downloads. Physical acceptance remains pending until the corrected card boots.

## Installer and first-boot diagnostics

New images write `pidock-early-boot.log` on the control/first boot partition as the signed initramfs discovers the root, creates its verity mapping and hands over to the OS. Missing early logs can also mean firmware did not reach the initramfs or that the card could not be written; their absence alone is not a failure code. These status files are diagnostic observations, never security attestations.

The first-boot service writes a bounded 64 KiB `pidock-install.log` there with setup state, Wi-Fi device availability, authentication rejection, association/DHCP progress, and authenticated-time status. It runs before the updater and management application on initial installation, for at most three minutes. It leaves the boot partition read-only afterward, and does not export during firmware trial boots. Successful initial setup disables that initial-install service on later boots.

Public reports are generated from a fixed set of status messages. Raw Wi-Fi events and wpa_cli output are never copied: passwords, authenticator seeds/codes, enrollment codes, SSIDs, BSSIDs and assigned addresses are excluded. An owner can also view/download the sanitized report under **PiDockOS → Installer diagnostics** after sign-in. Nothing is automatically uploaded to PiDockCloud.

Detailed system journals remain private on the Linux data partition, bounded to 32 MiB and seven days. They may contain ordinary system/network identifiers and should be handled as private support data. WPA key-debug logging is not enabled. If networking fails, power down, return the card to the computer, and open the two text reports on the first boot partition; no Linux filesystem formatting or ownership reset is needed.


### Headless early-boot diagnostics

The signed boot ramdisk records console opening, clock checks/adjustment, physical recovery-key waiting, signed root-descriptor loading, root-device discovery, verity opening, root mounting and the final OS handoff boundary in `pidock-early-boot.log` on the first/control FAT partition. Each marker includes monotonic uptime. Unexpected startup exits record an exit code; startup errors and a bounded tail of early kernel messages accompany each snapshot. The current file stays below 64 KiB, and `pidock-early-boot.previous.log` retains one bounded previous attempt across resets. A sudden kernel panic or storage hang can leave only the last successfully exported boundary; this is not a full persistent kernel console.

Logs are exported before normal services or operator recovery authentication. The exporter does not read provisioning or owner state, and recovery stops exporting before the operator enters commands or credentials. Logs remain diagnostic observations, never a source of root hashes, ownership, verification decisions or trusted time. Inspect the card on another computer after powering the Pi off; no monitor is required. The existing `pidock-install.log` covers subsequent first-boot setup and networking.

Run `os/tests/verify_boot_logging.sh TRUSTED_INITRAMFS.gz` inside an isolated native builder as root with chroot capability. It checks bounded output, previous-attempt retention, rejected stages and duplicate media, then injects clock and console failures into the actual normal startup script to prove their errors reach the SD-readable export. Run the existing initramfs runtime checks too. Updating the diagnostic scripts on existing media requires rebuilding and signing the ramdisks; editing files outside `boot.img` cannot change their trusted code.


### Bounded clock bootstrap

Normal and recovery startup use a dedicated signed `boot-clock` helper instead of BusyBox date parsing/formatting. Its worker only advances `CLOCK_REALTIME` to the signed build epoch when necessary; it does not write the hardware RTC or move a newer clock backward. A supervisor measures a five-second deadline with `CLOCK_MONOTONIC`, kills a stalled worker, and returns a distinct timeout code. Startup records `clock_ready` on success or `clock_deferred` with the failure code, then continues to verified-root discovery or physical recovery. An unavailable optional clock bootstrap must not terminate PID 1 or make recovery unreachable.

This signed timestamp is only a plausibility floor. Authenticated NTS time evidence and the existing trusted-clock authentication/TLS rules remain required. Physical attempts on November 16 stopped at the old `clock_set` boundary with an RTC date in 1970; the bounded replacement addresses that startup failure window, while physical acceptance still requires another boot. Native ramdisk checks inject successful, failed and stalled workers and verify startup continues beyond clock bootstrap; the real worker is also checked with clock-setting capability denied.


### Verity command compatibility

The physical boot after the clock correction reached root-device discovery and failed before verification because the shipped veritysetup rejected `--readonly`. Its verity `open` implementation always activates a read-only mapping; the unsupported flag has been removed. The signed root hash, data/hash geometry, read-only ext4 mount and restart-on-corruption policy are retained. Native initramfs acceptance now executes the exact signed startup `veritysetup open` command against a deliberately nonexistent device, requiring option parsing to succeed and the nonexistent input to be rejected without creating any mapping. This check reproduces the old command's unknown-option failure and passes the corrected command. Physical successful-root verification and network acceptance remain pending.


### Identifying a fresh headless boot

Each signed initramfs now embeds a 64-character `image_marker`, derived from its startup code, root descriptor, build epoch and slot role. The early report also records the kernel's random `boot_id`, which changes on each boot. Compare the image marker with `pidock-boot-expected.json` beside the logs on prepared diagnostic cards. That expectation file explicitly describes prepared files; it is not evidence that they ran. A matching marker in a newly written log and a new boot ID distinguish current-image execution from an unchanged older report. All of these remain diagnostic observations rather than security attestations.

Entry and mount errors are captured in writable initramfs memory before console setup. The first SD export is attempted after devtmpfs, proc and the runtime directory are available, before sysfs, console access and clock bootstrap. Export retries at later stages if the card is not yet discoverable; the previous report is rotated only at the first successful export of that boot. A firmware failure or kernel failure before device discovery cannot be saved by this userspace logger. Existing provisioning and owner data are not read or modified.


### Diagnostic observation must not delay the management UI

Physical evidence confirmed a current image marker, successful verified-root handoff and applied setup. The first-boot observer can run for three minutes; ordering it before the appliance service unnecessarily held the UI until that observation ended. It now orders only before the updater, retaining protection against diagnostic remounts during A/B update operations. Normal signed initramfs startup stages the corrected complete unit in `/run/systemd/system` so existing verified roots receive the same ordering fix without changing their payload or user data.

The initramfs also stages a `SyncIntervalSec=1s` journal drop-in before systemd starts. The recovered private journal previously contained only the initial flush near nine seconds, despite later installer observations; faster durable journal synchronization retains later service evidence across abrupt power-off or reset. Existing journal size limits remain unchanged. Earlier boot IDs indicate multiple attempts but do not by themselves establish a watchdog reset. Physical Wi-Fi/time readiness and successful HTTPS access remain to be checked on the next boot.


### Pi watchdog timeout correction — November 17, 2026

The new image marker confirmed current code, successful root verification and applied setup. The retained installer report contained 256 distinct attempts, and the recovered private journal contained 407 boot sessions with their last saved events under 25 seconds. Some attempts successfully associated and received a Wi-Fi address, but the appliance did not remain up long enough for authenticated time and HTTPS acceptance.

The shipped Linux 6.12.61 bcm2835 watchdog driver writes `(timeout << 16) & 0x000fffff` directly to its hardware register. Whole-second requests must fit in 15 seconds; the existing 20-second runtime request encodes four seconds, shorter than systemd's refresh interval. The previous 60-second reboot request also wraps. Both settings now use 10 seconds, preserving watchdog recovery within the driver's register range. Regression coverage rejects out-of-range configuration and reproduces the old 20-to-four-second encoding.

Normal signed boot ramdisks stage `/run/systemd/system.conf.d/zz-pidock-watchdog.conf` with valid runtime/reboot values before PID 1 starts, correcting existing immutable roots without altering setup or installed payload. The filename sorts after the old `/etc` drop-in; actual systemd configuration merging was checked. Native ramdisk acceptance verifies the staged values. The existing first-boot UI ordering fix, one-second journal synchronization, unique image marker and per-boot ID remain enabled. Physical stable-boot, authenticated-time and HTTPS acceptance still require the next cold boot.


### Authenticated-time monitor socket correction — November 17, 2026

After the watchdog correction, the Pi remained up for over 45 minutes and joined Wi-Fi, but HTTPS port 443 refused connections. The private journal showed Chrony selecting a time source while local identity issuance waited for the time guard. The guard used `ProtectSystem=strict` with only `/run/pidock-time` writable. The shipped chronyc 4.8 creates a random temporary client socket below `/run/chrony`; the sandbox rejected that directory creation with `Read-only file system`, preventing authenticated-time evidence from being read.

The guard now also permits writes to `/run/chrony`. Its capability bound remains only `CAP_DAC_OVERRIDE`, `ProtectClock=true` and `ProtectSystem=strict` remain enabled, and the existing NTS authentication, accuracy, recency and stable-sample criteria are unchanged. Signed normal ramdisks stage the corrected complete time-guard unit in `/run/systemd/system` for existing immutable roots. No certificate is pre-issued and no trusted-time requirement is bypassed.

Run `python3 os/tests/verify_chrony_monitor.py` on the native Docker build host against trusted Buildroot output. It runs the actual target daemon and client with networking and clock control disabled, reproduces the client failure with a read-only socket directory, and verifies a successful monitoring query when only that directory is writable. The returned test report remains unsynchronized; query access itself does not establish trusted time. Fixtures are removed afterward. Native ramdisk checks also inspect the staged unit's exact write paths and retained clock restrictions. Physical certificate issuance and HTTPS acceptance require the next boot.
