# PiDockOS app UI conformance fixture

For a single-command disposable review inside the real PiDockOS shell, run from the repository root:

```sh
python3 tools/PiDock.UiReview/run-app-sample.py
```

Open `http://127.0.0.1:7355/apps/ui-sample/main`. The launcher builds both fixtures, creates a temporary private bridge credential, filters inherited appliance configuration, and starts loopback-only services with isolated sample state, shared simulated I/O and engineering services, and no Docker/physical-hardware authority. Ctrl+C stops both processes. It does not use a live Owner account or deploy an app. Private bridge authorization expires after one hour; restart the review to renew it. Optional `--host-port`, `--sample-port` and `--services-port` select different unprivileged ports.

This Development-only server demonstrates the shared style bundle, isolated host launch, authenticated server-side counter commands and a bounded live counter/clock feed. It does not implement Logic Engine or control hardware. Its counter, replay journal and command IDs are disposable process state.

Start the fixture with private environment settings `Sample__BridgeToken` (a newly generated 64-hex-character token) and `Sample__BridgeExpiresAt` (an explicit future UTC timestamp):

```sh
ASPNETCORE_ENVIRONMENT=Development dotnet run --project tools/PiDock.AppUi.Sample
```

Do not paste the token into the browser or commit it. Provision the same token and expiry to the appliance's private `PiDock:ApplicationUi:Credentials:sample` configuration. Without them, the page loads but runtime commands/feed are denied. The fixture listens only on loopback, default port 7361 (`Sample__Port` overrides it).

Add this host-owned registration through local configuration:

```json
{
  "PiDock": {
    "ApplicationUi": {
      "Registrations": [{
        "contribution": {
          "schemaVersion": 1,
          "appId": "ui-sample",
          "id": "main",
          "title": "UI sample",
          "entryPath": "index.html",
          "requiredUiVersion": "1.0",
          "requiredStyleVersion": "1.0"
        },
        "serviceOrigin": "http://127.0.0.1:7361/",
        "enabled": true,
        "publicAssets": ["assets/components.css", "assets/tokens.css", "assets/client.js"],
        "bridge": {
          "version": "1.0",
          "credentialId": "sample",
          "commandsPath": "commands",
          "commands": ["counter.increment", "counter.reset", "stream.disconnect", "demo.start", "demo.stop", "demo.bank-toggle", "demo.reset"],
          "streamPath": "events"
        }
      }]
    }
  }
}
```

Environment equivalents begin `PiDock__ApplicationUi__Registrations__0__Contribution__AppId`, etc. Private credential keys are `PiDock__ApplicationUi__Credentials__sample__Token` and `PiDock__ApplicationUi__Credentials__sample__ExpiresAt`. Environment changes require process restart; reload-capable configuration providers can revoke grants while running. Credentials never appear in discovery, contribution lists, app pages or app arguments.

Open **Applications → UI sample**, or its generated PiDockOS navigation entry at `/apps/ui-sample/main`. Verify the shared raspberry action styling and that connection status becomes `connected`. **Increment app counter** changes server state and receives an acknowledgement; the displayed counter updates from the independent live stream. Server ticks and stream sequence continue increasing without clicks. **Reset app counter** also uses an authenticated command.

**Disconnect sample stream** asks the sample service to end its current feeds. The SDK marks the feed disconnected/stale, then reconnects with its cursor and resumes updates without resetting the counter. A bounded 64-event journal supplies consecutive updates; an older or unknown cursor gets a fresh snapshot. After process restart, a new epoch also requires a snapshot. Heartbeats are sent while there are no new journal entries. The sample retains up to 128 command IDs for process-lifetime idempotency; restart the disposable fixture to clear that test state.

Reload or close the interface and reopen it: subscriptions are cleaned up, while the sample's process and values continue. Test a narrow viewport, denied command name, absent grant, expired credential and disabled registration. Ordinary app assets stay read-only through the resource gateway; commands and live readings use the private bridge over the host's authenticated circuit. Direct browser fetch/form submission and arbitrary HTTP targets remain unavailable.

