# Shared application health and readiness

PiDockOS provides optional app liveness/readiness reporting through a bounded private heartbeat. It applies to any compatible app, including future Logic Engine runtimes. Bootstrap acknowledgement establishes credential/data delivery; a separate health report establishes app-reported readiness for work.

## Discover and request

Read authenticated `GET /api/platform/v1/app-health` or the read-only MCP resource `pidock://platform/app-health`. Require `enabled: true`; an old or unavailable broker is not proof of support. Discovery advertises report and reader paths, protocol, limits, check frequency and the bundled reader source URL/SHA-256. Fetch that source through authenticated `/api/platform/v1/app-health/reader.c` or the bundled MCP resource `pidock://platform/app-health/reader.c`, verify its advertised digest, and compile it into the app image during the build. Public schemas include `application-health-policy`, `application-health-report`, `application-health-status` and `application-health-support`.

Health reporting requires managed app resources and the appliance host service, broker and Docker. It works without a browser/cloud connection and does **not** require automatic recovery to be enabled. Apps opt in through the shared Applications deployment form or runtime manifest:

```json
"resources": {
  "persistentData": true,
  "credentials": ["bridge"],
  "automaticRecovery": true,
  "health": {
    "startupSeconds": 60,
    "freshnessSeconds": 15,
    "failureThreshold": 3,
    "restartOnFailure": false
  }
}
```

Persistent data, credentials and automatic recovery are optional; requesting health alone enables managed bootstrap. The image must initialize bootstrap, include the fixed native reader and publish health reports. Images without a health contract retain their existing process-based recovery behavior and report **Health reporting not configured**; PiDockOS does not invent a Ready state for them.

Health policy is part of the deployed manifest; enabling recovery on an existing app does not add a health contract. Changing health policy/image requires a later deployment through the normal compatible image workflow, not a browser-supplied probe command.

## App contract

Call `await ApplicationBootstrap.InitializeAsync()` before other application work. Create an optional `ApplicationHealthReporter` from its returned packet:

```csharp
var bootstrap = await ApplicationBootstrap.InitializeAsync();
var health = ApplicationHealthReporter.Create(bootstrap);
health?.Report(live: true, ready: false);
// After initialization and whenever critical work/dependencies are checked:
health?.Report(live: criticalWorkIsProgressing, ready: acceptingWork);
```

`Create` returns null outside a health-enabled managed startup. Publish at least once every five seconds; continue publishing while alive but not ready. Derive the booleans from the actual critical work and required local checks. A separate timer that always reports success cannot detect a stalled engine. For Logic Engine, readiness must eventually account for validated program activation and permitted safe I/O initialization; these platform health fields do not grant physical I/O access.

The SDK requires the dedicated tmpfs `/run/pidock/health` and writes `health.json` atomically with private permissions. PiDockOS supplies `PIDOCK_HEALTH_REPORTING=1`; no secret enters that environment flag. The 64 KiB tmpfs is app-owned, mode 0700, non-executable, nodev and nosuid. It is recreated on each start, separate from durable data and credential storage. No image-created health directory ownership change is needed.

Other-language apps can implement the same atomic private JSON file:

```json
{"schemaVersion":1,"deliveryId":"<current bootstrap delivery UUID>","sequence":1,"reportedAt":"2026-11-13T22:00:00Z","live":true,"ready":false}
```

Use the current bootstrap delivery ID, a positive increasing browser-safe integer sequence, and current UTC time. Repeated checks can reread the same sequence until the report expires; older sequences and changed content under the same sequence are rejected. Ready implies Live. Reports from another startup, expired timestamps, timestamps over two seconds in the future, invalid JSON or excessive nesting/size are rejected. A new bootstrap resets identity, sequence and first-readiness history. Use correct appliance time. Reports contain no free-form diagnostics, credentials, variable values or user content; diagnostics belong in the authorized logging service.

Build and include the [shared fixed native reader](../sdk/health-probe/README.md) at `/usr/lib/pidock/health-probe`. PiDockOS invokes only that path with no arguments or shell, as UID/GID 1654, without privileges or TTY. The reader rejects symlinks, non-regular files and reports outside 1–1024 bytes. Docker stdout framing is bounded to eight frames, 1024 report bytes and 2048 total frame payload bytes, with a two-second check deadline. Stderr and raw reader errors are never sent to the UI. There is no HTTP health port, arbitrary URL, configurable executable, host mount or app Docker socket.

## States and failure rules

