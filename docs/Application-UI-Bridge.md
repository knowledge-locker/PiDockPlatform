# Authenticated app commands and live data

PiDockOS UI bridge 1.0 is a shared engineering-preview contract for any registered app. It adds named authenticated commands and bounded live data to the existing isolated UI host. It does not implement Logic Engine, grant physical GPIO, install packages or expose a general network proxy.

## Transport and authorization

The app remains in `sandbox="allow-scripts"`, with no same-origin permission and `connect-src 'none'`. It does not receive owner cookies, service credentials or an owner identity document. The trusted PiDockOS host imports its own versioned module, transfers a private `MessageChannel` to the specific iframe and binds it to that contribution's authenticated server component.

The app SDK accepts the bootstrap only from `window.parent` at the origin of the host-served SDK script. The host does not accept arbitrary `null`-origin window messages: only the transferred port and a correlated bootstrap nonce complete the handshake. The one required `postMessage` target wildcard is a delivery mechanism to an exact opaque iframe WindowProxy, not a wildcard origin trust policy. Once established, a frame load never transfers a replacement channel; an explicit host reload creates a new frame and channel. Replacing, closing or navigating away from the host component cancels its subscriptions and closes the port.

Commands and stream requests travel over the existing authenticated Blazor circuit, not a browser-accessible bearer-token endpoint. The server independently checks the current Owner principal against `UserStore`, the live enabled registration, compatible bridge version, exact named command/stream grant and an unexpired server-held credential. Discovery tokens and app-origin messages cannot become an Owner session. The server pins the target origin and paths to host configuration, refuses redirects and never forwards browser credentials or arbitrary headers.

A separate app-specific Bearer credential authenticates the host to the app's loopback service. It is loaded from protected server configuration, is never sent to the iframe, and must have an explicit expiry. Credential rotation, expiry, removal, registration changes or user/session revocation invalidate the authorization snapshot. Active streams check that snapshot every 500 ms, including while an upstream read is idle. A command already accepted by the app may have changed state before revocation; a lost or invalidated acknowledgement is reported as an uncertain outcome, not a rollback.

Only Owner users are supported in this preview. Multi-role delegation, per-operation step-up authentication, package-driven grant issuance and disclosure of user context are not implemented. Applications must still validate arguments and enforce their domain rules and any separate hardware/maintenance grants. An app UI bridge grant does not authorize the host broker or another app.

## Host configuration

Extend an existing UI registration with an optional `bridge` object. Leave it absent for a presentation-only workspace. This is host configuration; it is not a new field in `application-manifest` and cannot be provisioned by an app request.

```json
{
  "bridge": {
    "version": "1.0",
    "credentialId": "sample",
    "commandsPath": "commands",
    "commands": ["counter.increment", "counter.reset"],
    "streamPath": "events"
  }
}
```

Command names are exact, case-sensitive, bounded identifiers. Up to 16 are allowed. Paths are validated relative resource paths on the contribution's existing literal loopback service origin. For a stream-only app, omit `commandsPath` and use an empty or omitted `commands` list. For a commands-only app, omit `streamPath`. A grant needs at least one supported operation.

Private configuration under `PiDock:ApplicationUi:Credentials:{credentialId}` supplies `Token` (64 hexadecimal characters) and `ExpiresAt` (explicit UTC timestamp). Use the server's approved private environment/configuration mechanism; do not put raw credentials in source, app pages, query strings, browser storage or release packages. Provision the corresponding credential to the app service separately. For the disposable sample only, the equivalent app settings are `Sample:BridgeToken` and `Sample:BridgeExpiresAt`. Production credential issuance/injection is still a separate platform prerequisite.

Neither `commandsPath` nor `streamPath` may be approved as a `publicAssets` resource. Public static SDK/style assets remain non-secret; all live data travels over the private channel. Current registration IDs, target origin and grants remain server-owned.

## App client SDK

Discover the SDK path and hash through `GET /api/platform/v1/ui/bridge` or MCP resource `pidock://platform/ui/bridge`. `client.js` is a framework-neutral classic script; `host.js` is a trusted host module, never an app-supplied script. Assets are served under `/api/platform/v1/ui/bridge/1.0/` and embedded from the same shared source bytes. Use a new version for changes after publishing an immutable asset version.

Load the host-served client SDK in the app document. The fixture also serves its exact client bytes as a separately approved public static resource. Do not load the legacy PiDockOS shell stylesheet into the app.

```javascript
const capabilities = await PiDockUi.ready();
// capabilities: { version: "1.0", commands: [...], streaming: true|false }
const result = await PiDockUi.command("counter.increment", {});
const unsubscribe = await PiDockUi.subscribe(
  event => renderValues(event.data),
  status => showConnectionStatus(status)
);
// On app-specific panel teardown:
unsubscribe();
```

The bridge resolves command promises only from correlated replies. There are at most four pending commands. Failure codes include `not_authorized`, `command_denied`, `invalid_arguments`, `payload_too_large`, `busy`, `rate_limited`, `command_id_conflict`, `app_rejected` and `outcome_unknown`. An app rejection does not include a private upstream error body.

No command is automatically retried. On `outcome_unknown`, reconcile against live state or an app-specific status operation before the operator decides to try again. A timeout or closed interface does not prove that an accepted command failed to execute.

One subscription is allowed per frame. The SDK reports `connecting`, `connected`, `disconnected`, `stale`, `gap`, `protocol_error`, `rate_limited`, `busy`, `unavailable`, `revoked` and `not_authorized` as applicable. It retains displayed values on a disconnect so the app can mark them stale; it must not present them as current. A broken or idle channel triggers bounded reconnect with a cursor. Eight consecutive unsuccessful reconnects stop automatic recovery until an explicit reload/new subscription. Revocation/denial stop retries immediately. Callback errors or callbacks exceeding 2.5 seconds are reported as `handler_error` and stop the subscription. A fresh snapshot replaces the displayed state after a gap or epoch change.

