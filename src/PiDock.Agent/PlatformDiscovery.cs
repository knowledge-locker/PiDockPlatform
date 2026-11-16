using System.Runtime.InteropServices;
using System.Text.Json;
using PiDock.Core;

namespace PiDock.Agent;

public sealed class PlatformDiscovery(CloudEnrollmentClient enrollment, GpioSimulator gpio, ApplicationIoService io)
{
    public PlatformDiscoveryDocument Read() => new(1, PlatformContracts.Version, PlatformContracts.ApiVersion,
        "PiDockOS", "engineering-preview", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        null, ReadCloud(),
        [
            new("platform.discovery", CapabilityState.Available, "Read-only target and registered-cloud context.", ["discovery.read"]),
            new("platform.contracts", CapabilityState.Available, "Versioned schemas and authoring guidance.", ["contracts.read"]),
            new("ui.styles", CapabilityState.Available, "Versioned scoped PiDockOS style assets 1.0.", ["ui.styles.read"]),
            new("ui.hosting.readonly", CapabilityState.Available, "Owner-configured isolated GET/HEAD resources; optional commands/streaming advertised separately.", ["ui.discovery.read"]),
            new("ui.hosting.interactive", CapabilityState.Available, "Owner-bound named app commands and acknowledged live NDJSON delivery through an isolated frame bridge. Per-contribution grants required; no hardware authority.", ["ui.bridge.discovery.read"]),
            new("apps.resources", CapabilityState.Available, "Managed app volumes and protected startup delivery are implemented behind broker opt-in. Probe app-resources; storage admission and backups are discovered separately. Hard quotas and purge remain separate.", ["app-resources.read"]),
            new("apps.lifecycle", CapabilityState.Available, "Opt-in resource-app supervision with durable Stop intent, current credential delivery, bounded retry and stability reset. Probe app-lifecycle; health reporting is separately discovered; dependency ordering is separately discovered; no deleted-container recreation.", ["app-lifecycle.read"]),
            new("apps.health", CapabilityState.Available, "Opt-in bounded app heartbeat liveness/readiness checks, startup grace, observation expiry and optional recovery using the existing retry budget. Probe app-health; app reporting is not hardware safety certification.", ["app-health.read"]),
            new("apps.orchestration", CapabilityState.Available, "Opt-in required-app ready/running conditions, reverse-order stop/restart, durable maintenance pause and broker-shutdown recovery. Probe app-orchestration; no service grants, forced power-loss cleanup or package rollback.", ["app-orchestration.read"]),
            new("apps.updates", CapabilityState.Available, "Opt-in immutable image-only updates, readiness-gated promotion, retained previous version and controlled rollback. Probe app-updates; requires backward-compatible live data, not schema/data rollback or package rollout.", ["app-updates.read"]),
            new("apps.backups", CapabilityState.Available, "Bounded encrypted local managed-data snapshots and paused-app staged restore behind broker opt-in. Probe app-backups; off-device export and schedules remain separate.", ["app-backups.read"]),
            new("apps.storage", CapabilityState.Available, "Opt-in new-volume capacity admission and bounded managed-volume usage estimates. Probe app-storage; no hard quotas or ongoing growth enforcement.", ["app-storage.read"]),
            new("apps.install-approval", CapabilityState.Available, "Opt-in expiring Owner approval of exact immutable image and runtime settings. Probe app-install-trust; unsigned-image exception, not publisher-signature verification.", ["app-install-trust.read"]),
            new("apps.removal", CapabilityState.Available, "Opt-in paused-app runtime removal with retained data and durable cleanup barrier. Probe app-removal; Owner confirmation of external access cleanup remains required.", ["app-removal.read"]),
            new("apps.runtime-access", CapabilityState.Available, "Opt-in private startup binding, broker observation, lifecycle revocation and permanent registration fencing. Probe app-runtime-access; simulated I/O only, no physical safety guarantee.", ["app-runtime-access.read"]),
            new("apps.engineering", CapabilityState.Available, "Opt-in managed-credential variable set/force/release with runtime revision, expiry and audit. Probe app-engineering; no physical GPIO authority.", ["app-engineering.read"]),
            new("gpio.control", CapabilityState.Planned, "Physical GPIO operations and electrically validated hardware leases are not implemented.", []),
            new("gpio.app-simulation", io.Support().Enabled ? CapabilityState.Simulated : CapabilityState.Planned,
                "Scoped app-facing simulated aliases, exclusive leases, batch outputs and fallback. Probe app-io; Development-only and never physical GPIO.", []),
            new("gpio.simulation", gpio.Enabled ? CapabilityState.Simulated : CapabilityState.Planned,
                gpio.Enabled ? "Development dashboard simulation enabled; the separate app-facing adapter is discovered through app-io." : "Development dashboard simulation is disabled.", []),
            new("hardware.buses", CapabilityState.Planned, "App I2C/SPI/PWM/serial/USB interfaces are not implemented.", []),
            new("video.channels", CapabilityState.Planned, "Camera and display channel allocation is not implemented.", []),
            new("system.resources", CapabilityState.Available, "Scoped app system reads.", ["system.read"]),
            new("network.configuration", CapabilityState.Planned, "App Wi-Fi/network configuration and rollback are not implemented.", []),
            new("telemetry.exchange", CapabilityState.Available, "Scoped typed app measurements and bounded polling; streaming remains planned.", ["telemetry.read", "telemetry.write"]),
            new("services.discovery", CapabilityState.Planned, "App-to-app service exports and grants are not implemented.", []),
            new("logging.central", CapabilityState.Available, "Shared structured app/PiDock host journal; Docker stdout collection remains planned.", ["logs.read", "logs.write"]),
            new("alarms.shared", CapabilityState.Available, "Durable app raise/update/acknowledge/clear and priority-triggered local conditions; optional local notifications are discovered separately.", ["alarms.read", "alarms.raise", "alarms.acknowledge"]),
            new("alarms.notifications.local", CapabilityState.Available, "Opt-in bounded durable app notification inbox and separate receipts. Probe app-diagnostics; no external delivery.", ["app-diagnostics.read"]),
            new("packages.upload", CapabilityState.Planned, "Direct application package upload is not implemented.", []),
            new("packages.cloud", CapabilityState.Planned, "Compatible PiDockCloud supports registry-reference publishing via separate credentials; appliance installation remains planned.", [])
        ], PlatformContracts.List().Select(c => c.Id).ToArray(), ["discovery.read", "contracts.read"]);

