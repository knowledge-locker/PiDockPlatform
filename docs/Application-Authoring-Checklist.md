# Shared app authoring preflight

For target acceptance, use the [scoped development gate record](Application-Development-Gate.md) and [readiness assessment](Application-Readiness.md). The reviewed Docker simulation target permits app development; this is not blanket deployment or hardware approval for other targets.

The read-only offline checker catches mismatched app identities, incompatible UI/style versions, unsafe resource paths, unpinned runtime images, invalid resource policies and missing named bridge credential requests before a local fixture is configured. It is shared by all apps. It does not install, contact an appliance, approve an image, create credentials or pass the platform conformance gate.

From the repository root:

```sh
dotnet run --project tools/PiDock.AppAuthoring.Check -- tools/PiDock.AppAuthoring.Check/example.json
```

Pass `-` to read standard input. Input is bounded to 64 KiB, JSON depth 16 and a ten-second read deadline. The checker accepts only its documented typed fields; unknown and duplicate property names are rejected. It returns fixed error codes and field names, without echoing submitted values, file paths or exception details. Never put secrets into an authoring file. The included image digest is a placeholder for offline validation and cannot identify a deployable sample image.

The input combines `runtime` (`ApplicationManifest`), optional `ui` (`ApplicationUiContribution`), `serviceOrigin`, exact `publicAssets`, optional `bridge` (`ApplicationUiBridgeGrant`) and `requiredCapabilities`. This is an **offline planning envelope**, not a new package, broker deployment or UI registration contract. Existing schemas and actual endpoints remain authoritative. Use named credential references only. Managed examples must request the bridge credential in `runtime.resources.credentials`; independently configured non-managed previews can have different provisioning, outside this checker's managed example path.

A successful process exit means the offline input is valid. Every report still has `targetVerified=false` and `readyForDeployment=false`. The manifest hash is useful for comparing an exact runtime review; it is not an approval. Required checks are deliberately retained even when syntax passes. Rejecting a token-shaped unknown field prevents echo, but cannot identify secrets deliberately placed into otherwise valid user labels or paths; keep all planning metadata non-secret.

Before relying on a feature:

1. Discover the actual target through `/api/platform/v1/discovery` and its feature-specific support endpoint, or the matching `pidock://platform/...` resources. Recheck required versions, enabled flags, app grants and supported target architecture. An available schema or saved cloud URL does not prove target enablement or permission.
2. Use the PiDockOS host shell and versioned shared style assets. Configure an exact literal loopback service origin, matching the approved host port; list only non-secret static assets. The host owns registration and launch. Use `PiDockUi.command` and `PiDockUi.subscribe` for named authenticated operations and bounded live data. Never give a frame a service credential.
3. Prepare `/var/lib/pidock/app-data` for UID/GID 1654 when requesting persistence. Run `ApplicationBootstrap.InitializeAsync` before app work. Read private named tmpfs credentials server-side. Discover health, orchestration, recovery, update, storage and backup support separately; do not infer any from persistent data alone.
4. Get Owner approval under the actual installation policy. Exact-manifest unsigned-image approval is not signature verification or a UI, service, GPIO or engineering grant. Verify the real image is Linux ARM64 and supports the requested bootstrap/health protocol before deployment.
5. Use `io.read`/`io.write`, plus explicit `io.simulate` for simulated stimulus, only with discovered app-I/O simulation and separate alias grants. This Development-only adapter is bounded to the shared 120 requests/minute rate and is unsuitable for a 20 ms PLC scan. `physical=false` and `sim:` aliases must remain visible; physical GPIO stays unavailable until its driver and board gate pass.
6. Use separate managed `signals.runtime` and `signals.engineer` credentials for variable engineering. Runtime instance/revision, normal/effective values, freshness, expiry and acknowledgement are distinct from variable exposure. The app must consume effective values and pending writes at its own execution boundary. Physical GPIO forcing is not supplied by this preview.
7. Run the relevant disposable fixture and negative/recovery scenarios, including denial, revocation, stale feeds, closure-independent execution, restart, maintenance, image/data recovery and removal. Record actual target evidence rather than treating offline validation or passing unit tests as product or hardware approval.

The existing [app UI sample](../tools/PiDock.AppUi.Sample/README.md) provides a server-side latch, nonblocking delayed indicator and live watch. Its values are simulated; the fixture does not silently connect to the app-I/O or engineering adapters. These are separate conformance exercises. Package distribution, fleet features and extra buses are not requirements for this local authoring check.
