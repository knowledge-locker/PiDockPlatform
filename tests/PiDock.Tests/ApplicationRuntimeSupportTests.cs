using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationRuntimeSupportTests
{
    [Fact]
    public async Task AppSupportRequiresSystemReadAndDoesNotGrantDiscoveryOrLifecycleMutation()
    {
        var root = StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pdrs-" + Guid.NewGuid().ToString("N")[..12]));
        var socket = Path.Combine(root, "b.sock");
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socket));
        await using var broker = builder.Build();
        broker.MapGet("/applications/resources", () => new ApplicationResourceSupport(1, true, ApplicationResourceRules.DataDirectory, ApplicationResourceRules.CredentialDirectory, "pidock-stdin-v1", []));
        broker.MapGet("/applications/health/support", () => new ApplicationHealthSupport(1, true, "pidock-heartbeat-v1", ApplicationHealthRules.ReportPath, ApplicationHealthRules.ProbePath, 5, 1024, [], ApplicationHealthAssets.SourcePath, ApplicationHealthAssets.ReaderSha256));
        broker.MapGet("/applications/lifecycle/support", () => new ApplicationLifecycleSupport(1, true, 5, 60, "host", []));
        broker.MapGet("/applications/orchestration/support", () => new ApplicationOrchestrationSupport(1, true, 16, 8, 180, []));
        await broker.StartAsync();
        try
        {
            using var factory = new Factory(root, socket);
            using var anonymous = factory.Client(null); using var reader = factory.Client("reader"); using var runtime = factory.Client("runtime");
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(ApplicationRuntimeSupportClient.Path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await runtime.GetAsync(ApplicationRuntimeSupportClient.Path)).StatusCode);
            using var response = await reader.GetAsync(ApplicationRuntimeSupportClient.Path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("deliveryId", body); Assert.DoesNotContain(factory.Token("reader"), body);
            var support = (await response.Content.ReadFromJsonAsync<ApplicationRuntimeSupport>())!;
            Assert.True(support.Resources.Enabled); Assert.True(support.Health.Enabled); Assert.False(support.Io.Enabled); Assert.False(support.Io.Physical);
            Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/api/platform/v1/app-health")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await reader.PostAsync(ApplicationRuntimeSupportClient.Path, null)).StatusCode);
            Assert.DoesNotContain("deliveryId", PlatformContracts.Schema("application-runtime-support")!.ToJsonString());
            factory.Services.GetRequiredService<IConfiguration>()["PiDock:Apps:Credentials:0:ExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
            Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync(ApplicationRuntimeSupportClient.Path)).StatusCode);
        }
        finally { await broker.StopAsync(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task UnreachableBrokerNeverInventsRuntimeSupport()
    {
        var root = StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pdrs-" + Guid.NewGuid().ToString("N")[..12]));
        try
        {
            using var factory = new Factory(root, Path.Combine(root, "missing.sock")); using var reader = factory.Client("reader");
            using var response = await reader.GetAsync(ApplicationRuntimeSupportClient.Path); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Factory(string root, string socket) : WebApplicationFactory<PiDock.Appliance.Web.ApplianceMarker>
    {
        private readonly Dictionary<string, string> tokens = new() { ["reader"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), ["runtime"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
        public string Token(string id) => tokens[id];
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development").UseSetting("PiDock:StateDirectory", Path.Combine(root, "state"))
                .UseSetting("Logging:LogLevel:Default", "Warning");
            var settings = new Dictionary<string, string?> { ["Broker:SocketPath"] = socket, ["PiDock:RequireTrustedClock"] = "false" }; int index = 0;
            foreach (var (id, token) in tokens)
            {
                var prefix = "PiDock:Apps:Credentials:" + index++ + ":";
                settings[prefix + "Id"] = id; settings[prefix + "Subject"] = "test-app";
                settings[prefix + "ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
                settings[prefix + "TokenSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
                settings[prefix + "Scopes:0"] = id == "reader" ? "system.read" : "signals.runtime";
            }
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        }
        public HttpClient Client(string? id)
        {
            var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            if (id is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens[id]);
            return client;
        }
    }
}
