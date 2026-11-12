using System.Net.Http.Headers;
using System.Text;
using ModelContextProtocol;
using PiDock.Core;

namespace PiDock.Agent.Mcp;

public sealed class DiscoveryApiClient(HttpClient http) : IDisposable
{
    public static DiscoveryApiClient FromEnvironment()
    {
        var address = Environment.GetEnvironmentVariable("PIDOCK_APPLIANCE_URL");
        var token = Environment.GetEnvironmentVariable("PIDOCK_DISCOVERY_TOKEN");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("PIDOCK_APPLIANCE_URL must be an HTTPS origin.");
        if (token is not { Length: 64 } || !token.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("PIDOCK_DISCOVERY_TOKEN must be a locally provisioned discovery credential.");
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = uri, Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new(client);
    }

    public Task<string> Discover(CancellationToken ct) => Read("discovery", ct);
    public Task<string> Contracts(CancellationToken ct) => Read("contracts", ct);
    public Task<string> Guide(CancellationToken ct) => Read("authoring", ct);
    public Task<string> Schema(string id, CancellationToken ct)
    {
        if (!PlatformContracts.List().Any(c => c.Id == id)) throw new McpException("Unknown PiDock contract.");
        return Read("contracts/" + id, ct);
    }

    private async Task<string> Read(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ct = timeout.Token;
        try
        {
            using var response = await http.GetAsync(PlatformContracts.ApiRoot + "/" + path, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new McpException("PiDock discovery is unavailable or not authorized. Check the target and discovery credential locally.");
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var bytes = new byte[131073]; int length = 0;
            while (length < bytes.Length)
            {
                int count = await stream.ReadAsync(bytes.AsMemory(length), ct);
                if (count == 0) break;
                length += count;
            }
            if (length > 131072) throw new McpException("PiDock discovery response exceeded its limit.");
            return Encoding.UTF8.GetString(bytes, 0, length);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // Do not forward response bodies, URLs, headers or connection exception details to agents.
            throw new McpException("PiDock discovery could not be reached securely within the request deadline.");
        }
    }
    public void Dispose() => http.Dispose();
}
