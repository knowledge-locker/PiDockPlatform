using System.Security.Cryptography;
using System.Text;
using PiDock.Core;

namespace PiDock.Web.Shared;

public static class ApplicationUiBridgeAssets
{
    public static readonly string[] Names = ["host.js", "client.js"];
    public static string? Read(string name)
    {
        if (!Names.Contains(name, StringComparer.Ordinal)) return null;
        using var stream = typeof(ApplicationUiBridgeAssets).Assembly.GetManifestResourceStream("PiDock.UiBridge.1.0." + name)!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public static ApplicationUiBridgeDiscovery Discovery() => new(1, "1.0", "private-message-channel/blazor-circuit/ndjson",
        ApplicationUiBridgeRules.MaximumCommandBytes, ApplicationUiBridgeRules.MaximumEventBytes,
        Names.Select(name => new ApplicationUiBridgeAsset(name, $"{PlatformContracts.ApiRoot}/ui/bridge/1.0/{name}",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Read(name)!))), "text/javascript")).ToArray(),
        ["Owner sessions only", "Explicit per-contribution commands and stream grant", "Server-side expiring app credential",
         "No automatic command retry; uncertain outcomes must be reconciled", "No package-driven registration or physical I/O grant"]);
}
