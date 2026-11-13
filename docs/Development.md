# Development and validation

## Services

| Project | Purpose |
| --- | --- |
| PiDock.Core | Storage policy, wire contracts, application validation and atomic state writes |
| PiDock.Agent | Host measurements, bounded history, GPIO simulation, MQTT client and broker client |
| PiDock.Web.Shared | Console provisioning, authentication, TOTP and shared UI assets |
| PiDock.Appliance.Web | Local Blazor management interface |
| PiDock.Appliance.Broker | Narrow privileged Docker API adapter, Unix socket only |
| PiDock.Cloud.Web | Tenant-scoped Blazor fleet monitoring and MQTT ingestion |
| PiDock.Tests | Security, retention, broker and HTTP integration tests |

State defaults to `data/` inside each web project. Set `PiDock__StateDirectory` to a private absolute path to override it. Keep separate directories for the cloud and appliance. State directories reject symlinked ancestors; on macOS use a canonical path such as `/private/tmp/...`, not `/tmp/...` or `/var/...`.

## Appliance telemetry to the local broker

From the repository root, after preparing the private PKI and starting Compose:

```sh
ASPNETCORE_ENVIRONMENT=Development \
PiDock__StateDirectory="$PWD/.state/appliance" \
Mqtt__Enabled=true \
Mqtt__Host=localhost \
Mqtt__TenantId=local \
Mqtt__DeviceId=pi5 \
Mqtt__CertificatePath="$PWD/.state/pki/device.crt" \
Mqtt__PrivateKeyPath="$PWD/.state/pki/device.key" \
Mqtt__CaPath="$PWD/.state/pki/ca.crt" \
dotnet run --project src/PiDock.Appliance.Web
```

The same state-directory override must be supplied when commissioning the account. The certificate CN `local--pi5` maps to an explicit Mosquitto ACL allowing publication only to `pidock/v1/tenants/local/devices/pi5/telemetry`. The worker certificate CN `pidock-ingest` can subscribe to telemetry and publish persistence receipts; it cannot publish device telemetry. Each device can subscribe only to its own receipts. Broker client IDs are bound to the certificate-derived username. Client-supplied usernames do not select broker identity.

Telemetry is sampled locally every two seconds for the UI and queued every 30 seconds or when an alarm opens/recovers. A separate delivery worker handles network retries. The portal uses receipt time to show online (<90 seconds), stale (<180 seconds), or offline. The schema includes device/tenant, message ID, boot ID and sequence. Duplicate and non-increasing sequences for the active boot are rejected; a bounded retired-boot list reduces replay across boots. Historical frames are archived separately from live device state; replay does not refresh live contact. The JSON archive is bounded pending the PostgreSQL implementation.

The private CA validation preserves hostname and chain checks. Preview certificate revocation is not automated; the PKI has deliberately short lifetimes. Do not add a permissive TLS validation callback.

## Restricted host broker

Docker Desktop on macOS exposes its socket below the home directory. Linux commonly uses `/var/run/docker.sock`. Set the actual path explicitly:

```sh
Broker__SocketPath="$PWD/.state/host-broker/broker.sock" \
Broker__DockerSocket="$HOME/.docker/run/docker.sock" \
Broker__EnableDeployments=true \
dotnet run --no-launch-profile --project src/PiDock.Appliance.Broker
```

In a second terminal, start the appliance with `Broker__SocketPath` set to the same socket (plus any telemetry settings). The directory is owner-writable/group-traversable and the socket owner/group-readable/writable. A production Linux service must assign the `pidock` group deliberately; the service units under `os/systemd` describe this boundary. Other local users must not receive this group.

The broker has no TCP listener. Deployment support is disabled by default. Its allowlist includes only managed container listing, digest-pinned image pulls, creation of private per-app networks, constrained container creation and managed start/stop. It never accepts shell commands, arbitrary host paths, Docker API passthrough, privileged mode or host networking. Broker access is privileged even with this allowlist; it must not be exposed to application containers.

The dashboard's runtime changes require a fresh owner password and unused TOTP. App deployment currently omits persistent volumes, environment secrets, publisher signature verification, deletion, image archive upload and bus grants. Docker log rotation caps each managed app at two 5 MiB files; those runtime logs are not yet collected into PiDock history. Stopping does not delete application networks or images.

For an API validation from a trusted local console:

```sh
curl --unix-socket .state/host-broker/broker.sock http://localhost/applications
```

Do not remove an existing broker socket until you have established that the previous process stopped. Unexpected socket presence fails startup rather than deleting another process's listener.

## Tests

```sh
dotnet restore --locked-mode
dotnet test
dotnet build --configuration Release --no-restore
dotnet list package --vulnerable --include-transitive
docker compose --env-file .env -f deploy/compose.yaml config --quiet
```

Tests cover RFC TOTP vectors, replay/lockout persistence, no second appliance owner, CSRF and protected HTTP routes, tenant/topic matching, snapshot deduplication, unsafe manifest rejection, isolation options, unmanaged-container rejection, storage pressure/rotation and symlink rejection. Tests use disposable private state and never touch user application files.

For real broker/container validation, start Compose, connect the appliance, check `.state/cloud/fleet.json`, and verify that a wrong-topic publish receives MQTT `Not authorized`. Live platform integration needs Docker; the normal test suite does not depend on Docker or Raspberry Pi hardware.

## Maintenance

Update NuGet lockfiles deliberately, keep supported .NET patches current, and regenerate the short-lived preview PKI before it expires. The PKI script refuses to overwrite existing material. Archive or remove disposable preview state only after checking it contains no required accounts/data. Removing state is not the production recovery process.

Backup accounts together with their Data Protection key ring; those keys are required to decrypt accounts and validate sessions. No backup automation is implemented yet. The atomic JSON stores support only one process per directory.

## Simulate a broker outage without hardware

```sh
dotnet build
docker build -f deploy/Dockerfile -t pidock-preview-cloud .
python3 tools/verify-delivery.py
```

This uses the existing preview PKI but creates its own Docker network, broker, cloud container and temporary appliance state. It checks a real mTLS receipt, stops the isolated broker until two samples are buffered, restarts it, and verifies replay and deduplication. It cleans up its own resources and does not restart the normal preview stack. Allow roughly two minutes. See [monitoring and delivery](Monitoring-and-Delivery.md) for protocol and limits.

## Device enrollment

Use the PiDockCloud **Add device** screen and PiDockOS **Cloud connection** screen after commissioning each service. See [Device enrollment](Device-Enrollment.md) for signing configuration, code recovery semantics and the opt-in preview overlay. New enrollments activate MQTT immediately. Existing manually configured MQTT settings retain their startup snapshot; restart after editing those files.


Human sign-in uses an encrypted, HttpOnly, same-site browser cookie and protected server-side session state (`sessions.protected`). “Keep me signed in on this browser for 7 days” is selected by default; unchecking it creates a browser-session cookie with a 12-hour absolute limit. Remembered sessions have a fixed seven-day expiry and survive browser restarts and service replacements when the state directory and Data Protection key ring are retained. Session validation still checks expiry, account stamp, tenant and role. Sign out revokes only the current browser session, including copied cookies and other circuits using that session (revalidation runs every ten seconds). At most 20 active browser sessions are retained per account. Existing legacy cookies keep their original short lifetime; sign in once after upgrading to create a remembered session. Protected actions continue to require fresh password/TOTP confirmation independently of the viewing session. Back up session state with the account file and key ring.
