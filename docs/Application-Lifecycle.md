# Shared application lifecycle and recovery

PiDockOS can supervise compatible resource-enabled applications without an open browser or cloud connection. This is a shared platform capability, not a Logic Engine runtime feature. Recovery uses the same restricted containers, durable app volumes, protected startup protocol and current credential vault described in [application resources](Application-Resources.md).

## Enable and discover

A trusted administrator enables `Broker:EnableAppResources=true`, `Broker:EnableAppRecovery=true`, and either `Broker:EnableLifecycle=true` or `Broker:EnableDeployments=true`. The broker requires its private state directory and rejects recovery configuration without resource/lifecycle support. The supplied systemd unit leaves app recovery disabled by default. The PiDockOS appliance host, broker and Docker must run for supervision; recovery starts when those services become available after reboot.

Discover target support with authenticated `GET /api/platform/v1/app-lifecycle` or the read-only MCP resource `pidock://platform/app-lifecycle`. A missing/old/unreachable broker does not imply support. The response advertises opt-in status, retry budget, stability window and limits. Optional [app health reporting](Application-Health.md) is discovered separately. Public schemas include `application-lifecycle-support` and `application-lifecycle-status`; no secret startup material or mutation tools are exposed through MCP.

Enable recovery per app through the Applications page, or request it at deployment:

```json
"resources": {
  "persistentData": true,
  "credentials": ["bridge", "services"],
  "automaticRecovery": true
}
```

An app can request automatic recovery without durable data or credentials, but still must support the shared bootstrap protocol. Each resource-enabled container keeps Docker's restart policy set to `no`; PiDockOS controls startup and credential delivery. Older non-resource containers retain their existing Docker `unless-stopped` policy and are not adopted into this supervisor. Existing resource records default to recovery disabled; enabling an old record adopts its current running/stopped state once.

## User controls and saved intent

The Applications page exposes Start, Stop, Restart, Enable/Disable recovery, runtime state, requested state, retry count, next check and recovery outcome. Refresh obtains current state from the broker. Every user mutation requires the current Owner plus password and a fresh authenticator code, using the existing lifecycle authorization boundary. Remote lifecycle actions retain their existing separately authorized path.

- **Stop** persists a stopped request before contacting Docker. Recovery never overrides that request, including after host restart. An already stopped container is a successful Stop. If Docker rejects stopping a running app, requested and runtime states can differ; the UI shows both.
- **Start / Restart** resolve live named credentials, then persist a running request, clear the paused attempt budget and perform the startup handshake. Restart stops the existing process first. A failed startup handshake leaves its running intent available for opted-in recovery. A request rejected before credential validation does not change intent.
- **Enable recovery** permits supervised recovery when the saved request is running. It does not turn a saved Stop into Start or reset an exhausted retry budget.
- **Disable recovery** prevents subsequent recovery requests from acting. It does not stop an app or undo a startup already completed before the policy change.

A durable revision changes with every lifecycle intent/policy/attempt transition. Automatic recovery rechecks that revision and current Docker state under the same broker lock used by manual controls. Queued requests cannot restart an app after a newer Stop, Restart or policy change. Multiple coordinators cannot spend the same revision twice.

## Recovery sequence

The appliance host checks approximately every five seconds, with one bounded pass and sequential attempts. The broker owns durable intent, revisions, attempt counts and deadlines; the host supplies fresh credential material from the current vault. The host never falls back to an encrypted but stale broker credential snapshot for automatic recovery.

Only a known resource-enabled container that is requested running, has recovery enabled, is stopped (`created`, `exited` or `dead`), has budget left, and has reached its next-attempt time is eligible. Successful process exits are recovered too: a saved running request means a continuing service. One-shot jobs should leave automatic recovery disabled. Paused/removing/restarting containers are not unpaused, recreated or interfered with automatically.

Before startup, the broker persists the incremented attempt count and next deadline. A failure, host cancellation or broker restart cannot erase that attempt. Each attempt uses the existing 30-second bounded startup acknowledgement. Failure stops the process. If broker termination interrupts startup before a readiness acknowledgement is durably recorded, the host's reconciliation pass stops the leftover unacknowledged process before trying delivery again; simply finding that process running does not establish readiness. Exhausted attempts still permit this cleanup, but no further automatic start.

The current fixed policy allows five attempts. Delays increase from 5 to 10, 20, 40 and 80 seconds; five-second polling and other app operations can make actual starts later. A continuously observed running period of 60 seconds resets the attempt budget for apps without health reporting; health-enabled apps require 60 seconds of fresh Ready reports. A process that acknowledges and immediately exits still spends an attempt, so rapid crashes cannot bypass the limit. Retry state and UTC deadlines survive host/broker restarts. Correct appliance time remains required for both deadlines and credential expiry.

Missing, expired or revoked credentials produce **Waiting for current credentials** and a 30-second recheck, without consuming a startup attempt or using old saved material. Restoring an active credential allows later recovery automatically. Rotation/revocation of credentials does not restart an otherwise running app; manually restart it to replace delivered files. A missing/corrupt protected startup record blocks recovery. A missing container is not recreated. Stop remains available for a known managed container with a missing/corrupt record.

After five attempts, the UI reports recovery paused. An Owner uses Start or Restart to retry after correcting the problem. Healthy running observations reset the budget automatically; toggling recovery alone does not.

