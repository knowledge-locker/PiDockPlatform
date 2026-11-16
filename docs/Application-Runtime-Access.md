# Lifecycle-bound application resource authority

This opt-in shared preview connects managed PiDockOS lifecycle/maintenance observations to **simulated I/O leases and variable engineering**. It applies to every compatible app, not one product. Discover `/api/platform/v1/app-runtime-access` or `pidock://platform/app-runtime-access`. Metadata contains enablement, the binding header name, observation policy and limitations; it contains no startup identity values.

Enable `PiDock:Apps:RuntimeAccess:Enabled` in the appliance host before starting it. This policy is captured at startup. Its broker must support managed resources and orchestration and the private runtime-access endpoint. With the policy enabled, a missing, unreadable, incompatible or unavailable broker closes access; it never falls back to an unbound app credential. The default disabled mode retains the older development-only preview behavior and supplies no lifecycle binding. I/O and engineering support metadata advertise `lifecycleBound`.

## Bind the current private startup

After the ordinary managed bootstrap, bind each server-side platform API client:

```csharp
var bootstrap = await ApplicationBootstrap.InitializeAsync();
if (bootstrap is null) throw new InvalidOperationException("Managed startup required.");
ApplicationRuntimeAccess.Bind(platformClient, bootstrap);
```

The helper sets `PiDock-Runtime-Delivery` from the current bootstrap delivery ID. Treat the **value as private startup authority**, alongside scoped app credentials. Never put it in URLs, public responses, UI frames, discovery, logs or browser storage. Its value is already delivered privately by the existing bootstrap protocol; no public issuance endpoint was added. The usual app credential scopes, grants, ownership, revision checks, expiry and HTTPS requirements still apply. UI commands use their separate named host bridge, not this header.

The host matches that identity to the currently selected managed app and creates an internal admission. Each lease, runtime registration, force and pending write retains its admission. A new broker instance, a changed startup, a revoked observation or an expired observation invalidates the old admission; it is never revived by later availability. A failed or restarted process cannot keep using a previous startup identity. Registrations invalidated under this policy are permanently fenced: a fresh instance GUID is required after re-admission, and old frames cannot restart them. This is startup containment, not independent validation of arbitrary competing producers inside an app.

## Eligibility and revocation

The actual Docker broker derives the private snapshot from protected managed records and current runtime state. Eligibility requires requested-running intent, a running selected container, completed bootstrap, satisfied dependencies, no blocking version/start transition, and no global pause/restore/removal/shutdown barrier. The selected original can keep working during staging; the selected replacement or restored original can initialize I/O during readiness verification. Switching, pending rollback and failed rollback block admission. This runtime policy is separate from the stricter prohibition on manual lifecycle mutations during an update. Declared health must have a usable observation: startup/non-Ready initialization is permitted, but explicit liveness failure, unhealthy or unknown/expired observation withdraws authority. App authors must report critical work honestly; health does not independently verify program correctness.

The host polls every 500 ms and rechecks before resource requests. Mutations force a new observation; GETs may coalesce for 250 ms. A broker read has a one-second deadline and bounded 64 KiB response. Observations expire after three monotonic seconds. These are scheduling/transport policies, **not hard real-time or atomic cross-process shutdown guarantees**. A broker operation holding its lifecycle lock can make the read unavailable; that closes access rather than permitting stale writes.

When access is lost, host-side sweeps reset simulated outputs to their configured fallback and remove leases. Engineering clears forces and queued writes and fences the old registration, independently of app cleanup or UI subscriptions. Service operations also validate current authority. Old lease IDs cannot renew/read/write after revocation. Returning connectivity or resuming maintenance does not restore leases, forces, queued writes or a fenced registration.

Apps should still release resources gracefully on Stop/termination. Failure detection and expiry cover absent cleanup; no persisted force is restored. Recovery/Start/Restart uses current credentials and the new private bootstrap identity. Stop intent remains authoritative. Unknown broker state closes all bound admissions; unaffected app admissions remain stable when only another app changes.

## Private broker protocol and public discovery

`GET /applications/runtime-access` exists only on the broker's protected Unix transport. It includes current coordinator identity and private per-app delivery identities; it is **not** an app-facing API, public schema or MCP resource. Only the support schema `application-runtime-access-support` is published. Normal discovery credentials gain no lifecycle or resource mutation rights.

This increment does not implement physical GPIO, electrical fallback, power-loss guarantees, app logic certification, hard scan deadlines or full product conformance. Actual supported-target timing, interruption and isolation evidence remain required.

## Disposable lifecycle review

Run the [connected sample launcher](../tools/PiDock.AppUi.Sample/README.md), then open `/sample-lifecycle` in the PiDockOS review shell. Its Owner-only development panel uses `BrokerClient` lifecycle/maintenance methods against an isolated Unix process adapter. The review host now supervises the actual sample process, regenerating its private delivery identity on every start. It supports pause/resume, Stop/Start, Restart and abrupt process failure; all are restricted to that sample. It has no Docker or physical-device operations and is not a container isolation test. Production Applications controls and their fresh Owner confirmation are unchanged.

The panel reads authoritative lease/force/write counts and fallback status from shared services. Verify an active output force first, pause or stop, and observe zero counts/fallback before exit. Resume/Restart produces a different process and fresh cleared runtime. Stop during maintenance must remain stopped on Resume. Abrupt failure skips app cleanup and still clears resources. Automatic process recovery is not enabled in this fixture; the actual broker's existing opt-in recovery policy is separate.

## Docker evidence (2026-11-15)

The separate [Docker conformance tool](../tools/PiDock.DockerConformance/README.md) passed on Docker Desktop 29.6.1, Linux ARM64. It deploys a digest-pinned sample with the actual `DockerOperations` implementation and real stdin/tmpfs bootstrap, serving actual lifecycle/orchestration methods on private Unix HTTP paths. Shared HTTPS services, authentication, monitors and resource policies run unchanged. This is a disposable host for those broker methods, not the production broker executable or systemd service.

Each transition began with an active force and three confirmed simulated outputs on. PID 1 was suspended with SIGSTOP while Docker continued to report running, then an engineering write was queued. Maintenance pause, operator stop, restart and SIGKILL failure cleared the lease, force and queued write, fenced the registration and reset all outputs without app cleanup. Observed cleanup was approximately 1.5 seconds for the first three and 0.75 seconds for failure, below the test's four-second bound and the five-/fifteen-second freshness/lease expiries. Fresh starts changed Docker start time, private delivery and runtime instance; prior startup binding was denied. Stop intent survived maintenance Resume. These measured workstation observations do not establish a real-time guarantee, Pi hardware acceptance or physical fallback.

The subsequent Docker `--health-recovery` scenario also verifies startup-bound cleanup during automatic recovery. The actual hosted worker returns the sample to fresh Ready after SIGKILL and stale-heartbeat failure, with no restored forces/writes/latch and simulated outputs at fallback. Old startup bindings stay denied. Intentional Stop survives a stale recovery request and three subsequent background passes. See the [health/recovery procedure](../tools/PiDock.DockerConformance/README.md).
