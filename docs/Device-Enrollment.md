# PiDockPlatform device enrollment

PiDockOS is the appliance. PiDockCloud is the self-hostable cloud service. PiDockPlatform is the collective name. Internal project names, wire topics, account protection purposes and cookie names retain their existing PiDock identifiers for compatibility.

## Owner workflow

1. Commission a PiDockCloud account through its trusted console and sign in with your external authenticator. Owner and Administrator roles can manage invitations for their own organization.
2. Open **Add device**, enter a friendly name, and confirm your cloud password and a fresh authenticator code. Copy the code shown once. It expires in ten minutes.
3. Commission PiDockOS locally, establish networking and correct time, then sign into its local HTTPS dashboard.
4. Open **Cloud connection**, enter the cloud HTTPS origin and enrollment code, and confirm the local owner's password and a fresh authenticator code. For a private CA, supply its independently verified public certificate in the optional trust field.
5. PiDockOS generates and saves a private RSA key locally, submits only a signed certificate request, validates the returned identity and certificate, then activates MQTT without restarting the appliance. Allow up to a minute for the first sample.
6. Back in PiDockCloud, select **Device registered — watch for connection**. The screen refreshes registration status and waits up to two minutes for a recent live reading from that exact tenant/device, then returns to **Fleet overview**. You can also watch an existing registration after returning to the page. Certificate issuance alone does not confirm live contact.
7. If no device registers, select **Try registration again** and use fresh cloud credentials to generate a replacement code; the previous pending code is cancelled. If a certificate was already issued but no reading arrives, retry the saved connection on PiDockOS and watch again. This avoids silently issuing another device identity.

The cloud never receives the local password, authenticator secret or device private key. Network enrollment never claims local ownership or provides local credential recovery. A connected device cannot be moved to another server using this flow; migration and certificate revocation need separate explicit local administration.

## Code lifecycle and retry behavior

Codes contain 128 random bits. Only a SHA-256 digest is stored. Creation and cancellation require a current authenticated organization Owner/Administrator plus fresh MFA in the management screen. Invalid, cancelled and expired codes cannot issue certificates. There are limits of 100 active invitations per organization and 10,000 retained registrations globally in the preview store.

Redemption is anonymous possession-based authentication at `POST /api/enrollment/redeem`, over HTTPS only, with a 64 KiB request limit and 20 requests per source IP per minute. Treat the code as a secret. Do not place it in URLs, logs or screenshots. The endpoint returns no-store responses. At an internet-facing proxy, add a global connection/request limit as well as the application's per-source limit.

One code binds one device identity and one public key. If a response is lost, the same device can retry with that key until code expiry and retrieve the same certificate. A different key cannot reuse the code. This is recovery of the original enrollment, not a second registration. Pending keys survive a normal device-service restart. If the code expires before recovery, create a new invitation and cancel/retire the abandoned registration administratively; automated revocation is not implemented.

The server validates the CSR signature and ignores its requested subject and extensions. The invitation supplies tenant/device identity; the issuer supplies a non-CA, client-authentication-only certificate. Certificates last at most 90 days and never exceed issuer expiry. The MQTT CN is `tenant/devices/device`, with identifiers restricted to ASCII letters, digits and hyphens. Mosquitto's `%u` patterns grant only that verified certificate's telemetry publication and persistence-receipt subscription. Existing manually provisioned preview identities retain their explicit ACLs.

## Cloud operator configuration

Enrollment is disabled until these settings are supplied:

| Setting | Value |
| --- | --- |
| `Enrollment:IssuerCertificatePath` | Public issuing CA certificate file |
| `Enrollment:IssuerKeyPath` | Separately supplied private issuing key file |
| `Enrollment:BrokerCaPath` | Public CA that validates both the issued device certificate and broker's TLS certificate in this initial implementation |
| `Enrollment:MqttHost` | Broker hostname reachable by devices; must match its TLS certificate |
| `Enrollment:MqttPort` | TLS MQTT port, default 8883 |

Environment variables replace colons with double underscores. The broker must trust the issuing CA and use the updated ACL. The issuer key is never included in the image or source repository. Mount only the specific issuer files and provide persistent private cloud state. This initial file-backed signer is an engineering implementation; an isolated constrained signing service/intermediate, automated renewal and working revocation/disconnect are production release gates. It does not yet support independently rooted device and broker certificate chains.

For an **existing local preview** with its disposable PKI, opt in explicitly:

```sh
docker compose --env-file .env -f deploy/compose.yaml -f deploy/compose.enrollment-preview.yaml up --build -d
```

This overlay mounts the disposable preview CA key into the cloud service and advertises `localhost:8883`. It is only for local development. It is not usable by a separate Pi without deliberate networking, matching server certificates and reachable endpoints. Keep the standard preview stack unchanged if enrollment signing is not needed. Do not use the preview CA as a production online issuer.

## Device persistence and data boundaries

`cloud-connection.json` is the atomic authoritative connection record, including the private key, protected by a private state directory and owner-only file permissions. `cloud-mqtt` contains the runtime certificate/key files with the same protections. Pending private keys reside in `cloud-enrollment-pending.json` until enrollment is saved. Protect these files as device identity material. Software permissions do not prevent extraction from a stolen SD card.

The connection loads after restart and activates the separate delivery worker immediately after successful enrollment. Pre-enrollment local history is not silently uploaded. Existing outbox data remains bound to its original endpoint/trust/tenant/device scope. Enrollment does not change storage limits or transfer queues to another tenant.

## Validation

Automated tests cover role/tenant isolation, hashed codes, cancellation, expiration, concurrent redemption, state-write failures, hostile CSR identity/extensions, public-key binding, normal restart, HTTPS, request throttling, and a real HTTPS PiDockOS client including lost-response recovery and immediate MQTT activation. Trusted-proxy tests reject forged HTTPS assertions from an unknown source.

The isolated Docker check exercises the actual cloud endpoint, issued certificate, broker ACL, cloud receipts and outage recovery:

```sh
dotnet build
docker build -f deploy/Dockerfile -t pidock-preview-cloud .
python3 tools/verify-delivery.py --enrollment
```

The test seeds a disposable invitation directly to isolate the transport/issuer/broker path; creation authorization is tested separately. It does not provision a real user or change the normal preview stack. Current validation passed on macOS and Linux ARM64, and the real broker denied cross-tenant publication before delivering and replaying four unique samples with zero pending.

The loopback UI review tools show the real forms but disable enrollment networking and have no configured signing key. Use a commissioned product instance for a real enrollment.

Implementation references: [Microsoft CSR validation API](https://learn.microsoft.com/dotnet/api/System.Security.Cryptography.X509Certificates.CertificateRequest.LoadSigningRequestPem), [Mosquitto certificate identities and ACLs](https://mosquitto.org/man/mosquitto-conf-5.html).

## Connection recovery and default server

PiDockOS fills in its cloud address from `Enrollment:DefaultCloudUrl`, falling back to `https://pidock.knowledgelocker.ai`. The development Pi sets this value to `https://pidock-dev.knowledgelocker.ai`. Self-hosted installations can configure their own default; the field remains editable.

Before registration, **Start over** clears form inputs. It preserves the pending device key so a retry with the same code can recover a lost response. A new code creates a new pending key through the normal enrollment path. After registration, **Retry cloud connection** requires the current local owner password and a fresh authenticator code. It resets the MQTT transport and its retry delay; registration, certificate/private key, routing scope and buffered telemetry are retained. This is transport recovery, not certificate revocation or server migration.
