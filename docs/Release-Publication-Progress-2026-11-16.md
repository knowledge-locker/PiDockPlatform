# Release publication progress — November 16, 2026

PiDockCloud **0.1.0-rc.20** is deployed and verified on development and production. [PR #26](https://github.com/knowledge-locker/PiDockPlatform/pull/26) merged as `dfd3b76695bf8f30a0f6d37782f05f74e8037908`.

The OS release page now reports actual transferred bytes and upload percentage, followed by separate indeterminate verification and catalog-publication stages. Signature verification runs away from the UI synchronization context. Authorization, fresh confirmation, trusted signature checks, quotas and immutable release versions remain enforced.

- Image: `ghcr.io/knowledge-locker/pidockplatform@sha256:4f6e1e451a26a644f867efe951f48ee36528557a72bf48acc7c8778b14ca982f`.
- [Publication CI](https://github.com/knowledge-locker/PiDockPlatform/actions/runs/43944695092) passed all 462 release tests. Store tests cover real byte progress, phase order, rejected packages and cancelled verification cleanup; rendered component tests cover upload values and indeterminate verification/publication. Local browser review confirmed both visible states.
- Rollback image: rc.19, `ghcr.io/knowledge-locker/pidockplatform@sha256:0a32b74106db9c2626d5f99723f331a14ddd27fa7b461c16fc2b228522f57231`.

The user-requested cancellation stopped the previous production upload and removed its 165,053,952-byte partial file. Account fingerprints were unchanged. Beta.2 was not published; the signed source package remains available for a fresh upload after refreshing the page. No Pi installation was queued.

Fresh encrypted backups passed full data-integrity and restore checks before deployment, with checksum-verified incremental off-host encrypted repository copies and separately protected recovery passwords:

| Environment | Snapshot | Restored files checked |
| --- | --- | ---: |
| Development | `2ddc92d411741b06e5d9a81117230d40f655133c7b1ff4962495fe2be0c27fdc` | 2,943 |
| Production | `195c7e53a86c49b69e5f20f8a14e7b78cb5fe35088956e1c69e64fa40d7b3ed9` | 2,355 |

Development passed before production promotion of the same immutable image. Both retained accounts, key rings, device identities, factory catalogs and application catalogs. Public HTTPS health, sign-in, shared/provisioning assets and access protections passed. Both cloud containers remain non-root with read-only roots; production signing remains network-isolated. Deployment guards defer active image transfers and retain health-failure rollback.

The Pi's last cloud contact was already stale before deployment. This release does not confirm its current runtime or update the Pi. The parallel Logic Engine work was excluded from the release and left intact.