## State and boundaries

Lifecycle fields extend the existing encrypted per-container broker record; no app-data volumes or credential files are deleted by supervision. Startup/policy logs contain identifiers, revisions and error types, not credential values or upstream response bodies. Protect and back up broker state and its keys as described in the resource contract. Browser closure, logout and cloud outages do not stop supervision.

The internal Unix API adds lifecycle support/status reads, policy changes, reconciliation, revision-bound recovery and credential-unavailable reporting. The Unix socket remains privileged; caller-supplied container names, URLs, images or arbitrary commands are not accepted for recovery. No additional browser-facing Docker API was introduced.

This increment recovers process exits and reboot-stopped containers. Optional [app health checks and readiness](Application-Health.md) can now request bounded unhealthy-app recovery using this same budget. [Shared app orchestration](Application-Orchestration.md) now adds opt-in dependency gates and coordinated shutdown. It does not implement safe GPIO fallback, package updates, rollback, recreation of deleted containers, uninstall or volume backup/purge. The bootstrap acknowledgement proves successful delivery, not ongoing application health. Logic Engine must define retained state and safe output initialization separately before hardware execution.

## Verification

The connected sample's [Docker conformance tool](../tools/PiDock.DockerConformance/README.md) adds a repeatable `--health-recovery` scenario using the actual hosted recovery worker, current vault resolution and actual private broker methods. Real Linux ARM64 Docker validation covers fresh Ready after SIGKILL, stale-heartbeat health recovery, one shared attempt per recovery, new startup identity, cleared forces/writes/latch, simulated output fallback, rejection of previous binding and an intentional Stop that survives three subsequent background passes plus a stale recovery candidate. Docker restart policy stays disabled. This extends the earlier policy evidence to connected I/O/engineering; it does not exercise systemd or Pi hardware.

Automated tests exercise durable retry state, Stop/policy revision races, bounded failed startup and rapid-exit loops, stable-period reset, current/invalid/missing credentials, per-app opt-in, missing records, foreign-container denial, interrupted acknowledgements, and the appliance coordinator over a real Unix HTTP socket without a browser/cloud client. Local ARM64 Docker validation covers supervised restarts with retained data, recreated credential tmpfs, replacement credential delivery, persisted retry state and stale-request rejection after Stop. This is not a physical host power-cycle or Pi hardware certification.

[Image-only app updates](Application-Updates.md) now use this lifecycle: current credential delivery, dependent-first switching, protected active pointers and controlled rollback. Start/Restart/recovery cannot race a version operation; Stop and coordinated shutdown preserve intent.

## Docker retry-exhaustion evidence (2026-11-15)

The [Docker conformance tool](../tools/PiDock.DockerConformance/README.md) now provides `--retry-limits`. With the actual hosted worker and unchanged clock/policy, repeated sample SIGKILL failures consume five automatic attempts. The tool verifies recorded 5/10/20/40/80-second deadlines, monotonic spacing and no start before the prior deadline. After failure of the fifth replacement, it observes suspension past the final deadline and multiple additional passes, confirming no sixth start. Successful bootstrap alone never clears the budget.

A normal explicit Start resets the exhausted budget. In a fresh cycle, Stop during a future retry deadline cancels the retry while leaving recovery enabled; the process remains stopped past that former deadline and a delayed stale recovery request. The Docker Desktop Linux ARM64 run observed four post-deadline passes in both suspension and Stop cases, with no leases/forces/queued writes and simulated outputs at fallback. This is process-exit policy validation; continuous-stability reset, health-failure exhaustion, credential-loss and full appliance recovery remain separate drills.

## Docker sustained-health reset evidence (2026-11-15)

The tool's `--budget-reset` scenario validates automatic replenishment with the native reader and actual worker. Brief Ready followed by live/Not-Ready past the original window retains attempt one. Restoring Ready starts a new window; sixty seconds of continuous fresh Ready are required, with the observed reset at 61.399 seconds. The reset clears the deadline without restarting the app. A subsequent failure spends attempt one again.

Stop during the next stability period survives its former deadline, three later background passes and stale recovery replay. Its administrative counter reset is distinct from the automatic healthy reset; existing explicit Start/Restart/Stop semantics are unchanged. See the [repeatable procedure](../tools/PiDock.DockerConformance/README.md). This checks real Docker health continuity/readiness interruption; host-observation gaps, clock jumps and full appliance restart remain separate evidence.

## Docker current-credential recovery evidence (2026-11-15)

The [Docker tool](../tools/PiDock.DockerConformance/README.md) now provides `--credential-recovery`. With the actual vault and worker, revoked and naturally expired I/O credentials each cause repeated thirty-second waits without spending attempts or bootstrapping retained snapshots. Fresh vault issuance resumes automatic recovery at the next eligible check, with current version/material, new startup identity, cleared controls and native Ready. Old token and old startup binding are rejected independently.

Repair while intentionally stopped cannot start the app, including past the former credential deadline and four worker passes or after stale recovery replay. Explicit Start resolves current material and resumes cleared. The evidence applies to the connected Linux ARM64 Docker Desktop fixture; production Owner authentication, retry policy and private broker trust boundaries are unchanged. No credentials or startup identities enter the result artifact.
