# Docker sample lifecycle conformance

Run from the PiDock checkout:

```sh
dotnet run --project tools/PiDock.DockerConformance -c Release -- "$PWD"
```

This opt-in executable performs real Docker operations. It requires a Linux ARM64 Docker Desktop daemon, its Unix socket, the .NET 10 SDK, and access to the pinned ASP.NET runtime and `registry:2` images. It refuses to proceed if any PiDock-managed containers already exist, so it cannot pause unrelated managed apps. Other Docker workloads remain untouched. Docker Desktop supplies `host.docker.internal`; this fixture does not claim general Linux host networking support.

The tool publishes the disposable sample, builds an image from the pinned shared runtime base, pushes it to a temporary loopback-only local registry and deploys its digest through the actual `DockerOperations.DeployPrepared` path. The broker's normal restricted configuration applies: UID/GID 1654, read-only root, dropped capabilities, no privilege escalation, bounded CPU/memory/PIDs, dedicated network, loopback port mapping and memory-backed credential delivery. No host Docker socket or device is mounted into the sample.

The host uses isolated protected managed records, a fresh credential vault and a temporary pinned HTTPS certificate. Only authenticated shared I/O/engineering routes are exposed on the loopback HTTPS listener. The sample's explicit Development-only Docker setting permits exactly `host.docker.internal` in addition to the earlier loopback origins, retains hostname/certificate checks, and requires actual managed stdin bootstrap credentials and startup binding. Credentials are never image layers, command arguments, browser data or evidence output. API/MCP contracts and production authentication rules are unchanged.

The test exercises actual lifecycle and orchestration methods through their private Unix HTTP paths, then observes the same independent access monitor and shared services as the appliance host. This is a disposable host of the production broker implementation; it does not start the broker executable/systemd service or a real appliance account.

Checks include:

- Start pulse, retained latch, nonblocking delay and normal Stop output fallback.
- Before each lifecycle transition: an active force and all three accepted simulated output commands true.
- Suspend PID 1 with SIGSTOP while Docker still reports it running; then queue a write that the app cannot consume. This avoids relying on application shutdown cleanup and avoids Docker pause itself changing eligibility.
- PiDockOS maintenance pause, operator Stop, Restart and abrupt SIGKILL failure remove the lease, force and queued write, fence the registration and return every output to configured simulated fallback. The test requires cleanup within four seconds, before the five-second engineering freshness and fifteen-second lease expiry; measured times are observations, not real-time guarantees.
- Stop while maintenance-paused remains stopped after Resume/reconcile.
- New Docker start time, private bootstrap delivery and runtime instance on restart/start, cleared latch/controls, and denial of the previous startup binding.

The generated `artifacts/docker-sample-lifecycle.json` contains only non-secret evidence. Cleanup removes the exact sample container/network, temporary registry container/anonymous volume, sample image and private host state. Dependency images and Docker build cache may remain. It never prunes Docker resources. On a forcibly terminated test process, inspect and remove only its `pdc-*` registry, `pidock-ui-sample` container and exact `pidock-ui-sample-*` network before rerunning; normal failures execute cleanup.

Physical GPIO, electrical fallback, Pi hardware, power loss, full install/update/restore/remove conformance and hard real-time timing remain separate acceptance work. The default lifecycle mode deliberately disables automatic recovery to make failure cleanup independently observable.

## Health checks and automatic recovery

Run the separate health/recovery scenario with:

```sh
dotnet run --project tools/PiDock.DockerConformance -c Release -- "$PWD" --health-recovery
```

This mode builds the exact shared native health-reader source for Linux ARM64 in a pinned SDK build stage, installs its static executable root-owned with mode 0555 at `/usr/lib/pidock/health-probe`, and requests the normal managed health tmpfs. The build stage installs GCC/libc development packages; they are absent from the final runtime image. The app's existing reporter publishes actual worker liveness and connected-runtime readiness using the current startup identity.

