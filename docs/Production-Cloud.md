# Hosted PiDockCloud production

Portal: https://pidock.knowledgelocker.ai. Remote interfaces use isolated `d-<device-hash>.pidock-access.knowledgelocker.ai` origins. Both names resolve to `159.223.104.44`. This hosts the engineering preview release candidate; it does not change the product's remaining renewal, revocation or backup-scheduling limits.

## Deployment

Release `0.1.0-rc.1`, source `a167dff24865d9d85123a11f329867bf7b698874`, passed all 150 tests locally and in [publication CI](https://github.com/knowledge-locker/PiDockPlatform/actions/runs/43279111864). Immutable image: `ghcr.io/knowledge-locker/pidockplatform@sha256:ed43b31ee19ae17c1027f8d00a40432dfd1891d1563b10575d53f0223f8681be`.

Production uses one explicitly managed Compose stack at `/opt/pidock/production-deploy/compose.yaml` with a protected image `.env`. Do not also deploy this cloud through PRX3MUS: its single-writer runtime profiles cannot share the required signer socket volume. Existing hosted applications and the PRX3MUS control plane remain independently managed. The production runtime-profile example is historical preparation, not this active deployment.

- `pidock-production-cloud`: non-root UID 1654, read-only root, dropped capabilities, loopback HTTP at port 18080. State remains in labelled external `pidock-production-cloud` storage.
- `pidock-production-broker`: certificate-authenticated TLS on production TCP 8883, with labelled external broker state and tenant/device topic ACLs. There is no public plaintext MQTT listener.
- `pidock-production-signer`: non-root UID 1655, no network interface, private CA key mounted only here, and a group-restricted Unix socket shared with cloud. It accepts only bounded device signing requests, rebuilds validated tenant/device subjects, ignores CSR extensions, and issues non-CA client-only certificates. It logs issuance identity and expiry without private keys or CSR content.

The cloud has no CA private-key mount. Its configured `Enrollment__SignerSocket=/run/pidock-signer/sign.sock` fails closed if the signer is unavailable. The restricted socket volume is ephemeral transport and contains no durable identity state. Runtime settings are `/etc/pidock/production/configuration/cloud.env`. Caddy is a host service and forwards through exact trusted gateway `172.18.0.1`. Revalidate that value if the support network changes.

Caddy configuration retains existing site routes and adds `/etc/caddy/prox3mus.d/pidock-production.caddy`. On-demand device certificates are authorized by the production cloud's exact registered-device allow endpoint. Unknown device names are rejected. Each session still requires local appliance consent, fresh cloud MFA and PiDockOS authentication; grants last five minutes. The cloud processes relayed interface traffic. [Remote access design](Remote-Interface-Access.md).

## Independent production identities

Production PKI is separate from development. Public CA SHA-256 fingerprint:

`2E:E3:56:8A:5A:3B:92:E5:E0:9E:4B:2B:46:11:4D:96:A9:6A:7D:43:EB:61:6D:89:43:39:2B:1E:68:76:25:97`

Broker and worker certificates expire **2027-02-10 18:22 UTC**; the CA expires **2027-11-12 18:22 UTC**. Device certificates last at most 90 days. Plan manual renewal before expiry. Automated private-PKI renewal and certificate revocation/disconnect are not implemented. The isolated issuer reduces key exposure; it is not an HSM or an offline root. No CA was added to an operating-system trust store.

The production signed-release trust key is `pidock-prod-20261112`, public material at `/etc/pidock/production/secrets/updates/pidock-prod-20261112.pub`. Its private signing key remains in protected operator storage, outside the cloud server. Existing appliances need this public key installed locally before accepting packages signed by this new production key. Development release trust and appliance update permission are not silently changed.

## Private owner setup and device connection

No development account, MFA secret, session or device identity was copied into production. Run personally in a private Mac Terminal:

```sh
ssh -t -o ServerAliveInterval=30 -o ServerAliveCountMax=3 knowledgelocker-prod "tmux new-session -A -s pidock-production-owner /opt/pidock/commission-production-cloud.sh"
```

If already at the production root shell, run `tmux new-session -A -s pidock-production-owner /opt/pidock/commission-production-cloud.sh` directly. If SSH disconnects, reconnect and run `tmux attach-session -t pidock-production-owner` to resume the same private terminal; do not start a second setup process.

Choose a production username and password, tenant `local`, role `Owner`. Add the displayed setup secret to an external authenticator and provide two consecutive codes. Do not send passwords, setup secrets or codes through chat. The helper uses the maintenance lock and a temporary network-isolated setup container with Docker logging disabled, then restarts the serving cloud. Sign in at the production portal afterward.

Create an invitation with **Add device**. Locally on PiDockOS, connect to `https://pidock.knowledgelocker.ai` using that invitation and complete local ownership authentication. Changing a Pi's registration replaces its development connection; do this deliberately through its local interface. Review the independent remote-interface permission, then approve a fresh remote session from the production device dashboard. No physical Pi was moved automatically during server deployment.

## Recovery and acceptance

Before provisioning, baseline snapshot `b4006570346173c24a9f8498032649d885b42974c97e296c5f0e58a59dd43d1f` passed encrypted checking and an eight-file restore check. After provisioning, pre-first-start snapshot `f0cf5064a092c6c2ff48eddc9dc15e2ee4e460107820b95bf291a2df04f557d2` passed checking and an 18-file restore check. Its owner-only off-host archive SHA-256 is `c3a2ef7fb55176d23eab7625d93f8875a524cf8c62af921a9fda99c0b8284d69`.

Verified: public trusted HTTPS liveness and login; unauthenticated device API 401; unknown remote hostname authorization 404; private broker TLS and successful certificate-authenticated MQTT CONNECT; signer readiness; no signer network; no cloud CA-key mount; constrained read-only cloud container. The owner authorized the provider firewall change: production firewall `2d2e5a24-8c78-4985-b766-ee21bac2d721` now allows inbound TCP 8883 from All IPv4, preserving the previous nine rules. External testing from the operator Mac verifies the production broker certificate and hostname and confirms an unauthenticated client is rejected. Private owner commissioning has completed; production physical-device enrollment and remote acceptance remain pending.

After commissioning and enrollment, take another coordinated offline encrypted backup and retain its verified off-host copy. Back up cloud/broker state, secrets and settings together. There is no automatic off-site schedule or pruning policy yet. See [Persistent storage](Persistent-Storage.md).

An initial private commissioning attempt was interrupted by an SSH disconnect. Read-only recovery confirmed no account file had been saved. The abandoned temporary setup container was stopped, its helper cleanup restarted cloud, and the maintenance lock was verified free. Public liveness passed afterward. Subsequent commissioning should use the persistent private tmux session above. No password, authenticator setup secret or setup terminal contents were inspected during recovery.

The operator completed private production owner setup. The protected account file exists, the temporary setup container is absent, the maintenance lock is free, and all three production services are running with public liveness passing. Post-owner snapshot `a46322c47fbd19c821ca587fa8fbaa95bab5a7ab9f331a0d6a92554873be67a0` passed encrypted checking and a 25-file restore check. Account and key-ring hashes were preserved through the coordinated backup. Its protected off-host archive matches SHA-256 `c34ff1efb3f21262190e11d32352f52339b93842805264c538535af606868558`. No account contents or MFA secrets were inspected.
