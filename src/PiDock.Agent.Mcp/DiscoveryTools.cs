using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PiDock.Core;

namespace PiDock.Agent.Mcp;

[McpServerToolType]
public sealed class DiscoveryTools(DiscoveryApiClient api)
{
    [McpServerTool(Name = "pidock_discover", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read PiDockOS capabilities and saved registered PiDockCloud URL. Publishing permission is never inferred from registration.")]
    public Task<string> Discover(CancellationToken cancellationToken) => api.Discover(cancellationToken);

    [McpServerTool(Name = "pidock_list_contracts", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List versioned PiDockOS contract schemas and their draft/preview status.")]
    public Task<string> Contracts(CancellationToken cancellationToken) => api.Contracts(cancellationToken);
}

[McpServerResourceType]
public sealed class DiscoveryResources(DiscoveryApiClient api)
{
    [McpServerResource(UriTemplate = "pidock://platform/app-runtime-access", Name = "PiDockOS lifecycle-bound app runtime authority", MimeType = "application/json")]
    public Task<string> AppRuntimeAccess(CancellationToken ct) => api.AppRuntimeAccess(ct);

    [McpServerResource(UriTemplate = "pidock://platform/app-diagnostics", Name = "PiDockOS app diagnostics and local notification support", MimeType = "application/json")]
    public Task<string> AppDiagnostics(CancellationToken ct) => api.AppDiagnostics(ct);

    [McpServerResource(UriTemplate = "pidock://platform/app-removal", Name = "PiDockOS managed app removal support", MimeType = "application/json")]
    public Task<string> AppRemoval(CancellationToken ct) => api.AppRemoval(ct);

    [McpServerResource(UriTemplate = "pidock://platform/app-gpio-readiness", Name = "PiDockOS physical GPIO readiness evidence", MimeType = "application/json")]
    public Task<string> AppGpioReadiness(CancellationToken ct) => api.AppGpioReadiness(ct);

    [McpServerResource(UriTemplate = "pidock://platform/app-storage", Name = "PiDockOS app storage admission support", MimeType = "application/json")]
    public Task<string> AppStorage(CancellationToken ct) => api.AppStorage(ct);

    [McpServerResource(UriTemplate = "pidock://platform/app-install-trust", Name = "PiDockOS exact app image approval support", MimeType = "application/json")]
    public Task<string> AppInstallTrust(CancellationToken ct) => api.AppInstallTrust(ct);

    [McpServerResource(UriTemplate = "pidock://platform/app-io", Name = "PiDockOS app I/O adapter support", MimeType = "application/json")]
    public Task<string> AppIo(CancellationToken ct) => api.AppIo(ct);

    [McpServerResource(UriTemplate = "pidock://platform/app-engineering", Name = "PiDockOS variable engineering support", MimeType = "application/json")]
    public Task<string> AppEngineering(CancellationToken ct) => api.AppEngineering(ct);

    [McpServerResource(UriTemplate = "pidock://platform/app-backups", Name = "PiDockOS app data backup and restore support", MimeType = "application/json")]
    public Task<string> AppBackups(CancellationToken ct) => api.AppBackups(ct);

    [McpServerResource(UriTemplate = "pidock://platform/discovery", Name = "PiDockOS target", MimeType = "application/json")]
    public Task<string> Discovery(CancellationToken cancellationToken) => api.Discover(cancellationToken);

    [McpServerResource(UriTemplate = "pidock://platform/app-updates", Name = "PiDockOS app image updates and rollback", MimeType = "application/json")]
    public Task<string> AppUpdates(CancellationToken ct)=>api.AppUpdates(ct);

    [McpServerResource(UriTemplate = "pidock://platform/app-orchestration", Name = "PiDockOS app dependency ordering and coordinated shutdown", MimeType = "application/json")]
    public Task<string> AppOrchestration(CancellationToken ct)=>api.AppOrchestration(ct);

    [McpServerResource(UriTemplate = "pidock://platform/app-health/reader.c", Name = "PiDockOS fixed health reader source", MimeType = "text/x-c")]
    public static string HealthReaderSource() => ApplicationHealthAssets.ReaderSource;

    [McpServerResource(UriTemplate = "pidock://platform/app-health", Name = "PiDockOS app health and readiness reporting", MimeType = "application/json")]
    public Task<string> AppHealth(CancellationToken ct) => api.AppHealth(ct);

    [McpServerResource(UriTemplate = "pidock://platform/app-lifecycle", Name = "PiDockOS app lifecycle and recovery support", MimeType = "application/json")]
    public Task<string> AppLifecycle(CancellationToken ct) => api.AppLifecycle(ct);

    [McpServerResource(UriTemplate = "pidock://platform/app-resources", Name = "PiDockOS managed app data and credential delivery", MimeType = "application/json")]
    public Task<string> AppResources(CancellationToken ct) => api.AppResources(ct);

    [McpServerResource(UriTemplate = "pidock://platform/ui/bridge", Name = "PiDockOS authenticated app commands and live streaming", MimeType = "application/json")]
    public Task<string> UiBridge(CancellationToken cancellationToken) => api.UiBridge(cancellationToken);

    [McpServerResource(UriTemplate = "pidock://platform/ui", Name = "PiDockOS UI and styling support", MimeType = "application/json")]
    public Task<string> Ui(CancellationToken cancellationToken) => api.Ui(cancellationToken);

    [McpServerResource(UriTemplate = "pidock://platform/authoring", Name = "PiDockOS bundled authoring guide", MimeType = "text/plain")]
    public static string Authoring() => PlatformContracts.AuthoringGuide;

    [McpServerResource(UriTemplate = "pidock://platform/contracts/{id}", Name = "PiDockOS bundled contract schema", MimeType = "application/schema+json")]
    public static string Contract(string id) => PlatformContracts.Schema(id)?.ToJsonString()
        ?? throw new McpException("Unknown PiDock contract.");
}

[McpServerToolType]
public sealed class CloudPublishingTools(CloudPublishingClient cloud)
{
    [McpServerTool(Name = "pidock_cloud_discover", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Authenticate to the registered, pinned PiDockCloud and discover tenant publishing/catalog permissions. Requires separately configured publisher credentials.")]
    public Task<string> Discover(CancellationToken cancellationToken) => cloud.Discover(cancellationToken);

    [McpServerTool(Name = "pidock_cloud_catalog", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Read the registered PiDockCloud tenant's latest 100 application releases. Does not install apps.")]
    public Task<string> Catalog(CancellationToken cancellationToken) => cloud.Catalog(cancellationToken);

    [McpServerTool(Name = "pidock_publish_release", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Publish immutable registry-reference package JSON to the registered and explicitly pinned PiDockCloud. Reusing a version with changed content is rejected. This does not deploy or run an app.")]
    public Task<string> Publish(string packageJson, CancellationToken cancellationToken) => cloud.Publish(packageJson, cancellationToken);
}
