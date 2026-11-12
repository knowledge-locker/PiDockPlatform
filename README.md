# PiDockPlatform

**PiDockOS** is the Raspberry Pi container appliance. **PiDockCloud** is its self-hostable .NET Blazor fleet service. Together they are **PiDockPlatform**. The production reference is Pi 5 with an RTC battery; initial hardware testing uses the available Pi 4 and Pi 3 Model B boards.

**Current stage: working engineering preview, not a production microOS image.** The services build and run on .NET 10. The cloud and MQTT broker run in containers. Hardware security, recovery and image generation remain release gates in the [specification](docs/PiDock-Specification-and-Implementation-Plan.md).

## What runs now

- Appliance dashboard with Linux host CPU, memory, temperature, volume usage and network counters. Unsupported host measurements are labeled unavailable.
- Capacity-based monitoring history: calculated budget, free-space reserve, oldest-first cleanup, policy preview and saved settings. Only managed history segments are deleted.
- Standard 40-pin header map with opt-in, clearly marked GPIO simulation and 60-second override leases. **No physical GPIO writes.**
- Local-console account creation, password plus external TOTP, replay protection, durable throttling, protected account files, CSRF protection and short authenticated sessions. No public registration or account-reset endpoint.
- A separate Unix-socket host broker for listing, deploying, starting and stopping managed Docker containers. Deployments require a digest-pinned ARM64 image and enforce non-root execution, read-only root, dropped capabilities and resource limits. Optional ports bind to loopback only.
- MQTT 5 over mutual TLS, per-device broker ACLs, a durable bounded outbox, cloud persistence receipts, historical replay and tenant-scoped Blazor dashboards.
- Sustained CPU, memory, temperature and storage alarms, persisted active state, recovery hysteresis and reported alarm counts.
- Single-use, ten-minute device invitations, local device key generation, HTTPS certificate enrollment and automatic MQTT activation.
- HTTPS self-hosted PiDockCloud plus Mosquitto using Compose. Device private keys remain on PiDockOS. Enrollment signing requires a separately supplied issuer; no private CA is built into the image.

## Hardware test targets

Use a Pi 4 first, then a Pi 3B for constrained-resource testing, both with a 64-bit OS. Keep the cloud platform on a separate server. Battery-free development is supported through network time after boot; continuous power does not preserve the clock through every restart or interruption. Pi 5-specific boot security, GPIO controller and RTC battery testing remain separate release gates. See [hardware testing](docs/Hardware-Testing.md).

## Review the UI without hardware

Use the [isolated UI review tools](docs/UI-Review.md) to explore the actual appliance and cloud components with sample data and simulated I/O. No real account enrollment or hardware is needed.

## Start locally

Prerequisites: .NET SDK selected by `global.json`; Docker Desktop/Engine for container features; OpenSSL for the private preview PKI.

```sh
dotnet restore --locked-mode
dotnet test
# Interactive terminal required. Choose your own username and 14+ character password.
dotnet run --project src/PiDock.Appliance.Web -- --commission
# Default launch profile serves HTTPS on loopback.
dotnet run --project src/PiDock.Appliance.Web
```

