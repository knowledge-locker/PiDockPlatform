# PiDockCloud deployment environments

The source repository is [knowledge-locker/PiDockPlatform](https://github.com/knowledge-locker/PiDockPlatform). PiDockCloud is the web application published from this repository; PiDockOS runs on the device. The repository's existing MIT license is preserved.

The owner approved these portal names:

| Environment | Portal | PRX3MUS target |
| --- | --- | --- |
| Development | `pidock-dev.knowledgelocker.ai` | `knowledgelocker-dev` |
| Production | `pidock.knowledgelocker.ai` | `knowledgelocker-prod` |

PRX3MUS's current profile defaults to `dev.{domain}`. Our selected development name is a custom hostname, not `dev.pidock.knowledgelocker.ai`; check its per-application override before changing DNS. Do not change the global default for other applications.

## Container publication

The manually dispatched **Publish PiDockCloud container** workflow tests the source, publishes `ghcr.io/knowledge-locker/pidockplatform:<version>` (the cloud application image under the repository-based PRX3MUS name), and uploads a `prox3mus-release-<version>` artifact containing the verified immutable digest and source commit. Its metadata matches the existing PRX3MUS publication contract. It publishes an AMD64 cloud image for the current hosting servers. The local self-hosted image can still be built for its host architecture.

The publication build explicitly selects HTTP port 8080 inside the private reverse-proxy network and leaves the image's local HTTPS certificate paths empty. The normal `deploy/Dockerfile` build retains HTTPS on 8443 for local Compose. Never expose the internal HTTP listener directly to the internet.

PRX3MUS should deploy the digest to development, verify the application and supporting services, then promote that exact digest to production. Creating a catalog entry or uploading an image does not deploy a working cloud stack. DNS cutover follows route readiness.

## Runtime requirements before installation

- Persistent private state at `/var/lib/pidock`, with the correct non-root ownership. This stores accounts, Data Protection keys, invitations and telemetry; it must survive container replacement.
- A separately operated MQTT broker with mTLS, the current ACL, persistent broker state and an inbound TLS TCP route (normally 8883). The HTTPS Caddy route does not provide native MQTT connectivity.
- Device-reachable MQTT hostnames and matching broker certificates. The web hostname may be reused on port 8883 only if that server has the broker and firewall route. Development and production identities, state and issuer keys must be separate.
- Issuer settings from [Device enrollment](Device-Enrollment.md), supplied as protected runtime files/secrets. No signing key belongs in the repository, image, publishing workflow or browser form.
- Exact `AllowedHosts`, `Mqtt:Enabled`, MQTT worker credentials and endpoint configuration.
- Caddy HTTPS and WebSocket forwarding. Enable `ReverseProxy:Enabled` and configure `ReverseProxy:KnownProxies` or `ReverseProxy:KnownNetworks` for the actual private proxy source. The application trusts no forwarded headers by default; universal networks are rejected. Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED` as a trust-all workaround. See [Microsoft proxy guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).
- A cloud owner account commissioned through a trusted server console, with an external authenticator; no default account is shipped.
- Backups for state and cryptographic keys, and an initial restore check. An ephemeral web-only container is insufficient.

The observed PRX3MUS hosting form provides hostname, server and application port settings. Persistent volumes, protected issuer files, exact proxy network and native MQTT routing must be verified separately before installation. These details cannot be inferred from the web-hosting form.

## Validation and DNS cutover

Before DNS changes, verify internal health, persistent state, supporting services and the staged Caddy route. Then cut over only the two approved hostnames, let Caddy obtain their certificates, and verify public HTTPS and WebSocket behavior, local login/MFA, code creation/cancellation/expiry, device HTTPS enrollment, certificate-based MQTT, wrong-tenant rejection, live readings, cloud receipts, outage replay and restart persistence. Confirm the same account and registration remain after replacing the container. Verify the broker is unavailable without a valid client certificate.

Development is an engineering environment. Production remains gated on isolated signing, certificate renewal/revocation, trusted-clock/local recovery enforcement and the remaining security work in the main specification. Reserving its hostname does not declare it production-ready.

## Current setup status

PiDockPlatform has been discovered and approved as a web application in PRX3MUS. Both approved hostname relationships and the Development → Production flow are saved in its per-application hosting form. Public DNS and live installations remain pending supporting-service and runtime configuration.

The first development container was published successfully as `ghcr.io/knowledge-locker/pidockcloud:0.1.0-dev.1` from source `9c17744dab05b12c1bf072cfc4018926560d543b`. Immutable image: `ghcr.io/knowledge-locker/pidockcloud@sha256:f2d9210cb54c4eff9b9af3c1ea33b3a74995010903e8546b37a64d513a0f648d`. [Publication run](https://github.com/knowledge-locker/PiDockPlatform/actions/runs/43063467498). GitHub Linux AMD64 validation passed all 59 tests, in addition to macOS and local Linux ARM64. SDK selection is exact so locked dependencies match CI and Docker.

The canonical repository-named cloud image is now `ghcr.io/knowledge-locker/pidockplatform:0.1.0-dev.3`, published from `4497628c172c9d6b184b621db14a3baac2ae67e4` with digest `sha256:8e673129ab177ca152d17591beb51ad485ffc7814435162469aafaf843cb8a9f`. [Publication run](https://github.com/knowledge-locker/PiDockPlatform/actions/runs/43068673957). PRX3MUS has also recognized the preceding development publication as verified and installable; reconcile the catalog before choosing the latest digest. Registry access is already configured on both hosts; anonymous local pulls of the private package are not expected to work. No registry credentials or package visibility were changed. No application was installed and no public DNS records were changed.
