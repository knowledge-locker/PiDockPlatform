# Device dashboard, trends and container management

Open the eye icon in **Fleet overview → Device inventory** to view one device. Last contact uses a relative age, with the exact UTC timestamp available as a tooltip. The dashboard shows uptime, CPU, memory, free storage and board temperature; offline devices retain clearly labelled last-known readings.

## Historian workspace

Choose a preset (1h, 6h, 1d, 7d or 30d) or a custom UTC range of at most 31 days. Live mode moves the window forward; editing a range pauses it. Earlier/later controls pan by the window width, and Zoom in halves the window. Select CPU, memory, storage, temperature and/or uptime signals. Each signal has its own labelled scale and min/max/latest values. Hovering shows an approximate chart cursor; the sample table gives exact values for the latest 50 points.

Unknown readings remain blank. Lines do not bridge gaps longer than two minutes. Replayed historical samples are included and identified. CSV/JSON downloads contain all retained samples in the selected device/time window and selected signals, rather than only the visible table rows. CSV cells are escaped for spreadsheet safety. Export is authenticated, tenant-scoped and marked no-store.

The preview archive remains globally bounded to 100 MiB / 10,000 frames. Choosing a long time window does not recover purged history or imply a guaranteed retention period. The interface reports the actual retained sample count. Sampling is normally every 30 seconds. This increment does not add a new historical database or change the retention budget.

## Remote installed-container controls

The device reports only containers already labelled as PiDock-managed, using the existing restricted Unix-socket broker. Cloud controls provide start, stop and restart; image deployment, system-container access, arbitrary commands, shell access, host mounts and device I/O are outside this command surface.

1. Install the updated PiDockOS agent and broker. `deploy/update-pi-device-dashboard.sh` installs both, enables only the broker's narrow lifecycle routes, preserves existing state and keeps previous program files for rollback. It requires local sudo. Image deployment remains controlled separately by `Broker:EnableDeployments`; the new flag is `Broker:EnableLifecycle`.
2. On PiDockOS **Cloud connection**, the local owner selects **Allow cloud container control** and saves with the appliance password and a fresh authenticator code. This setting defaults to off and persists in private local state. Inventory reporting is available without enabling write operations.
3. In the cloud device dashboard, expand **Managed containers**. An organization Owner or Administrator supplies their cloud password and a fresh authenticator code for each action. A recent live device/inventory report, local permission, an approved action and a reported managed container ID are required.
4. Watch the operation result in the same panel. A command waits for the next live telemetry receipt, then its result returns in a subsequent telemetry sample. Allow up to two minutes. Expired or unconfirmed operations require checking the reported container state before a new request.

Commands are attached to the existing tenant/device-scoped persistence receipt, over the existing certificate-authenticated MQTT channel. No new public device listener, broker topic privilege or Docker socket access is added to the cloud. Commands have unique IDs and a maximum two-minute acceptance lifetime. Replayed historical frames never dispatch commands. The appliance checks identity, action, expiry and local permission, then journals acceptance before invoking the broker. Repeated delivery, including after a process restart, cannot re-execute that ID. An interrupted operation is reported as unconfirmed instead of automatically replayed.

The local command journal is bounded to 128 unexpired entries, the input queue to 16, reported results to 8 and inventory to 16 managed containers. The cloud retains at most 512 recent operation records and permits one outstanding operation per device. Its operation log records requester, action, timestamps and device result. Runtime state changes still use the broker's fresh managed-label membership check.

Disabling local permission rejects new operations; it does not reverse an operation already accepted by the device. Cloud password, OTP, local password and private device key never travel in the command payload. The cloud service is a trusted command issuer once the local owner opts in. Production signing, revocation, stronger audit storage and finer device grants remain in the platform release plan.

## Verification

Automated checks cover relative timestamps, viewer/tenant isolation, stale inventory and local opt-in, unsupported/expired/wrong-device commands, persistent duplicate prevention, scoped command results, ordered historical queries, CSV escaping, legacy telemetry JSON compatibility and authenticated exports. The HTTP test checks a viewer's export against a same-device ID in another tenant.

For isolated transport verification:

```sh
dotnet build -c Release
docker build -f deploy/Dockerfile -t pidock-device-dashboard-test .
python3 tools/verify-delivery.py --enrollment --remote-command --image pidock-device-dashboard-test
```

This uses disposable cloud/broker state and a fake local Unix runtime. It validates real HTTPS enrollment, mutual TLS, cross-tenant broker rejection, one expiring container command/result round trip, continued sampling during broker outage and deduplicated replay after reconnection. It never changes real containers on a physical Pi.
