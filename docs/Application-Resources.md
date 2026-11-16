# Shared application data and credentials

PiDockOS supplies managed persistent data and private credential-file delivery for any compatible app, including Logic Engine. This is an opt-in engineering preview. It does not grant GPIO access or complete the product-app conformance gate.

## Discover and enable

Read `GET /api/platform/v1/app-resources` over authenticated HTTPS, or the read-only MCP resource `pidock://platform/app-resources`. Require `enabled: true`; an unavailable broker is not evidence of support. The contract catalogue includes `application-runtime-manifest`, `application-resource-support`, and public `application-credential-info`. Package publishing manifests remain separate. Secret-bearing startup records are deliberately absent from public discovery/schema resources.

A trusted appliance administrator enables `Broker:EnableAppResources=true` together with `Broker:EnableDeployments` and/or `Broker:EnableLifecycle`, and supplies an absolute private `Broker:StateDirectory`. The supplied systemd unit prepares `/var/lib/pidock-broker` with mode 0700, but leaves deployment and app resources disabled by default. Only the privileged Unix broker socket accepts prepared deployment material. Public browser and MCP clients have no credential transport or arbitrary Docker mutation endpoint.

The Applications page requests persistent data and named credentials. The Owner-only **App credentials** page creates or rotates a credential by app ID/name, purpose and expiry (up to 90 days), and revokes existing entries. Each mutation checks the current Owner and fresh password/TOTP. Values are generated on the server, never displayed or copied into a browser. Up to 128 app credentials and eight credential references per app are supported.

## App image contract

Prepare the fixed data directory in the image before switching to user 1654:

```dockerfile
RUN mkdir -p /var/lib/pidock/app-data && chown 1654:1654 /var/lib/pidock/app-data
USER 1654:1654
```

Run `await PiDock.Core.ApplicationBootstrap.InitializeAsync()` before creating the app host, producing any stdout, starting services or touching hardware. An image without that protocol must not request managed resources. Apps outside managed startup receive `null` from the helper. A resource-enabled runtime manifest resembles:

```json
{
  "name": "my-app",
  "image": "registry/my-app@sha256:<64 lowercase hex digits>",
  "memoryMiB": 128,
  "cpuLimit": 0.5,
  "resources": { "persistentData": true, "credentials": ["bridge", "services"] }
}
```

PiDockOS mounts a local Docker named volume, `pidock-app-{appId}-data`, at `/var/lib/pidock/app-data`. Docker initializes a new volume from the image directory, preserving its ownership. Bootstrap verifies that the app can write to it. Existing volumes must have PiDockOS ownership labels for that exact app, the local driver and no external driver options. Arbitrary paths, host bind mounts and Docker sockets are not accepted.

A stable app ID reuses the same data volume across container replacement; stop, failed startup and broker cleanup never delete app data. Plan app schema migrations and transactional updates explicitly. Volume quotas, backup/restore, purge, automatic replacement/rollback remain planned. Opt-in [app supervision](Application-Lifecycle.md) now supports process-exit and reboot recovery. There is no guarantee that an app that acknowledged startup is subsequently healthy.

## Startup delivery protocol

The broker attaches privately to the container stdin/stdout stream before start. It sends one four-byte big-endian length followed by a JSON packet (schema 1, random delivery ID, app ID, data flag and credential files), limited to 32 KiB and eight credentials. The shared helper checks identities/expiry, requires Linux and a dedicated tmpfs at `/run/secrets/pidock`, and creates `{credentialName}.json` with mode 0400 under the app-owned mode-0700 directory. It then writes a single-line JSON acknowledgement carrying only schema, delivery ID, app ID and readiness. The broker accepts a matching acknowledgement within 30 seconds. Failure stops the app; initial deployment also removes the newly created container/network while retaining the volume.

Only `PIDOCK_BOOTSTRAP_PROTOCOL=1` and `PIDOCK_APP_ID` enter container environment metadata. Credential values do not enter environment variables, Docker create metadata, image layers, host bind mounts, stdout or the iframe bridge. The tmpfs is size-limited, non-executable and recreated on each container start. Apps read a validated live credential using `ApplicationBootstrap.ReadCredential("services")` and send its token only to the intended authenticated backend. Never return the credential to app UI code or log the startup packet.

