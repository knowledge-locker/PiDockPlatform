# Shared app image updates and rollback

PiDockOS supports optional image-only updates for compatible managed apps. It stages a digest-pinned replacement, coordinates dependent shutdown, verifies readiness and retains the previous container for rollback. The capability is shared by Logic Engine and other apps through the existing resources, health, lifecycle, UI and API/MCP contracts.

## Scope and data compatibility

An update changes only the image. App identity, CPU/memory limits, loopback port mapping, credential references, dependency declarations, shutdown grace and managed data volume remain unchanged. The runtime container ID/name changes; stable app ID, shared UI contribution and launch route remain unchanged. The app must preserve compatibility with its registered interfaces/assets and existing callers; package-driven UI registration changes are not included.

**Rollback restores an image, not a data snapshot.** The new version may have written live data. Both versions must continue to understand it. Do not enable this capability for irreversible migrations or formats the previous version cannot read. Readiness reports cannot independently prove data or API compatibility. Configuration changes, volume/schema migrations, backups, package distribution/signatures and data rollback remain separate work.

## Enable and discover

Enable `Broker:EnableAppUpdates=true` together with resources, orchestration and lifecycle/deployment permissions. Startup rejects incomplete configuration. The supplied systemd unit leaves updates disabled by default. The appliance host runs a separate update worker so image pulling does not stall health/recovery polling. No browser or cloud connection is required after an operation is queued. Registry access/authentication follows the existing broker deployment path; this does not add registry credential provisioning.

Use authenticated `GET /api/platform/v1/app-updates` or read-only MCP resource `pidock://platform/app-updates`. Require `enabled: true`; missing or old broker support is not inferred. Schemas include `application-update-policy`, `application-update-policy-request`, `application-update-support`, `application-update-status` and `application-update-request`. Private credential-bearing advance requests are not public schema resources. No MCP update/deployment authority was added.

Apps opt in through runtime resources:

```json
"resources": {
  "persistentData": true,
  "health": { "startupSeconds": 60, "freshnessSeconds": 15, "failureThreshold": 3 },
  "updates": {
    "dataCompatibility": "backward-compatible",
    "readinessSeconds": 60,
    "stableSeconds": 10
  }
}
```

The Owner can also enable update policy for an existing resource app with declared health. Policy changes are blocked while a version operation or retained rollback slot is unsettled. Legacy non-resource containers and apps without health reporting cannot be updated through this path. Readiness timeout is 30–120 seconds; stable Ready is 5–30 seconds, shorter than that timeout. The readiness deadline includes bootstrap/startup time. A queued update requires a currently Ready, requested-running app and satisfied dependencies.

The shared Applications page exposes current/requested/retained images, progress, policy opt-in, **Stage app update**, **Restore previous version** and **Discard retained previous container**. Each user mutation verifies the current Owner, password and fresh authenticator code. The operation then progresses asynchronously; Refresh versions reads its latest state. Images must be immutable sha256 references, and the expected current image and operation ID prevent stale or conflicting submissions. Repeating the same matching operation is idempotent.

## Staging and promotion

Only one version operation is active globally. One previous container may be retained per app. This bounds retained runtime containers to two per logical app; it is not image-cache garbage collection or a disk quota.

1. The broker saves an encrypted operation record. It pulls the immutable Linux ARM64 image outside the lifecycle lock with a two-minute pull deadline. The existing app/dependents can keep running during staging. Stop remains available while a pull is in flight.
2. The broker creates a stopped candidate using the same isolation/resource limits. Deterministic operation-owned names/labels allow interrupted creation to recover its own candidate/network. It rejects unrelated objects. Candidate data mounts disable image copy-up so staging cannot copy image defaults into the live volume.
3. With fresh credentials available, the broker marks switching, suspends dependents in reverse order and confirms the previous process stopped. It then makes the candidate the logical active instance and bootstraps it with current credentials. At most one platform-controlled version writes the volume at a time.
4. Fresh Ready reports and satisfied dependencies must persist for the stability window before promotion. Expired observations/gaps reset that window. Missing bootstrap acknowledgement, process exit, sufficient explicit health failures or readiness deadline cause rollback. Runtime check errors remain unknown and cannot establish readiness.
5. Commit releases dependents to the existing ordered startup path and retains the old container stopped. Its tmpfs credentials are gone; it cannot be restarted through ordinary public lifecycle controls. UI/bridge service endpoints remain mapped to the same configured loopback port.

