# Shared application variable engineering preview

This optional platform service is shared by applications. It manages declared **application variables**, not GPIO lines, program execution or a physical safety function. It does not begin Logic Engine development or satisfy the hardware conformance gate.

Enable `PiDock:Engineering:Enabled` only on a commissioned target with reachable broker orchestration. The default is disabled. Use the existing HTTPS application service authentication and rate limit. Explicitly provision separate managed platform credentials with `signals.runtime` and `signals.engineer`; giving a runtime permission to publish values never grants engineering permission. Configuration-only credentials and agent registrations do not authorize this preview. Keep credentials server-side and expose authorized UI operations through the shared UI bridge.

## Runtime and variables

`POST /api/apps/v1/engineering/runtime` registers one runtime instance GUID, a program revision and at most 256 unique typed variables. There are at most 16 app registries. Types are Boolean, finite Number and Text up to 256 characters. Access is Constant, ReadOnly or ReadWrite. ReadOnly describes external writes: the designated application runtime may still publish its normal value. Constants remain at their declared initial value and cannot be forced. Other variables must explicitly declare `forceable` before an engineering force is accepted.

A new runtime instance replaces the old registry and clears all queued writes and forces. Generate a fresh instance GUID in memory on **every process start**; never persist or reuse it. An existing instance cannot change its revision or declarations; activate a new instance to replace a program revision. Successful same-instance re-registration also clears forces and queued writes, while preserving normal values and the increasing sequence. Registration is therefore an explicit engineering-reset boundary: retrying it may cancel temporary controls.

The application must reject or fence stale producers within its own process. The opt-in [runtime-access policy](Application-Runtime-Access.md) now binds a registration to the broker's current private bootstrap identity and fences it after observed stop, pause, failure or replacement. It does not independently verify competing producers inside an app or decide program correctness. Without that policy, lifecycle binding remains unavailable.

`POST /frames` accepts a strictly increasing sequence and a complete normal-value frame for all non-constant variables. It also acknowledges the IDs of current queued writes the runtime applied at its own scan boundary. Frame validation finishes before normal values change. `GET /runtime` returns normal/effective values plus pending writes. `GET /watch`, restricted to engineering authority for the same app, returns watch values without the runtime write queue. All routes in this section are relative to `/api/apps/v1/engineering`.

The application owns the scheduling and scan semantics: consume applicable queued writes, compute/publish its normal frame, then consume returned effective values where its execution model permits. The service does not reach into application memory or change already executed logic. Releasing a forced input does not undo a latch or another downstream state change.

## Engineering operations

Every mutation identifies the expected runtime instance and program revision. The authenticated credential determines the app and actor; callers cannot choose another app in a body or route.

| Operation | Behavior |
| --- | --- |
| `POST /set-once` | Queue a typed write to a ReadWrite variable; does not immediately rewrite the normal value. One pending write per variable, at most 64 per runtime, expiry at most 30 seconds. |
| `POST /force` | Set a forceable variable's effective value for 1–300 seconds, capped by the credential expiry. Normal values continue updating. Another credential cannot silently replace an existing force. |
| `POST /release` | Release the selected signal, or all forces when `signalId` is omitted. Any current engineering credential explicitly scoped to that app can release its forces. |

Forces carry actor, expiry and ID; snapshots include the original declaration, normal value, effective value, runtime sequence, observation time and quality. Pending writes carry their own actor, ID and expiry. Bounds use monotonic elapsed time as well as UTC expiry, so a backward wall-clock adjustment cannot extend the requested force lifetime.

## Expiry, authorization and maintenance

Forces and writes are memory-only. They never survive platform restart or successful runtime registration/replacement. Clearing on an application process restart relies on the required fresh-instance registration above. Each operation and the idle monitor rechecks managed credential version, revocation, expiry and required scope. Revoked/rotated engineering authority clears its forces and queued writes. Loss of runtime authority clears all engineering state for that runtime.

A runtime becomes stale after five seconds without a normal frame. Stale runtimes lose forces and pending writes; publishing fresh values does not restore them. The host checks orchestration before requests and every second while idle. A paused or unavailable broker status clears engineering state and blocks registration, frames and engineering mutation. Unknown status starts paused. This is a variable-service maintenance policy with bounded observation latency, not an atomic physical GPIO shutdown guarantee.

Accepted engineering mutations write durable metadata audit records through the platform history policy before taking effect. Audits include app, actor, privilege scope, instance, revision, action, signal and timestamps, without variable values or tokens. If audit storage cannot accept a new force/write, that mutation fails. Automatic safety releases still happen when audit storage is unavailable; support metadata counts those audit failures. Audit retention follows the shared bounded history policy rather than promising an indefinite immutable archive.

## Limits and remaining integration

This preview does not provide physical GPIO forcing, GPIO lease integration, a browser watch component, alarm notifications or an application scan engine. GPIO engineering controls must use the brokered I/O authority and fallback contract when supplied. A successful force acknowledgement means this service accepted an effective value; it does not prove a downstream application or electrical output applied it. Applications must display stale/disconnected state and confirm acknowledgements through the shared live-data bridge.

## Connected conformance fixture

The disposable [AppUi.Sample](../tools/PiDock.AppUi.Sample/README.md) now consumes this service through real authenticated HTTPS calls. Named host bridge commands carry the displayed instance/revision and use separate runtime/engineering credentials. Its normal/effective watch, queued bank-enable write, latch/delay and simulated output batch demonstrate force/release and expiry end to end. The private review coordinator and low-rate cycle do not establish production broker/process fencing or physical GPIO authority.

Lifecycle-bound callers must supply the private startup binding on runtime and engineering clients. Revocation permanently fences the registration, clears forces/writes and rejects old frames; a fresh instance is required after re-admission. Support metadata advertises `lifecycleBound`. The connected lifecycle review exercises these services against a real disposable local process, using the same broker protocol as the Docker-backed implementation.
