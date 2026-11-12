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
    [McpServerResource(UriTemplate = "pidock://platform/discovery", Name = "PiDockOS target", MimeType = "application/json")]
    public Task<string> Discovery(CancellationToken cancellationToken) => api.Discover(cancellationToken);

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
