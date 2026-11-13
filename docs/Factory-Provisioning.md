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
