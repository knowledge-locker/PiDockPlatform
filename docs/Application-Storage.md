# Shared app storage management preview

PiDockOS retains managed app data until an explicit removal operation. Stop, restart, an image rollback, a failed deployment and a low-space condition do not silently delete data. Backup retention is separately bounded by [app backups](Application-Backups.md).

This increment adds optional **new-volume admission** and bounded Docker-reported volume usage. It does not enforce ongoing per-app quotas. A running app can still consume free space after admission; apps must bound their files and handle write failures. Kernel/filesystem-backed per-app quotas, volume migration, automatic storage cleanup and off-device disaster recovery remain separate gates.

## Enable and discover

Read authenticated `/api/platform/v1/app-storage` or `pidock://platform/app-storage` for metadata; require `enabled: true` before relying on admission. The privileged broker additionally exposes `/applications/storage` for Owner-facing capacity and managed-volume usage. App credentials receive no Docker storage authority, host paths or purge operation.

An administrator configures `Broker:AppStorage` with `capacityDirectory`, `backingFilesystemConfirmed: true` and optionally `policy`. The directory must identify the actual **local filesystem backing Docker app volumes**, already exist, and have no symlink components. Docker installed on a separate mount needs that mount; the broker's root directory is not sufficient evidence. Do not enable this against a remote Docker daemon. The explicit confirmation is an administrator assertion; PiDockOS cannot establish remote filesystem equivalence.

Default policy reserves the larger of 512 MiB or 5% of filesystem capacity, plus 32 MiB for each new-volume admission. The supported policy range is 5–80% reserve, 512 MiB–1 TiB minimum reserve and 1–1024 MiB admission allowance. Invalid configuration, unavailable measurements or insufficient free space reject a new managed volume before Docker creates it. Existing volumes remain accessible for recovery and are not deleted under pressure. The allowance is an admission threshold, not a reserved allocation or an image-size estimate.

Usage comes from Docker's `/system/df`, capped at one MiB of metadata and a 20-second request deadline. Only exact PiDockOS-owned local data-volume identities without external driver options are reported. Unsupported, unknown or negative sizes remain unknown rather than becoming zero. Reported bytes are estimates observed at the response time, not a live capacity guarantee. Large Docker installations can exceed the metadata cap; report unavailable and inspect locally rather than increasing the bound silently.

Automated tests cover reserve boundaries, invalid measurements/configuration, rejection before volume/container creation, filtering foreign/external volumes, unknown sizes and oversized reports. Supported Pi filesystem/mount layouts and sustained storage-pressure operation still require target validation.
