using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class PlatformDiscoveryTests
{
    [Fact]
    public async Task DiscoveryRequiresItsOwnCredentialAndCannotGrantOwnerAccess()
    {
        using var factory = new DiscoveryFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        foreach (var path in new[] { "discovery", "contracts", "contracts/application-manifest", "authoring" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(PlatformContracts.ApiRoot + "/" + path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token);
        var response = await client.GetAsync(PlatformContracts.ApiRoot + "/discovery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var discovery = (await response.Content.ReadFromJsonAsync<PlatformDiscoveryDocument>())!;
        Assert.Equal(CloudRegistrationState.Unregistered, discovery.Cloud.Registration);
        Assert.Null(discovery.Cloud.BaseUrl);
        Assert.False(discovery.Cloud.CanPublish);
        Assert.Equal(["discovery.read", "contracts.read"], discovery.PermittedActions);
        Assert.All(discovery.Capabilities.Where(c => c.State != CapabilityState.Available), c => Assert.Empty(c.Operations));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(PlatformContracts.ApiRoot + "/contracts/unknown")).StatusCode);
        using var plain = factory.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        plain.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await plain.GetAsync(PlatformContracts.ApiRoot + "/discovery")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(PlatformContracts.ApiRoot + "/discovery")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token);
        factory.Services.GetRequiredService<IConfiguration>()["PiDock:Discovery:ExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(PlatformContracts.ApiRoot + "/discovery")).StatusCode);
    }

    [Fact]
    public async Task SavedCloudUrlIsProjectedWithoutKeysGuessedEndpointsOrConnectivityClaims()
    {
        using var factory = new DiscoveryFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token);
        var connection = new CloudConnection("https://cloud.example.test:8443/", "tenant-one", "pi4",
            "PRIVATE-CERT-SENTINEL", "PRIVATE-KEY-SENTINEL", "PRIVATE-CA-SENTINEL", "broker.example.test", 8883, DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(factory.Root, "cloud-connection.json");
        StateFiles.WriteAtomic(path, JsonSerializer.Serialize(connection, StateFiles.Json));
        var response = await client.GetAsync(PlatformContracts.ApiRoot + "/discovery");
        var raw = await response.Content.ReadAsStringAsync();
        var discovery = JsonSerializer.Deserialize<PlatformDiscoveryDocument>(raw, StateFiles.Json)!;
        Assert.Equal(CloudRegistrationState.Registered, discovery.Cloud.Registration);
        Assert.Equal(connection.CloudUrl, discovery.Cloud.BaseUrl);
        Assert.Equal(connection.TenantId, discovery.Cloud.TenantId);
        Assert.Equal(DiscoveryStatus.NotDiscovered, discovery.Cloud.Connectivity);
        Assert.Equal(DiscoveryStatus.NotDiscovered, discovery.Cloud.Publishing);
        Assert.Null(discovery.Cloud.PublishingEndpoint);
        Assert.Null(discovery.Cloud.CatalogEndpoint);
        Assert.False(discovery.Cloud.CanPublish);
        Assert.DoesNotContain("PRIVATE-", raw);
        Assert.DoesNotContain("broker.example.test", raw);
        StateFiles.WriteAtomic(path, JsonSerializer.Serialize(connection with { CloudUrl = "https://user:password@example.test/?token=secret" }, StateFiles.Json));
        var invalid = await client.GetFromJsonAsync<PlatformDiscoveryDocument>(PlatformContracts.ApiRoot + "/discovery");
        Assert.Equal(CloudRegistrationState.Unavailable, invalid!.Cloud.Registration);
        Assert.Null(invalid.Cloud.BaseUrl);
        StateFiles.WriteAtomic(path, "{invalid");
        Assert.Equal(CloudRegistrationState.Unavailable, (await client.GetFromJsonAsync<PlatformDiscoveryDocument>(PlatformContracts.ApiRoot + "/discovery"))!.Cloud.Registration);
        StateFiles.WriteAtomic(path, "null");
        Assert.Equal(CloudRegistrationState.Unregistered, (await client.GetFromJsonAsync<PlatformDiscoveryDocument>(PlatformContracts.ApiRoot + "/discovery"))!.Cloud.Registration);
    }

    [Fact]
    public async Task SchemasPreserveNumericPriorityAndAdvertisePreviewContracts()
    {
        using var factory = new DiscoveryFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token);
        var list = await client.GetFromJsonAsync<ContractDescriptor[]>(PlatformContracts.ApiRoot + "/contracts");
        Assert.Contains(list!, c => c.Id == "application-manifest" && c.Status == "preview");
        foreach (var contract in list!)
        {
            var response = await client.GetAsync(contract.SchemaPath);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/schema+json", response.Content.Headers.ContentType!.MediaType);
            using var schema = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
        }
        var logSchema = PlatformContracts.Schema("application-log-event")!;
        Assert.Equal("[1,2,3,4,null]", logSchema["properties"]!["priority"]!["enum"]!.ToJsonString());
        var log = new ApplicationLogEvent(1, Guid.NewGuid(), "pump-stopped", DateTimeOffset.UtcNow,
            "Information", OperationalPriority.Critical, "Stopped", null, []);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(log, StateFiles.Json));
        Assert.Equal(1, json.RootElement.GetProperty("priority").GetInt32());
        Assert.Equal(4, (int)OperationalPriority.Notice);
    }

    [Theory]
    [InlineData("Owner", HttpStatusCode.OK)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task OwnerSessionIsSupportedWithoutGivingOtherRolesDiscoveryAuthority(string role, HttpStatusCode expected)
    {
        using var factory = new DiscoveryFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var secret = Totp.NewSecret();
        factory.Services.GetRequiredService<UserStore>().Add("discovery-user", "integration-test-password", secret, "local", role, role == "Owner", -1);
        var html = await client.GetStringAsync("/auth/login");
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "discovery-user", ["password"] = "integration-test-password",
            ["code"] = Totp.Code(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal(expected, (await client.GetAsync(PlatformContracts.ApiRoot + "/discovery")).StatusCode);
        // Explicit bad credentials must not silently downgrade to the owner's existing cookie.
        client.DefaultRequestHeaders.Authorization = new("Bearer", new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(PlatformContracts.ApiRoot + "/discovery")).StatusCode);
    }

    private sealed class DiscoveryFactory : WebApplicationFactory<PiDock.Appliance.Web.ApplianceMarker>
    {
        public string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-discovery-" + Guid.NewGuid());
        public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development")
            .UseSetting("PiDock:StateDirectory", Root).UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting("PiDock:Discovery:TokenSha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Token))))
            .UseSetting("PiDock:Discovery:ExpiresAt", DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing && Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
