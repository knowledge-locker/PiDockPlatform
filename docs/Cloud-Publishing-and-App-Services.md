# Cloud publishing and shared app services

This engineering increment adds working registry-reference package publishing to PiDockCloud and shared structured logging, measurements, system reads and alarm lifecycle APIs to PiDockOS. The same contracts are available through platform schemas. These APIs do not yet satisfy the full platform conformance gate for product apps: physical hardware, package installation, container credential injection, stdout collection and cloud alarm/log delivery remain separate work.

## Service identities and setup

All new service endpoints require HTTPS and separately provisioned 64-hex-character bearer tokens. They do not accept owner cookies, discovery tokens, enrollment invitations or device telemetry certificates. Requests are bound to configuration-derived identities; payloads cannot select an app or tenant. Store only SHA-256 hashes on the service, computed from the exact token text without a newline. Each credential needs an explicit expiry; changing/removing the configured entry revokes access to subsequent requests (restart for environment-based configuration).

Configure credentials through protected service configuration or environment variables. This is a configuration example, not a shipped account/token:

```json
{
  "PiDock": {
    "Publishing": {
      "Credentials": [{
        "Id": "pump-publisher",
        "Subject": "pump",
        "TenantId": "your-tenant-id",
        "TokenSha256": "<sha256-of-random-token-text>",
        "ExpiresAt": "<explicit-future-UTC-timestamp>",
        "Scopes": ["packages.read", "packages.publish"]
      }]
    }
  }
}
```

The cloud token may publish only packages whose manifest ID is `pump`, into the configured tenant. `packages.read` allows bounded catalog reads within that tenant. A read-only identity omits `packages.publish`. The cloud remains a single-writer engineering preview; include `application-catalog.json` in protected state backups. Catalog records contain immutable external image references; operators must retain those referenced images in their registry separately.

PiDockOS has an independent configuration section:

```json
{
  "PiDock": {
    "Apps": {
      "Credentials": [{
        "Id": "pump-service",
        "Subject": "pump",
        "TokenSha256": "<sha256-of-a-different-random-token-text>",
        "ExpiresAt": "<explicit-future-UTC-timestamp>",
        "Scopes": ["logs.write", "logs.read", "telemetry.write", "telemetry.read", "alarms.raise", "alarms.read"],
        "AlarmPriorities": [1, 2]
      }, {
        "Id": "diagnostic-reader",
        "Subject": "diagnostics",
        "TokenSha256": "<sha256-of-another-random-token-text>",
        "ExpiresAt": "<explicit-future-UTC-timestamp>",
        "Scopes": ["logs.read", "telemetry.read", "alarms.read", "alarms.acknowledge", "system.read"],
        "ReadSources": ["pump", "pidockos"]
      }]
    }
  }
}
```

Each app defaults to reading only its own source. `ReadSources` adds named sources; it is not a wildcard or global read permission. It applies alongside the operation scopes. The reserved source `pidockos` cannot be assigned as an app identity. The [Agent access page](Agent-Access.md) now issues encrypted/retrievable discovery credentials with optional system reads. Hosted-app credential issuance, broader owner grant UI and container secret injection are not implemented; provision test credentials locally and never mount the Docker socket or give an app an owner credential to work around that boundary.

The environment equivalents use array indexes, for example `PiDock__Apps__Credentials__0__Scopes__0=logs.write`. Rate limits are 120 requests per minute per credential identity. Duplicate token assignments fail closed. All responses are non-cacheable. Missing/expired credentials return 401; missing scope or source access returns 403; invalid contracts 400; changed content under an immutable ID/version 409; capacity rejection 507; unavailable persistence 503.

## Cloud releases

| Endpoint | Scope |
| --- | --- |
| `GET /api/applications/v1/discovery` | Any authenticated publisher/catalog identity |
| `GET /api/applications/v1/releases` | `packages.read` |
| `GET /api/applications/v1/releases/{appId}/{version}` | `packages.read` |
| `POST /api/applications/v1/releases` | `packages.publish`, bound to manifest app ID |

Publish this package shape, with a real digest-pinned image you own:

```json
{
  "schemaVersion": 1,
  "manifest": {
    "schemaVersion": 1,
    "id": "pump",
    "version": "1.0.0",
    "requiredApiVersion": "v1",
    "image": "registry.example.test/pump@sha256:<64-lowercase-hex-digest>",
    "requestedCapabilities": ["logs.write", "telemetry.write"]
  },
  "releaseNotes": "Initial release"
}
```

Version syntax is bounded `major.minor.patch` with an optional alphanumeric prerelease suffix. The server validates the version, image reference and manifest shape and records a canonical SHA-256 package digest, authenticated publisher, tenant and receipt time. Capability order is normalized for digest calculation. A repeated identical release is idempotent (200); a new version is durably saved before 201; changed content at an existing version is rejected (409). Download the exact release and recompute its package digest using `ApplicationContractRules.Digest` before later installation work.

Catalog lists return the latest 100 releases; exact versions remain retrievable. Preview quotas are 1,000 releases per tenant, 10,000 globally, a 16 MiB catalog snapshot and a protected disk reserve. Publication never fetches or executes an image, proves its actual architecture, verifies publisher signatures or installs a container. This first format is `registry-reference-v1`: image archives, an integrated OCI registry, binary artifact uploads, channels, withdrawal, device-target rollout tracking and local installation/approval are still pending. Registry availability is required to obtain the images later.