Transport freshness does not certify sensor freshness or clock trust. App data must carry its own quality/scan/revision information where needed. A stream sequence counts emitted UI events, not PLC scans; coalesce high-rate domain changes into bounded UI updates rather than omitting intermediate event sequence numbers.

## App service command contract

The host posts `application/json` to the fixed `commandsPath`, using the app-specific Bearer credential:

```json
{
  "schemaVersion": 1,
  "commandId": "3f07854f-0000-4000-8000-000000000001",
  "name": "counter.increment",
  "arguments": {}
}
```

The app validates authentication, expiry, command identity, arguments and its domain policy, then responds with the same command ID:

```json
{
  "schemaVersion": 1,
  "commandId": "3f07854f-0000-4000-8000-000000000001",
  "succeeded": true,
  "data": { "counter": 1 },
  "errorCode": null
}
```

The host limits command JSON to 8 KiB, JSON depth to 16, responses to 32 KiB, upstream command concurrency to four and the request deadline to ten seconds. At most 60 new commands per minute are admitted for one authenticated session identity and contribution. A bounded host cache retains up to 128 correlated command results for at least two minutes, coalescing identical in-flight requests and rejecting changed payloads under an existing ID. Configuration/credential changes cannot reuse an old response as a response from a new target.

This cache is in-memory and does not promise exactly-once execution across restart or expiry. The app must implement its own appropriately durable idempotency/reconciliation policy using `commandId`. The sample retains 128 command IDs for its process lifetime. No automatic retry is added when the app returns a redirect/error, response validation fails, the deadline expires or authority changes before acknowledgement.

The host logs operator, app/contribution, command name, command ID and outcome through the existing platform logger. Arguments, bearer tokens and private upstream error bodies are not logged.

## App service live stream contract

The host opens the fixed `streamPath` with `Accept: application/x-ndjson` and the app credential. A reconnect supplies a validated `Last-Event-ID: {epoch}:{sequence}` header; no caller URL or query is forwarded. The app responds with newline-delimited JSON and `Content-Type: application/x-ndjson`:

```json
{"schemaVersion":1,"epoch":"boot-one","sequence":0,"kind":"snapshot","observedAt":"2026-11-13T20:00:00Z","data":{"counter":0}}
{"schemaVersion":1,"epoch":"boot-one","sequence":1,"kind":"update","observedAt":"2026-11-13T20:00:01Z","data":{"counter":1}}
{"schemaVersion":1,"epoch":"boot-one","sequence":1,"kind":"heartbeat","observedAt":"2026-11-13T20:00:02Z","data":{}}
```

An initial subscription needs a snapshot. Updates must be consecutive within an epoch. Heartbeats repeat the current sequence. A snapshot may replace state on reconnect or restart; sequence cannot go backward within the same epoch. A new epoch snapshot resets the sequence. Missing/out-of-order/duplicate updates generate `gap`; malformed, oversized or truncated events generate a protocol error. The SDK requests a fresh snapshot after a gap. If a bounded app replay journal cannot satisfy a cursor, the app sends its current snapshot, not an invented complete replay.

Each event is limited to 32 KiB and 16 JSON nesting levels, with browser-safe integer sequences. The host accepts at most 20 events per second, including heartbeats. Send a valid event/heartbeat at least every five seconds. There are at most eight concurrent subscriptions globally and two per authenticated session identity/contribution. Stream connections renew after two minutes. EOF, protocol/transport errors, revocation and client cancellation release their slots.

The host sends one event at a time and waits up to three seconds for the app callback acknowledgement before reading more. This bounds browser-side queued delivery; a stalled consumer is canceled rather than accumulating an unbounded channel. App service output and replay journals must also be bounded and honor HTTP backpressure. Closing a subscription or the host workspace stops presentation traffic, not the app process or its background workload.

## Discovery and validation

The shared contract catalogue includes `application-ui-command`, `application-ui-command-result`, `application-ui-event`, `application-ui-cursor`, `application-ui-bridge` and `application-ui-bridge-grant`. API discovery and the read-only MCP guide describe the same bridge. No MCP mutation tool or arbitrary HTTP proxy was added.

The [sample app](../tools/PiDock.AppUi.Sample/README.md) exercises authenticated increment/reset commands, a live counter/clock, ordered snapshots and replay, and deliberate stream interruption followed by reconnect. Its counter can now use managed persistent app data and a named credential file; local Development runs remain disposable unless managed bootstrap is enabled. Platform tests cover authorization and cross-app denial, credential expiry, command limits and deduplication, uncertain results, cursor ordering, malformed/oversized frames, idle timeout, cancellation and revocation. Full platform/hardware conformance remains separate.

## Managed credential delivery

The shared [app resources](Application-Resources.md) capability can supply the bridge credential from the protected app vault and deliver it privately to the app as a tmpfs JSON file. Use purpose `ui-bridge` and the same app/credential name in the grant. Managed revoked/expired names fail closed instead of using configured tokens. Rotation requires a managed app restart to refresh the file. Private configuration remains a compatibility option for names with no managed record.

App execution and recovery are independent of UI subscriptions. The generic [app lifecycle contract](Application-Lifecycle.md) supplies opt-in supervision, durable Start/Stop intent and current credential delivery after exit/reboot. Closing a workspace must not be used as a runtime Stop operation.