`publicAssets` approves exact non-secret CSS/JS/image/font paths for anonymous static loading, because the opaque frame cannot send owner cookies for subresources. Never list HTML, private configuration, runtime data or secrets there. The `commands` and `events` paths cannot be public static assets. The frame keeps `sandbox="allow-scripts"` and must not gain `allow-same-origin`.

See the shared [bridge contract](../../docs/Application-UI-Bridge.md) and [UI integration guide](../../docs/Application-UI-Integration.md). The appliance UI review harness can use the same registration for loopback-only checks with its existing disposable review identity; production still requires the normal Owner session.

## Managed resources

The Development-only fixture calls `ApplicationBootstrap.InitializeAsync` before creating its web host. When launched with managed resources it reads the named `bridge` credential from the shared tmpfs delivery path (`Sample:CredentialName` selects another name), and retains its counter under `/var/lib/pidock/app-data/counter.json` if persistent data was requested. Prepare that directory for UID/GID 1654 in the image. Its command-ID cache and replay journal remain process-local; this is not a durable exactly-once runtime. Outside managed bootstrap, the original Development-only private environment configuration still applies. See [shared app resources](../../docs/Application-Resources.md).

## Optional app health

When deployed with `resources.health` and the [fixed native reader](../../sdk/health-probe/README.md) in its image, the sample creates the shared reporter. Its counter/tick worker publishes liveness each second; the host-start event enables readiness and the stopping event withdraws it. The report remains server-side, contains no credentials and uses the current bootstrap identity. This demonstrates the general [health contract](../../docs/Application-Health.md), not Logic Engine scan health.

## Latch and nonblocking delay conformance demonstration

The same fixture now includes **Pulse Start**, **Pulse Stop**, **Toggle bank enable** and **Reset demonstration**. Add their four `demo.*` names from the registration above to the host's explicit command grant. Pulse Start latches a request and starts a three-second non-retentive delay. Repeated Start does not restart that timer; Stop clears it. Bank enable gates the output without pausing the timer. A server-side monotonic clock measures elapsed time; a 100 ms best-effort worker publishes progress without sleeping inside a command. This is neither a real-time deadline nor a physical output command.

While the delay is pending, increment the counter: it acknowledges immediately while the timer continues. Close the workspace and reopen it after three seconds: the process and demonstration continue independently. Disconnecting the stream does not stop the worker. Process restart clears the latch/timer and bank-enable state; only the optional original counter uses managed persistence.

The watch distinguishes normal/effective columns, but **no force or physical I/O adapter is connected** in this fixture, so those values agree. This disposable UI/command/timer example is not Logic Engine, a production scan engine or evidence of GPIO/engineering integration. Shared authenticated I/O and engineering APIs are exercised separately by `ApplicationIoTests` and `ApplicationEngineeringTests`; their remaining cadence, lifecycle and hardware gates still apply. The private server also accepts `demo.inputs` with Boolean `start`/`stop` fields for reset-priority conformance tests; it is not granted by the sample browser registration.

Automated conformance checks use authenticated hosted HTTP commands, worker progress with no UI subscription, deterministic monotonic timer traces, reset priority, repeated Start, gate behavior, command replay and credential expiry. Full platform install/remove, hardware fault, power-loss and production release gates are not claimed by this fixture.

The fixture performs server work every 100 ms, publishes periodic display updates twice per second, and paces replay delivery below the shared host's 20-events/second ceiling. Reconnecting with an accumulated journal does not dump that journal in a single burst. Commands remain independently acknowledged. The display cadence is not a physical I/O or PLC scan guarantee.

## Connected shared-service demonstration (2026-11-15)