| State | Meaning |
| --- | --- |
| Not configured | No declared health reporting contract; readiness is unknown |
| Starting | Waiting for app readiness/report during initial startup grace or for bootstrap completion |
| Ready | A fresh report for this startup says Live and Ready |
| Not ready | An app that was ready is still Live but has temporarily stopped accepting work |
| Unhealthy | Negative liveness, expired/missing/invalid heartbeat, or first-readiness deadline exceeded |
| Unknown | No check yet, expired observation, or the runtime check could not be completed |
| Stopped | Container is not running; cached reports cannot imply readiness |

Startup grace defaults to 60 seconds and can be 15–300. Report freshness defaults to 15 seconds and can be 10–120. The failure threshold defaults to three and can be 1–10. Checks run approximately every five seconds and duplicate requests inside that interval cannot increase the failure count. Busy operations or runtime problems can delay checks; reporting is not a hard real-time safety mechanism.

Missing/invalid/stale reports during initial grace remain Starting. An explicit negative liveness report counts immediately. If the app never becomes ready before grace expires, fresh Live/not-Ready reports count as readiness timeout failures. After the app has first become ready, Live/not-Ready is a normal readiness withdrawal and does not trigger a restart. A fresh successful report clears failed-check counts. Check transport errors produce Unknown, clear the consecutive failure count and do not count against the app.

Reported readiness expires if either the report or the broker observation is too old. When monitoring resumes after a gap, the stability window starts again; an unobserved gap cannot clear the retry budget. For health-enabled apps, the recovery budget resets only after 60 seconds of continuously observed fresh Ready reports. Apps without the contract retain the prior running-process stability rule.

The Applications page shows readiness, a fixed non-secret reason, last check and failed checks alongside requested/runtime state and recovery status. Readiness does not automatically gate hosted UI launch or app commands; declared dependency conditions are separately enforced by app orchestration, and apps must still validate whether they can accept each operation.

## Optional health-driven recovery

Health reporting is observational by default. To restart an unhealthy app, **all** must be enabled: the app's `health.restartOnFailure`, the app's automatic recovery policy, and appliance recovery support. The saved request must still be Running. Recovery never overrides an operator Stop.

After the threshold, a fresh unhealthy observation can stop the app and enter the existing [revision-bound recovery workflow](Application-Lifecycle.md). Startup redelivers current credentials and resets health identity. Health and process failures share the same five-attempt budget/backoff; a bootstrap acknowledgement or repeated unhealthy startup cannot reset it. Exhaustion pauses recovery and stops an unhealthy leftover process until an Owner starts/restarts it. Credentials unavailable for the next start remain a visible wait state.

Changing readiness alone after initial success is not a liveness failure. Healthy processes are not restarted just because credentials rotate, the UI closes or cloud is unavailable. No watchdog guarantee is made if the host/broker/Docker themselves stop; observations expire and recovery resumes when services return.

## Validation and limits

Automated tests cover report identity/time/sequence validation, startup grace, first-readiness timeout, readiness withdrawal, stale observations, monitoring gaps, failure thresholds, duplicate checks, reader command restrictions, bounded/malformed output, transport failures, recovery gates/budget, Stop revisions, and monitoring with automatic recovery disabled. A real Linux ARM64 container test passed Starting → Ready → stale/Unhealthy → supervised restart → Ready with retained data and a fresh startup identity. Native reader checks also reject missing reports, symlinks, oversized files, FIFOs and arguments without emitting file contents.

This is app-reported health, not independent proof of correct output or hardware safety. [Dependency ordering and coordinated shutdown](Application-Orchestration.md) now build on these reports. GPIO fallback, package rollback and physical appliance certification remain separate work.

## Connected sample Docker health/recovery (2026-11-15)

The repeatable [Docker conformance tool](../tools/PiDock.DockerConformance/README.md) now has a `--health-recovery` mode. It compiles the shared native reader into the sample image, enables normal managed reporting and runs the actual host `ApplicationRecoveryService` against private broker health/lifecycle/orchestration endpoints. Startup reaches Starting then Ready; SIGKILL and a SIGSTOP-stalled heartbeat each recover automatically to a new startup and fresh Ready. A stale report is observed as Unhealthy with recovery required before the health-driven restart. Docker's automatic restart policy remains disabled.

Both failures begin with all three simulated outputs forced on and a pending engineering write. Host-side cleanup removes the lease, force and write; recovered runtime state starts cleared at fallback and rejects previous startup binding. Intentional Stop remains stopped through at least three background passes over 16 seconds with automatic recovery still enabled; a stale recovery request cannot override it. Evidence contains only non-secret health observations and results. This is real Linux ARM64 Docker Desktop evidence using the production methods in a disposable host; Pi/systemd, retry-exhaustion and electrical acceptance remain separate.
