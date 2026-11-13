# Release integration audit — 2026-11-12

## Reconciliation update — 2026-11-13

PR #1 now reconciles the unique application-platform work against the release branch. The combined implementation includes authenticated discovery and versioned schemas, the local MCP stdio bridge, local-owner agent registrations, scoped shared app services and tenant/application-bound cloud publishing. Newer remembered sessions, trusted-clock enforcement, signed updates, secure remote access and isolated certificate signing are retained. Agent credential actions use the shared MFA popup, require a local current owner, and invalidate issued tokens after ownership recovery. Commissioning diagnostics retain clock metadata while redacting MFA values.

All 204 .NET tests and all five Linux image-tool checks pass locally. No vulnerable NuGet packages were reported by the configured sources. Existing production rc.3 and the prepared alpha.1 image remain older artifacts; the next release must be built from the combined main source and use a new immutable version. No production deployment, SD write, credential change or OTP programming is part of these PR merges. The original dirty main checkout remains untouched.

The inventory below records the original audit and explains how the preserved source was accounted for. PR #1 is being finalized into the release branch before PR #2 consolidates that branch into main.


All four local branches, all three active worktrees, remote refs, stashes, open PRs and recent publication history were reviewed. There were no staged changes and no stashes. The original main checkout held uncommitted work; it was not clean or safe to reset.

