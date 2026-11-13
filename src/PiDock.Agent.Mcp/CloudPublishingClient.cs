using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;
using PiDock.Core;

namespace PiDock.Agent.Mcp;

public sealed class CloudPublishingClient(DiscoveryApiClient appliance, Func<string, string?>? settings = null, Func<HttpClient>? clientFactory = null)
{
    private async Task<(HttpClient Client, CloudApplicationDiscovery Discovery)> Connect(CancellationToken ct)
    {
        var target = JsonSerializer.Deserialize<PlatformDiscoveryDocument>(await appliance.Discover(ct), StateFiles.Json)
            ?? throw new McpException("Invalid appliance discovery.");
        var readSetting = settings ?? Environment.GetEnvironmentVariable;
        var expected = readSetting("PIDOCK_CLOUD_URL");
        var token = readSetting("PIDOCK_PUBLISHER_TOKEN");
        if (target.Cloud.Registration != CloudRegistrationState.Registered ||
            !Uri.TryCreate(target.Cloud.BaseUrl, UriKind.Absolute, out var registered) ||
            !Uri.TryCreate(expected, UriKind.Absolute, out var pinned) || registered != pinned ||
            registered.Scheme != "https" || registered.AbsolutePath != "/" || registered.UserInfo.Length != 0 ||
            registered.Query.Length != 0 || registered.Fragment.Length != 0 || token is not { Length: 64 } || !token.All(char.IsAsciiHexDigit))
            throw new McpException("Cloud publishing requires a registered target, matching PIDOCK_CLOUD_URL and a separate publisher credential.");
        var client = clientFactory?.Invoke() ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        client.BaseAddress = registered; client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            var json = await Send(client, HttpMethod.Get, "/api/applications/v1/discovery", null, ct);
            var discovery = JsonSerializer.Deserialize<CloudApplicationDiscovery>(json, StateFiles.Json);
            if (discovery is null || discovery.SchemaVersion != 1 || discovery.TenantId != target.Cloud.TenantId ||
                discovery.PublishingEndpoint != "/api/applications/v1/releases" || discovery.CatalogEndpoint != "/api/applications/v1/releases" ||
                discovery.PackageFormat != "registry-reference-v1" || discovery.PermittedActions is null)
                throw new McpException("Cloud publishing discovery does not match the registered tenant or supported endpoints.");
            return (client, discovery);
        }
        catch { client.Dispose(); throw; }
    }
    public async Task<string> Discover(CancellationToken ct)
    {
        try
        {
            var (client, discovery) = await Connect(ct); using (client)
                return JsonSerializer.Serialize(new { cloudUrl = client.BaseAddress!.AbsoluteUri, discovery }, StateFiles.Json);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or OperationCanceledException)
        { throw new McpException("Authenticated cloud discovery failed. Check registration, trust and publisher configuration locally."); }
    }
    public async Task<string> Catalog(CancellationToken ct) => await Execute(null, ct);
    public async Task<string> Publish(string packageJson, CancellationToken ct)
    {
        if (packageJson.Length > 16384) throw new McpException("Package metadata exceeds its limit.");
        ApplicationReleaseRequest package;
        try
        {
            package = JsonSerializer.Deserialize<ApplicationReleaseRequest>(packageJson, StateFiles.Json) ?? throw new ArgumentException();
            ApplicationContractRules.Validate(package);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { throw new McpException("Invalid registry-reference package contract."); }
        return await Execute(package, ct);
    }
    private async Task<string> Execute(ApplicationReleaseRequest? package, CancellationToken ct)
    {
        try
        {
            var (client, discovery) = await Connect(ct); using (client)
            {
                var scope = package is null ? "packages.read" : "packages.publish";
                if (!discovery.PermittedActions.Contains(scope)) throw new McpException("The publisher credential does not grant this action.");
                return await Send(client, package is null ? HttpMethod.Get : HttpMethod.Post,
                    package is null ? discovery.CatalogEndpoint : discovery.PublishingEndpoint, package, ct);
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or OperationCanceledException)
        { throw new McpException("Cloud catalog request failed. No alternate destination was used."); }
    }
    private static async Task<string> Send(HttpClient client, HttpMethod method, string path, ApplicationReleaseRequest? package, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15)); ct = timeout.Token;
        using var request = new HttpRequestMessage(method, path);
        if (package is not null) request.Content = JsonContent.Create(package, options: StateFiles.Json);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new McpException($"Cloud request rejected (HTTP {(int)response.StatusCode}); retry the same immutable package after checking authorization/capacity.");
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[1048577]; int length = 0;
        while (length < bytes.Length)
        {
            int count = await stream.ReadAsync(bytes.AsMemory(length), ct); if (count == 0) break; length += count;
        }
        if (length > 1048576) throw new McpException("Cloud response exceeded its limit.");
        return System.Text.Encoding.UTF8.GetString(bytes, 0, length);
    }
}
