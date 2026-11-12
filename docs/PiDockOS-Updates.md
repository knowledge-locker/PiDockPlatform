# Signed PiDockOS service updates

PiDockOS supports the same signed package through two paths:

- **Local appliance → PiDockOS updates**: upload and verify a package, then approve installation with a fresh local Owner password and authenticator code. No cloud registration or cloud permission is required.
- **PiDockCloud → PiDockOS releases**: an organization Owner/Administrator publishes a signed package with fresh MFA. From a device dashboard, choose a compatible release and approve that exact digest with fresh MFA. The device must be online and its local owner must separately enable **Allow cloud-managed PiDockOS updates** under **PiDockOS updates**. Container control permission does not grant update permission.

These packages replace the existing `/usr/lib/pidock/appliance` and `/usr/lib/pidock/broker` services on the current Linux host. They do not replace the kernel, boot firmware, root filesystem, .NET runtime, systemd units, independent updater or recovery image. Production signed A/B boot images remain a separate hardware release gate. No unattended fleet-wide rollout is implemented.

## One-time appliance bootstrap

An older appliance needs one final local administrator installation of an updated web/broker build and the independent updater. This establishes the trusted installer that subsequent managed updates use. Keep existing account, enrollment, MQTT, application and history directories intact. The existing SSH update procedure can install the first web/broker build.

Publish the updater for the appliance runtime:

```sh
dotnet publish src/PiDock.Updater/PiDock.Updater.csproj -c Release -r linux-arm64 --self-contained false /p:UseAppHost=false -o artifacts/updater
```

Transfer the published updater, this repository's bootstrap script and systemd template, and the independently verified **public** release-signing key to the Pi. Preserve the `deploy/` and `os/systemd/` relative layout. Verify the transferred bootstrap files through the existing administrator channel, then run locally:

```sh
sudo ./deploy/install-pi-updater.sh ./artifacts/updater release-2026 ./release-2026-public.pem
```

The script installs `pidock-updater.service`, enables the private `/run/pidock-updater/update.sock`, configures the appliance to use it, and installs the public trust anchor at `/etc/pidock/update-trust/release-2026.pem`. Both the updater and its journal are root-owned; only root and the existing `pidock` management group can connect to the socket. The updater runs separately from the services it replaces, starts on boot, and can finish recovery after their shutdown or a host restart. Cloud-managed updates default to off.

The bootstrap requires the existing `pidock` account/group, .NET 10 runtime, both service installations, and the appliance TLS certificate at `/etc/pidock/appliance.crt`. It does not open a public listener. The updater's program files and its public-key trust store require separate local administrator maintenance; update packages cannot replace them.

## Signing and building a package

Keep the P-256 release-signing private key offline or in a controlled signing environment. It is separate from web/MQTT TLS keys. Never upload it to either PiDockOS or PiDockCloud. Distribute only the corresponding public key through a verified administrator channel. For example, on the signing workstation:

```sh
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -pkeyopt ec_param_enc:named_curve -out /secure/offline/release-2026.key
openssl pkey -in /secure/offline/release-2026.key -pubout -out release-2026-public.pem
./deploy/package-pidockos-update.sh 0.1.0-dev.11 linux-arm64 release-2026 /secure/offline/release-2026.key ./pidockos-0.1.0-dev.11-linux-arm64.zip
```

Use OpenSSL 3 and named-curve encoding for P-256. The macOS system LibreSSL may emit explicit curve parameters that the .NET macOS key importer rejects; the development rollout used `/opt/homebrew/opt/openssl@3/bin/openssl`. Use appropriate private-file permissions on the signing workstation. The packaging script publishes both services for the target runtime and calls the updater's offline `pack` command. It does not invoke privileged installation or send the private key over the network. `linux-arm64` and `linux-x64` are supported package targets; each appliance requires an exact runtime match and a compatible installed .NET 10 runtime.

The ZIP contains an exact-byte signed `manifest.json`, an ECDSA/SHA-256 `signature.bin`, and the `appliance/` and `broker/` payload files. Every file has a signed path, size and SHA-256 digest. The verifier rejects unknown signers, invalid signatures, wrong runtimes, changed/missing/extra files, duplicate paths, traversal, links, special files and oversized packages. ZIP64 and archive comments are not supported by this format. Compressed and expanded payloads are each bounded to 512 MiB; metadata, entry count and central-directory allocations are bounded separately. There are no package-supplied install scripts.

## Cloud setup and operation

