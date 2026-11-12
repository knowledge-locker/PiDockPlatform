# Implementation status

Engineering preview v0.1 — November 10, 2026.

The specification remains the production target. This increment establishes runnable appliance/cloud services and tests; it does not deliver a flashable or production-hardened OS.

| Area | Implemented now | Remaining release work |
| --- | --- | --- |
| Accounts | Console commissioning, password/TOTP, replay persistence, throttling, protected accounts, CSRF, revocable remembered sessions and shared per-action MFA dialog | Physical-presence recovery, hardened clock gate, audited credential lifecycle, trusted local commissioning UX |
| Appliance UI | Overview, storage policy, applications, live pin map, network counters | Files, network changes/rollback, raw diagnostics, complete error/status handling |
| Containers | Separate Unix broker, constrained digest-pinned ARM64 deployment, start/stop, resource limits | Archive import, signature policy, persistence, secrets, Compose subset, hardware grants and update rollback |
| Storage | Percentage budget, protected reserve, per-class oldest-first cleanup, policy preview/save and persistent unsent-loss counters | Allocated-block accounting, application quotas, class/age editing, long-run SD testing |
| I/O | Correct header map, unknown physical values, opt-in timed simulation | Pi 5 driver, exclusive app handoff, PWM, input simulation API, physical fallback and raw capture |
| Telemetry | Independent sampling/delivery, mTLS, bounded durable queue, cloud persistence receipts, deduplicated replay, local resource alarms and bounded cloud archive | Remote audit/log sync, cloud incident workflow, notification delivery and PostgreSQL history |
| Cloud users | Multiple console-provisioned users, tenant claim enforcement, Owner/Administrator-only enrollment with fresh MFA | Online user administration, fleet/device grant scopes, other privileged action policies, account recovery |
| Enrollment | Single-use expiring codes, CSR validation, device-specific certificates, key-bound response recovery, local key generation and automatic MQTT activation | Isolated signing service, renewal, revocation, migration and production provisioning |
| Persistence | Atomic protected account files, segmented local telemetry, atomic fleet JSON | PostgreSQL/RLS, migrations, historical queries, log/object storage, scheduled off-site backups |
| Packaging | HTTPS cloud container, mTLS broker, local-only Compose package, non-root services | Complete release signing/CA lifecycle, public hosting, certificate provisioning, MQTT routing and PRX3MUS application installation |
| Remote interface | Local-owner opt-in, enrolled-device outbound relay, isolated device origins, fresh cloud MFA, five-minute sessions, revocation and bounded audit logging; development cloud deployed | DNS owner verification, signed dev.14 Pi installation and local owner enablement pending; physical remote-session acceptance pending |
| OS | Systemd integration units and acceptance checklist | Buildroot image, supported kernel/drivers, RTC/NTS, verified root, signed A/B updates, secure provisioning |

## Next implementation order

1. Validate the application stack on the available Pi 4 with a 64-bit OS, then the Pi 3B with its tighter memory budget. Use network time after boot for battery-free development. Validate Pi 5-specific kernel/RP1 I/O, RTC holdover and secure boot/update behavior separately when that board is available.
2. Add hardware-backed commissioning/recovery flow and trusted-clock gating before allowing production authentication.
3. Implement application manifests, persistent data and publisher policy; integrate the broker with narrow device grants.
4. Extend the implemented local resource alarms and telemetry replay with remote audit/log synchronization, a cloud incident workflow and notification routing.
5. Replace the cloud JSON store with PostgreSQL and RLS, then add durable alarms, user administration and notifications.
6. Build and fault-test the signed image/update pipeline, complete independent security review, and validate PROX3MUS deployment.

No DNS, public service, production credentials, firmware fuses or Raspberry Pi hardware were changed by this increment.

## Verified in this workspace

- Fifty-nine automated tests passed on macOS and Linux ARM64, including HTTP authentication/CSRF, TOTP persistence, storage rotation, tenant isolation, broker isolation, alarm recovery and offline telemetry delivery.
- Release builds completed with zero warnings and zero errors.
- The cloud Compose stack built and served HTTPS; its broker accepted the client certificate and forwarded real host telemetry to the durable fleet store.
- A certificate-authenticated attempt to publish into another tenant returned MQTT Not authorized.
- The Unix broker deployed the official digest-pinned .NET sample on Docker ARM64; its HTTP endpoint responded. Container inspection confirmed UID/GID 1654, read-only root, privileged=false, dropped capabilities and a 128 MiB memory limit. Start and stop both worked.
- NuGet reported no known vulnerable package dependencies at validation time; this does not substitute for OS/runtime or security review.
- Initial product browser testing was blocked by the untrusted development HTTPS certificate. A subsequent isolated Development-only review harness now renders the actual screen components with sample data on loopback HTTP; desktop/mobile visual and interaction checks were completed without changing certificate trust. See UI-Review.md.

Disposable validation accounts and the sample workload are removed after verification. No default user credentials are shipped.

## Monitoring increment

Local sampling now runs independently of network delivery. The bounded outbox persists across restart and clears a sample only after the cloud saves it and publishes a scoped receipt. Lost receipts produce safe retries; replay is archived without replacing newer live readings. Pending samples may still be purged under the configured data policy, with persistent loss counters.

