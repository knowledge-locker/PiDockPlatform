# Application development gate: Docker simulation target

November 16 update: the shared app platform is merged with current main, **440 combined tests pass**, cloud/container images and the signed alpha.7 factory image are rebuilt and validated. The local preview is updated. See the [exact release identities and deployment boundaries](Reconciled-Images-2026-11-16.md). The simulation-first development gate remains PASS; physical/Production commissioning is separate.

Decision: **PASS for simulation-first application development**, reviewed November 15, 2026 (final run recorded November 16 at 03:16 UTC). This completes the three-batch pre-app queue. No further shared validation batch is required before starting Logic Engine within this scope. This is a development decision, not production commissioning or physical GPIO approval.

## Reviewed target and scope

The target is Docker Desktop 4.82.0 (233772), Engine 29.6.1/API 1.55, Linux ARM64, kernel 6.12.76-linuxkit, containerd 2.2.5 and runc 1.3.6. The actual appliance and broker executables run on macOS 27.2.1 with .NET 10.0.10, supervising the real restricted Linux container. They are not systemd services on a Raspberry Pi. The final report records the immutable sample image, source revision, dirty working-tree status and SHA-256 hashes of the five platform assemblies; this is reviewed working-tree evidence, not a signed release build.

Accepted development path: exact-image/runtime Owner approval, explicitly configured UI registration and grants, normal shared hosting/styling, server-side private credentials, bounded managed app data, low-rate simulated I/O and engineering controls. Configured registration and explicit external cleanup are sufficient for this development slice; automatic transactional package registration/uninstall and publisher signing are not claimed. Discover live versions, availability and grants on every new target; this record does not enable features or authorize deployments elsewhere.

The actual Production host was tested with normal Owner password/TOTP authentication, CSRF and role enforcement. Simulation remained disabled in Production. The host was then restarted explicitly in Development for simulator tests. Development simulator availability must never be presented as physical support or Production availability.

## Reviewed evidence

| Batch | Result and retained evidence |
| --- | --- |
| Credential-loss and repaired-credential recovery | PASS: revocation and natural expiry wait without consuming attempts, repair uses current credentials, and Stop wins. [Report](../artifacts/docker-sample-credential-recovery.json). |
| Integrated app workflow | PASS: approved install/configured registration, real shared-shell browser launch, update and image rollback retaining data, encrypted backup/restore with safety snapshot, and removal including external cleanup. [Report](../artifacts/docker-sample-workflow.json). |
| Target security/restart and this scope review | PASS: actual production authentication and negative cases, kernel/container restrictions, private credential delivery, actual broker and appliance executable restarts, retained data, cleared controls, and Stop across both restarts. [Report](../artifacts/target-security-restart.json). |

Earlier actual Docker runs cover pause/stop/restart/crash, fresh native readiness and health recovery, five-attempt recovery suspension/backoff, sustained-health budget reset, and cleared forces. See the [readiness history](Application-Readiness.md) and [reproducible procedures](../tools/PiDock.DockerConformance/README.md). The current Release regression suite passes **348 tests**; the complete solution builds with **zero warnings and errors**.

The final security run denied anonymous and Viewer access, missing CSRF, TOTP replay across host reload, untrusted TLS, an unapproved Host header, wrong scopes, absent startup binding and ungranted aliases. It observed secure session-cookie attributes, UID 1654, read-only root, no effective capabilities, no-new-privileges, seccomp, resource bounds, no host bind/socket/GPIO access, and private credential ownership/modes. VM clock skew was zero seconds at observation; clock jumps and loss of time synchronization were not exercised.

Graceful restart of the actual broker stopped/resumed the sample through its protected shutdown state with a fresh startup and current credential delivery, counter 5 retained and no restored forces. Actual appliance-host restart invalidated old leases, cleared engineering state and returned simulator outputs to fallback; the sample held unavailable after transport loss until explicit app Restart reinitialized it. That fail-closed/manual restart behavior is accepted for this development slice. Protected session keys survived host restart; an unrelated key ring could not decrypt the account state. Intentional Stop survived restarting both services and sixteen seconds of background observation without a new startup.

## Work now permitted

Logic Engine can begin its program model, execution semantics, deterministic engine tests, durable program service and app scaffold/editor within the shared UI contract. Integrate the canonical latch/delayed-output/watch program against the discovered simulator. Product-specific conformance, producer correctness and runtime semantics are still work to implement and test, rather than another platform prerequisite batch.

Keep simulation identity visible, physical execution disabled, and force/release authorization server-side. Use approved shared styles and launch/command/stream contracts; the reference mockup's styles, clock and browser overrides remain provisional. Current shared service limits are 120 requests/minute per credential: the low-rate adapter cannot support a 20 ms physical scan. A fast internal engine may be tested independently with a fake monotonic clock; do not equate that with hardware timing.

## Separate release and target gates

Before claiming a Pi/Production or physical release, validate its actual scope: appliance Linux/systemd commissioning and permissions, key provisioning/backup/recovery, clock behavior, Docker-daemon/host reboot and power-loss recovery, storage pressure/corruption and any required growth limits, packaging/trust/registration policy, and required diagnostics. Local snapshots and retained keys do not prove disaster recovery. None of those disruptive target operations was performed here; the Docker daemon and unrelated workloads were left running.

Physical GPIO additionally requires the real driver, supported board/line mapping, ownership/handoff, electrical fallback and measured timing/fault acceptance, plus a suitable transport. No Raspberry Pi was accessed; no physical pins were driven and no board support is asserted. Shared API/MCP guidance and this scoped decision apply to other apps through the same capability-specific review process, not through Logic Engine-specific platform exceptions.

## Retained final build identity

Report timestamp: 2026-11-16T03:16:47.85152+00:00. Source HEAD: `6e3a5bacd6797dcf6842e6f6ab9d7e9b814ad6a8`, with uncommitted changes (`workingTreeDirty=true`). Assembly hashes remained unchanged after the final clean Release solution build. The source revision alone is not sufficient to reconstruct this working tree.

| Reviewed assembly | SHA-256 |
| --- | --- |
| PiDock.Core | `cd26d0136792adb4a49e196b3d4e78d0ca8b57de3d7c0c4d4d64ce04f7e0b17c` |
| PiDock.Agent | `463e89f42726f6d7835b0a7c32f88284d18280e5a18bc426ba808131786b7751` |
| PiDock.Web.Shared | `d79fa17975eaa099972000587acfeb0f21dc9aa0326739411cf4e0a23ed4f945` |
| PiDock.Appliance.Broker | `a4186e173a3e5486de54f75a6c3e096402904784f1f3ca73543377d149018b98` |
| PiDock.Appliance.Web | `b931850c49cd9779bc8cce69d7e976d119b32fceb61bebdaf95b8384f2b4db20` |

Final sample image: `localhost:62201/pdc-target-c04e05c16f@sha256:aeed31b9e18e9902f67f4d5eb531e51d749fa24fe3915ac18089bd8ac5e9b1ae`.
