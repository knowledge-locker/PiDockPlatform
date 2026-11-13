# Implementation status

Engineering preview v0.1 — updated November 11, 2026.

The specification remains the production target. The November 11, 2026 application platform expansion now includes API/MCP discovery, scoped shared app services and registry-reference cloud release publishing. Physical hardware, full package installation and cloud alarm/log delivery remain pending. See the [version 1.2 specification and gates](PiDock-Specification-and-Implementation-Plan.md#application-platform-api-and-development-contract). This increment establishes runnable appliance/cloud services and tests; it does not deliver a flashable or production-hardened OS.

| Area | Implemented now | Remaining release work |
| --- | --- | --- |
| Accounts | Console commissioning, password/TOTP, replay persistence, throttling, protected accounts, CSRF, revocable remembered sessions and shared per-action MFA dialog | Physical-presence recovery, hardened clock gate, audited credential lifecycle, trusted local commissioning UX |
| Appliance UI | Overview, storage policy, applications, live pin map, network counters | Files, network changes/rollback, raw diagnostics, complete error/status handling |
| Containers | Separate Unix broker, constrained digest-pinned ARM64 deployment, start/stop, resource limits | Archive import, signature policy, persistence, secrets, Compose subset, hardware grants and update rollback |
| Application platform | Versioned discovery/schemas, scoped expiring app credentials, typed .NET client, shared measurements and system reads | Hosted-app credential issuance/injection, full manifests/conformance, hardware leases, subscriptions and service exports |
| Agent interface | Owner-managed encrypted/retrievable agent registrations, revocation/rotation, MCP discovery/resources and separate scoped cloud publishing | Full manifest/conformance validation, broader hosted-app grants and installation tools |
| Shared logging/alarms | Structured app/PiDock host journal, UTC/boot/monotonic metadata, priorities 1–4, scoped queries, priority-granted conditions and durable raise/update/ack/clear | Docker/system log collection, coordinated verbosity, full rule UI, cloud forwarding/notifications and escalation |
| App distribution | Tenant catalog, immutable registry-reference releases, package digests, publisher scopes and authorized version retrieval | Binary package uploads, image/signature verification, release withdrawal/channels, rollout tracking and approved local installation |
| Storage | Percentage budget, protected reserve, per-class oldest-first cleanup, policy preview/save and persistent unsent-loss counters | Allocated-block accounting, application quotas, class/age editing, long-run SD testing |
| I/O | Pi 5 RP1 GPIO v2 discovery, line direction/bias/consumer reporting, default-deny pin policy, exclusive timed maintenance claims, local owner/fresh MFA, per-page release and normal-release fallback; development simulation | Physical driver acceptance on Pi 5, electrical fallback/power-loss validation, exclusive application handoff, PWM, input simulation API and raw capture |
| Telemetry | Independent sampling/delivery, mTLS, bounded durable queue, cloud persistence receipts, deduplicated replay, local resource alarms and bounded cloud archive | Remote audit/log sync, cloud incident workflow, notification delivery and PostgreSQL history |
| Cloud users | Single console-established platform Owner; Owner-gated account/workspace signup with TOTP; multiple workspace memberships and roles; single-use username-bound invitations; team management; workspace switching; support banner/audit; stale-session rejection and legacy-account migration | Fleet/device grant scopes, expanded role-specific incident policies, verified-email/recovery-code account recovery, email invitation delivery |
| Enrollment | Single-use expiring codes, CSR validation, device-specific certificates, key-bound response recovery, local key generation and automatic MQTT activation | Isolated signing service, renewal, revocation, migration and production provisioning |
| Persistence | Atomic protected account files, segmented local telemetry, atomic fleet JSON | PostgreSQL/RLS, migrations, historical queries, log/object storage, scheduled off-site backups |
| Packaging | HTTPS cloud container, mTLS broker, local-only Compose package, non-root services | Complete release signing/CA lifecycle, public hosting, certificate provisioning, MQTT routing and PRX3MUS application installation |
| Remote interface | Local-owner opt-in, enrolled-device outbound relay, isolated device origins, fresh cloud MFA, five-minute sessions, revocation and bounded audit logging; development cloud deployed | Development and production DNS verified; signed dev.14 installed healthy on the physical Pi through cloud MFA approval. Local owner permission enabled; Safari form-origin correction deployed in cloud dev.17. Physical remote interface opened successfully with fresh MFA and matching SessionOpened audit evidence |
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

## November 12 V1 hardware clarification and GPIO increment

The opening baseline above describes the original preview; subsequent deployment records below contain later delivered capabilities. A new V1 acceptance pass must reconcile each requirement against those records and hardware evidence, rather than removing preview labels as a substitute for acceptance.

The owner confirmed that the current test device is a wall-powered Pi 5 with **no RTC backup battery and no connected GPIO hardware**. Its empty GPIO policy must remain in place. GPIO maintenance implementation and its physical acceptance procedure are documented in [GPIO maintenance](GPIO-Maintenance.md). Automated tests establish software policy and lifetime behavior; they do not establish electrical correctness on the physical board.

No battery-backed offline clock holdover can be claimed for this configuration. Authenticated network time, trusted-clock gating and physical recovery still require implementation and real-device acceptance. The full original appliance specification also requires a reproducible boot image, verified root, signed boot and A/B recovery; the existing signed service-update packages do not satisfy those requirements. No OTP provisioning, physical output writes or changes to the running appliance were performed for this increment.

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

## Workspace teams development deployment — November 12, 2026

Release `0.1.0-dev.15` deploys global accounts with workspace memberships, team invitations, switching, visible/audited Owner support access and an Owner-only public-signup toggle (enabled by default). Signup is blocked until the Owner exists; disabling public signup preserves invited-account creation directly in the assigned workspace. Membership/context changes invalidate stale access, while the selected browser retains its original remembered-sign-in expiry. The release integrates with the newer remote interface and shared fresh-MFA approval flows rather than replacing them. The Release build is warning-free and all 148 tests passed locally and in Linux publication. Verified encrypted backup/restore and hosted state/HTTPS/device checks are recorded in [Development cloud](Development-Cloud.md). Production was not changed.

Production hosting: release candidate `0.1.0-rc.1` is deployed at `pidock.knowledgelocker.ai`, with production remote origins, independent PKI, an isolated network-disabled device signer, protected release trust and verified encrypted recovery. All 150 tests pass. The production MQTT ingress rule and external server-certificate/unauthenticated-client rejection checks pass. Owner commissioning and verified post-owner recovery are complete. Physical-device production enrollment and verified post-enrollment recovery are complete. Fresh-MFA production remote browser acceptance remains pending; see [Production cloud](Production-Cloud.md).

## Hosted workspace checklist acceptance — November 12, 2026

Development release `0.1.0-dev.19` fixes shared authentication/workspace throttling and browser form-origin rejection found during real hosted testing. All 46 hosted assertions and 151 automated tests pass. Four temporary accounts, their sessions/memberships/invitations, two workspaces and two synthetic device records were removed with retained-account/key/device baseline verification. The original Owner context and enabled signup setting were restored. See [hosted acceptance details](Development-Cloud.md).


## Application platform reconciliation — November 13, 2026

The preserved API/MCP and shared-services increment has been reconciled with the signed-update and production release source. Discovery and shared APIs require HTTPS and scoped bearer credentials; browser owners cannot use their cookie as a publishing/app credential. Owner-managed discovery registrations are local-only, encrypted/retrievable, individually expiring and revocable, and use the shared action-specific MFA popup. Tokens are invalidated by owner recovery and pause during loss of required clock trust. The MCP bridge retains pinned registered-cloud matching and separate publisher credentials. Cloud catalog routes enforce tenant/application identity and immutable registry-reference releases. App GPIO/hardware grants, archive installation and automatic publisher provisioning remain separate unimplemented gates. All 204 combined tests and five Linux image-tool tests pass; production and prepared media have not been redeployed by reconciliation. See [release accounting](Release-Integration.md).
