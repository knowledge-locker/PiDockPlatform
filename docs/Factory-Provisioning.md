# PiDockOS factory provisioning

PiDockCloud's **Create SD image** page (`/provisioning`) is available to workspace Owners and Administrators. It publishes credential-free, signed Raspberry Pi 5 factory packages and prepares private, headless installation downloads in the browser. It does not build arbitrary operating-system source on the cloud server or receive release-signing private keys.

Choose a published image, configure a separate appliance owner and Ethernet or Wi-Fi, add the new authenticator entry, and verify two consecutive codes. Download authorization uses the shared cloud MFA dialog. Flash the resulting `.img.gz` with Raspberry Pi Imager's custom-image option, without Imager OS customization. Flashing erases the target media. After boot, connect to `https://pidock.local`, review the local certificate, and sign in. Cloud enrollment and remote permissions remain separate local actions. GPIO outputs start disabled.

Device passwords, authenticator seeds and wireless credentials stay in the browser. The browser derives an ASP.NET Identity v3 PBKDF2-SHA512 password hash and WPA2 PSK, then streams personalization into the factory image's reserved 1 MiB setup file. Signed system, boot and recovery bytes are preserved. The download contains private provisioning material: keep it secure, never publish it as a factory release, and delete it after flashing. SD cards are not encrypted; deleting the consumed setup file is not secure erasure of flash media.

The signed first-boot importer accepts only bounded, typed owner/network settings at a fixed boot-partition location. It protects the imported account using the appliance's own key ring, writes Wi-Fi settings with root-only permissions, and removes the consumed boot file. It never replaces an existing owner or enables cloud permissions. An interrupted import can resume without resetting an existing account. Initial provisioning is not an ownership-recovery API.

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

Large downloads and browser compression require a current desktop browser and sufficient memory. The cloud distributes a prebuilt signed base; personalization runs locally. New-card hardware boot, Wi-Fi, trusted time, health rollback and recovery must be accepted on a physical Pi before claiming production image readiness. Firmware OTP provisioning is a separate irreversible step; a signed development image does not establish hardware-enforced verified boot by itself.


## Alpha.2 headless SD acceptance preparation — November 13, 2026

The credential-free **1.0.0-alpha.2** factory package, SHA-256 `79f9f770edce573d4337b0c01840ccb181be0220a29be4f62a8fe05f1d636974`, is prepared and verifies against the production release public key. The signed system was built from `104d1e350f04470497d16a63e6b1f3b4d6fd3d93`, including the reconciled rc.4 agent/API source and image-specific broker/data-growth fixes. The boot signature uses the existing development RSA key; no hardware trust/OTP provisioning has occurred.

A private copy was personalized for the identified new 64 GB SDXC card. Encrypted owner state/key-ring bytes and local CA were preserved exactly; privately entered Wi-Fi was included with root-only permissions. Signed system/boot ranges matched the public image. Only data/container partition metadata and private data changed. The new data range is prepared for approximately 59.18 GB, with ext4 expansion at first boot; a separate filesystem growth/check preserved the owner bundle. GPIO output grants and cloud permissions remain off.

The operator authorized the raw write privately in macOS. The 8,392,802,304-byte prepared prefix was written, the complete raw readback matched its recorded checksum, and the card was safely ejected. Private image/checksum/flash receipts remain in protected operator state, not in the shared factory package. Physical Pi boot, Wi-Fi/NTS, local sign-in, A/B health/rollback and headless recovery acceptance remain pending. Preserve the old Pi media during those tests.
