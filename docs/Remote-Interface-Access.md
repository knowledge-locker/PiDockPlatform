# PiDockOS remote interface through PiDockCloud

Remote interface access is an explicit local-owner opt-in, independent of cloud updates and container control. New installations default to **Off**. Install the signed service release, then visit **PiDockOS → Remote interface access** from a local appliance connection. Saving requires local Owner password/TOTP. This permission cannot be changed through a relayed connection, including its Blazor circuit. Turning it off cancels existing connections immediately. Storage or audit failures cannot enable new access.

From the cloud device dashboard, an organization Owner/Administrator selects **Open PiDockOS**, confirms fresh password/TOTP in the shared dialog, then follows **Open secure session**. The session lasts five minutes from approval; it cannot renew itself. The user signs into the appliance using its separate account. Existing appliance confirmations remain in effect. Ending a session needs no additional MFA. Cloud sign-out, expiry, device disconnection, local permission removal, or a cloud restart invalidates the session and its streaming connections. At most four sessions per device, 64 overall, and 12 simultaneous upstream connections per session are allowed. The appliance bounds concurrent tunnel channels to 16.

## Trust and transport

The cloud is a trusted relay: HTTPS terminates at its hosting proxy and it can process appliance interface traffic, including credentials entered through that interface. Enable this only for a cloud operator you trust. This is encrypted transport across each network leg, not browser-to-appliance encryption that conceals traffic from the cloud operator.

Each device uses an isolated HTTPS origin, distinct from both the cloud application and every other device. Its label is a hash of tenant/device identity, not a user-selectable destination. Cloud cookies and the remote gateway cookie are never forwarded to the appliance. Only the appliance and antiforgery cookies pass through; returned cookies are restricted to these names, forced to be host-only, Secure, HttpOnly and SameSite Strict. Relayed pages cannot open cloud Blazor sockets or submit cross-origin cloud actions. Remote unsafe requests and WebSockets require their exact device Origin. There is no permissive CORS or iframe embedding.

The Pi initiates an outbound WSS control connection to its registered HTTPS cloud origin. The cloud challenges it with a new random nonce; the Pi proves possession of its existing enrolled RSA key using RSA-PSS/SHA-256. The cloud matches the exact issued certificate to an active registration and checks its validity. The proof cannot be replayed for another challenge. The device private key never leaves the Pi.

Each stream uses a one-time, device-connection-specific capability in an Authorization header. The Pi forwards bytes only to **127.0.0.1:7241**, over TLS validated against the appliance's installed public certificate and the localhost name. There is no arbitrary target, port, shell, LAN proxy or inbound home firewall change. HTTP bodies and WebSocket data stream through 16 KiB buffers rather than being accumulated in memory. Blazor's WebSocket transport is supported.

A cloud viewing session is insufficient to create a remote grant. The cloud's server-side action handler requires fresh MFA first. Launch requires the same still-valid cloud browser session. A one-time code is handed to the device origin by a no-store HTML POST with restricted form/script policy, not a token in a URL. A Secure, HttpOnly, host-only `__Host-PiDockRemote` cookie then gates that device origin. Every request rechecks the grant, cloud account/session, device connection and deadline; active streams are cancelled on revocation. Relayed responses and launch pages are no-store and suppress referrers.

## Server and DNS configuration

Configure the cloud with a public HTTPS origin and a separate remote hostname suffix:

```text
RemoteAccess__CloudOrigin=https://pidock-dev.knowledgelocker.ai
RemoteAccess__Domain=pidock-access-dev.knowledgelocker.ai
AllowedHosts=pidock-dev.knowledgelocker.ai;*.pidock-access-dev.knowledgelocker.ai
```

Point `*.pidock-access-dev.knowledgelocker.ai` at the development host. The production equivalents are `pidock.knowledgelocker.ai` and `*.pidock-access.knowledgelocker.ai`, pointing at the production host. DNS reservation does not deploy or commission the production application.

Caddy's on-demand TLS authorization endpoint is `/api/remote/tls-allow?domain=...`: only the exact deterministic hostnames of active registered devices are approved. Unknown names fail closed; wildcard certificate issuance and DNS API credentials are not needed. Configure the global `on_demand_tls ask` option and a restricted HTTPS catch-all route using [Caddy's on-demand TLS guidance](https://caddyserver.com/docs/automatic-https#on-demand-tls). The `deploy/prx3mus/pidock-remote-access.development.caddy` snippet contains that route. Preserve the ordinary PRX3MUS application hostname and route; this separate snippet survives cloud container replacement. Retain Caddy's TLS state and the existing exact trusted-proxy configuration.

Remote gateway sessions and one-time transport grants are intentionally memory-only and do not survive a cloud restart. Local permission and the device identity are persistent. Relaying remains unavailable until its server configuration, HTTPS names and local opt-in are ready.

## Audit and verification

Both services persist private remote-access audit metadata atomically. `remote-access-audit.json` retains the most recent 2,000 events; the cloud keeps it under its `remote-access` state subdirectory. Events record timestamps, action, actor, tenant/device scope, session ID and outcome. They include permission requests/changes, authorization, browser launch, connections and closure/revocation/expiry. They never contain passwords, OTPs, private keys, launch codes, cookies, authorization capabilities, request bodies or query strings. Local owners see local events; cloud Owners/Administrators see only their own tenant/device events. These bounded local files are not a tamper-proof external audit service; back them up with application state.

Tests exercise certificate/key proof and challenge replay rejection, local permission persistence and separation, inability to change permission through a remote connection, one-time launch, host/browser scoping, cross-origin blocking, cookie filtering, actual HTTP/WebSocket transport, grant expiry, cloud sign-out, revocation and a real Pi agent using pinned loopback TLS. A wrong loopback certificate is rejected without crashing the agent, and local disable closes the cloud path. Physical remote access still requires a one-time local owner opt-in after deployment; signed installation does not grant it.
