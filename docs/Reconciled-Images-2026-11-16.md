# Reconciled application-platform images — November 16, 2026

The app-platform work is committed and merged through [PR #21](https://github.com/knowledge-locker/PiDockPlatform/pull/21), merge `cf569551a5c410feae1ecb7cd0bcfcedc1769bed`. The preserved local snapshot is `7172763`; reconciliation with main is `1d99a29`, and image packaging/isolated cross-target lock fixes are `4a3fce46b8c67f189da2426a73ee552ddae1e844`. The tested source tree at `4a3fce4` is identical to the merge tree at `cf56955`.

Newer main authentication, remembered sessions, trusted-clock checks, remote restrictions, factory provisioning and Linux boot fixes were retained. Rich app runtime controls use the shared action-confirmation dialog. Container builds include the native health SDK and separate cloud/appliance/broker entry points. Source lock files are not rewritten by architecture-specific fixture publishing.

## Built artifacts

| Artifact | Version / identity | Validation |
| --- | --- | --- |
| Published PiDockCloud, Linux AMD64 | `ghcr.io/knowledge-locker/pidockplatform:0.1.0-dev.20261116.1` · `sha256:c72c1ce9f1316b01ece3b2b7f84566bdfb83ba5525b847d2eb8fee387a85d536` | Published from merged `cf56955`; locked restore, clean build and 440 Linux tests pass. Provenance/SBOM and release metadata emitted by the [successful publication](https://github.com/knowledge-locker/PiDockPlatform/actions/runs/43900661946). |
| PiDockOS Pi 5 whole-image development factory package | `1.0.0-alpha.7`, sequence 7, setup schema 2 · SHA-256 `4d9d287a70f06265d11422bf33a08c3ac0bc1ddf577e7d04ac2442aeca48ef56` | Public target, driver indexes, native Main/Node import/replay, sanitized readable installer report, runtime crypto/globalization, normal/recovery ramdisk execution, verity, boot signatures and complete signed package/content verification pass. |
| Local cloud container | `sha256:f1a45d17ef59693ca807a07830358fca79a94dcc4464c8ba48ab7b1a1a25a26a` · amd64 · source `4a3fce4` | Production HTTPS startup, anonymous denial, shared style assets, restricted user/root, restart and retained key ring. |
| Local appliance container | `sha256:9bd8296f6a106c2e1a0847425edb3e2b629dd2ee13e753c0320519dfcd537e24` · arm64 · source `4a3fce4` | Production HTTPS startup, anonymous denial, shared style assets, restricted user/root, restart and retained key ring. |
| Local broker container | `sha256:f326d2d9d645bba9e22fce84944942c716995f874df24f7881372b9f8775507d` · arm64 · source `4a3fce4` | Private Unix health and graceful restart; no host Docker socket mounted. |

The factory package contains no owner/device state. It uses the existing **development RSA boot key** and existing P-256 factory-release signer; it does not program OTP or certify a physical boot. Its kernel uses 4 KiB pages; the upstream kernel release suffix still contains `v8-16k` and is not evidence of the configured page size. Package size is 669128885 bytes. Local package: [pidockos-1.0.0-alpha.7.pidockfactory](../artifacts/releases/pidockos-1.0.0-alpha.7.pidockfactory).

The local container variants were exercised directly. The registry publication has source/build/test/provenance evidence; pulling that private registry image from this desktop returned `unauthorized`, so a separate local runtime probe of that exact published digest was not performed. The published variant uses its existing HTTP-behind-trusted-proxy configuration; local HTTPS variants use the same merged source. No registry credentials were changed to bypass that restriction.

## Running environment and gate

The local PiDockCloud preview at `https://localhost:8443` is updated to the tested `4a3fce4` image and passes trusted HTTPS health. Its prior executable filesystem was exported/imported into a recovery image because the old image's backing metadata/content was missing; application state was backed up consistently while stopped. Both remain in protected local operator state. The existing MQTT broker was left running.

**440 Release regression tests pass** locally and in GitHub; the solution builds without warnings/errors. JavaScript provisioning/streaming checks and GitHub Linux image-tool checks pass. Reconciled actual Production authentication, kernel restrictions, credential delivery and broker/appliance process restarts passed again, with retained app data, cleared leases/forces and persistent Stop intent. The target-report writer now observes Git status instead of retaining the earlier fixture's hard-coded dirty flag.

The [simulation-first development gate](Application-Development-Gate.md) remains PASS. No shared pre-app validation batch remains before starting Logic Engine. Its own model/runtime/editor validation is subsequent product work.

This rebuild does not deploy the hosted production cloud, publish alpha.7 into a workspace's factory catalog, flash a card or update a physical Pi. Those existing installations are not asserted current. Pi/systemd/hardware timing, power loss and physical app GPIO acceptance remain separate release gates. Retained detailed local evidence is under `artifacts/reconciled-*.json`, `artifacts/target-security-restart.json` and `artifacts/pidockos-alpha7-verification.json`; build/operator logs and private recovery material are excluded from Git.
