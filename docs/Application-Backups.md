# Shared application data backup and restore

The opt-in local preview is a PiDockOS facility for every managed app with persistent data. It is independent of image update/rollback and does not introduce Logic Engine-specific storage APIs. Probe `/api/platform/v1/app-backups` or `pidock://platform/app-backups` and check `enabled` on the target. Capability discovery and browser controls expose metadata only, never archive bytes or broker keys.

## Operator workflow

1. Use Applications to finish a coordinated **maintenance pause**. All app containers sharing the target volume, including retained image versions, must be stopped.
2. Open App data backups and authenticate as Owner with a fresh password/TOTP confirmation for each mutation. Create a snapshot of an installed persistent-data app, or select an existing snapshot for that same app and exact installed image.
3. Restore creates a separate encrypted **pre-restore safety snapshot** before changing live data. Keep one snapshot slot free for it. Restore stages and validates files, replaces the complete tree, removes obsolete files, and verifies the resulting tree. Empty directories are retained.
4. Maintenance remains paused after successful restore. Review the result, then explicitly resume apps through Applications. Stored running intent remains governed by normal lifecycle/dependency rules.

If interrupted, the protected restore journal blocks app startup, deployment, update and Resume even when backup operations are subsequently disabled. Repair runtime/storage availability, re-enable the same trusted helper, and retry the **same restore operation ID**. Starting a different restore cannot bypass it. Interrupted moves and cleanup are resumable; a failed operation is never presented as an automatic rollback. Do not manually delete control files, the journal, or Docker volumes to clear the barrier.

## Data and compatibility

Only the target app's managed named volume at `/var/lib/pidock/app-data` is captured. Runtime credentials, `/run/secrets`, health tmpfs, broker state and platform encryption keys are outside the archive. App-written secrets inside the data directory are included, so the whole snapshot is authenticated/encrypted with the broker's Data Protection key ring. Loss of those keys makes snapshots unrecoverable. Root/Docker administrators remain trusted.

Restoring another app's data or a snapshot from another installed image is rejected. Roll back the app image first when appropriate; schema migration, compatible-version declarations and cross-device import are future contracts. Restoring app data does not restore GPIO state, leases, engineering overrides, process memory or platform credentials. Apps must define safe startup and data validation independently.

The preview supports regular files and directories only. It rejects symbolic/hard links, devices, sparse/special files, traversal, duplicates, conflicting paths, oversized TAR metadata and reserved `.pidock-data-` names. Imported files use UID/GID 1654, mode 0600, and directories 0700; executable bits, ownership, timestamps and arbitrary filesystem metadata are not restored. Use this for app programs/configuration, not executable packages.

Limits are 16 MiB of file data, 20 MiB canonical archive, 1,024 entries including implicit parent directories, 240-character relative paths, four snapshots per app including safety snapshots, and a 256 MiB encrypted local-store budget. No automatic retention deletion occurs. Before saving, the store preserves the greater of 512 MiB or 5% of its filesystem plus bounded operation allowance. Target data-volume exhaustion can still interrupt staging; storage admission is separate from hard write quotas. Delete old snapshots explicitly; snapshots required by an incomplete restore cannot be deleted.

## Broker configuration and helper

Backups default off. Enable `Broker:EnableAppBackups` only with managed resources and orchestration enabled. Set `Broker:AppDataHelperImage` to a **preloaded, trusted immutable Linux ARM64 image reference** (`repository@sha256:...` or local `sha256:...`). It is not fetched while the app is paused. Build the generic helper from the repository root:

```sh
docker build --platform linux/arm64 -f tools/PiDock.AppData.Helper/Dockerfile -t pidock-appdata-helper:reviewed .
docker image inspect pidock-appdata-helper:reviewed --format '{{.Id}}'
```

Review and pin the resulting immutable identity in the broker configuration. The helper has no network, credentials, broker/Docker socket or host bind mounts. It runs as 1654 with a read-only root, dropped capabilities, no new privileges, bounded CPU/memory/process count, private tmpfs and only the verified app data volume. Its command accepts only fixed apply/finish operations plus the broker-generated operation ID. A durable in-volume move journal and completion marker allow crash-safe continuation without parsing user commands or following links. Trusted staged files are synchronized before live replacement. Helper containers are removed after successful completion; app volumes are never removed by backup operations.

The snapshot store and restore journal are retained under `<Broker:StateDirectory>/backups`, even when the feature flag is disabled, so a saved barrier cannot be bypassed by configuration. Local snapshots do not protect against device loss, disk loss or key loss. Off-device export/import, key recovery, scheduling, remote retention and migration remain future work.

## Private host-to-broker interface

These Unix-socket operations are not public app permissions:

| Method and path | Operation |
| --- | --- |
| `GET /applications/backups/support` | Versioned support and limits, including disabled state |
| `GET /applications/backups` | Snapshot metadata |
| `GET /applications/backups/restore` | Last restore metadata or no content |
| `POST /applications/{containerId}/backup` | Idempotent snapshot with `ApplicationBackupRequest` |
| `POST /applications/{containerId}/restore` | Bounded restore/retry with `ApplicationRestoreRequest` |
| `DELETE /applications/backups/{snapshotId}` | Explicit protected-artifact deletion, never app-data deletion |

The shared Owner UI authenticates mutations; apps and MCP discovery receive no raw filesystem, helper execution or archive-upload permission. An HTTP failure may mean the operation was accepted but interrupted: inspect the journal and retry its operation rather than assuming nothing changed.