## MCP publishing

Existing appliance discovery still reports saved registration without claiming live connectivity or publisher authority. Three additional MCP tools perform separate authenticated cloud work:

- `pidock_cloud_discover`: validates cloud API version/format, registered tenant and publishing/read permissions.
- `pidock_cloud_catalog`: reads authorized tenant releases.
- `pidock_publish_release`: publishes package JSON; does not install or run it.

In addition to the existing appliance URL/discovery token, supply `PIDOCK_CLOUD_URL` and `PIDOCK_PUBLISHER_TOKEN` through the MCP host's private environment. The pinned cloud origin must exactly match the registered appliance URL before the publisher token is sent. Discovery must then report the same tenant as registration and supported relative catalog/publishing paths. Redirects and alternate publishing URLs are rejected. Normal TLS trust applies; there is no certificate bypass. Package metadata is limited to 16 KiB in the MCP tool; responses are bounded to 1 MiB with a 15-second deadline per request. Do not embed credentials in tool arguments, package JSON or release notes.

## Shared services on PiDockOS

| Endpoint under `/api/apps/v1` | Scope and behavior |
| --- | --- |
| `POST /logs` | `logs.write`; structured `ApplicationLogEvent` |
| `GET /logs` | `logs.read`; latest 100 retained authorized log events |
| `POST /measurements` | `telemetry.write`; typed `ApplicationMeasurement` |
| `GET /measurements` | `telemetry.read`; latest 100 retained authorized measurements |
| `POST /alarms` | `alarms.raise`; raise/update own named condition |
| `POST /alarms/{condition}/clear` | `alarms.raise`; resolve own active condition |
| `GET /alarms` | `alarms.read`; active and recently resolved authorized conditions |
| `GET /alarm-events` | `alarms.read`; retained transition journal |
| `POST /alarms/{source}/{condition}/acknowledge` | `alarms.acknowledge` and source read grant |
| `GET /system` | `system.read`; latest host resource snapshot |
| `GET /health` | `system.read`; pending alarm transitions, host-log drops and rejected source writes |

`ApplicationServicesClient` in `PiDock.Core` provides typed .NET log/measurement/alarm methods over a caller-supplied authenticated HTTPS `HttpClient`. Other languages use the same JSON contracts. This increment uses bounded polling reads, not event subscriptions or an inter-app service directory.

The collector supplies source identity, receipt UTC, boot ID, monotonic timestamp/frequency and explicit `Unverified` clock trust. Caller observation time is source-reported and preserved; the envelope normalizes it to UTC. Durable segment names establish ingestion order. Diagnostic levels are Trace, Debug, Information, Warning, Error and Critical. Operational priorities remain independent: Critical=1, Warning=2, Alert=3, Notice=4. Never include passwords, tokens, private keys or other secrets in message text or structured fields. Automatic general-purpose secret detection/redaction is not promised.

PiDock's own `PiDock.*` application logs use the same journal through an ILogger provider (Information and above, subject to normal logging configuration). This is not yet systemd/kernel log or Docker stdout/stderr collection. The provider excludes framework request logs, does not recursively log persistence failures, and exposes a process-lifetime drop count. App diagnostic levels are standardized in the contract; coordinated app verbosity controls remain pending.

Logs are limited to 8 KiB, messages to 2,048 characters and 16 bounded field names; telemetry accepts finite numeric values, bounded names/units and up to 32 metric names in the retained source window. Source retention is at most 128 segments per history class. Storage uses the existing percentage budget, oldest-first cleanup and host reserve. Purge and rejected-write counts are visible; reads can therefore contain gaps. Event IDs deduplicate within retained source history and reject changed payloads. This is not indefinite exactly-once delivery after history has been purged.

## Shared alarms and failure behavior

Applications can explicitly raise/update a condition without implementing alarm storage. A repeated condition preserves its incident ID and acknowledgement; updating priority/message does not clear acknowledgement. Acknowledgement does not resolve the condition. Clearing sets resolution time; a later new event/raise can create another activation. Alarm snapshots survive log rotation and normal service restart.

Only owner-configured `AlarmPriorities` may translate an app's priority-bearing log into a local alarm, keyed by app ID and event code. No priorities alarm automatically unless granted; Notice does not notify by default. Replaying an already retained log event cannot reopen its resolved alarm. An app cannot use priority to execute a command or grant itself another capability.

Limits are 16 active conditions per app, 256 globally and 128 recent resolved entries. Alarm state is separate bounded metadata. A 64-transition durable outbox retries journal writes on subsequent alarm operations/reads; full history does not silently resolve a condition. A full transition outbox rejects further state changes. Under severe persistence failure a caller may receive 503 after a state commit; read the condition before retrying. The log-to-alarm path spans log and alarm writes, so it is not a transactional or lossless incident-delivery guarantee during power loss/I/O failure.

Local condition handling is implemented. Cloud log/alarm forwarding, notification routing/email/webhooks, a unified log/alarm UI, durable operator rules, timed suppression/escalation, host stdout capture and production failure/recovery validation are still pending. No live deployment or notification destination is configured by this increment.
