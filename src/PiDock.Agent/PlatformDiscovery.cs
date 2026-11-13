using System.Runtime.InteropServices;
using System.Text.Json;
using PiDock.Core;

namespace PiDock.Agent;

public sealed class PlatformDiscovery(CloudEnrollmentClient enrollment, GpioSimulator gpio)
{
    public PlatformDiscoveryDocument Read() => new(1, PlatformContracts.Version, PlatformContracts.ApiVersion,
        "PiDockOS", "engineering-preview", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        null, ReadCloud(),
        [
            new("platform.discovery", CapabilityState.Available, "Read-only target and registered-cloud context.", ["discovery.read"]),
            new("platform.contracts", CapabilityState.Available, "Versioned schemas and authoring guidance.", ["contracts.read"]),
            new("gpio.control", CapabilityState.Planned, "Local-owner Pi 5 GPIO maintenance is separate; GPIO access and leases for apps are not implemented.", []),
            new("gpio.simulation", gpio.Enabled ? CapabilityState.Simulated : CapabilityState.Planned,
                gpio.Enabled ? "Development UI simulation enabled; no app I/O endpoint." : "Development simulation is disabled.", []),
            new("hardware.buses", CapabilityState.Planned, "App I2C/SPI/PWM/serial/USB interfaces are not implemented.", []),
            new("video.channels", CapabilityState.Planned, "Camera and display channel allocation is not implemented.", []),
            new("system.resources", CapabilityState.Available, "Scoped app system reads.", ["system.read"]),
            new("network.configuration", CapabilityState.Planned, "App Wi-Fi/network configuration and rollback are not implemented.", []),
            new("telemetry.exchange", CapabilityState.Available, "Scoped typed app measurements and bounded polling; streaming remains planned.", ["telemetry.read", "telemetry.write"]),
            new("services.discovery", CapabilityState.Planned, "App-to-app service exports and grants are not implemented.", []),
            new("logging.central", CapabilityState.Available, "Shared structured app/PiDock host journal; Docker stdout collection remains planned.", ["logs.read", "logs.write"]),
            new("alarms.shared", CapabilityState.Available, "Durable app raise/update/acknowledge/clear and priority-triggered local conditions; notifications remain planned.", ["alarms.read", "alarms.raise", "alarms.acknowledge"]),
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
