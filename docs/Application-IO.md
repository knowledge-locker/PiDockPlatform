# App-facing I/O simulation preview

This is a shared, opt-in **Development conformance-fixture capability**. It is not a physical GPIO driver, a production PLC scan transport or Logic Engine's runtime. The existing dashboard pin simulator remains independent; it cannot manipulate these app leases.

Discover `/api/platform/v1/app-io` before depending on this contract. The app-facing root is `/api/apps/v1/io`. Use HTTPS and a separately issued platform credential; neither opening an app workspace nor installing a container grants I/O access. The API uses the existing service request limits (120 requests/minute per credential), so use it for bounded, low-rate conformance exercises. A 20 ms scan requires a separately validated transport/adapter; do not promise that cadence through this preview.

## Enable a disposable fixture

Both the Development environment and `PiDock:Apps:Io:Enabled=true` are required. The production environment refuses to enable the simulation even if configuration requests it. No `/dev` mounts, privileged containers or direct GPIO bypasses are introduced.

Configure `PiDock:Apps:Io:Grants` as an array of explicit Owner/deployment-provisioned grants. Each grant has `appId`, `alias`, `direction` (`Input` or `Output`), logical `fallback` (default false), `activeLow` (default false), and `pull` (`none`, `up`, or `down`). Maximum 64 grants; duplicate app/alias grants and unsupported pins fail closed. Configuration files are privileged deployment inputs, not writable by apps. Editing this configuration is not a browser engineering action.

For the canonical fixture, grant app `sample` inputs `sim:bcm5` and `sim:bcm6`, and outputs `sim:bcm17`, `sim:bcm27`, and `sim:bcm22`. `sim:` identity is mandatory and cannot be renamed to a physical alias. Signals report explicit BCM and 40-pin header mapping. Reserved bus pins are excluded. The alias-to-header table models the standard header for this fixture and does not attest a detected board's wiring.

Issue the app `io.read` and `io.write` scopes. Only a fixture credential explicitly granted `io.simulate` may submit simulated input stimulus. A stimulus credential must also own the lease under the same app, credential ID and credential fingerprint; this endpoint does not grant a second credential the right to override another lease. Engineering test overrides have their own shared contract and are not silently mixed with I/O stimulus.

## API

| Method and relative path | Scope | Result |
| --- | --- | --- |
| `GET /support` | `io.read` | Enablement, simulation identity and limits |
| `GET /signals` | `io.read` | Only the authenticated app's explicitly granted signals |
| `POST /leases` | `io.write` | Acquire 1–16 distinct aliases exclusively; `seconds` 1–30, default 10 |
| `GET /leases/{id}` | `io.read` | One coherent simulated input/output frame for the same credential |
| `POST /leases/{id}/renew` | `io.write` | Renew with `expectedRevision` and bounded `seconds` |
| `POST /leases/{id}/outputs` | `io.write` | Atomic batch of logical output values and `expectedRevision` |
| `POST /leases/{id}/simulated-inputs` | `io.simulate` | Atomic fixture input stimulus and `expectedRevision` |
| `DELETE /leases/{id}` | `io.write` | Release and reset values to configured defaults |

Use `ApplicationIoClient` in the shared .NET SDK with a caller-owned authenticated HTTPS `HttpClient`. Maximum eight leases/app and 64 total. Aliases are exclusive across all apps, including input aliases. A grant is permission to request a lease, not ownership. Unknown, expired, foreign-app or different-credential leases return denial. A stale revision or occupied alias returns conflict. Validate the complete output batch before changing any value, including ownership and direction; failed batches do not partially write. Renew increments the revision, as do successful write batches. Reads do not renew a lease.

Frames include boot ID, monotonic timestamp/frequency, UTC observation time, frame sequence and lease revision. Values have `quality=simulated`. Output `commanded` is a logical command, never a measured electrical result; inputs have no commanded output. `value` and `fallback` are logical states. `activeLow` is inversion metadata; initial simulated inputs translate pull-up/down electrical level into logical level through that inversion. An unpulled simulated input starts from an inactive electrical low fixture level. Explicit stimulus always supplies a logical value.

## Expiry and failure

Lease timeout uses a monotonic clock, so moving the wall clock backwards does not extend it. Every operation and the hosted 250 ms sweep recheck current grants, credential expiry and revocation/rotation. The lease retains only its internal credential fingerprint and identifiers, not the bearer token. Revocation, lost scope, changed grants, invalid grant configuration, release, and clean host shutdown remove leases and reset simulated output values. The sweep interval is a best-effort scheduling interval, not a hard deadline. After process restart, all leases and simulated values are gone; apps must discover and reacquire. No active lease or stimulus is persisted.

The original unbound development mode is not coupled to lifecycle. Enable the shared [runtime-access policy](Application-Runtime-Access.md) to bind managed startup identity and revoke leases on pause, stop, restart, failure or stale/unavailable broker observations. Unbound fixtures must explicitly release leases; expiry provides their bounded crash cleanup. Production requires supported-target validation of lifecycle/maintenance integration, a validated high-rate transport if needed, independently enforced physical ownership and fallback, measured freshness/errors, physical disconnect/crash/power-loss tests, and a supported-board driver. Neither this preview nor passing simulated tests opens the production application conformance gate.

With runtime access enabled, bind the private managed bootstrap identity using `ApplicationRuntimeAccess.Bind` on the server-side I/O client. Old lease handles never revive after revocation; resuming requires fresh admission/acquisition. Public I/O metadata advertises `lifecycleBound`. This remains simulated I/O, not physical ownership or electrical fallback.
