using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PiDock.Appliance.Broker;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationStorageTests
{
    [Fact]
    public async Task LowCapacityAndUnconfirmedFilesystemRejectNewVolumeBeforeAllocation()
    {
        using var handler = new Docker(); using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var manifest = new ApplicationManifest("fixture", "registry/app@sha256:" + new string('a', 64), Resources: new(PersistentData: true));
        var pressured = new DockerOperations(http, new Store(), new Delivery())
        {
            StorageConfiguration = new("/verified-docker-filesystem", true), StorageCapacityProbe = new Capacity(10L << 30, 512L << 20)
        };
        await Assert.ThrowsAsync<IOException>(() => pressured.Deploy(manifest, default));
        Assert.Equal(0, handler.VolumeCreates); Assert.Equal(0, handler.ContainerCreates);
        var unconfirmed = new DockerOperations(http, new Store(), new Delivery()) { StorageConfiguration = new("/verified-docker-filesystem", false) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => unconfirmed.Deploy(manifest, default));
        Assert.Equal(0, handler.VolumeCreates); Assert.Equal(0, handler.ContainerCreates);
    }

    [Fact]
    public async Task UsageIsScopedToOwnedLocalDataVolumesAndUnknownDoesNotBecomeZero()
    {
        using var handler = new Docker(); using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var broker = new DockerOperations(http) { StorageConfiguration = new("/verified", true), StorageCapacityProbe = new Capacity(10L << 30, 2L << 30) };
        var status = await broker.StorageStatus(default);
        Assert.True(status.Capacity!.CanAllocate);
        Assert.Collection(status.Volumes,
            item => { Assert.Equal("fixture", item.AppId); Assert.Equal(1234, item.UsedBytes); Assert.Equal("docker-estimate", item.Quality); },
            item => { Assert.Equal("unknown", item.AppId); Assert.Null(item.UsedBytes); Assert.Equal("unknown", item.Quality); });
        Assert.DoesNotContain("/verified", JsonSerializer.Serialize(status, StateFiles.Json));
        Assert.False(broker.StorageSupport().HardQuotas);
        handler.HugeReport = true;
        await Assert.ThrowsAsync<IOException>(() => broker.StorageStatus(default));
    }

    [Fact]
    public void ReservePolicyRejectsInvalidMeasurementsAndKeepsReservePlusAllowance()
    {
        var policy = new ApplicationStoragePolicy();
        var budget = policy.Evaluate(10L << 30, (512L << 20) + (32L << 20));
        Assert.True(budget.CanAllocate); Assert.Equal(512L << 20, budget.ReserveBytes);
        Assert.False(policy.Evaluate(10L << 30, (512L << 20) + (32L << 20) - 1).CanAllocate);
        Assert.False(policy.Evaluate(1L << 20, 1L << 20).CanAllocate);
        Assert.Throws<ArgumentException>(() => policy.Evaluate(long.MaxValue, -1));
        Assert.Throws<ArgumentException>(() => new ApplicationStoragePolicy(ReservePercent: 0).Evaluate(1000, 1000));
    }

    private sealed class Capacity(long total, long free) : IApplicationStorageCapacityProbe
    { public (long TotalBytes, long FreeBytes) Read(string directory) => (total, free); }
    private sealed class Store : IManagedDeploymentStore
    {
        public ManagedDeployment? Find(string containerId) => null;
        public void Save(ManagedDeployment deployment) => throw new InvalidOperationException("Unexpected allocation.");
        public void Forget(string containerId) { }
    }
    private sealed class Delivery : IApplicationBootstrapDelivery
    { public Task Deliver(string id, ApplicationBootstrapPacket packet, Func<Task> start, CancellationToken ct) => throw new InvalidOperationException("Unexpected startup."); }
    private sealed class Docker : HttpMessageHandler
    {
        public int VolumeCreates, ContainerCreates;
        public bool HugeReport;
        private const string Label = "ai.knowledgelocker.pidock.managed";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/system/df", StringComparison.Ordinal))
            {
                if (HugeReport) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1048577)) });
                object Volume(string app, long size, bool managed = true, string driver = "local") => new { Name = "pidock-app-" + app + "-data", Driver = driver,
                    Labels = new Dictionary<string, string> { [Label] = managed ? "true" : "false", [Label + ".app"] = app }, UsageData = new { Size = size, RefCount = 1 } };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { Volumes = new[] { Volume("fixture", 1234), Volume("unknown", -1), Volume("foreign", 9, false), Volume("external", 9, driver: "nfs") } }, options: new JsonSerializerOptions()) });
            }
            if (path.Contains("/volumes/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (path.EndsWith("/volumes/create", StringComparison.Ordinal)) VolumeCreates++;
            if (path.EndsWith("/containers/create", StringComparison.Ordinal)) ContainerCreates++;
            string body = path.EndsWith("/containers/json", StringComparison.Ordinal) ? "[]" : path.EndsWith("/networks/create", StringComparison.Ordinal) ? "{\"Id\":\"fixture-network\"}" : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