Open [the appliance dashboard](https://localhost:7241). The service has no default account. Provisioning prints a private manual TOTP enrollment secret and requires two consecutive authenticator codes. Do not copy the secret into logs or issue trackers. Use the .NET development certificate; trust it through your normal local development process if necessary.

For a local GPIO simulation:

```sh
PiDock__SimulateGpio=true dotnet run --project src/PiDock.Appliance.Web
```

Simulation is permitted only in the Development environment. Console commissioning is an engineering tool; **physical-presence enforcement on a provisioned Pi is not implemented yet**. There is no credential reset command in this preview. Do not mistake local shell access for hardware-enforced recovery.

## Self-hosted cloud preview

For the new **Add device → Cloud connection** workflow, see [device enrollment](docs/Device-Enrollment.md). The approved hosted environment names and PRX3MUS requirements are in [deployment environments](docs/Deployment-Environments.md). Host volumes, PRX3MUS runtime profiles and encrypted recovery are described in [persistent storage](docs/Persistent-Storage.md).

```sh
./deploy/prepare-preview.sh
docker compose --env-file .env -f deploy/compose.yaml up --build -d
# Stop the web process to establish the single platform Owner, then restart.
docker compose --env-file .env -f deploy/compose.yaml stop cloud
docker compose --env-file .env -f deploy/compose.yaml run --rm cloud --commission
docker compose --env-file .env -f deploy/compose.yaml up -d cloud
```

Open [the local cloud portal](https://localhost:8443). Establish the single platform Owner with `--commission` before tenant signup is available. Public workspace signup is enabled by default; the Owner can disable it under **Workspaces & team → Workspace signup settings**. When enabled, users can create an account and workspace from the sign-in page and complete authenticator setup. Team invitations continue to work with public signup disabled: new invited users create an account directly in the invited workspace without creating another tenant. Each workspace owns its PiDockOS registrations. The header switches between memberships and applies that workspace’s role; the Owner explicitly selects a workspace for audited support access. See [workspaces and teams](docs/Cloud-Workspaces.md) for roles, invitations and migration. Do not run multiple processes against the same state directory.

The PKI script creates a private preview CA and 14-day leaf certificates under `.state/pki`; it does not change your OS trust store or replace existing identity material. Use `.state/pki/ca.crt` explicitly when validating endpoints. Both HTTPS and MQTT publish to **127.0.0.1 only**. These are development certificates and deployment settings, not public hosting instructions. Keep `.state` and `.env` private and out of source control.

To connect the appliance to this broker, see [the development guide](docs/Development.md). Single-use enrollment is implemented; see [device enrollment](docs/Device-Enrollment.md). Automated certificate renewal and revocation remain release work.

Stop the preview services without erasing state:

```sh
docker compose --env-file .env -f deploy/compose.yaml down
```

## Application platform discovery

Versioned platform discovery and an agent MCP stdio bridge are available. Use **Agent access** in PiDockOS to register third-party applications, generate encrypted/retrievable tokens, copy connection prompts and revoke access. See [agent access](docs/Agent-Access.md). Agents can inspect contract schemas, capability status and the saved registered PiDockCloud URL using a separate expiring discovery credential. Scoped shared logs, measurements and app alarms, plus tenant-scoped registry-reference cloud publishing, are now available in engineering preview. Hardware and package installation remain planned. See [shared services and cloud publishing](docs/Cloud-Publishing-and-App-Services.md). See [application platform setup and limits](docs/Application-Platform.md).

## Container applications

The dashboard connects to an independently started host broker over a Unix socket. The web process never receives Docker socket access. Deployment controls are off unless enabled explicitly on the broker. See [the development guide](docs/Development.md#restricted-host-broker) for setup.

This preview supports registry pulls of immutable ARM64 images and basic lifecycle control, not image archive uploads, Compose, host mounts, device grants or persistent volumes. Containers run as UID/GID 1654; images must be compatible. Application data is ephemeral unless the image uses an external service. Do not run stateful workloads yet.

## Security and release boundaries

- TOTP uses the host clock. RTC/NTS trust state is displayed as unverified; the production clock gate is not implemented.
- Account files are encrypted with ASP.NET Core Data Protection, but the local key ring is protected by filesystem permissions, not a hardware secure element. This does not protect a stolen readable disk.
- Session expiry is 15 minutes; interactive circuits revalidate every 10 seconds. There are no network account resets.
- Only the appliance owner can request runtime changes, with a fresh password/TOTP challenge. Signature verification for application publishers is not yet implemented: deploy only images you independently trust.
- Storage accounting uses logical file sizes plus actual free space. Allocated-block accounting, enforced application quotas, class-share editing and age expiry remain to be implemented. Purge and unsent-loss counters now persist across normal restarts.
- Cloud snapshots use atomic JSON files and a bounded telemetry archive (100 MiB / 10,000 frames) in this preview. Local resource alarms and offline telemetry replay are implemented. PostgreSQL/RLS, fleet permission scopes, a cloud incident workflow, email/webhooks and remote log synchronization remain to be implemented. See [monitoring and delivery](docs/Monitoring-and-Delivery.md).
- Raw interface capture, physical GPIO, signed A/B boot, verified root, and physical recovery are unavailable. The `os/` directory contains integration guidance and service units, not a flashable image.
- Container services are non-root with read-only roots and dropped capabilities. Replace preview PKI, pin approved image digests and complete the release security gates before external deployment. The development cloud is deployed through PRX3MUS; [hosted development setup](docs/Development-Cloud.md) tracks connectivity and owner commissioning. Production is not deployed.

See [implementation status](docs/Implementation-Status.md) for the current verification and next milestones.

PiDockOS supports signed service updates through PiDockCloud with separate local owner permission, plus local package uploads for cloud-free operation. An independent updater stages, checks and rolls back the appliance/broker programs. A one-time local bootstrap and verified public signing keys are required. See [PiDockOS updates](docs/PiDockOS-Updates.md).
