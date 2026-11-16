# Shared app dependency ordering and coordinated shutdown

PiDockOS provides optional dependency-aware lifecycle management for compatible resource apps. It builds on [managed resources](Application-Resources.md), [recovery](Application-Lifecycle.md) and [health/readiness](Application-Health.md). It is general platform support for Logic Engine and other apps, without adding service, network, credential or hardware authority.

## Enable and discover

Enable `Broker:EnableAppOrchestration=true` together with managed resources and either lifecycle or deployment permission. The broker rejects incomplete configuration. The supplied systemd unit leaves orchestration disabled by default. The appliance host coordinates queued startup without a browser or cloud connection. It checks health first, then dependency/shutdown state, then eligible startups.

Discover support through authenticated `GET /api/platform/v1/app-orchestration` or read-only `pidock://platform/app-orchestration`. Support advertises the 16-app limit, eight dependencies per app, 180-second shutdown deadline and limitations. Schemas include `application-dependency`, `application-dependency-status`, `application-orchestration-support` and `application-orchestration-status`. Existing runtime-manifest/lifecycle-status schemas include declarations, pending startup and pause state. MCP exposes planning/discovery, not lifecycle mutation authority.

## Declare required apps

```json
"resources": {
  "managedLifecycle": true,
  "dependencies": [
    { "appId": "signal-service", "condition": "ready" },
    { "appId": "event-worker", "condition": "running" }
  ],
  "shutdownSeconds": 10
}
```

References use stable, exact app IDs from managed manifests. `ready` is the default and requires a fresh declared health report for the provider's current startup. `running` explicitly accepts a running process with completed bootstrap; it does not imply business readiness. A provider with no health contract cannot satisfy `ready`.

`managedLifecycle: true` explicitly opts into bootstrap-backed lifecycle when no other resources are needed. Requests for data, credentials, health, recovery, dependencies or a nondefault shutdown grace also opt in. Such images must implement shared bootstrap. Non-resource legacy Docker containers are not adopted and remain outside dependency ordering/coordinated shutdown.

Up to eight distinct required apps are supported. Self references, duplicate references, invalid conditions, ambiguous installed identities and known cycles are rejected. Graph validation happens before image pull for new deployments; an edge to an app not yet installed is permitted but blocks startup. Deploying the missing app later cannot introduce a cycle. Missing containers are not recreated automatically.

A dependency must be requested running, actually running, past bootstrap and satisfy its own transitive requirements. Fresh health must remain available for `ready`. Dependencies do not install/start an explicitly stopped provider, disclose its credentials, connect isolated networks or grant app-to-app APIs. Service grants remain separate work.

## Startup and runtime dependency loss

Manual start, deployment and automatic recovery all use the same broker dependency gate. Once credentials validate, an unavailable dependency can leave the app Created/Stopped with a saved running request and **Start pending**. Waiting consumes no startup attempts. The appliance coordinator resolves current named credentials again when startup becomes eligible. A stopped provider still requires an authorized Start from its user or its own previously authorized recovery policy.

Providers start before dependents. A Ready requirement waits for a fresh report; a bootstrap acknowledgement alone is insufficient. Startups run sequentially with bounded passes, so readiness polling can delay later nodes. There is no hard real-time startup guarantee.

If a provider becomes unavailable, transitive dependents stop in reverse order. Their running requests become pending so they can resume after the provider returns. Explicitly stopped dependents retain their stopped choice. This suspension/resumption does not require opting into crash recovery: it completes an existing approved running request. A later independent crash still requires the app's separate recovery permission.

Actual deferred starts/resumes use the existing five-attempt budget and backoff; waiting does not. Flapping dependencies cannot create unlimited restart loops. Start/Restart can clear a paused budget after the fault is corrected; maintenance Resume does not erase it. Health-enabled apps need continuously observed Ready stability to reset the budget. An observed dependency gap clears the stability window even for apps that use process-based health.

## Stop and restart a provider

An Owner Stop persists the stopped request first, invalidating old queued startups. Its transitive dependents are stopped before the provider, preserving their running requests. Restart similarly suspends dependents, restarts the provider through bootstrap and waits for its required availability before resuming them.

PiDockOS waits for each dependent to stop and checks the runtime state before signalling its provider. If a dependent stop fails, the provider is retained and the operation reports failure. The saved intent/pending state allows a later reconciliation pass to finish. Independent apps are unaffected by an individual provider Stop/Restart.

