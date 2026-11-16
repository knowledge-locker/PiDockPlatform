using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PiDock.Agent;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationDiagnosticsTests
{
    [Fact]
    public void PriorityPoliciesAndReceiptsSurviveRestartWithoutAcknowledgingTheAlarm()
    {
        using var fixture = new Fixture(); var service = fixture.Create();
        var alarm = service.Raise("pump", new("hot", OperationalPriority.Critical, "Hot"));
        service.Raise("pump", new("ordinary", OperationalPriority.Notice, "Notice"));
        service.Raise("other", new("hot", OperationalPriority.Critical, "Other"));
        var notification = Assert.Single(service.Notifications(["pump"]));
        Assert.Equal(alarm.Id, notification.AlarmId); Assert.Null(notification.ReadAt);
        Assert.Empty(service.Notifications(["other"]));
        service.Raise("pump", new("hot", OperationalPriority.Critical, "Hot"));
        Assert.Single(service.Notifications(["pump"]));
        Assert.Null(service.AcknowledgeNotification(["other"], notification.Id, "other-reader"));
        var receipt = service.AcknowledgeNotification(["pump"], notification.Id, "reader")!;
        Assert.NotNull(receipt.ReadAt); Assert.Equal("reader", receipt.ReadBy);
        Assert.Equal(receipt, service.AcknowledgeNotification(["pump"], notification.Id, "another-reader"));
        Assert.Null(service.Alarms(["pump"]).Single(a => a.Id == alarm.Id).AcknowledgedAt);
        var restarted = fixture.Create(); Assert.Equal(receipt, Assert.Single(restarted.Notifications(["pump"])));
        var disabled = new SharedApplicationServices(fixture.Root, fixture.History, TimeProvider.System, new());
        Assert.False(disabled.DiagnosticsSupport().LocalNotificationsEnabled);
        Assert.Single(disabled.Notifications(["pump"]));
        disabled.Raise("pump", new("disabled", OperationalPriority.Critical, "No new notification"));
        Assert.Single(disabled.Notifications(["pump"]));
    }

    [Fact]
    public void FullInboxRejectsAlarmChangeAtomicallyAndRecordsPressureWithoutDroppingUnreadEntries()
    {
        using var fixture = new Fixture(); var service = fixture.Create();
        for (int index = 0; index < ApplicationNotificationRules.MaximumUnreadPerApp; index++)
            service.Raise("pump", new("hot", OperationalPriority.Warning, "Version " + index));
        var before = Assert.Single(service.Alarms(["pump"]));
        Assert.Throws<PlatformCapacityException>(() => service.Raise("pump", new("hot", OperationalPriority.Critical, "Rejected change")));
        Assert.Equal(before, Assert.Single(service.Alarms(["pump"])));
        Assert.Equal(ApplicationNotificationRules.MaximumUnreadPerApp, service.Notifications(["pump"]).Length);
        Assert.Equal(1, fixture.Create().DiagnosticsSupport().RejectedNotifications);
        var entry = service.Notifications(["pump"])[0]; service.AcknowledgeNotification(["pump"], entry.Id, "reader");
        service.Raise("pump", new("hot", OperationalPriority.Critical, "Accepted after receipt"));
        Assert.Equal("Accepted after receipt", Assert.Single(service.Alarms(["pump"])).Message);
        Assert.Equal(32, service.Notifications(["pump"]).Count(n => n.ReadAt is null));
    }

    [Fact]
    public async Task NotificationEndpointsEnforceHttpsScopesAndSourceIsolation()
    {
        using var factory = new Factory(); using var writer = factory.Client("pump"); using var reader = factory.Client("reader");
        using var other = factory.Client("other"); using var anonymous = factory.Client(null);
        const string notifications = "/api/apps/v1/notifications";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(notifications)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await writer.PostAsJsonAsync("/api/apps/v1/alarms", new ApplicationAlarmRequest("hot", OperationalPriority.Critical, "Hot"))).StatusCode);
        var entry = Assert.Single((await writer.GetFromJsonAsync<ApplicationNotification[]>(notifications))!);
        Assert.Empty((await other.GetFromJsonAsync<ApplicationNotification[]>(notifications))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync(notifications + "/" + entry.Id + "/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync(notifications + "/" + entry.Id + "/acknowledge", null)).StatusCode);
        var receipt = await writer.PostAsync(notifications + "/" + entry.Id + "/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Equal("pump", (await receipt.Content.ReadFromJsonAsync<ApplicationNotification>())!.ReadBy);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-notifications-" + Guid.NewGuid());
        public HistoryStore History { get; }
        public Fixture() { History = new(Root, new Capacity()); }
        public SharedApplicationServices Create() => new(Root, History, TimeProvider.System, new(), () => [new("pump")]);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class Capacity : ICapacityProbe
    { public StorageCapacity Read(string path, long historyBytes) => new(32L << 30, 20L << 30, historyBytes); }
    private sealed class Factory : WebApplicationFactory<PiDock.Appliance.Web.ApplianceMarker>
    {
        private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-notifications-http-" + Guid.NewGuid());
        private readonly Dictionary<string, string> tokens = new() { ["pump"] = Token(), ["reader"] = Token(), ["other"] = Token() };
        private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development").UseSetting("PiDock:StateDirectory", root).UseSetting("Logging:LogLevel:Default", "Warning");
            var settings = new Dictionary<string, string?> { ["PiDock:Apps:Notifications:Policies:0:AppId"] = "pump", ["PiDock:Apps:Notifications:Policies:0:MaximumPriority"] = "Warning" };
            int number = 0;
            foreach (var (id, token) in tokens)
            {
                var prefix = "PiDock:Apps:Credentials:" + number++ + ":";
                settings[prefix + "Id"] = id; settings[prefix + "Subject"] = id == "reader" ? "pump" : id;
                settings[prefix + "TokenSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
                settings[prefix + "ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
                settings[prefix + "Scopes:0"] = "notifications.read";
                if (id != "reader") { settings[prefix + "Scopes:1"] = "notifications.acknowledge"; settings[prefix + "Scopes:2"] = "alarms.raise"; }
            }
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureServices(services => services.AddSingleton<ICapacityProbe, Capacity>());
        }
        public HttpClient Client(string? id)
        {
            var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            if (id is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens[id]);
            return client;
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing && Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
