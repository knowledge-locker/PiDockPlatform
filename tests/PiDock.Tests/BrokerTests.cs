using System.Net;
using System.Text;
using System.Text.Json;
using PiDock.Core;
using PiDock.Appliance.Broker;
namespace PiDock.Tests;

public sealed class BrokerTests
{
    [Theory]
    [InlineData("latest")]
    [InlineData("alpine:latest")]
    [InlineData("x;reboot@sha256:00")]
    [InlineData("--privileged")]
    public void MutableTagsAndCommandsCannotEnterManifest(string image) => Assert.Throws<ArgumentException>(() => new ApplicationManifest("app", image).Validate());
    [Fact]
    public async Task DeployEnforcesIsolationAndNeverUsesHostNetworkOrMounts()
    {
        using var handler = new FakeDocker(); using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var docker = new DockerOperations(http);
        await docker.Deploy(new("test", "mcr.microsoft.com/dotnet/samples@sha256:" + new string('a', 64), 128, .5, 8080, 18080), default);
        var body = handler.CreateBody!.RootElement;
        Assert.Equal("1654:1654", body.GetProperty("user").GetString());
        var host = body.GetProperty("hostConfig"); Assert.True(host.GetProperty("readonlyRootfs").GetBoolean()); Assert.False(host.GetProperty("privileged").GetBoolean());
        Assert.Equal("ALL", host.GetProperty("capDrop")[0].GetString()); Assert.Equal(128, host.GetProperty("pidsLimit").GetInt32());
        Assert.Equal("127.0.0.1", host.GetProperty("portBindings").GetProperty("8080/tcp")[0].GetProperty("hostIp").GetString());
        Assert.False(host.TryGetProperty("binds", out _)); Assert.NotEqual("host", host.GetProperty("networkMode").GetString());
    }
    [Fact]
    public async Task NonManagedContainerCannotBeControlled()
    {
        using var http = new HttpClient(new FakeDocker()) { BaseAddress = new Uri("http://localhost") };
        await Assert.ThrowsAsync<ArgumentException>(() => new DockerOperations(http).ChangeState(new string('a', 64), false, default));
    }
    private sealed class FakeDocker : HttpMessageHandler
    {
        public JsonDocument? CreateBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath, body = "{}";
            if (path.EndsWith("/containers/json")) body = "[]";
            if (path.EndsWith("/networks/create")) body = "{\"Id\":\"network-id\"}";
            if (path.EndsWith("/containers/create")) { CreateBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)); body = "{\"Id\":\"" + new string('b', 64) + "\"}"; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing) { CreateBody?.Dispose(); base.Dispose(disposing); }
    }
}