Other-language clients can implement the same bounded packet/ack protocol, mount and permission checks. The C# helper is the supplied implementation; independent client conformance remains required.

## Purpose, rotation and lifecycle

`ui-bridge` credentials authenticate the PiDockOS server to the app backend. Set the contribution grant's `credentialId` to the named managed credential for that app. Once a managed name exists, an expired or revoked value cannot fall back to a legacy configured token. Existing explicit private configuration remains compatible for contributions that have never had a managed credential of that name.

`platform` credentials authenticate an app to shared `/api/apps/v1` HTTPS services. The Owner explicitly selects from `system.read`, `logs.read`, `logs.write`, `telemetry.read`, `telemetry.write`, `alarms.read`, `alarms.raise`, and `alarms.acknowledge`. Record reads stay scoped to the app itself. These tokens do not confer Owner, publisher, discovery-agent, cross-app or hardware authority, or permission to raise privileged log-derived alarms.

The host vault checks expiry/revocation for each authentication. Bridge commands and active streams revalidate their lease. Rotation or revocation invalidates the old host-side authority immediately. The app's already delivered file is a snapshot: restart it through PiDockOS to replace that file, and expire/recheck app-side credentials appropriately. There is no push revocation of arbitrary app memory.

PiDockOS resolves fresh vault material for managed start/restart. The broker validates exact manifest references and retains encrypted startup records to redeliver still-live material when controlled directly from its trusted local socket. Missing protected records for a resource-labeled app fail closed. Docker automatic restart is deliberately `no` for resource-enabled containers; manually running `docker start` bypasses the delivery contract and is unsupported. Apps with opted-in [supervised recovery](Application-Lifecycle.md) and saved running intent are restarted through PiDockOS after reboot; otherwise an operator starts them manually.

## Protected state and trust boundary

The host vault and retained broker startup records use separate Data Protection purposes/application key rings and atomic private state files. Protect and back up **both** state and keys. Losing keys makes encrypted records unavailable; these files are not a substitute for app-data backups. Encryption protects stored records, but a root/Docker administrator or someone with state and its keys is trusted and can recover or replace material. Hardware-backed key protection and encrypted Docker data volumes are not supplied by this increment.

Managed data files contain whatever the app writes. Never copy credential files into the durable data directory. Bootstrap cannot prevent an authorized app from leaking a token; deploy only trusted, reviewed images.

## Validation

Automated coverage includes protected vault reopen, metadata-only projection, MFA replay denial, scoped HTTPS authentication, rotation/revocation/expiry, UI-only token exclusion from platform auth, exact app credential assignment, opt-in, named volume ownership checks, failure cleanup, retained data and refreshed restart delivery. Packet sizes and acknowledgement correlation are tested. A local Linux ARM64 Docker probe also passed two starts of the same read-only, non-root container: data advanced from 1 to 2, credential tmpfs was recreated, replacement credentials were readable only as mode 0400 files, and no token appeared in Docker logs. Physical appliance recovery and production deployment have not been validated.

## App readiness after delivery

The bootstrap acknowledgement proves delivery only. Opt-in [shared app health reporting](Application-Health.md) adds a separate private heartbeat tmpfs, fixed reader, app liveness/readiness status and optional bounded unhealthy-app recovery. No readiness is invented for apps without that contract.

## Coordinated lifecycle

[Dependency ordering and shutdown](Application-Orchestration.md) apply to resource-enabled apps behind orchestration opt-in. `managedLifecycle: true` can request bootstrap-backed management without data, credentials or health. Dependency conditions and shutdown intent persist in protected platform state; no container/data deletion is added.

[App image updates and rollback](Application-Updates.md) reuse the same app volume with candidate copy-up disabled. Updates require explicit backward-compatible data policy; restoring a container never restores a data snapshot.