The app requests automatic recovery and health restart, with a 30-second startup grace, 10-second freshness and one failed-check threshold. The host runs the actual `ApplicationRecoveryService` every five seconds against the actual broker health, lifecycle and orchestration methods over private Unix HTTP. It resolves all current startup credentials from the isolated vault on every recovery. Docker's own restart policy stays `no`. No test-side Start/Restart/Recover call drives either automatic failure-recovery scenario.

The test verifies:

- Native reader observations reach Starting then fresh Ready.
- SIGKILL of a running forced sample clears leases/forces/queued writes and automatically starts a new process that reaches Ready.
- SIGSTOP leaves Docker reporting Running while the app heartbeat stops. The real reader observes stale health; Unhealthy with recovery required causes bounded stop/recovery and fresh Ready.
- Each recovery consumes exactly one shared attempt and changes the private delivery, runtime instance and Docker process start time. Forces, queued writes and latch state are cleared, outputs are at simulated fallback, and the old startup binding is denied.
- An intentional Stop with recovery still enabled stays stopped for 16 seconds and at least three actual background passes. An explicitly replayed stale recovery candidate also cannot start it. Leases, forces and writes remain absent and outputs stay at fallback.

Non-secret evidence is written separately to `artifacts/docker-sample-health-recovery.json`, including native health-state observations, attempt counts, measured cleanup/Ready times and Stop observation passes. Cleanup and isolation rules are identical to the default mode. Measured recovery includes heartbeat freshness, five-second polling, retry backoff, grace and startup/Ready observation; it is not an immediate or hard real-time guarantee. This validates two automatic recoveries and Stop protection, not retry-budget exhaustion, stable-window reset, credential-loss recovery, host/systemd restart or electrical safety; focused platform tests cover additional policy cases.

## Retry limits, backoff and Stop precedence

Run the longer process-failure scenario with:

```sh
dotnet run --project tools/PiDock.DockerConformance -c Release -- "$PWD" --retry-limits
```

Allow approximately four to five minutes. This mode uses the actual host recovery worker, current credential vault, protected broker records and restricted Docker sample, with the real system clock and unchanged retry policy. Health reporting is not requested in this scenario: it isolates repeated process exits; the separate health mode covers the shared health-driven recovery path. Five-second background checks still run. Each recovered process is killed before it can satisfy the 60-second stability reset, and Docker automatic restart stays disabled.

The test observes metadata from actual protected record writes without changing or logging credential/startup material. It independently checks scheduled delays of **5, 10, 20, 40 and 80 seconds**, verifies subsequent attempts do not precede the prior deadline, and measures spacing with a monotonic clock. The first recovery is eligible immediately; each attempt records the deadline that would gate the next attempt. The five-attempt limit prevents a sixth even when the final 80-second deadline passes.

Checks include:

- Begin with all three outputs forced on, then repeatedly SIGKILL the sample. Every recovery gets fresh startup authority and cleared controls/output fallback; old binding is rejected. Successful startup alone does not reset the attempt budget.
- After five automatic recoveries, kill the fifth replacement and observe persisted `suspended` state with requested-running intent and recovery policy retained. Observe through the final 80-second deadline plus 16 seconds and at least three further background passes; no sixth attempt or process start is permitted.
- Use the normal explicit Start operation to reset the exhausted budget. After two more recoveries, crash during the ten-second backoff and issue Stop while its retry deadline is still in the future. Stop clears the pending deadline and running request while leaving automatic recovery enabled.
- Continue through that former deadline plus 16 seconds and at least three background passes, then replay the stale pre-Stop recovery candidate. No subsequent attempt/start is allowed; leases/forces/writes remain absent and outputs stay at simulated fallback.