The one-command launcher now connects the demonstration to the actual shared app I/O and variable engineering HTTPS endpoints. The shell uses port 7355, the sample uses 7365, and a loopback-only HTTPS service listener uses 7356 by default. The sample pins the generated service certificate exactly, including hostname checking; no system certificate trust or HTTPS authentication rule is changed.

The fresh review state contains three separately scoped managed credentials: I/O/stimulus, runtime publication, and engineering. Trusted fixture setup seeds its private encrypted vault and delivers a mode-0600 connection file in a mode-0700 temporary directory. That file and the temporary TLS key never enter the frame or a public API. This is disposable console fixture provisioning, not a production credential-issuance or delivery contract. The launcher filters real appliance configuration. Shutdown stops both processes and deletes the delivery/state files.

The engineering service uses its normal broker-status monitor plus the shared lifecycle-bound runtime guard. A private Unix-only **review process adapter** now supervises the actual sample process and serves the same lifecycle/access protocol as the Docker broker. It changes private startup identity on each start and reports pause, stop, restart and exit. It has no Docker or physical operations and does not establish container isolation or physical safety. See the lifecycle review below.

- **Pulse shared Start** writes a logical input stimulus through the leased `sim:bcm5` API. **Pulse shared Stop** uses `sim:bcm6`. The runtime samples the pulse at the next cycle and clears the stimulus afterward; a second pulse before sampling replaces the pending input state.
- A reset-priority latch publishes `run-request`. Its effective value enables a non-retentive monotonic three-second timer. Effective `bank-enabled` gates the normal output without resetting the timer. The output's effective value is submitted as one batch to leased `sim:bcm17`, `sim:bcm27` and `sim:bcm22`.
- **Set bank enable once** queues an actual shared ReadWrite variable command. The runtime applies it and acknowledges its write ID in the next complete normal frame.
- **Force selected signal**, **Release selected signal**, and **Release all forces** use the engineering credential and expected displayed instance/revision. The shared service supplies ownership, audit, expiry and normal/effective values. `delay-seconds` is a constant and cannot be forced.
- The watch shows force actor/expiry, normal/effective values, frame and revision. A separate line reports the last GPIO batch accepted by the simulator. An accepted force is not proof that the next output batch has been applied. Forcing `output-bank` can keep outputs on after Stop; release resumes its current normal value. Releasing Start does not undo the latch it already set.

Control cycles run every **1.5 seconds** to stay within the current per-credential API quotas; display updates run twice per second. The timer waits at least three monotonic seconds from enablement, with completion observed at a cycle boundary. Commands, UI closure and display refresh do not drive execution. This is a low-rate integration fixture, not a 20 ms or hard-real-time PLC claim.

Connection loss, revoked I/O authority or an invalid frame puts the runtime in `unavailable`, clears local latch/timer intent and attempts lease release; the simulator's independent lease expiry remains the fallback. It does not silently reacquire after a running-session fault. Repair the cause and use **Reset shared runtime**, which releases controls and creates a fresh instance before resuming with cleared state. Shutdown releases forces and the I/O lease where reachable. Health readiness follows a fresh connected cycle. Normal platform force freshness/expiry and credential checks remain active.

Without `Sample:ConnectionFile`, the earlier local-only counter/latch fixture remains available for isolated transport tests. It does not claim shared I/O or engineering integration.

## Lifecycle and maintenance review (2026-11-15)

The launcher starts the review host, which owns the sample process. Use **App lifecycle controls** in the review banner, or open `http://127.0.0.1:7355/sample-lifecycle`. The development-only Owner panel invokes the same private BrokerClient lifecycle/maintenance methods used by the platform, restricted to this disposable sample. No live appliance account or workload is controlled.