| Work | Durable location | Status |
| --- | --- | --- |
| Production rc.3 provisioning and earlier signed updates/remote access | Source `ebdd38f`, release branch | Deployed; physical factory-image acceptance remains pending |
| Workspace/Safari/rate-limit fixes, monitoring corrections and remote-interface summaries | Development branch through `367a867`; merged at `8a951f0` | Integrated with production source; 184 combined Release tests passed; merged source not yet deployed |
| Canonical main integration | [PR #2](https://github.com/knowledge-locker/PiDockPlatform/pull/2) | Ready for review; incorporates both release histories, retains production security changes |
| Uncommitted application-platform/agent work | Snapshot `3839684935198eff02bfd09683aab90baaaff61a`, pushed branch `codex/preserve-main-wip-20261112`; [draft PR #1](https://github.com/knowledge-locker/PiDockPlatform/pull/1) | Preserved for reconciliation; not a deployable release |

The 88-file snapshot was built using a temporary Git index and a commit parented to the original main HEAD `6e3a5ba`. The original working files and index were left untouched. It excludes ignored runtime state, signing keys and prepared owner data. Against the combined release index, 22 changed files were already identical, 40 had overlapping/divergent implementations, and 26 existed only in the snapshot.

During reconciliation, the snapshot was not accepted wholesale: older overlapping authentication, updater and UI copies could regress released functionality. Its unique API/MCP discovery, registered-agent credentials, shared application services, catalog and associated tests were integrated against current remembered sessions, clock checks, per-action MFA, remote-session restrictions and certificate-signer isolation. PR #1 tracks this reconciliation explicitly.

## Snapshot file accounting

### Only in the preserved checkout

- `docs/Agent-Access.md`
- `docs/Application-Platform.md`
- `docs/Cloud-Publishing-and-App-Services.md`
- `src/PiDock.Agent.Mcp/CloudPublishingClient.cs`
- `src/PiDock.Agent.Mcp/DiscoveryApiClient.cs`
- `src/PiDock.Agent.Mcp/DiscoveryTools.cs`
- `src/PiDock.Agent.Mcp/PiDock.Agent.Mcp.csproj`
- `src/PiDock.Agent.Mcp/Program.cs`
- `src/PiDock.Agent.Mcp/packages.lock.json`
- `src/PiDock.Agent/PlatformDiscovery.cs`
- `src/PiDock.Agent/PlatformLogProvider.cs`
- `src/PiDock.Agent/SharedApplicationServices.cs`
- `src/PiDock.Appliance.Web/Components/Pages/AgentAccess.razor`
- `src/PiDock.Appliance.Web/PlatformDiscoveryEndpoints.cs`
- `src/PiDock.Appliance.Web/SharedApplicationEndpoints.cs`
- `src/PiDock.Cloud.Web/ApplicationCatalog.cs`
- `src/PiDock.Core/ApplicationServices.cs`
- `src/PiDock.Core/ApplicationServicesClient.cs`
- `src/PiDock.Core/PlatformContracts.cs`
- `src/PiDock.Web.Shared/RegisteredAgentStore.cs`
- `src/PiDock.Web.Shared/ServiceCredentials.cs`
- `src/PiDock.Web.Shared/wwwroot/pidock-agent-access.js`
- `tests/PiDock.Tests/ApplicationServicesTests.cs`
- `tests/PiDock.Tests/PlatformDiscoveryTests.cs`
- `tests/PiDock.Tests/PlatformMcpTests.cs`
- `tests/PiDock.Tests/RegisteredAgentTests.cs`

### Overlapping files requiring reconciliation

- `PiDock.sln`
- `README.md`
- `docs/Cloud-Workspaces.md`
- `docs/Implementation-Status.md`
- `docs/PiDock-Specification-and-Implementation-Plan.md`
- `docs/PiDockOS-Updates.md`
- `src/PiDock.Agent/CloudEnrollmentClient.cs`
- `src/PiDock.Agent/HistoryStore.cs`
- `src/PiDock.Agent/MonitoringService.cs`
- `src/PiDock.Appliance.Broker/Program.cs`
- `src/PiDock.Appliance.Web/Components/Layout/MainLayout.razor`
- `src/PiDock.Appliance.Web/Components/Pages/Telemetry.razor`
- `src/PiDock.Appliance.Web/Components/Pages/Updates.razor`
- `src/PiDock.Appliance.Web/Program.cs`
- `src/PiDock.Cloud.Web/Components/Layout/MainLayout.razor`
- `src/PiDock.Cloud.Web/Components/Pages/Device.razor`
- `src/PiDock.Cloud.Web/Components/Pages/Home.razor`
- `src/PiDock.Cloud.Web/Components/Pages/OsUpdatePanel.razor`
- `src/PiDock.Cloud.Web/Components/Pages/OsUpdates.razor`
- `src/PiDock.Cloud.Web/EnrollmentStore.cs`
- `src/PiDock.Cloud.Web/FleetStore.cs`
- `src/PiDock.Cloud.Web/OsUpdateStore.cs`
- `src/PiDock.Cloud.Web/Program.cs`
- `src/PiDock.Core/Models.cs`
- `src/PiDock.Core/OsUpdates.cs`
- `src/PiDock.Updater/Program.cs`
- `src/PiDock.Updater/UpdateEngine.cs`
- `src/PiDock.Web.Shared/Authentication.cs`
- `src/PiDock.Web.Shared/CloudWorkspaceEndpoints.cs`
- `src/PiDock.Web.Shared/UserStore.cs`
- `src/PiDock.Web.Shared/wwwroot/pidock.css`
- `tests/PiDock.Tests/CommissioningCodeTests.cs`
- `tests/PiDock.Tests/PiDock.Tests.csproj`
- `tests/PiDock.Tests/WebTests.cs`
- `tests/PiDock.Tests/WorkspaceTests.cs`
- `tests/PiDock.Tests/packages.lock.json`
- `tools/PiDock.Cloud.UiReview/PiDock.Cloud.UiReview.csproj`
- `tools/PiDock.UiReview/PiDock.UiReview.csproj`
- `tools/PiDock.UiReview/Program.cs`
- `tools/PiDock.UiReview/ReviewFixture.cs`

### Already present with identical contents

- `deploy/install-pi-updater.sh`
- `deploy/package-pidockos-update.sh`
- `docs/Device-Dashboard.md`
- `os/README.md`
- `os/systemd/pidock-updater.service`
- `src/PiDock.Agent/OsUpdateAgent.cs`
- `src/PiDock.Agent/TelemetryDelivery.cs`
- `src/PiDock.Appliance.Web/Components/App.razor`
- `src/PiDock.Cloud.Web/ApplicationCommandStore.cs`
- `src/PiDock.Cloud.Web/DeviceHistory.cs`
- `src/PiDock.Cloud.Web/IngestionService.cs`
- `src/PiDock.Core/Enrollment.cs`
- `src/PiDock.Core/ResourceAlarms.cs`
- `src/PiDock.Core/StateFiles.cs`
- `src/PiDock.Updater/PiDock.Updater.csproj`
- `src/PiDock.Updater/SystemUpdateHost.cs`
- `src/PiDock.Updater/packages.lock.json`
- `src/PiDock.Web.Shared/ResourceTrends.razor`
- `src/PiDock.Web.Shared/TelemetryExport.cs`
- `src/PiDock.Web.Shared/TelemetryHistory.cs`
- `tests/PiDock.Tests/MonitoringPipelineTests.cs`
- `tests/PiDock.Tests/OsUpdateTests.cs`

## Completion gates

- Merge/review PR #2 through normal checks; it is not an automatic deployment.
- Reconcile and validate draft PR #1 feature by feature, retaining newer security and release behavior; then include accepted work in a named release.
- Keep preservation and development refs until their unique changes are integrated. Do not reset or clean the original main checkout until its preserved source has been accounted for and its owner is ready to move it forward.
- A successful software merge does not complete physical Pi image boot, Wi-Fi, rollback, recovery or irreversible boot-key provisioning acceptance.