Staging failure cleans up temporary resources without restarting the old app. Retained/staged containers are hidden from ordinary active inventory to avoid duplicate app identities. Version status remains visible when a candidate disappears so the worker can attempt restoration of the retained original. A deleted retained original is not recreated automatically.

With startup-bound shared I/O/engineering enabled, the selected original retains runtime access during staging and the selected replacement can initialize authorized services while verifying readiness. The same applies to a restored original during rollback verification. Switching, pending rollback and failed rollback remain denied, and changed startup binding fences old handles. Runtime initialization does not enable manual lifecycle mutations or release dependent apps before promotion.

## Rollback

A failed replacement requests one controlled restoration attempt. The broker stops any candidate, selects the retained original, resolves current credentials again and verifies the restored app with its declared health policy. Restoring the image does not revert data. A credential that was rotated/revoked during the update cannot be taken from an old retained startup snapshot.

Missing credentials produce a visible wait and a later recheck. If the restored version cannot start or pass readiness, the operation holds at **Rollback needs Owner attention**. Normal recovery cannot race it into an infinite restart or version loop. The Owner can correct the fault and retry restoration, or Stop. Ordinary runtime recovery policy is preserved from the active record, rather than reset to the original deployment manifest's default.

An Owner can request rollback after commit while the previous container exists. If the current app was explicitly stopped, restoration leaves the old version stopped and does not test it by violating that choice. After successful restoration, failed/replaced candidate containers and their private networks are cleaned up, and the previous app remains active. No data volumes are removed.

After a successful update, another update requires explicitly discarding the retained previous container. This removes that stopped container and its private network, with volumes retained. It does not remove the currently active container or application data. Failed cleanup is recorded and retried. Once discarded, the previous container is no longer a rollback target; restoring another version requires a new authorized update. Image layers may remain in Docker cache.

## Stop, maintenance and restart

- **Stop during update** aborts it, stops dependents first, restores the original container selected/stopped and persists stopped intent. It never starts either version to satisfy Stop. A completed update uses the normal Stop behavior on the current version.
- **Maintenance pause or graceful broker shutdown** interrupts an in-progress update by selecting the original stopped container and retaining the appropriate running request. The existing shutdown journal/barrier then controls resume. After resume, normal credential/readiness/dependency rules apply. An explicit maintenance pause still requires explicit Owner Resume.
- **Broker interruption** retains phase, revision, active pointer, deadlines and deterministic candidate identity. Repeated steps revalidate operation/revision. An unacknowledged running startup is not promoted merely because the process exists. Completed bootstrap can continue readiness verification; incomplete/failed startup restores the old version or holds for attention.

Lifecycle Start/Restart/recovery and recovery-policy changes cannot race an active version operation. Dependencies remain blocked during switching/verification/restoration, even for a declared Running condition. Staging does not itself block healthy providers. Existing app-command/UI protocols are not automatically quiesced by version status: apps must stop accepting unsafe work, handle termination and implement durable command/reconciliation semantics. In-flight commands can have uncertain outcomes; the platform does not automatically retry them.

## Protected state and repair

Update journals live in the private broker state directory and use a separate Data Protection purpose. They contain manifests, active/previous identities and progress, not bearer values. Current credentials travel only over the existing privileged Unix path and into protected startup records/tmpfs. Public status contains no credentials or upstream error bodies. Disabling the feature does not erase active pointers or allow lifecycle startup to bypass an unfinished update; Stop and coordinated shutdown can still restore stopped intent.

Back up broker state, keys and update journals together. Keep accepted journals: they still identify active update-created containers and distinguish retained/staged instances. Never delete them or start retained containers through Docker manually. Corrupt journals, missing original containers, unknown ownership or damaged state require trusted repair. Root/Docker administrators remain trusted. The broker does not infer an active version from ambiguous containers or perform package/data recovery.

## Validation

Automated checks cover stable readiness, observation gaps/deadlines, failed pull/staging, missing candidate visibility, fresh rollback credentials, owner policy opt-in, feature-disable barriers, stopped intent, Stop during pull, dependency shutdown, encrypted restart recovery, failed rollback hold, explicit previous-version discard and private Unix worker transport.

A temporary local registry and real Linux ARM64 Docker images passed digest-pinned update/promotion, unchanged loopback endpoint, manual rollback and automatic rollback of a never-ready replacement. Persistent counters advanced across versions and restoration (they were retained, not reverted), and only one logical app remained visible. Test registry, containers, data volume, networks and image tags were removed. This was not a hosted deployment, physical Pi power-cycle, migration rollback or safety certification.
