using System.Net;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PiDock.Agent.Mcp;

namespace PiDock.Tests;

public sealed class PlatformMcpTests
{
    [Fact]
    public async Task StdioServerNegotiatesAndSeparatesDiscoveryFromPublishing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var transport = new StdioClientTransport(new()
        {
            Name = "PiDock discovery test", Command = "dotnet",
            Arguments = [typeof(DiscoveryTools).Assembly.Location],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["PIDOCK_APPLIANCE_URL"] = "https://localhost:1/",
                ["PIDOCK_DISCOVERY_TOKEN"] = new string('A', 64)
            }
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(["pidock_cloud_catalog", "pidock_cloud_discover", "pidock_discover", "pidock_list_contracts", "pidock_publish_release"], tools.Select(t => t.Name).Order().ToArray());
        var resources = await client.ListResourcesAsync(cancellationToken: timeout.Token);
        Assert.Contains(resources, r => r.Uri == "pidock://platform/discovery");
        Assert.Contains(resources, r => r.Uri == "pidock://platform/authoring");
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: timeout.Token);
        Assert.Contains(templates, t => t.UriTemplate == "pidock://platform/contracts/{id}");
        var guide = await client.ReadResourceAsync("pidock://platform/authoring", cancellationToken: timeout.Token);
        Assert.Contains("engineering preview", Assert.IsType<TextResourceContents>(Assert.Single(guide.Contents)).Text);
        var schema = await client.ReadResourceAsync("pidock://platform/contracts/application-manifest", cancellationToken: timeout.Token);
        Assert.Contains("urn:pidock:contracts:1.0:application-manifest", Assert.IsType<TextResourceContents>(Assert.Single(schema.Contents)).Text);
        // A transport/network error must be surfaced, never substituted with fake target context.
        var result = await client.CallToolAsync("pidock_discover", cancellationToken: timeout.Token);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task BridgeUsesOnlyFixedDiscoveryPathsAndHidesRemoteErrors()
    {
        using var handler = new RecordingHandler();
        using var api = new DiscoveryApiClient(new HttpClient(handler) { BaseAddress = new("https://appliance.example.test/") });
        Assert.Equal("{}", await api.Discover(default));
        Assert.Equal("/api/platform/v1/discovery", handler.LastPath);
        await api.Schema("application-log-event", default);
        Assert.Equal("/api/platform/v1/contracts/application-log-event", handler.LastPath);
        var calls = handler.Calls;
        await Assert.ThrowsAsync<McpException>(() => api.Schema("../../auth/login", default));
        Assert.Equal(calls, handler.Calls);
        handler.Status = HttpStatusCode.Redirect;
        var error = await Assert.ThrowsAsync<McpException>(() => api.Discover(default));
        Assert.DoesNotContain("REMOTE-SECRET", error.Message);
        handler.Status = HttpStatusCode.OK;
        handler.Body = new string('x', 131073);
        await Assert.ThrowsAsync<McpException>(() => api.Discover(default));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? LastPath;
        public int Calls;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = "{}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            LastPath = request.RequestUri!.AbsolutePath; Calls++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Status == HttpStatusCode.OK ? Body : "REMOTE-SECRET") });
        }
    }
}