Evidence is written to `artifacts/docker-sample-retry-limits.json`, including scheduled deadlines, observed attempt metadata, actual spacing, suspension and Stop checks. Existing cleanup/isolation rules apply. This supplies real Docker process-exit retry/exhaustion evidence; continuous-stability reset, credential-loss recovery, host/systemd/Pi restart, health-failure exhaustion and physical safety remain distinct scenarios. Use only one scenario flag per run.

## Sustained health and recovery budget reset

Run the native-health stability scenario with:

```sh
dotnet run --project tools/PiDock.DockerConformance -c Release -- "$PWD" --budget-reset
```

Allow approximately four to five minutes with the real sixty-second stability window. This mode uses the native health reader, actual reporter/recovery worker, protected records and unchanged clock/policy. It enables an authenticated `/fixture/readiness` fault-injection route only in the explicitly opted-in Development Docker image. That route accepts a Boolean using the same private bridge credential; it is absent from ordinary sample runs and is not a UI bridge grant or platform API/MCP contract. The scenario checks anonymous denial and malformed-body rejection.

Checks include:

- Crash/recover once and verify bootstrap and brief Ready operation retain attempt one.
- After twenty seconds of Ready, withdraw readiness while leaving the process live. Keep it Not-Ready past the original sixty-second window. Neither total uptime nor a short healthy period may reset the budget; readiness withdrawal alone must not restart the live process.
- Restore Ready and begin a new stability window. Require continuous fresh native Ready observations for sixty seconds; the test rejects any early reset. Actual protected record writes must show an automatic `stable` reset only after that window, without a process restart or output-state restoration.
- Crash after reset and verify recovery spends attempt one again. Stop partway through its new healthy window, observe past the former stability deadline plus sixteen seconds and three further background passes, then replay a stale recovery candidate. No late reset or recovery may restart the process or restore resources.

Only **automatic** budget replenishment requires sustained health. Explicit operator Start/Restart/Stop retain their existing administrative reset semantics; Stop also withdraws the running request and clears the stability window. Non-secret evidence, including automatic versus administrative reset metadata, is written to `artifacts/docker-sample-budget-reset.json`. Existing cleanup and target limits apply. This scenario does not simulate a host clock jump or loss of the health-check observer; platform policy tests cover monitoring gaps separately.

## Credential loss, repair and Stop precedence

Run the native-health credential scenario with:

```sh
dotnet run --project tools/PiDock.DockerConformance -c Release -- "$PWD" --credential-recovery
```

Allow approximately four to five minutes. This mode uses the actual credential vault, Owner password/fresh-TOTP confirmation, recovery worker, protected broker records, stdin/tmpfs delivery and native health reader. It provisions only a disposable isolated Owner account. Secrets and authenticator codes remain in process/private state; no real account or credential is accessed. Clock, token validity rules and the thirty-second credential recheck policy are unchanged. Waiting for an unused real TOTP step may add time.

The scenario revokes the sample's named I/O credential while outputs are forced on, then crashes the process. It observes at least two unavailable-credential decisions across the real thirty-second deadline: attempts and startup identity must remain unchanged, no process may start, resources must be cleared, and the broker's retained credential snapshot must not be used as a fallback.

Repair issues a fresh credential version through the vault. Without a test-side Start/Restart/Recover call, the background worker resumes at its next eligible check, resolves all current credentials and spends exactly one startup attempt. The broker delivery material must match the current vault version; reaching fresh Ready and producing shared frames establishes that the actual container can use it. Old tokens are rejected with current startup binding, and old startup binding is rejected independently using unchanged valid engineering credentials. Output state and forces are not restored.

The first repaired credential has a real short expiry after the deferred startup deadline. The tool waits for natural expiry without editing the clock or stored credential, crashes the process again and checks repeated waiting with its existing attempt count preserved. A new valid version resumes automatic recovery with exactly one additional attempt. Expiry alone does not request process restart; the scenario explicitly introduces the process failure after expiry.

