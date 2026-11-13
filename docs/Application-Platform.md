# Application platform discovery preview

The initial read-only discovery layer now accompanies [cloud publishing and shared app services](Cloud-Publishing-and-App-Services.md). Hardware access, binary package upload, installation validation and automated container credential delivery remain pending. The [specification](PiDock-Specification-and-Implementation-Plan.md#application-platform-api-and-development-contract) remains the target; the local platform conformance gate has not passed.

## Available interfaces

| HTTPS endpoint | Returns |
| --- | --- |
| `GET /api/platform/v1/discovery` | API/contract versions, architecture, capability states, permitted discovery operations and public registered-cloud context |
| `GET /api/platform/v1/contracts` | Contract IDs, versions, status and schema paths |
| `GET /api/platform/v1/contracts/{id}` | JSON Schema 2020-12 for `discovery`, `application-manifest`, or `application-log-event` |
| `GET /api/platform/v1/authoring` | Concise agent authoring guidance |

Contracts live in `PiDock.Core`. Version 1 uses camelCase JSON, explicit string capability/registration states, and numeric operational priorities Critical=1, Warning=2, Alert=3, Notice=4. Diagnostic level is a separate field. The manifest is accepted in registry-reference cloud releases, and the log schema is accepted by the shared app API; neither implies installation or hardware access. Schema reads are not an installation policy validator. Unknown contract IDs return 404. Responses require HTTPS and are marked `Cache-Control: no-store`; calls are limited to 120 per minute per authenticated discovery identity.

Discovery now also lists the separately authenticated shared logging, measurements, alarm lifecycle and system-read APIs. Existing owner dashboards, host resource alarms and GPIO simulation do not imply application-facing APIs. Disabled simulation is marked planned; enabled Development-only simulation is explicitly simulated with no app control operations. Board model is currently unknown (`null`); architecture is the runtime host architecture, not certification of a Pi model or image.

The cloud projection reads the saved enrollment record on every discovery request. It returns its approved HTTPS base URL, tenant and device IDs without keys, certificates, MQTT configuration or enrollment tokens. An unregistered target has a null URL; an unreadable/invalid registration returns `Unavailable` without exposing file/error details. No cloud requests are made. Connectivity/publishing remain `NotDiscovered` for registered targets, catalog/publishing endpoints stay null, and `canPublish` is false. An offline registered device still has saved target context; registration is not proof of connectivity, instance capabilities or publisher permission. Cloud instance IDs remain future work; authenticated endpoint/publisher discovery is now available separately through the cloud MCP tools.

## Discovery authentication

**Recommended:** use PiDockOS → **Agent access** to register an application, generate an encrypted/retrievable token, copy its connection prompt, rotate or revoke it. See [owner-managed agent access](Agent-Access.md). No anonymous remote API access is enabled. The configuration procedure below remains available for explicit legacy/development setups.

An existing authenticated appliance Owner session can read the endpoints. For an agent, provision a separate discovery-only credential through local host configuration:

- `PiDock__Discovery__TokenSha256`: SHA-256 hex digest of a random 32-byte token encoded as 64 hex characters (hash the exact token text, with no newline).
- `PiDock__Discovery__ExpiresAt`: explicit UTC expiry in ISO 8601 format.

The agent sends the original token as `Authorization: Bearer <token>`. Missing configuration, invalid tokens and expired credentials fail closed with 401. The credential works only on the discovery policy: it cannot read `/api/status`, control Docker/hardware, publish to cloud, or become an owner. Other authenticated roles receive 403. Supplying an invalid Authorization header never falls back to an owner cookie. Revocation/rotation changes the configured digest or expiry; environment-based changes require restarting the appliance service. The discovery policy has one locally provisioned agent identity. App/publisher credentials use separate scope-bound configuration; production issuance, OAuth and grant-management UI remain pending.

For isolated development, generate two private environment files (the command refuses to overwrite existing credentials):

```sh
python3 - <<'PY'
import datetime, hashlib, os, pathlib, secrets
root = pathlib.Path('.state/platform-discovery')
root.mkdir(parents=True, exist_ok=True)
os.chmod(root, 0o700)
token = secrets.token_hex(32)
expiry = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=1)).isoformat()
files = {
    'appliance.env': 'PiDock__Discovery__TokenSha256=' + hashlib.sha256(token.encode()).hexdigest() + '\nPiDock__Discovery__ExpiresAt=' + expiry + '\n',
    'agent.env': 'PIDOCK_DISCOVERY_TOKEN=' + token + '\nPIDOCK_APPLIANCE_URL=https://localhost:7241/\n',
}
for name, content in files.items():
    fd = os.open(root / name, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w') as stream:
        stream.write(content)
PY
```

In the appliance terminal, load only `appliance.env`, then start the existing service:

```sh
set -a
source .state/platform-discovery/appliance.env
set +a
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/PiDock.Appliance.Web --no-launch-profile --urls https://localhost:7241
```

Use the appliance's normal certificate setup. The HTTPS certificate must be trusted by the agent machine; there is no certificate validation bypass. Keep the raw-token `agent.env` private and out of source control. Do not pass the token as a command-line argument or put it in chat/logs. No accounts, cloud registration or live settings are changed by building this feature.

## Agent MCP bridge

`src/PiDock.Agent.Mcp` is an executable MCP server using the official C# SDK and standard stdio transport. The agent launches it locally; the bridge calls only the fixed HTTPS discovery endpoints on the selected appliance. It does not read the appliance's state directory. It rejects non-HTTPS origins, URL credentials, paths, query strings and fragments, refuses redirects, uses normal TLS verification, limits responses to 128 KiB and applies a 15-second request timeout. Network/authentication failures are reported without inventing target data or forwarding private server response bodies.

Build once, then launch from a separate agent terminal with the private environment loaded:

```sh
dotnet build src/PiDock.Agent.Mcp -c Release
set -a
source .state/platform-discovery/agent.env
set +a
dotnet src/PiDock.Agent.Mcp/bin/Release/net10.0/PiDock.Agent.Mcp.dll
```

Configure an MCP host to launch `dotnet` with the absolute path to that DLL and supply `PIDOCK_APPLIANCE_URL` and `PIDOCK_DISCOVERY_TOKEN` through its private environment mechanism. All bridge diagnostics go to stderr; stdout carries MCP only. This is a stdio bridge to an HTTPS API, not a publicly exposed HTTP MCP endpoint.

| MCP surface | Name or URI |
| --- | --- |
| Read-only tool | `pidock_discover` |
| Read-only tool | `pidock_list_contracts` |
| JSON resource | `pidock://platform/discovery` |
| Authoring resource | `pidock://platform/authoring` |
| Schema resource template | `pidock://platform/contracts/{id}` |

Tools accept no target URLs or arbitrary paths. Authorized registry-reference publishing is now provided by separate cloud tools; see the shared-services guide. Upload, deployment, hardware writes and network changes remain unavailable. The authoring guide and schema resources are bundled from contract version 1.0 and work without an appliance connection. Compare their version with live target discovery; `pidock_list_contracts` describes the target rather than the local bundle. The bridge refreshes target context for every discovery invocation. The cloud publishing client separately discovers endpoints and obtains tenant publisher authority; it does not infer these from the saved cloud URL.

## Compatibility and verification

`/v1` and contract version `1.0` identify this preview wire contract; discovery also has `schemaVersion: 1`. Future incompatible wire changes require a new major API/schema version. Additive changes must preserve existing field meanings and state semantics. Treat unknown capabilities conservatively; do not turn a future unknown state into permission. These are engineering preview contracts; installation and hardware conformance remain gated independently.

Automated checks cover discovery authorization/expiry and owner isolation, registered/unregistered/invalid context, secret exclusion, schema responses and priority values, bounded fixed-path bridge requests, owner/other-role authorization, and a real stdio MCP negotiation, offline resource read and connection-error path. These are software checks, not proof of physical I/O, complete platform conformance or cloud publishing. Offline authoring can use this guide and the bundled MCP guide/schema resources; target discovery and contract listing require an accessible authenticated appliance.

Implementation references: [official MCP C# SDK getting started](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/getting-started.md) and [resource definitions](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/resources/resources.md).
