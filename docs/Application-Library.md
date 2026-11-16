# Application library and local app definitions

PiDockCloud and PiDockOS accept the same `.pidock.json` app definition. A definition references an immutable Linux ARM64 Docker image in an external registry. It does not contain the image, credentials, signatures, or permission grants. PiDockOS needs registry connectivity when installing; cloud registration is optional for local upload.

## Publish and install

In PiDockCloud, open **Application library**, upload a definition, review its settings, and confirm publication with fresh workspace Owner/Administrator authentication. Releases are private to the current workspace. ID/version pairs are immutable, including runtime settings and release notes. Identical publication is idempotent. Existing metadata-only releases remain readable; publish a new version with installation settings to make them installable. The library shows all retained workspace releases (up to 1,000), with a name filter. The existing publisher list API keeps its latest-100 response bound.

Download a definition from the library and upload it under **PiDockOS → Applications → Install from an app definition**. Review its image, settings, and requested access. Where exact-image approval is enabled, enter a trust reason and approve these exact settings first; installation is a separate freshly authenticated action. The broker downloads the image and deploys through the existing restricted container/resource/bootstrap path. No cloud registration or publisher credential is needed locally.

For a registered device, enable **PiDockOS → Cloud connection → Allow cloud application installation** with local Owner authentication. This permission defaults off and is separate from start/stop/restart permission. Turning off container access or moving to another cloud also revokes installation permission. On the cloud device dashboard, select a published version, review the settings, and confirm installation. The server checks current workspace membership, Owner/Administrator role, recent live inventory, and installation permission before queuing the exact published definition. An already reported installed app is rejected; use local version controls for upgrades.

Requests travel in authenticated MQTT telemetry receipts. Acceptance expires after two minutes; an accepted registry installation can take up to three minutes. PiDockOS independently checks target identity, expiry, local permission, definition validity, and required runtime features. It records acceptance before deployment so replay/restart cannot execute the same request twice. Follow the final result under **Managed containers → Recent operations**. Expired, failed, timed-out, or interrupted requests require inspection of the actual appliance before retrying; a timeout is not proof that nothing was installed. Disabling access rejects new operations, rather than cancelling an already accepted deployment.

## Definition contract

The existing `application-release` contract gains an optional `installation` member containing the existing `application-authoring-plan`. No competing runtime manifest or Compose dialect is introduced. Metadata-only publisher requests remain supported, with unchanged serialized content/digests when `installation` is absent. Installable definitions are bounded to 16 KiB, schema version 1, and API `v1`; uploads reject unknown fields, duplicate fields (including case variants), invalid runtime settings, and identity/image/capability mismatches. The image is digest-pinned, not a mutable Docker tag.

Illustrative definition only: replace the reserved registry hostname and all-zero digest with the actual published ARM64 image before use.

```json
{
  "schemaVersion": 1,
  "manifest": {
    "schemaVersion": 1,
    "id": "sample-app",
    "version": "1.0.0",
    "requiredApiVersion": "v1",
    "image": "registry.example.test/sample-app@sha256:0000000000000000000000000000000000000000000000000000000000000000",
    "requestedCapabilities": ["apps.resources", "apps.health"]
  },
  "releaseNotes": "Initial managed application",
  "installation": {
    "schemaVersion": 1,
    "runtime": {
      "name": "sample-app",
      "image": "registry.example.test/sample-app@sha256:0000000000000000000000000000000000000000000000000000000000000000",
      "memoryMiB": 256,
      "cpuLimit": 0.5,
      "resources": {
        "persistentData": true,
        "health": {
          "startupSeconds": 60,
          "freshnessSeconds": 15,
          "failureThreshold": 3,
          "restartOnFailure": false
        }
      }
    },
    "requiredCapabilities": ["apps.resources", "apps.health"]
  }
}
```

`manifest.id` and `manifest.image` must exactly match `installation.runtime.name` and `.image`. Both capability lists must contain the same set. Installation preserves every runtime setting: memory, CPU, paired loopback ports, persistent data, named credential IDs, health, recovery, dependencies, managed lifecycle, shutdown grace, and compatible-image update policy. Capability declarations are reviewed requirements, not automatic grants or proof of live target compatibility. The installer checks runtime resource/health/recovery/orchestration/update enablement; app-specific capabilities and conformance still require live target discovery and host authorization.

Optional authoring-plan UI contribution, public assets, loopback service origin, and bridge metadata are validated and preserved. They do **not** mutate the host-owned UI registry or create bridge grants. Configure those separately using [UI integration](Application-UI-Integration.md) and [UI bridge](Application-UI-Bridge.md). This avoids treating publication as permission to expose an interface or issue credentials.

## Logic Engine compatibility

Use Logic Engine's current authoring/runtime settings from its parallel development work, rather than the illustrative sample above. The shared format can carry its persistent data, health, managed lifecycle, resource limits, credentials, loopback port mapping, UI contribution, and bridge requirements without changing its code. Its current host expects private `io`, `runtime`, `engineering`, and (for the hosted workspace increment) `bridge` credentials. Provision their exact purpose/scopes and app bindings locally; do not put token values in the definition. Keep required PiDockOS runtime-support and simulation/engineering grants enabled. Follow the Logic Engine project's own current requirements as that work evolves.

The current runtime manifest has no arbitrary environment-variable facility. An image that needs an appliance HTTPS origin or public certificate pin must use its existing supported configuration/build path. Do not embed private credentials or silently add unrestricted environment, mounts, Docker socket, devices, or networking to work around that boundary.

## API, storage and limits

- Existing authenticated publisher API: `POST /api/applications/v1/releases` accepts the extended release shape; publisher identity/app binding and tenant scoping remain unchanged. See [publishing](Cloud-Publishing-and-App-Services.md).
- Browser download: `GET /api/application-library/{id}/{version}/definition` requires a current workspace session, returns only that workspace's installable definition, and sets `no-store` and attachment headers. It returns 404 for unavailable/cross-workspace releases.
- Catalog state remains `application-catalog.json`; existing atomic persistence, protected disk reserve, and single-writer limits apply. Back it up with cloud state. Images must be retained separately in the registry.
- Remote command receipts allow up to 32 KiB to carry a bounded 16 KiB definition alongside receipt/update metadata. Existing metadata-only commands and default-off inventory serialization remain compatible.

This increment does not add an image registry, binary/archive uploads, disconnected installation, private-registry credential delivery, publisher signature verification, automatic UI registration, fleet bulk rollout, or package-driven upgrades. Use a registry reachable by the appliance. Existing image-update/rollback and unsigned-image approval policies remain authoritative. Product Pi/systemd and live registry installation acceptance remain separate from definition/API/command regression checks.

## Registry installation acceptance — November 16, 2026

The `--application-library` [Docker conformance scenario](../tools/PiDock.DockerConformance/README.md#application-library-and-registry-installation) passed against Linux ARM64 Docker 29.6.1. Actual HTTPS publication and session-protected definition download round-tripped the exact definition digest. Local upload/import and cloud-command installation both resolved the digest after the locally built image was removed. The real broker enforced exact-image approval and separate installation permission; startup reached Ready with current managed credentials, and the sample passed latch/delay/Stop checks. Replayed commands retained one installation and cloud result reporting completed. All disposable resources were removed; existing local preview and Logic Engine source were preserved. This validates registry/installation against a disposable app, rather than installation of the evolving Logic Engine product or a physical appliance.
