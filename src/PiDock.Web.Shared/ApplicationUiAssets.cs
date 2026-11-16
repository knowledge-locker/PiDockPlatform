using System.Security.Cryptography;
using System.Text;
using PiDock.Core;

namespace PiDock.Web.Shared;

public static class ApplicationUiAssets
{
    public static readonly string[] Names = ["tokens.css", "components.css"];
    public static string? Read(string name)
    {
        if (!Names.Contains(name, StringComparer.Ordinal)) return null;
        using var stream = typeof(ApplicationUiAssets).Assembly.GetManifestResourceStream("PiDock.Ui.1.0." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public static ApplicationUiDiscovery Discovery() => new(1, ApplicationUiRules.Version, ApplicationUiRules.StyleVersion,
        "isolated-ui-preview", ["GET", "HEAD"],
        ["arbitrary HTTP proxy", "user identity disclosure to apps", "package UI installation", "physical I/O grants"],
        Names.Select(name => new ApplicationUiStyleAsset(name, $"{PlatformContracts.ApiRoot}/ui/styles/1.0/{name}",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Read(name)!))))).ToArray());
}
