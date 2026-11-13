using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PiDock.Agent;
using PiDock.Agent.Mcp;
using ModelContextProtocol;
using PiDock.Cloud.Web;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class ApplicationServicesTests
{
    private static ApplicationReleaseRequest Package(string version = "1.0.0") => new(1,
        new(1, "pump", version, "v1", "registry.example.test/pump@sha256:" + new string('a', 64), ["logs.write", "telemetry.write"]), "First release");
    [Fact]
    public async Task CloudPublishingIsImmutableTenantScopedAndDurable()
    {
        using var cloud = new ServiceFactory<PiDock.Cloud.Web.CloudMarker>(true);
        using var publisher = cloud.Client("publisher");
        using var reader = cloud.Client("reader");
        using var other = cloud.Client("other");
        using var anonymous = cloud.Client(null);
        const string path = "/api/applications/v1/releases";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(path, Package())).StatusCode);
        var discovery = await publisher.GetFromJsonAsync<CloudApplicationDiscovery>("/api/applications/v1/discovery");
        Assert.Equal("alpha", discovery!.TenantId);
        Assert.Contains("packages.publish", discovery.PermittedActions);
        var first = await publisher.PostAsJsonAsync(path, Package());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var entry = (await first.Content.ReadFromJsonAsync<PublishedApplicationRelease>())!;
        Assert.Equal("alpha", entry.TenantId);
        Assert.Equal(ApplicationContractRules.Digest(Package()), entry.Digest);
        Assert.Equal(HttpStatusCode.OK, (await publisher.PostAsJsonAsync(path, Package())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await publisher.PostAsJsonAsync(path, Package() with { ReleaseNotes = "changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await publisher.PostAsJsonAsync(path, Package() with { Manifest = Package().Manifest with { Id = "other-app" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await publisher.PostAsJsonAsync(path, Package() with { Manifest = Package().Manifest with { Image = "pump:latest" } })).StatusCode);
        Assert.Single((await reader.GetFromJsonAsync<PublishedApplicationRelease[]>(path))!);
        Assert.Empty((await other.GetFromJsonAsync<PublishedApplicationRelease[]>(path))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(path + "/pump/1.0.0")).StatusCode);
        var downloaded = (await reader.GetFromJsonAsync<PublishedApplicationRelease>(path + "/pump/1.0.0"))!;
        Assert.Equal(entry.Digest, ApplicationContractRules.Digest(downloaded.Package));
        var reloaded = new ApplicationCatalog(new StateDirectory(cloud.Root), TimeProvider.System);
        Assert.Single(reloaded.List("alpha"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await publisher.GetAsync("/api/devices")).StatusCode);
        cloud.Services.GetRequiredService<IConfiguration>()["PiDock:Publishing:Credentials:0:ExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        Assert.Equal(HttpStatusCode.Unauthorized, (await publisher.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task AgentPublishingVerifiesPinnedRegisteredOriginAndTenantBeforeSendingPackage()
    {
        using var cloud = new ServiceFactory<PiDock.Cloud.Web.CloudMarker>(true);
        var target = new PlatformDiscoveryDocument(1, "1.0", "v1", "PiDockOS", "test", "arm64", null,
            new(CloudRegistrationState.Registered, "https://cloud.example.test/", "alpha", "pi4",
                DiscoveryStatus.NotDiscovered, DiscoveryStatus.NotDiscovered, null, null, false, "Saved registration"), [], [], []);
        using var handler = new TargetHandler(target);
        using var api = new DiscoveryApiClient(new HttpClient(handler) { BaseAddress = new("https://appliance.example.test/") });
        var settings = new Dictionary<string, string?> { ["PIDOCK_CLOUD_URL"] = "https://cloud.example.test/", ["PIDOCK_PUBLISHER_TOKEN"] = cloud.Token("publisher") };
        var bridge = new CloudPublishingClient(api, key => settings.GetValueOrDefault(key), () => cloud.Client("publisher"));
        var result = JsonSerializer.Deserialize<PublishedApplicationRelease>(await bridge.Publish(JsonSerializer.Serialize(Package(), StateFiles.Json), default), StateFiles.Json)!;
        Assert.Equal("alpha", result.TenantId);
        Assert.Equal(ApplicationContractRules.Digest(Package()), result.Digest);
        Assert.Contains("packages.publish", await bridge.Discover(default));
        settings["PIDOCK_CLOUD_URL"] = "https://different.example.test/";
        await Assert.ThrowsAsync<McpException>(() => bridge.Publish(JsonSerializer.Serialize(Package("1.0.1"), StateFiles.Json), default));
        settings["PIDOCK_CLOUD_URL"] = "https://cloud.example.test/";
        settings["PIDOCK_PUBLISHER_TOKEN"] = cloud.Token("other");
        await Assert.ThrowsAsync<McpException>(() => bridge.Publish(JsonSerializer.Serialize(Package("1.0.1"), StateFiles.Json), default));
        Assert.Single(cloud.Services.GetRequiredService<ApplicationCatalog>().List("alpha"));
    }
    private sealed class TargetHandler(PlatformDiscoveryDocument target) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(target, options: StateFiles.Json) });
    }

    [Fact]
    public async Task SharedLogsMeasurementsAndAlarmsEnforceSourceAndScopeAcrossRestart()
    {
        using var appliance = new ServiceFactory<PiDock.Appliance.Web.ApplianceMarker>(false);
        using var pump = appliance.Client("publisher");
        using var reader = appliance.Client("reader");
        using var stranger = appliance.Client("other");
        const string root = "/api/apps/v1";
        var log = new ApplicationLogEvent(1, Guid.NewGuid(), "pump-stopped", DateTimeOffset.UtcNow,
            "Information", OperationalPriority.Critical, "Pump stopped", "correlation-one", []);
        var first = await pump.PostAsJsonAsync(root + "/logs", log);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var envelope = (await first.Content.ReadFromJsonAsync<SharedServiceEvent>())!;
        Assert.Equal("pump", envelope.AppId);
        Assert.Equal("Unverified", envelope.ClockState);
        Assert.NotEqual(Guid.Empty, envelope.BootId);
        Assert.Equal(TimeSpan.Zero, envelope.ReceivedAt.Offset);
        Assert.Equal(HttpStatusCode.OK, (await pump.PostAsJsonAsync(root + "/logs", log)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await pump.PostAsJsonAsync(root + "/logs", log with { Message = "changed" })).StatusCode);
        Assert.Single((await reader.GetFromJsonAsync<SharedServiceEvent[]>(root + "/logs"))!);
        Assert.Empty((await stranger.GetFromJsonAsync<SharedServiceEvent[]>(root + "/logs"))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(root + "/logs", log)).StatusCode);
        var alarm = Assert.Single((await reader.GetFromJsonAsync<ApplicationAlarm[]>(root + "/alarms"))!);
        Assert.Equal(OperationalPriority.Critical, alarm.Priority);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsync(root + "/alarms/pump/pump-stopped/acknowledge", null)).StatusCode);
        var ack = await reader.PostAsync(root + "/alarms/pump/pump-stopped/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        var acknowledged = (await ack.Content.ReadFromJsonAsync<ApplicationAlarm>())!;
        Assert.NotNull(acknowledged.AcknowledgedAt); Assert.Null(acknowledged.ResolvedAt);
        var measurement = new ApplicationMeasurement(1, Guid.NewGuid(), "pressure", 42, "kPa", DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await pump.PostAsJsonAsync(root + "/measurements", measurement)).StatusCode);
        Assert.Equal(42, Assert.Single((await reader.GetFromJsonAsync<SharedServiceEvent[]>(root + "/measurements"))!).Payload.GetProperty("value").GetDouble());
        Assert.Empty((await stranger.GetFromJsonAsync<SharedServiceEvent[]>(root + "/measurements"))!);
        var reloaded = new SharedApplicationServices(appliance.Root, appliance.Services.GetRequiredService<HistoryStore>(), TimeProvider.System, new());
        Assert.NotNull(Assert.Single(reloaded.Alarms(["pump"])).AcknowledgedAt);
        Assert.Equal(HttpStatusCode.OK, (await pump.PostAsync(root + "/alarms/pump-stopped/clear", null)).StatusCode);
        Assert.NotNull(Assert.Single((await reader.GetFromJsonAsync<ApplicationAlarm[]>(root + "/alarms"))!).ResolvedAt);
        // Retained event replay must not reopen a resolved alarm.
        await pump.PostAsJsonAsync(root + "/logs", log);
        Assert.NotNull(Assert.Single((await reader.GetFromJsonAsync<ApplicationAlarm[]>(root + "/alarms"))!).ResolvedAt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pump.GetAsync("/api/status")).StatusCode);
        var service = appliance.Services.GetRequiredService<SharedApplicationServices>();
        Assert.Equal(0, service.PendingAlarmTransitions);
        Assert.Equal(3, service.Read("alarm", ["pump"]).Count);
    }

    [Fact]
    public void SourceRetentionAndAlarmOutboxStayBoundedUnderStoragePressure()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-service-limits-" + Guid.NewGuid());
        try
        {
            var probe = new Probe(); var history = new HistoryStore(root, probe);
            var services = new SharedApplicationServices(root, history, TimeProvider.System, new());
            for (int i = 0; i < 135; i++) services.Measure("pump", new(1, Guid.NewGuid(), "pressure", i, "kPa", DateTimeOffset.UtcNow));
            Assert.Equal(128, history.ReadSources<SharedServiceEvent>(HistoryClass.Telemetry, ["pump"], 256).Count);
            Assert.Equal(7, history.Status().DeletedSegments);
            probe.Full = true;
            var alarm = services.Raise("pump", new("offline", OperationalPriority.Warning, "Offline"));
            Assert.Equal(1, services.PendingAlarmTransitions);
            var restarted = new SharedApplicationServices(root, history, TimeProvider.System, new());
            Assert.Equal(alarm.Id, Assert.Single(restarted.Alarms(["pump"])).Id);
            Assert.Equal(1, restarted.PendingAlarmTransitions);
            probe.Full = false;
            restarted.Alarms(["pump"]);
            Assert.Equal(0, restarted.PendingAlarmTransitions);
            for (int i = 0; i < 15; i++) restarted.Raise("pump", new("condition-" + i, OperationalPriority.Alert, "Attention"));
            Assert.Throws<PlatformCapacityException>(() => restarted.Raise("pump", new("too-many", OperationalPriority.Alert, "Attention")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Probe : ICapacityProbe
    {
        public bool Full;
        public StorageCapacity Read(string path, long bytes) => new(32L << 30, Full ? 0 : (16L << 30) - bytes, bytes);
    }

    [Fact]
    public async Task TrustedClockRequirementAppliesToConfiguredBearerCredentials()
    {
        using var appliance=new ServiceFactory<PiDock.Appliance.Web.ApplianceMarker>(false,requireClock:true);
        using var client=appliance.Client("publisher");
        Assert.True(appliance.Services.GetRequiredService<ClockTrust>().Required);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/apps/v1/system")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/platform/v1/discovery")).StatusCode);
    }
    private sealed class ServiceFactory<T>(bool cloud, bool requireClock=false) : WebApplicationFactory<T> where T : class
    {
        public string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-app-api-" + Guid.NewGuid());
        private readonly Dictionary<string, string> tokens = new() { ["publisher"] = NewToken(), ["reader"] = NewToken(), ["other"] = NewToken() };
        private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development").UseSetting("PiDock:StateDirectory", Root).UseSetting("PiDock:RequireTrustedClock",requireClock.ToString()).UseSetting("Logging:LogLevel:Default", "Warning").UseSetting("AllowedHosts", "localhost;cloud.example.test");
            var settings = new Dictionary<string, string?> { ["PiDock:RequireTrustedClock"]=requireClock.ToString() }; int index = 0;
            if(!cloud) {
                settings["PiDock:Discovery:TokenSha256"]=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokens["publisher"])));
                settings["PiDock:Discovery:ExpiresAt"]=DateTimeOffset.UtcNow.AddHours(1).ToString("O");
            }
            foreach (var (id, token) in tokens)
            {
                var prefix = (cloud ? "PiDock:Publishing:Credentials:" : "PiDock:Apps:Credentials:") + index++ + ":";
                settings[prefix + "Id"] = id;
                settings[prefix + "Subject"] = id == "publisher" ? "pump" : id;
                settings[prefix + "TenantId"] = id == "other" ? "other" : "alpha";
                settings[prefix + "ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
                settings[prefix + "TokenSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
                string[] scopes = cloud ? id == "publisher" ? ["packages.read", "packages.publish"] : ["packages.read"]
                    : id == "publisher" ? ["logs.read", "logs.write", "telemetry.read", "telemetry.write", "alarms.read", "alarms.raise"]
                    : ["logs.read", "telemetry.read", "alarms.read", "alarms.acknowledge"];
                for (int i = 0; i < scopes.Length; i++) settings[prefix + "Scopes:" + i] = scopes[i];
                if (!cloud && id == "reader") settings[prefix + "ReadSources:0"] = "pump";
                if (!cloud && id == "publisher") settings[prefix + "AlarmPriorities:0"] = "1";
            }
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        }
        public string Token(string id) => tokens[id];
        public HttpClient Client(string? id)
        {
            var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            if (id is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens[id]);
            return client;
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing && Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