1. Open the sample workspace and force **Output bank** on for 300 seconds. Confirm all three accepted simulated GPIO commands are true.
2. **Pause apps**: authority is revoked before graceful sample exit. The panel must show zero leases, forces and queued writes, with outputs at fallback. **Resume requested apps** starts a fresh process with cleared controls.
3. **Stop sample** remains authoritative, including Stop while paused followed by Resume. Use **Start sample** explicitly to run it again.
4. **Restart sample** with an active force: the old admission is invalidated, the process ID changes, and the new process starts without inherited controls.
5. **Simulate process failure** kills the process without application cleanup. The host still removes its resources. This review does not automatically recover the failed process; use Start or Restart.

All I/O and engineering clients carry a private startup binding delivered only through the temporary owner-only connection file. Missing/old identities are rejected. The shared host also detects status changes while idle; no app request or browser subscription is required. Old handles and fenced registrations cannot revive. Workspace transport loss marks readings/force counts unconfirmed; the host panel remains the authoritative cleanup view.

Production target enablement, observation bounds and SDK binding are documented in [Application runtime access](../../docs/Application-Runtime-Access.md). This local-process fixture does not prove actual Docker isolation, physical fallback or hard real-time timing. Ctrl+C stops the host and its supervised sample and removes private delivery/state files.

## Real Docker lifecycle validation (2026-11-15)

The separate [Docker conformance tool](../PiDock.DockerConformance/README.md) builds and deploys this sample through the actual managed Docker broker implementation, delivers credentials through stdin into tmpfs, and checks shared simulated I/O/engineering cleanup through pause, stop, restart and SIGKILL failure. Run `dotnet run --project tools/PiDock.DockerConformance -c Release -- "$PWD"` from the checkout. It requires an otherwise unused PiDock-managed Linux ARM64 Docker Desktop target and cleans up its exact resources. It does not operate physical pins or replace the local UI review launcher.

The Docker mode is explicitly Development-only, requires managed bootstrap, binds the sample listener inside its container for the broker's loopback host port mapping, and permits the exact `host.docker.internal` HTTPS hostname with the temporary certificate pinned. Service tokens still come only from private managed credential files. The original loopback-only mode and production authentication are unchanged.

Add `--health-recovery` to the Docker command to validate the existing sample health reporter and real shared recovery worker. This installs the fixed native reader, enables the app's health/recovery policies and tests automatic recovery from SIGKILL and a stale heartbeat with cleared transient controls. It also verifies that intentional Stop remains authoritative through subsequent background passes and a stale recovery request. The original local UI launcher still does not enable automatic recovery.

Use `--retry-limits` instead for the longer repeated-process-failure scenario. It preserves the real five-attempt/5–80-second policy, checks actual retry spacing, observes suspension beyond the final deadline, then uses an explicit Start to reset the budget and tests Stop cancelling a pending retry. Allow four to five minutes; no accelerated clock or edited retry record is used. See the [procedure and evidence fields](../PiDock.DockerConformance/README.md).

Use `--budget-reset` instead to validate the sixty-second continuous healthy window. Brief Ready followed by live/Not-Ready does not replenish attempts; a fresh sustained Ready period does. This Docker scenario explicitly opts into private authenticated readiness fault injection and verifies Stop still wins across the former stability deadline. Ordinary sample runs do not expose that fixture route.

Use `--credential-recovery` instead to test revoked and naturally expired credentials across real thirty-second background rechecks. Unavailable credentials must not spend retries; vault repair must resume automatic recovery with current delivered material and cleared controls. Repair while intentionally stopped must leave the app stopped until explicit Start. The test uses an isolated disposable Owner with normal fresh password/TOTP confirmation and never changes a live account.

Use `--workflow` in an interactive terminal for the combined approved install, explicit host registration, browser launch, update/rollback, persistent-data backup/restore and removal scenario. It starts the PiDockOS review shell and pauses for five browser checks. The sample now reports a non-secret build version alongside its persistent counter so image changes are visible. See the [workflow procedure](../PiDock.DockerConformance/README.md); it uses configured registration rather than an unimplemented transactional package installer.
