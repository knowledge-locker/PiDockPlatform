using System.Net;
using Microsoft.Extensions.Configuration;
using PiDock.Core;

namespace PiDock.Web.Shared;

public sealed record ApplicationUiRegistration(ApplicationUiContribution Contribution, string ServiceOrigin, bool Enabled = true, string[]? PublicAssets = null, ApplicationUiBridgeGrant? Bridge = null);

// Host-owned configuration, never app-supplied request parameters. No registration mutation API in v1 preview.
public sealed class ApplicationUiRegistry(IConfiguration configuration)
{
    public ApplicationUiRegistration[] Read()
    {
        ApplicationUiRegistration[] registrations;
        try { registrations = configuration.GetSection("PiDock:ApplicationUi:Registrations").Get<ApplicationUiRegistration[]>() ?? []; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return []; }
        if (registrations.Length > 16 || registrations.Any(r => r is null || !ApplicationUiRules.Valid(r.Contribution) || !ValidOrigin(r.ServiceOrigin) || !ApplicationUiBridgeRules.Grant(r.Bridge) ||
                (r.Bridge is not null && (r.PublicAssets ?? []).Any(p => p == r.Bridge.CommandsPath || p == r.Bridge.StreamPath)) || (r.PublicAssets?.Length ?? 0) > 64 || (r.PublicAssets ?? []).Any(p => !ApplicationUiRules.SafePath(p) || !PublicAssetPath(p))) ||
            registrations.GroupBy(r => (r.Contribution.AppId, r.Contribution.Id)).Any(g => g.Count() != 1)) return [];
        return registrations.Where(r => r.Enabled).ToArray();
    }
    public ApplicationUiRegistration? Find(string appId, string id) => Read().SingleOrDefault(r => r.Contribution.AppId == appId && r.Contribution.Id == id);
    public static bool PublicAssetPath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".css" or ".js" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".ico" or ".woff" or ".woff2";
    public static bool PublicAssetType(string path, string type) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".css" => type == "text/css", ".js" => type is "text/javascript" or "application/javascript",
        ".png" => type == "image/png", ".jpg" or ".jpeg" => type == "image/jpeg", ".webp" => type == "image/webp",
        ".ico" => type == "image/x-icon", ".woff" => type == "font/woff", ".woff2" => type == "font/woff2", _ => false
    };
    public static bool ValidOrigin(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
        uri.Scheme == "http" && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0 &&
        uri.Port is >= 1024 and <= 65535 && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
}
