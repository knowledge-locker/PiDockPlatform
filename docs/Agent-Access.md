# Owner-managed agent access

PiDockOS provides **Agent access** in the sidebar at `/agent-access`. The page is available only to the authenticated appliance owner through a local connection; PiDockCloud remote-interface sessions cannot manage agent credentials. Remote API access is authenticated; there is no anonymous enrollment or public registration endpoint. The MCP server remains a locally launched stdio bridge that uses the authenticated appliance API.

## Register and connect

1. Sign in to PiDockOS as the owner and open **Agent access**.
2. Enter the third-party application's name and the appliance HTTPS origin that the remote application can reach. It must have a trusted certificate and an allowed hostname. `PiDock:PublicOrigin` can prefill the address; otherwise the current browser origin is used for owner review. No port forwarding, firewall rule or public exposure is created.
3. Choose a lifetime of 1, 7, 30 or 90 days (default 30). Discovery is granted by default. The optional **Also allow reading system resources** checkbox adds only `system.read`.
4. Choose **Register application**, then enter the appliance owner password and a fresh authenticator code in the shared confirmation popup.
5. Copy the generated connection prompt into the trusted third-party application. It includes the HTTPS discovery URL, bearer token, expiry, granted scopes and MCP bridge environment instructions. The token is sent through the Authorization header, never in a URL.

The prompt's MCP instructions use `PIDOCK_APPLIANCE_URL` and `PIDOCK_DISCOVERY_TOKEN` with the installed `PiDock.Agent.Mcp` stdio executable. The discovery URL is a REST endpoint, not an HTTP MCP transport endpoint. See [platform discovery](Application-Platform.md) for bridge installation. Normal certificate validation stays enabled.

## Re-copy, rotate and revoke

The registered application list shows names, discovery URLs, permissions, expiry and active/revoked/expired status without revealing tokens. Click the relevant button, then enter the owner password and a fresh authenticator code in its confirmation popup:

- **Reveal / copy again** decrypts access to the same active token and generates the prompt again.
- **Rotate token** generates a replacement for the same registration, using the currently selected lifetime. The old token stops working on subsequent requests.
- **Revoke access** immediately rejects subsequent requests and removes the stored token from that registration. A revoked credential cannot be revealed or reactivated; create a new registration instead.

Expired registrations cannot authenticate or reveal their old token. Create a new registration when needed. Revealed credentials are hidden automatically after two minutes, on navigation/disposal, and when session/registration checks fail. The page refreshes registration status every five seconds. Hiding a prompt does not erase clipboard contents or copies already made in another application. Revocation does not undo requests already completed or in flight.

## Storage and authorization

The complete registry is encrypted and integrity-protected with ASP.NET Core Data Protection in `registered-agents.protected`, under the appliance's private state directory. Atomic writes and filesystem permissions match the existing account storage. The persistent Data Protection key ring is needed to recover/reveal credentials after restart. Back up the registry and its keys only through approved secret-bearing recovery procedures. Encryption here is software-backed: someone able to read both state and the local key ring may recover tokens; hardware-backed key protection remains a production release gate.

Authentication compares token hashes in constant time. Lists never return raw or encrypted tokens. Tokens are generated from 32 random bytes, bound to the issuing owner’s security stamp, checked for expiry/revocation on each request, and assigned individual identities for rate limiting. Registrations cannot obtain owner roles, container deployment, hardware access, app log writes or cloud publisher authority. Cloud publishing and hosted application service credentials remain independent. Up to 96 active registrations and 128 total retained entries are supported; oldest inactive entries may be pruned when creating new registrations.

The page uses the existing authenticated Blazor circuit and verifies the current owner session again in the registration service. Create/reveal/rotate/revoke each recheck password plus fresh TOTP, with existing replay/throttle protections. The page response is `Cache-Control: no-store`; credentials are not placed in query strings, browser storage, service logs or ordinary listing responses. No HTTP management API that bypasses these checks is added.

Owner recovery invalidates previously issued registered-agent tokens. Image installations pause bearer discovery and application-service access when authenticated-clock evidence is missing or stale; restoring valid clock evidence does not require re-registering an otherwise valid token.

The earlier `PiDock:Discovery:TokenSha256` / `ExpiresAt` configuration remains an explicit compatibility path; it is disabled when absent. That separate configured credential is not managed or revoked by this page. Prefer UI registrations for remote agents, and remove an old configured discovery credential if all agent access should be managed here. Configuration-based app and cloud publisher credentials retain their independent scopes.

## Verification

Automated tests cover encrypted storage without plaintext tokens, retrieval with the same key ring, failure with a different key ring, fresh owner MFA/replay protection, anonymous/non-owner denial, HTTPS-only access, permission isolation, rotation, expiry, and immediate rejection after revocation. The isolated UI review renders the page with credential creation/reveal disabled; it does not create live agent access.
