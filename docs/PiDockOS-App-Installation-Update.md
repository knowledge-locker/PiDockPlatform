# PiDockOS application-installation update — November 16, 2026

Two signed ARM64 packages are prepared from source `11d26848348f6b633ca0c5dc766439cb6aa873fe`. They include the local app-definition uploader, separate cloud application-installation consent, registry-backed deployment flow, and the managed `system.read` runtime-support endpoint required by the current Logic Engine host. The Logic Engine project itself was not changed.

## Choose the package for the installed PiDockOS

| Installed PiDockOS | Prepared package | Size | Existing trusted release signer |
| --- | --- | ---: | --- |
| Beta Main image `1.0.0-beta.1`, image sequence 8 | [1.0.0-beta.2 image update](../artifacts/updates/pidockos-1.0.0-beta.2-linux-arm64-image.zip) | 309,706,547 bytes | `pidock-prod-20261112` |
| Older Linux-hosted service installation, such as `0.1.0-dev.18` | [0.1.0-dev.19 service update](../artifacts/updates/pidockos-0.1.0-dev.19-linux-arm64.zip) | 2,384,265 bytes | `pidock-dev-20261111` |

The image update advances the image sequence from 8 to 9 and retains minimum sequence 0 and the existing independent recovery. Its RSA boot key matches beta.1. Its three signed payloads replace the system root and A/B boot-slot programs, including the self-contained .NET 10.0.15 runtime. The service package replaces only appliance/broker programs and uses the Linux host's existing compatible .NET 10 runtime. It retains the older Pi's existing development release trust for local upload. The production cloud's separate trust store may not accept that legacy development signer.

These ZIPs are updater packages. Do not flash them with Raspberry Pi Imager or use the service ZIP on the immutable beta image.

## Install when the Pi is available

1. On the Pi, open **PiDockOS updates** and check whether the updater reports `image` or `services` and the installed version.
2. Upload the matching package above, verify the version, target and digest, then approve installation with the local Owner password and a fresh authenticator code. Keep the Pi powered through its health check/image trial. The image update restarts the Pi; the service update restarts the two management programs.
3. After a healthy result, open **Applications → Install from an app definition**. It accepts the same `.pidock.json` offered by the cloud library and downloads the pinned ARM64 image from its registry.
4. If cloud app installation is wanted, open **Cloud connection → Allow cloud application installation** and approve that separate local policy. Cloud start/stop control and cloud-managed **OS** updates remain separate settings.

Alternatively, an authorized cloud user can publish the matching trusted package under **PiDockOS releases** using fresh MFA and approve the exact release on an online compatible device. Cloud-managed OS-update permission must first be enabled locally. No package was published into a live workspace or dispatched to the Pi during preparation.

Accounts, enrollment, app data and current policy settings are retained by the supported updater. The image keeps the existing broker feature gates: uploading a definition does not itself enable managed app deployment/resources/recovery/orchestration, create credentials, register a UI bridge, or grant physical I/O. A target must have the required [broker configuration](Application-Resources.md), [exact-image approval policy](Application-Install-Trust.md), named credentials and app grants before installation. The Logic Engine runtime-support response advertises disabled features honestly; including this endpoint does not certify the evolving Logic Engine product for physical operation. Physical GPIO outputs stay disabled.

## Verification

The prepared source passes **456 platform tests** and **59 Logic Engine tests**. Both packages pass the PiDock verifier and independent OpenSSL P-256 signature verification, with every signed payload length and SHA-256 checked: 53 service files and three image components. The package ZIPs contain no signing private key or per-device owner/network setup.

Native Linux ARM64 checks passed target service/driver validation, Main/Node setup import and owner replay preservation, sanitized installer reporting, .NET cryptography/globalization, actual private broker health, and non-root appliance HTTPS startup with fresh monitoring. Both packaged boot wrappers were extracted and their RSA signatures/configuration verified, with the boot trust key matched to the flashed beta.1 base. Tests used isolated state and did not install on, write media for, or alter a physical Pi. Physical boot, image-trial promotion and board-level rollback remain acceptance steps on the actual device.

| Package | SHA-256 |
| --- | --- |
| Beta.2 image | `7c510f3a238b38e20463f315b1b9c525e0de19f9f742e14b973f762ec0aca883` |
| Dev.19 services | `5d14972969c50a1be67e4b9205bd7d89e1d6f7d64ddb28d6ddc62ee78e05f4db` |

[Machine-readable verification](../artifacts/pidockos-app-install-update-verification.json). Adjacent `.sha256` files accompany each ZIP. The updater's read-only `verify-update PACKAGE TRUST_DIRECTORY RUNTIME` command is also available for offline verification with independently trusted public keys.
