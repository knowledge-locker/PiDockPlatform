# Monitoring and delivery

## Local resource alarms

The appliance evaluates available measurements every two seconds independently of MQTT connectivity. Defaults are CPU ≥90% and memory ≥85% for five minutes, temperature ≥80°C for one minute, disk usage ≥80% for five minutes, and critical disk usage ≥90% for one minute. Storage reserve pressure opens a critical alarm immediately. Each rule has a lower recovery threshold and requires 30 seconds of recovery. CPU and memory clear below 80% and 75%, temperature below 75°C, and disk warning/critical below 75%/85%.

Hold/recovery durations use monotonic time so a wall-clock correction cannot trigger or clear alarms. An unavailable sensor breaks a pending hold and does not clear an active alarm. Active alarms persist across normal restart; incomplete hold/recovery timers restart. Open and recovered transitions are written to bounded audit history, and each transition queues a telemetry frame immediately.

Rules can be supplied in `Alarms:Rules` in the appliance configuration, using `Id`, `Title`, `Metric`, `Threshold`, `ClearBelow`, `HoldSeconds`, `RecoverySeconds` and `Severity`. Metrics are CpuPercent, MemoryPercent, TemperatureC, DiskUsedPercent and ReservePressure. Restart after configuration changes. There is no rule editor, silence/acknowledge workflow or email/webhook delivery yet.

## Durable outbox

New telemetry uses the same policy-managed segments as local history, with a pending marker and an immutable message ID. A process restart reloads pending records. Successful delivery changes the marker; it does not duplicate the payload in another spool. Older pre-outbox history lacks a destination binding and is deliberately not auto-uploaded.

The destination binding includes broker hostname/port, configured trust anchor, tenant and device. Manually configured MQTT settings are captured for the process lifetime. A newly completed enrollment activates its own immutable profile immediately. Restart after manual endpoint, trust or credential edits. Queued records for an old binding stay local until retention purges them; they are never silently transferred to another server or tenant.

The delivery worker prioritizes a fresh current-boot sample, then sends bounded historical batches. It retries missing receipts after 30 seconds and backs off connection failures with jitter up to about one minute. Sampling and alarm evaluation continue while the network worker is blocked or disconnected.

## Cloud persistence receipts

Telemetry topic: `pidock/v1/tenants/{tenantId}/devices/{deviceId}/telemetry`.

Receipt topic: `pidock/v1/tenants/{tenantId}/devices/{deviceId}/receipts`.

A receipt contains schema version, tenant ID, device ID and message ID. It carries no remote control operation. The device certificate can publish only its own telemetry and subscribe only to its own receipts; the ingestion identity can read telemetry and publish receipts. Anonymous access and device publication to receipt topics are denied. Certificate-derived broker identities also replace client-supplied client IDs to prevent another identity evicting a connection by reusing its client ID.

A broker PUBACK is not proof of cloud storage. The cloud sends an application receipt only after its archive and applicable current-state snapshot are written. On Linux, atomic state writes flush the file and synchronize the parent directory. Receipt publication occurs outside the receive callback to avoid MQTT QoS callback deadlocks. Lost receipts are safe: retries with the same message ID are deduplicated, while changed contents under that ID are rejected.

Replayed frames preserve capture time and alarm state in history, but cannot overwrite a newer live frame, reopen its resolved alarms, or refresh its last-live-contact time. A device with only historical replay is shown without live contact. Authenticated history reads at `/api/devices/{device}/history` return at most 100 frames and enforce the signed-in tenant.

The current cloud archive rotates oldest received frames at 100 MiB or 10,000 frames. This is a bounded JSON preview store, not the final PostgreSQL retention implementation. Acknowledgement confirms acceptance under that retention policy, not indefinite preservation.

## Storage pressure and failure limits

Pending telemetry obeys the appliance's percentage-based budget and host reserve. Oldest eligible segments can be removed even if unsent. The UI reports pending samples, purged-unsent samples and newly rejected queued writes. Counters persist across normal restart; sudden power loss during multi-file cleanup still requires hardware fault testing. A cloud outage never creates an unbounded spool.

Alarm active state is separate from rotating history. A failed snapshot write is retried from a bounded in-memory transition buffer (64 transitions). Permanent I/O failure or power loss before a write completes can still lose transitions. The dashboard surfaces persistence failure; this does not claim an infinite or lossless audit trail.

## Validation without a Pi

Automated tests cover broker-only acknowledgement, lost receipts, reconnect/replay, destination isolation, cloud write failure, bounded purging, restart persistence, tenant isolation, monotonic alarms, hysteresis, sensor loss and active-alarm recovery. `tools/verify-delivery.py` adds a real Docker/mTLS outage check using isolated resources and the preview PKI. It is not a Pi electrical, RTC or power-loss test.

Current validation: all 44 tests pass on macOS and Linux ARM64. The real outage check also passed: the broker was stopped while two samples accumulated, then restarted; the queue drained to zero with four unique archived samples, including historical replay.