Install the verified public PEM under a directory mounted read-only into the cloud, with filename matching the package's key ID, such as `/etc/pidock/update-trust/release-2026.pem`. Configure `Updates__TrustDirectory` if using another path. The cloud container user must be able to read these public files. Both the cloud and each appliance need their own independently provisioned trust anchors. Existing cloud deployments work without this directory, but release publication fails closed until a trusted signer is configured. Use a Compose override to mount the directory; no signing secrets belong in cloud volumes. For the PRX3MUS runtime profile, use its approved certificate target directory instead: mount the public file as `/certs/<key-id>.pem` and set `Updates__TrustDirectory=/certs` in the protected runtime environment. The development host provisions this mount source as UID/GID 1654 with mode 0400, matching the existing service-readable certificate policy.

Release publication is scoped to the signed-in user's organization. Versions are immutable per runtime. The preview limits publication to 20 releases per organization, 100 catalog entries overall, 8 GiB of package artifacts, and a free-space reserve of at least 1 GiB. Releases live separately from telemetry/history cleanup. Catalog cleanup/withdrawal and automated key rotation are not implemented yet; maintain this bounded preview catalog through administrator maintenance.

Update approvals are scoped to one organization, device, release digest, requester and unique operation ID. They expire after ten minutes. The existing mTLS MQTT receipt carries only this command and a random download capability; package bytes travel over outbound HTTPS to the appliance's already registered cloud origin. Redirects are disabled and normal HTTPS verification remains enabled, with the enrollment's saved private HTTPS trust anchor supported. New enrollments preserve that HTTPS anchor separately from the MQTT CA. Older private deployments with a different HTTPS CA should complete local trust maintenance before using cloud downloads.

The download capability travels in an Authorization header, is scoped to its command, and expires with approval. Download responses are marked no-store. They do not require a browser session. Knowledge of a device ID or package digest is insufficient to download a release. Replayed historical telemetry cannot dispatch update commands. Received commands are journaled before download; repeated receipts and agent restarts cannot reinstall an accepted command. The appliance rechecks expiry after download, exact digest, independent signature trust, runtime and local permission before acceptance.

The device dashboard reports queued/downloading/accepted/installing/checking/healthy/rolled-back/failed results as telemetry arrives. Loss of cloud connectivity does not interrupt an installation already accepted by the independent updater. Expiry or missing confirmation is not proof that installation failed; inspect the device's reported state before retrying.

## Installation, health checks and recovery

Only one package/install operation can run at a time. The updater preserves a free-space reserve of at least 1 GiB and requires room for staging and rollback. Packages and candidate programs are held outside the data-retention budget; ordinary telemetry purging cannot remove update artifacts or rollback files.

Before replacement, the updater verifies the package again against its current trust store and approved digest, extracts bounded regular program files into a root-owned transaction directory on the installation filesystem, and durably journals the operation. It then stops both services, durably renames their old program directories into the transaction backup, switches in the new files, and starts both services. Only root-created executable/read permissions are used; ZIP permissions cannot grant extra access.

Health checks use the appliance's verified local HTTPS endpoint and the broker's Unix socket. Both running processes must report the new release digest loaded at process startup. The appliance must also report a fresh monitoring sample without a persistence error. A leftover old process cannot satisfy the new digest check. Installation is promoted only after these checks pass. One previous program set is retained after success.

On failure, the updater stops the candidate services, restores both previous program directories and starts/checks them again. A legacy installation uses its existing liveness/runtime checks for the first rollback. The journal identifies incomplete switch states; updater restart or reboot restores the previous programs rather than silently continuing an interrupted candidate. If previous services cannot become healthy, the status becomes **RecoveryFailed**, backups/journal stay in place, and further installs are blocked pending local administrator recovery.

Accounts, registration, telemetry/history, installed application containers and volumes are never moved or rewritten by this updater. Program rollback does not undo application data/schema migrations; publishers must ship backward-compatible configuration/state changes or provide separate tested backup/recovery procedures. Signed older versions remain installable through explicit approval; this preview does not claim hardware anti-rollback enforcement or a complete release revocation service.

Turning cloud permission off rejects new installations and is rechecked immediately before replacement. An operation already replacing services completes its health check or rollback. Local upload/install remains available with local Owner MFA.

## Verification and deployment status

Automated tests exercise signature/digest/target checks, unsafe archive rejection, separate persistent cloud permission, revocation, local installation without cloud permission, failed health rollback, recovery at each directory-switch checkpoint, duplicate command prevention, tenant isolation, scoped downloads, immutable catalog versions, and the receipt/download/agent/install/status flow with a controlled service host.

The implementation and tests do not constitute a hardware power-cut certification or a deployment to the physical Pi. Validate the bootstrap, service health probes, restart and failure recovery on a development appliance before relying on unattended recovery.