Finally, revoke again and issue intentional Stop while recovery is waiting. Repair credentials while stopped, then observe through both repair and the former retry deadline plus sixteen seconds and at least three worker passes. A stale recovery candidate also cannot start it. Only an explicit Start is allowed to resume, and that Start must resolve current vault credentials and begin with cleared controls. The test stops the sample again afterward.

Non-secret evidence is generated at `artifacts/docker-sample-credential-recovery.json`, including versions, unchanged blocked counts, actual recheck deadlines and Stop observations. No token, bootstrap delivery identity, Owner password or authenticator secret/code is included. Existing cleanup and target limits apply. This covers revocation, natural expiry, repair and Stop for a named managed platform credential; unreadable/corrupt stores, removed containers and host/key recovery remain separate failure classes.

## Integrated app workflow and PiDockOS browser review

Run in an interactive terminal:

```sh
dotnet run --project tools/PiDock.DockerConformance -c Release -- "$PWD" --workflow
```

This scenario requires the sample data volume `pidock-app-ui-sample-data` to be absent; it never adopts existing retained data. It creates two immutable Linux ARM64 sample images with visible `one`/`two` version badges, a persistent-data volume, the exact native health reader and a separately pinned restricted data-restore helper. It also builds and starts the actual PiDockOS UI review shell on a temporary loopback port. Allow time for builds, five browser checkpoints and fresh Owner TOTP steps during final credential revocation.

The driver proves unapproved installation is denied, approves the exact runtime manifest, deploys with managed resources, and writes explicit host registration through a private reloadable file. **This is the supported configured registration path, not automatic transactional package installation.** The review shell reads the same disposable Unix broker inventory and hosts the actual app using the shared isolated UI/command/live-data bridge. It uses its Development review identity; production sign-in/step-up policy is unchanged. Operator lifecycle mutations are driven by the trusted fixture coordinator through actual broker methods; this does not claim every production Owner control was exercised through its form.

At each printed browser checkpoint, inspect the linked page and press Enter only after completing the review. Each checkpoint has a five-minute deadline; EOF/unattended execution fails rather than claiming browser evidence.

1. **Installed and registered:** confirm the Applications launch entry and managed Ready inventory, open the workspace, issue an authenticated counter command, close/reopen or refresh, and observe live state. The driver then seeds persistent counter 3.
2. **Updated:** the actual shared update worker stages the approved second image, switches and verifies Ready, retaining the loopback host route and counter 3. The workspace must show Version two. Increment the counter through the UI to exercise image-only rollback preserving newer data.
3. **Rolled back:** the shared worker restores the original image with fresh startup authority. Version one returns while the latest counter remains unchanged; rollback does not rewind data.
4. **Restored:** maintenance pause, encrypted backup, Resume, seven counter increments, another pause and restore must produce a pre-restore safety snapshot. Restore keeps maintenance paused until explicit Resume. The workspace returns to the snapshot counter with cleared forces and simulated outputs at fallback.
5. **Removed:** runtime/network removal must stop at `awaiting_external_cleanup` and reject Resume. Live host registration and configured bridge credential removal revoke content/command access; reload must show Application unavailable and remove the launch entry. The driver then revokes all managed credentials using actual fresh Owner confirmation, removes the shared I/O grants, and completes the removal barrier only after inspecting those domains. Maintenance remains paused. Data volume and both manual/safety snapshots must still exist.

The selected process may initialize its authorized I/O while staging or verifying update/rollback readiness. Switching, pending rollback and failed rollback remain blocked; old startup identities cannot revive. This avoids a circular dependency where readiness needs I/O but all I/O was denied until promotion.

The generated `artifacts/docker-sample-workflow.json` contains only non-secret stage results and image/data evidence. Exact fixture teardown then removes its test containers/networks/helper, images, data volume, registry anonymous volume, private state and snapshots **after** verifying that platform removal retained data. Dependency images/build cache may remain. UI screenshots may be retained separately as review artifacts. This does not validate package distribution/signatures, every interrupted-operation case, actual Pi/systemd commissioning or physical GPIO.