Resource alarms use monotonic hold/recovery timers, preserve active identity across restart and retain unknown-sensor alarms until a known recovery reading. The appliance overview shows alarms and delivery status; the cloud shows reported alarm counts. Alarm configuration is a startup configuration file, not yet a UI editor. External messages and log synchronization remain pending.

## Enrollment and naming increment

The products are now PiDockOS and PiDockCloud, collectively PiDockPlatform. PiDockCloud has an Add device screen and PiDockOS has Cloud connection. Enrollment uses ten-minute organization-scoped invitations, local private keys, HTTPS CSR exchange and MQTT activation without a service restart. Its certificate identity is constrained by Mosquitto patterns. The real Docker check confirmed HTTPS issuance, wrong-tenant rejection, persisted cloud receipts and outage replay. See [Device enrollment](Device-Enrollment.md) and [Deployment environments](Deployment-Environments.md).

The isolated real MQTT/mTLS outage test passed: first persistence receipt, continued sampling with the broker stopped, and reconnection with four unique archived samples and zero pending samples. The normal preview stack was left running. See [Monitoring and delivery](Monitoring-and-Delivery.md) for guarantees and limits.

Validation for the enrollment increment: 59 tests pass on macOS and Linux ARM64; the Release build has zero warnings/errors. The isolated HTTPS enrollment plus MQTT outage check passed, including cross-tenant rejection and four unique archived samples with zero pending. The source repository preserves its initial license and commit.

GitHub CI also passed on Linux AMD64. A manually dispatched publication produced the first PiDockCloud development image with SBOM/provenance and PRX3MUS release metadata. Both hosted domain relationships are staged; DNS and live service deployment remain separate pending runtime storage, broker and signing configuration.

## Hosted persistence increment

External environment-specific cloud/broker volumes and encrypted restic repositories are prepared on both hosts. PRX3MUS now supports operator-controlled labelled volumes, individual read-only secret mounts, constrained non-root runtimes, single-writer replacement and safe rollback. Both host agents were updated without replacing existing application containers. Disposable container replacement, account/key retention, active-writer rejection and encrypted recovery passed; the development host also passed real agent-driven replacement. No hosted PiDock application or DNS was installed. Scheduled off-site backups, certificates, broker routing and owner commissioning remain pending. See [Persistent storage](Persistent-Storage.md).

## Hosted development connectivity

PiDockCloud development release `0.1.0-dev.7` is now deployed through PRX3MUS with public Caddy HTTPS, separate development PKI and a certificate-only persistent MQTT broker. Its IPv4 TCP 8883 provider firewall rule is configured. Hosted HTTPS enrollment, cross-tenant MQTT rejection, telemetry persistence/receipt and cleanup passed; an external Mac check verified MQTT TLS and anonymous-client rejection. The cloud owner and first physical Pi are commissioned. Production remains uninstalled. The MQTT worker/broker certificates expire on 2027-02-09 UTC and do not renew automatically. See [Hosted development](Development-Cloud.md).

Authenticator commissioning now permits three attempts per code, explains the distinction between the setup secret and generated OTP, and handles terminal paste markers. It still requires two consecutive valid codes before saving an account; failed setup leaves no account. The exact release source passed 65 automated tests and an isolated interactive-terminal check including invalid OTP retry and pasted input.

Post-enrollment UX now polls tenant/device-specific live contact, returns automatically to the fleet on success and presents a retry path after two minutes without confirmation. Registered devices keep their identity when telemetry is unavailable. PiDockOS offers fresh-MFA transport reconnect while retaining its queued telemetry and prepopulates a configurable server address. The actual Pi update passed its HTTPS health check and retained live reporting. The published cloud source passed 75 tests; browser checks covered live redirect, registration timeout/retry and the filled server field.

## Device dashboard and controlled container operations

Development release `0.1.0-dev.9` adds relative contact age, a device view action, uptime/resource KPIs, selectable resource trend tracks, time-window navigation and tenant-scoped CSV/JSON exports. Cloud container start/stop/restart uses short-lived commands in existing mTLS telemetry receipts and persists device results. It requires fresh cloud Owner/Administrator MFA, a current inventory report and an explicit local owner opt-in. Managed-label checks and persisted device command IDs limit execution; remote image deployment, shell and device I/O are not included.

The published release passed 90 tests. The isolated real MQTT check passed HTTPS enrollment, cross-tenant rejection, one command execution/result round trip and outage replay with five archived samples and zero pending. Browser review exercised the device KPI/trend/container layout; HTTP checks validated selected-signal CSV/JSON downloads. The matching Pi packages were installed with local sudo. Installed hashes match the verified packages, both Pi services pass health checks, and live telemetry includes the refreshed container inventory. No managed containers are installed yet; remote control remains off. See [Device dashboard](Device-Dashboard.md).

Development release `0.1.0-dev.10` adds local-owner MFA protected telemetry channel administration on PiDockOS and visible cloud-control badges in the fleet and device dashboard. The channel policy applies to newly queued reports and survives restarts; management heartbeats and command acknowledgements remain available. See [Device dashboard](Device-Dashboard.md) for availability and buffering semantics.
