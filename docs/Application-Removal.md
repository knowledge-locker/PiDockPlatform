# Shared managed app removal

This engineering preview removes stopped managed app runtime containers and their verified private Docker networks. It retains application data volumes, local snapshots and cached image layers. It is shared platform functionality, not a product-specific removal path.

## Enable and discover

Enable `Broker:EnableAppRemoval=true` together with managed resources and app orchestration. Preserve the private broker state directory and keys. The encrypted removal journal is loaded whenever managed resource state is configured, including when removal mutations are disabled. An incomplete journal must continue to block startup; turning off the feature is not a recovery procedure.

The authenticated platform discovery endpoint `/api/platform/v1/app-removal` and MCP resource `pidock://platform/app-removal` describe target support. Require `enabled: true`. The private broker uses `/applications/removal/support`, `/applications/removal/status`, `POST /applications/{containerId}/remove` and `POST /applications/removal/complete`. Private broker access does not replace the host's current-Owner and fresh password/TOTP checks. App credentials and frames receive no removal authority.

## Workflow

1. Remove references from other apps' required-dependency declarations before removing their provider. Finish any active version change or its cleanup. A retained, settled previous image may be removed with the current image. Complete any active data restore first.
2. Finish a global maintenance pause. The broker requires the maintenance journal to be in `paused`, and independently confirms that each selected current/retained container is stopped. It refuses running, ambiguous, shared-network or unowned targets before deleting anything.
3. Submit a new operation ID and the expected current image. The broker journals the exact app identity, current/retained container identities and private networks before mutation. It preserves stopped intent, removes the verified containers without deleting volumes, removes empty owned networks, and forgets managed startup records and the active-version pointer.
4. Review the `awaiting_external_cleanup` checklist. Disable/remove UI contribution and bridge registrations; revoke all app credentials, including independently configured credentials; remove hardware, engineering, service and publishing grants and release leases. Those records belong to the host configuration and credential domains, so successful Docker deletion does not claim they were automatically removed.
5. A current Owner explicitly confirms that external cleanup is complete. The host must verify the managed credentials and UI registrations it can inspect, and require acknowledgement for externally configured grants/credentials. The broker also rechecks that no matching managed runtime appeared and no dependent declarations were introduced. Only then does it mark the removal complete and release its startup barrier. Maintenance remains paused until the Owner resumes requested apps separately.

The completion request's Owner identity is supplied by the authenticated host, never trusted from an app frame. Reusing an operation ID for a different container or expected image is rejected. Matching completed/review-stage requests are idempotent. A new removal cannot replace an unfinished operation.

## Interruption and retention

The removal journal has `prepared`, `removing`, `metadata`, `awaiting_external_cleanup` and `completed` phases. Startup, deployment and maintenance resume remain blocked globally while it is incomplete. Broker host-shutdown recovery must not auto-resume around that barrier. An interrupted operation is retried with the same operation ID; missing already-deleted owned targets are accepted, while remaining targets are revalidated. Failure to delete a network or reconcile metadata retains the journal for repair.

Unknown/corrupt journals or update/startup metadata fail closed. Restore verified broker state and keys or repair locally with trusted evidence; the broker never infers ownership from a convenient container name. Docker/root administrators remain trusted. No force-delete, volume purge, foreign-container cleanup, automatic grant guessing or cloud uninstall command is introduced.

Retained app data can be reused by a separately approved future deployment of the same app identity. That reuse still needs compatible data semantics; removal does not migrate schemas or erase secrets an application itself stored in its data directory. Snapshots and image cache retention need their own explicit policies. Full transactional package removal and automatic editing of external registration/configuration are beyond this bounded preview.

## Validation

Five focused Release tests cover stopped/expected-image/ownership checks, dependent-provider rejection, retained-version removal and active-pointer cleanup, no volume deletion, protected journal tampering, interrupted network deletion/retry, feature-disable startup barriers, explicit external-cleanup completion and rejection of a newly appeared runtime before completion. These use disposable private state and a fake Docker transport; no installed application or physical appliance was removed.

The later integrated Docker workflow also exercised actual restricted ARM64 container/network removal, an incomplete-removal Resume barrier, live PiDockOS registration/bridge revocation, fresh-Owner managed credential revocation, live I/O grant removal and completion with retained volume/snapshots. The [interactive conformance tool](../tools/PiDock.DockerConformance/README.md) records these results. Its final teardown explicitly deletes only its own test data after verifying platform retention; that cleanup does not change the platform removal policy.