## Final target security and actual executable restarts

Run after a Release solution build:

```sh
dotnet build PiDock.sln -c Release
dotnet run --project tools/PiDock.DockerConformance -c Release -- "$PWD" --target-validation
```

This mutually exclusive mode requires an idle PiDock managed-container inventory and absent sample data volume. It owns isolated state, accounts, credentials, registry, image, volume and real broker/appliance executable processes. It does not adopt installed apps or user data. Docker Desktop Linux ARM64 is the reviewed target; the host executables run on the Mac. Allow a few minutes for image builds, health/startup and graceful restarts.

The actual appliance first runs in Production with normal password/TOTP, CSRF, secure cookies and role checks. Anonymous/Viewer access, MFA replay, absent CSRF, an unapproved Host header and untrusted TLS are denied; Development I/O stays disabled. A temporary exact certificate pin is used only by private test clients and the disposable sample. Machine trust is unchanged. The certificate includes the reserved `unapproved.invalid` SAN solely so the negative Host-header request reaches HTTP filtering; that hostname is absent from AllowedHosts, and no request resolves it.

The host is explicitly restarted in Development for live simulator checks. The tool inspects actual kernel restrictions, resource limits and credential ownership/modes; tries forbidden root writes and checks no host socket/GPIO devices are present; tests scope/startup/grant denials; and observes VM clock skew. It checks protected shared-shell HTML without disclosing bootstrap credentials.

Graceful restarts target only the tool-owned actual broker/appliance executables. Broker restart must stop/resume the container with current delivery, retained data and cleared forces. Appliance-host restart must invalidate old leases, clear engineering state and restore simulator fallback; the disconnected sample holds unavailable until explicit app Restart, which must reinitialize without forces. Intentional Stop must survive restarting both services and a sixteen-second observation. Retained session keys and rejection of an unrelated decryption key ring are checked. These are service-process restarts, not systemd, Docker-daemon, VM, host reboot or power-loss tests.

The non-secret `artifacts/target-security-restart.json` records target versions, image identity, source revision/dirty status, platform assembly hashes, and all five result groups. Cleanup removes only owned processes, containers/networks, registry anonymous volume, sample volume/image and temporary private state; dependency images/build cache may remain. A passing run contributes evidence to the explicit [development gate review](../../docs/Application-Development-Gate.md); it does not approve production/physical operation on its own.

## Application library and registry installation

```sh
dotnet run --project tools/PiDock.DockerConformance -c Release -- "$PWD" --application-library
```

This mutually exclusive mode uses the disposable sample, an isolated HTTPS cloud with real publisher and browser-session endpoints, a loopback registry, private broker state/vault and the normal restricted Docker deployment path. It publishes an installable definition, verifies idempotency and immutable-version rejection, denies anonymous download, signs into the fixture cloud normally, downloads the attachment and imports its bytes through the shared local reader. No hosted account or Logic Engine source is used.

The actual broker denies installation before exact-runtime image approval. The test then approves those settings and removes the built image from Docker's image store before both local and cloud-command installation. Each install must resolve the digest through the registry; the sample runs as Linux ARM64, receives current private credentials/data/health resources, reaches Ready and passes the latch/delay/Stop exercise. Cloud command validation checks live local opt-in, rejects stale claimed permission when the actual local policy is off, exercises a receipt over the old 4 KiB bound, and records completion without replaying an accepted command. The command receipt is serialized and delivered in-process; this mode does not claim a separate live MQTT-network or physical Pi acceptance.

Non-secret evidence is written to `artifacts/docker-application-library.json`. Cleanup removes only the fixture containers, networks, image, private state, registry and sample data volume. Other local preview containers remain intact. The same idle-managed-inventory and absent-sample-volume guards apply.