App shutdown grace is 1–10 seconds, default 10. New containers specify SIGTERM; existing containers retain their existing image/container stop signal until redeployed. Docker may force-kill an app after its grace. A successful stop establishes that the process is stopped, not that it committed every transaction correctly.

Apps should handle termination, withdraw readiness, stop accepting work, finish or cancel bounded operations, flush durable data and exit within their grace. Keep required providers usable until dependent cleanup finishes. Do not make cleanup depend on an already stopping provider. Physical GPIO fallback remains a separate required hardware contract.

## Maintenance pause and resume

The shared Applications page shows dependency conditions/reasons, pending starts and coordinated shutdown progress. **Pause supervised apps** and **Resume requested apps** use current Owner password/TOTP, just like individual controls.

Pause first saves an encrypted shutdown journal containing the reverse dependency order. It then stops resource apps one at a time, persisting progress after each. The startup barrier is active for the entire operation. Cancellation, HTTP failure or broker interruption leaves the journal; retry resumes at the saved position. Already stopped apps are safe to revisit. Providers are never deliberately advanced past a dependent whose stop failed.

A completed maintenance pause remains paused across broker/host restarts until explicit Resume. Resume finishes any incomplete shutdown before opening the barrier, then queued requests converge provider-first using current credentials/readiness. It preserves stopped choices and exhausted retry budgets. Deployments and resource-app starts are blocked during pause. Legacy non-resource apps continue under their separate Docker lifecycle.

## Broker and host shutdown

When orchestration is enabled, graceful broker termination uses the same reverse-order shutdown journal with reason `host_shutdown`. The broker keeps startup blocked while its process is terminating. After broker restart, the coordinator completes an interrupted stop sequence and opens this host-shutdown barrier automatically; requested apps become pending for ordered startup. A pre-existing explicit maintenance pause keeps its reason and is never auto-resumed. An explicit maintenance pause can also replace an outstanding host-shutdown auto-resume request.

The supplied appliance unit is ordered after the broker. The broker is ordered after Docker, giving reverse shutdown order: appliance coordinator, broker/app cleanup, then Docker. The broker allows 180 seconds for a batch; its unit allows 220 seconds to stop. This also stops managed resource apps during a graceful broker service update/restart. Preserve broker state, Data Protection keys and journal across updates. Unexpected power loss or SIGKILL cannot run this shutdown sequence; subsequent startup follows retained intent/recovery state and cannot undo lost cleanup.

Correct graceful stop depends on Docker availability, intact metadata, host deadlines and application behavior. No guarantee is made if the runtime fails or the service manager forcibly terminates the broker. Shutdown does not uninstall containers or delete app data/credentials.

## Configuration and repair boundaries

Keep orchestration enabled while installed apps rely on ordering. Declared dependencies do not become bypassable startup conditions if the feature is disabled, and a saved shutdown pause still blocks resource-app startup. Re-enable orchestration to finish/resume that journal. Runtime ordering and coordinated shutdown are unavailable while the feature is disabled.

Ordering requires readable protected startup records and a valid graph. Unknown/corrupt records, ambiguous identities, cycles or corrupt journals block ordered operations; repair the private state from verified backups. A known container with an unreadable record can still use the existing direct Stop fallback, for which dependency ordering cannot be established. No fabricated ordering or automatic metadata repair is attempted.

This feature does not add package rollout/rollback, container recreation, volume backup/purge, distributed services, physical safety guarantees or cross-appliance orchestration.

## Verification

Tests cover graph diamonds/cycles, dependency gates, stale/missing readiness, explicit running conditions, Stop/revision races, transitive loss/recovery, confirmed dependent exit, failure retry, encrypted interrupted maintenance journals, host-restart resume, disabled-feature barriers and coordinator transport over a Unix socket. A two-app Linux ARM64 Docker check proved that the child waited for provider Ready, exited gracefully before provider SIGTERM, stayed paused until maintenance Resume, retained data and saved running requests through ordered restart, and preserved host-shutdown resume intent. The same check also terminated a real broker process with SIGTERM and verified that its shutdown handler stopped apps in order and retained the host-resume journal. It did not exercise systemd or power-cycle a physical Pi.

[Shared app image updates](Application-Updates.md) now suspend dependents through version verification and interrupt safely into this maintenance/host shutdown workflow. This does not add package rollout, data rollback or cross-appliance orchestration.
