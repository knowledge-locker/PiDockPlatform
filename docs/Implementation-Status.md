# Implementation status

Engineering preview v0.1 — updated November 11, 2026.

The specification remains the production target. The November 11, 2026 application platform expansion now includes API/MCP discovery, scoped shared app services and registry-reference cloud release publishing. Physical hardware, full package installation and cloud alarm/log delivery remain pending. See the [version 1.2 specification and gates](PiDock-Specification-and-Implementation-Plan.md#application-platform-api-and-development-contract). This increment establishes runnable appliance/cloud services and tests; it does not deliver a flashable or production-hardened OS.

| Area | Implemented now | Remaining release work |
| --- | --- | --- |
| Accounts | Console commissioning, password/TOTP, replay persistence, throttling, protected accounts, CSRF, session expiration | Physical-presence recovery, hardened clock gate, audited credential lifecycle, trusted local commissioning UX |
| Appliance UI | Overview, storage policy, applications, live pin map, network counters | Files, network changes/rollback, raw diagnostics, complete error/status handling |
| Containers | Separate Unix broker, constrained digest-pinned ARM64 deployment, start/stop, resource limits | Archive import, signature policy, persistence, secrets, Compose subset, hardware grants and update rollback |
| Application platform | Versioned discovery/schemas, scoped expiring app credentials, typed .NET client, shared measurements and system reads | Hosted-app credential issuance/injection, full manifests/conformance, hardware leases, subscriptions and service exports |
| Agent interface | Owner-managed encrypted/retrievable agent registrations, revocation/rotation, MCP discovery/resources and separate scoped cloud publishing | Full manifest/conformance validation, broader hosted-app grants and installation tools |
| Shared logging/alarms | Structured app/PiDock host journal, UTC/boot/monotonic metadata, priorities 1–4, scoped queries, priority-granted conditions and durable raise/update/ack/clear | Docker/system log collection, coordinated verbosity, full rule UI, cloud forwarding/notifications and escalation |
| App distribution | Tenant catalog, immutable registry-reference releases, package digests, publisher scopes and authorized version retrieval | Binary package uploads, image/signature verification, release withdrawal/channels, rollout tracking and approved local installation |
| Storage | Percentage budget, protected reserve, per-class oldest-first cleanup, policy preview/save and persistent unsent-loss counters | Allocated-block accounting, application quotas, class/age editing, long-run SD testing |
| I/O | Correct header map, unknown physical values, opt-in timed simulation | Pi 5 driver, exclusive app handoff, PWM, input simulation API, physical fallback and raw capture |
| Telemetry | Independent sampling/delivery, mTLS, bounded durable queue, cloud persistence receipts, deduplicated replay, local resource alarms and bounded cloud archive | Remote audit/log sync, cloud incident workflow, notification delivery and PostgreSQL history |
| Cloud users | Single console-established platform Owner; Owner-gated account/workspace signup with TOTP; multiple workspace memberships and roles; single-use username-bound invitations; team management; workspace switching; support banner/audit; stale-session rejection and legacy-account migration | Fleet/device grant scopes, expanded role-specific incident policies, verified-email/recovery-code account recovery, email invitation delivery |
| Enrollment | Single-use expiring codes, CSR validation, device-specific certificates, key-bound response recovery, local key generation and automatic MQTT activation | Isolated signing service, renewal, revocation, migration and production provisioning |
| Persistence | Atomic protected account files, segmented local telemetry, atomic fleet JSON | PostgreSQL/RLS, migrations, historical queries, log/object storage, scheduled off-site backups |
| Packaging | HTTPS cloud container, mTLS broker, local-only Compose package, non-root services | Complete release signing/CA lifecycle, public hosting, certificate provisioning, MQTT routing and PRX3MUS application installation |
| OS | Systemd integration units and acceptance checklist | Buildroot image, supported kernel/drivers, RTC/NTS, verified root, signed A/B updates, secure provisioning |

## Next implementation order

1. Validate the application stack on the available Pi 4 with a 64-bit OS, then the Pi 3B with its tighter memory budget. Use network time after boot for battery-free development. Validate Pi 5-specific kernel/RP1 I/O, RTC holdover and secure boot/update behavior separately when that board is available.
2. Add hardware-backed commissioning/recovery flow and trusted-clock gating before allowing production authentication.
3. Complete runtime credential issuance/injection, manifest/conformance validation, named service exports and hardware leases. API/MCP discovery, scoped shared services and independently authorized cloud publishing are now implemented in preview; appliance registration alone still grants no publishing authority.
4. Extend shared local services and pass full platform conformance before hosted product app development: scoped simulated/hardware I/O, authorized telemetry exchange, centralized host/app logging, common timestamps and diagnostic controls, priorities Critical=1 / Warning=2 / Alert=3 / Notice=4, and durable alarm lifecycle. Use small conformance fixtures to validate offline, restart, limits and isolation.
5. Implement expanded manifests, persistence/secrets, publisher trust, direct application package upload and health rollback. Add PiDockCloud tenant catalog, immutable package publishing, artifact storage/downloads, publisher grants and registered-device distribution subject to local installation approval/policy. Cloud registration alone grants no publishing or installation authority.
6. Extend monitoring/replay with selected shared logs/telemetry, correlated cloud incidents and notifications; replace the cloud JSON store with PostgreSQL/RLS and verify catalog/artifact/log backup and restore.
7. Build and fault-test the signed image/update pipeline, complete independent security review, and validate PROX3MUS deployment. Continue independent hardware/security work alongside platform implementation; re-estimate delivery dates for the expanded scope.

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

## Application platform discovery increment — November 11, 2026

Read-only `/api/platform/v1` discovery, contract schemas and authoring guidance now share models with the MCP stdio bridge. Discovery projects the saved cloud URL without credentials; connectivity, publisher permission and endpoints are not inferred. A locally provisioned expiring credential is restricted to discovery. Shared app runtime services, cloud publishing, installation validation and the product-app conformance gate remain incomplete. See [setup and limits](Application-Platform.md).

Validation for this discovery increment: locked restore succeeded; Release build completed with zero warnings/errors; all 102 tests passed on macOS, including seven discovery/MCP cases and offline guide/schema resource reads. NuGet reported no known vulnerable dependencies at check time. No live cloud publishing, appliance deployment, physical I/O or production credentials were changed.

### Local telemetry historian parity

PiDockOS **Telemetry** now uses the same resource trend component and CSV/JSON export formatting as the PiDockCloud device dashboard. Both expose presets, live/custom UTC windows, earlier/later navigation, zoom, signal selection, chart cursors and sample inspection. Local queries and authenticated downloads read appliance history without a cloud connection. New queued reports retain a private full local snapshot alongside the filtered delivery envelope; acknowledgement and restart preserve it within the existing telemetry retention budget. Earlier filtered or purged measurements cannot be reconstructed. See [Device dashboard](Device-Dashboard.md).

## Cloud publishing and shared-service increment — November 11, 2026

PiDockCloud now durably publishes immutable `registry-reference-v1` releases and exposes tenant-scoped catalog/version downloads. MCP verifies the registered origin against an explicit cloud pin and checks the tenant/permissions before publishing. PiDockOS now accepts scoped structured logs, typed measurements and app alarm raise/update/acknowledge/clear; PiDock host application logs use the same bounded source journal. Alarm snapshots and a bounded transition outbox survive restart and rotating history. See [service configuration, limits and remaining gates](Cloud-Publishing-and-App-Services.md).

This is an engineering increment, not completion of the app platform: binary packages, automatic install/credential delivery, Docker stdout/system log capture, physical I/O, streaming, coordinated verbosity, cloud log/alarm forwarding and notifications remain pending. No live applications, credentials or cloud publications were created by this work.

Validation for the shared-service/publishing increment: locked restore succeeded; Release build completed with zero warnings/errors; the full 110-test suite passed on macOS, including tenant/version immutability, pinned MCP-to-cloud publication, scoped source reads, retained-event idempotency, restart-preserved alarms, bounded source retention and storage-pressure recovery. NuGet reported no known vulnerable dependencies at check time.

## Owner-managed agent registrations — November 11, 2026

PiDockOS now has an owner-only Agent access page with named registrations, generated encrypted/retrievable tokens, HTTPS discovery URLs, copyable third-party connection prompts, expiry, rotation and revocation. Default access is discovery-only with optional system reads. Every secret operation verifies the current owner and fresh password/TOTP; token authentication checks revocation and expiry for each request. The registry uses the existing persistent Data Protection key ring and private state permissions. Hosted-app credential injection, broader grants and hardware-backed secret protection remain pending. See [agent access](Agent-Access.md).

Validation for owner-managed registrations: all 113 tests pass; Release build completed with zero warnings/errors; locked restore and whitespace checks pass. The actual Agent access page was inspected in the isolated browser preview with registration/reveal disabled. No live agent tokens or public network exposure were created.

### Signed PiDockOS service updates

Cloud release publication and individual device approvals, separate local cloud-update permission, cloud-free uploads, a shared P-256 signed package verifier, and the independent updater service are implemented. Service replacement uses a durable recovery journal, exact-release health probes and automatic restoration of previous appliance/broker programs. Bootstrap, signing and preview limits are documented in [PiDockOS updates](PiDockOS-Updates.md). This does not implement full A/B boot images and has not been installed on a physical appliance by this change.

## Workspaces and teams — November 11, 2026

PiDockCloud now separates global accounts from per-workspace memberships. Signup is blocked on both GET and POST until the single platform Owner is commissioned. Signup verifies an external authenticator before atomically creating the account, workspace and Administrator membership. Existing users can create more workspaces, accept username-bound seven-day invitations, and switch between independent membership roles. Administrators can assign roles, cancel invitations and remove members while retaining at least one Administrator; device registrations stay with the workspace. The Owner starts in platform administration and explicitly selects a tenant for visible, audited support access. Old cookies/circuits are invalidated when workspace context or membership changes; tenant data services also validate session state. Existing account arrays migrate without changing tenant/device identifiers; multiple legacy Owners stop startup pending local resolution. See [Cloud workspaces](Cloud-Workspaces.md).

Validation for the workspace increment: the solution Release build completed with zero warnings/errors and all 142 automated tests passed. Coverage includes both signup bootstrap gates, MFA and CSRF, legacy migration and multiple-Owner rejection, username-bound/expired/cancelled invitations, independent membership roles, last-Administrator protection, stale-session rejection, cross-workspace HTTP device reads, and preservation of device data after removal. Local browser review confirmed locked/unlocked signup, account setup layout, the platform Owner landing page, workspace switching, the named support banner and team-management controls. These changes are local; hosted deployment has not been performed.

## Owner-controlled public signup

An Owner-only, audited **Allow multi-tenant signup** setting now defaults to enabled and persists across restarts and upgrades. Disabling it blocks public workspace signup, stale signup forms and additional workspace creation by non-Owners. Team invitations remain active: new invited users complete MFA and are atomically added only to their invited workspace; existing-user acceptance is unchanged. The Owner can still create an initial workspace. Existing tenants, memberships and devices are preserved. All 144 tests passed, including default/persistence, Owner-only settings authorization, CSRF, stale-form rejection, invitation-only account creation with assigned roles, single-use consumption and workspace-creation denial. Hosted deployment has not been performed.
