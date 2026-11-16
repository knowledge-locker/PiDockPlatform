namespace PiDock.Core;

// Separate from package manifests: registration is a local host configuration grant in this preview.
public sealed record ApplicationUiContribution(int SchemaVersion, string AppId, string Id, string Title,
    string EntryPath, string RequiredUiVersion, string RequiredStyleVersion);
public sealed record ApplicationUiStyleAsset(string Name, string Path, string Sha256);
public sealed record ApplicationUiDiscovery(int SchemaVersion, string UiVersion, string StyleVersion,
    string HostingMode, string[] SupportedMethods, string[] PlannedFeatures, ApplicationUiStyleAsset[] Assets);

public static class ApplicationUiRules
{
    public const string Version = "1.0";
    public const string StyleVersion = "1.0";
    public static bool SafePath(string? path) => path is { Length: > 0 and <= 512 } &&
        !path.StartsWith('/') && path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not ".." &&
            segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    public static bool Valid(ApplicationUiContribution? value) => value is not null && value.SchemaVersion == 1 &&
        IdentityRules.IsId(value.AppId) && IdentityRules.IsId(value.Id) &&
        value.Title is { Length: > 0 and <= 80 } && !string.IsNullOrWhiteSpace(value.Title) && !value.Title.Any(char.IsControl) &&
        SafePath(value.EntryPath) && value.RequiredUiVersion == Version && value.RequiredStyleVersion == StyleVersion;
    public static string LaunchPath(ApplicationUiContribution value) => $"/apps/{value.AppId}/{value.Id}";
    public static string ContentPath(ApplicationUiContribution value) => $"/app-ui/{value.AppId}/{value.Id}/{value.EntryPath}";
}