    private CloudTarget ReadCloud()
    {
        try
        {
            var connection = enrollment.Connection;
            if (connection is null) return new(CloudRegistrationState.Unregistered, null, null, null,
                DiscoveryStatus.NotConfigured, DiscoveryStatus.NotConfigured, null, null, false,
                "No registered PiDockCloud. Direct package upload remains planned.");
            if (!Uri.TryCreate(connection.CloudUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
                !IdentityRules.IsId(connection.TenantId) || !IdentityRules.IsId(connection.DeviceId)) return Unavailable();
            // Project only public target context, never serialize CloudConnection's keys/certificates.
            return new(CloudRegistrationState.Registered, uri.AbsoluteUri, connection.TenantId, connection.DeviceId,
                DiscoveryStatus.NotDiscovered, DiscoveryStatus.NotDiscovered, null, null, false,
                "Saved registration only. Cloud connectivity, publishing endpoints and publisher authority have not been discovered.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Unavailable();
        }
    }

    private static CloudTarget Unavailable() => new(CloudRegistrationState.Unavailable, null, null, null,
        DiscoveryStatus.NotDiscovered, DiscoveryStatus.NotDiscovered, null, null, false,
        "Saved cloud registration cannot be read or validated. Repair it locally; no destination was inferred.");
}
