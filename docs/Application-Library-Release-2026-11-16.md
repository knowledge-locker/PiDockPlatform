# Application library release — November 16, 2026

PiDockCloud **0.1.0-rc.19** is deployed and verified on [development](https://pidock-dev.knowledgelocker.ai) and [production](https://pidock.knowledgelocker.ai). Development completed its state, public HTTPS and access-protection checks before promotion of the same immutable image to production.

- Source: `db8a60cce6b8ee73114751d5ee6bbde4eb523921`, [PR #24](https://github.com/knowledge-locker/PiDockPlatform/pull/24).
- Image: `ghcr.io/knowledge-locker/pidockplatform@sha256:0a32b74106db9c2626d5f99723f331a14ddd27fa7b461c16fc2b228522f57231`.
- [Publication CI](https://github.com/knowledge-locker/PiDockPlatform/actions/runs/43913646027) passed all **454 release tests** and built without warnings/errors. The development checkout also passed 456 tests, including two uncommitted tests from the parallel Logic Engine platform increment; those unrelated files were excluded from this release and preserved unchanged.
- Rollback image: rc.18, `ghcr.io/knowledge-locker/pidockplatform@sha256:a4ec8a003f857364ebd71dddcbfc9a81c7ce370f97fc3a12e8209f713eb08748`.

## Registry and installation acceptance

The repeatable [application-library Docker scenario](../tools/PiDock.DockerConformance/README.md#application-library-and-registry-installation) passed in the working checkout, then again from a clean Git archive of the exact published source commit. It used a disposable Linux ARM64 sample and isolated HTTPS cloud, registry, vault and actual Docker broker.

It verified real publisher API publication, immutable-version conflict/idempotency, normal browser-session authentication and definition attachment download, exact-digest local file import, required image approval, separate cloud installation permission, and local/cloud-command installations after eviction from Docker's image store. The sample reached Ready and passed its latch/delay/Stop exercise. Command replay did not install another container, and cloud completion reporting was retained. See the [clean-release evidence](../artifacts/docker-application-library-release.json).

The command receipt was serialized and delivered in-process to the production command agent, rather than through a separate live MQTT-network test. This was not a physical Pi or Logic Engine product installation. Disposable resources were removed; existing local preview containers were retained.

## Deployment and recovery

Both environments retained account fingerprints, Data Protection key-ring fingerprints, registered device identities, the factory catalog and the existing application catalog. Public trusted HTTPS health, sign-in HTML, shared/provisioning assets and authenticated-only application library, catalog, factory-download and remote-launch routes passed. Cloud remains non-root with a read-only root. The production broker is running, the signer remains network-isolated, and the cloud has no issuer-private-key mount.

Fresh encrypted backups passed full data integrity and restore checks before deployment:

| Environment | Snapshot | Restored files checked |
| --- | --- | ---: |
| Development | `fc2590e6a9bb1a29c856d488bfbf53fad96fd9ef27d7d96c5a983b811cac1c05` | 2,942 |
| Production | `7abce5dc109cd120d375aa9627563496619d55b25a5625bf960930216dfa26eb` | 2,355 |

Checksum-verified encrypted repository copies and separate protected recovery passwords are retained in ignored owner-only off-host storage. Production replacement used the existing guarded Compose stack; development used the existing authenticated PRX3MUS agent. Old-image guards and maintenance locks covered replacement, with health-failure rollback retained.

The physical Pi was not updated. Existing offline/last-contact status was verified as pre-existing. Its app-definition upload and cloud-install opt-in require a corresponding PiDockOS update before use. The parallel Logic Engine project and its uncommitted shared runtime-support work were left intact. See [application library usage and format](Application-Library.md).
