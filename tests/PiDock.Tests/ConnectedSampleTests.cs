using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PiDock.Appliance.Web;
using PiDock.Agent;
using PiDock.AppUi.Sample;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class ConnectedSampleTests
{
    [Theory]
    [InlineData("https://127.0.0.1:7356/",false,true)]
    [InlineData("https://host.docker.internal:7356/",false,false)]
    [InlineData("https://host.docker.internal:7356/",true,true)]
    [InlineData("http://host.docker.internal:7356/",true,false)]
    [InlineData("https://example.com/",true,false)]
    [InlineData("https://host.docker.internal.example.com/",true,false)]
    [InlineData("https://user@host.docker.internal/",true,false)]
    [InlineData("https://host.docker.internal/private",true,false)]
    public void PrivateSampleConnectionRestrictsDockerOriginToExplicitPinnedFixture(string origin,bool docker,bool allowed)
    {
        var settings=new SampleConnection(origin,new('A',64),new('B',64),new('C',64),new('D',64),Guid.NewGuid(),docker);
        if(allowed) { using var session=SampleConnectedSession.Create(settings); }
        else Assert.Throws<ArgumentException>(()=>SampleConnectedSession.Create(settings));
    }
    private static JsonElement Args(object? value = null) => JsonSerializer.SerializeToElement(value ?? new { });
    private static JsonElement Context(SampleConnectedSession session, object? values = null)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Args(values).GetRawText())!;
        fields["instanceId"] = Args(session.Read().InstanceId); fields["revision"] = Args(session.Read().Revision);
        return Args(fields);
    }
    private static ApplicationSignalState Signal(SampleConnectedSession session, string id) => session.Read().Signals.Single(s => s.Declaration.Id == id);
    [Fact]
    public async Task RealSharedEndpointsDriveLatchDelayAndThreeSimulatedOutputs()
    {
        await using var f = await Fixture.Create(); using var session = f.Session();
        await session.Connect(default); await session.Cycle(default);
        await session.Command("demo.start", Args(), default); await session.Cycle(default);
        Assert.True(Signal(session, "run-request").NormalValue.GetBoolean()); Assert.All(session.Read().Outputs, o => Assert.False(o.Value));
        f.Clock.Advance(1.5); await session.Cycle(default); Assert.False(Signal(session, "timer-done").EffectiveValue.GetBoolean());
        f.Clock.Advance(1.5); await session.Cycle(default); Assert.All(session.Read().Outputs, o => Assert.True(o.Value));
        await session.Command("demo.bank-toggle", Args(), default); await session.Cycle(default);
        Assert.False(Signal(session, "bank-enabled").NormalValue.GetBoolean()); Assert.True(Signal(session, "timer-done").NormalValue.GetBoolean());
        Assert.All(session.Read().Outputs, o => Assert.False(o.Value));
        await session.Command("demo.bank-toggle", Args(), default); await session.Cycle(default); Assert.All(session.Read().Outputs, o => Assert.True(o.Value));
        await session.Command("demo.stop", Args(), default); await session.Cycle(default);
        Assert.False(Signal(session, "run-request").EffectiveValue.GetBoolean()); Assert.Equal(0, Signal(session, "timer-elapsed").NormalValue.GetDouble());
        Assert.All(session.Read().Outputs, o => Assert.False(o.Value)); await session.Disconnect(default);
    }
    [Fact]
    public async Task ForceAndReleaseUseSharedAuthorityAndDoNotUndoLatchHistory()
    {
        await using var f = await Fixture.Create(); using var session = f.Session(); await session.Connect(default); await session.Cycle(default);
        await session.Command("engineering.force", Context(session, new { signalId = "start", value = true, seconds = 30 }), default); await session.Cycle(default);
        Assert.False(Signal(session, "start").NormalValue.GetBoolean()); Assert.True(Signal(session, "start").EffectiveValue.GetBoolean());
        await session.Command("engineering.release", Context(session, new { signalId = "start" }), default); await session.Cycle(default);
        Assert.True(Signal(session, "run-request").NormalValue.GetBoolean()); Assert.Null(Signal(session, "start").Force);
        await session.Command("demo.stop", Args(), default); await session.Cycle(default);
        await session.Command("engineering.force", Context(session, new { signalId = "output-bank", value = true, seconds = 30 }), default); await session.Cycle(default);
        Assert.False(Signal(session, "output-bank").NormalValue.GetBoolean()); Assert.True(Signal(session, "output-bank").EffectiveValue.GetBoolean());
        Assert.All(session.Read().Outputs, o => Assert.True(o.Value)); Assert.Equal(1, session.Read().ForceCount);
        await session.Command("engineering.release-all", Context(session), default); await session.Cycle(default);
        Assert.Equal(0, session.Read().ForceCount); Assert.All(session.Read().Outputs, o => Assert.False(o.Value));
        Assert.Contains(f.Factory.Services.GetRequiredService<HistoryStore>().ReadSources<ApplicationEngineeringAudit>(HistoryClass.Audit, ["ui-sample"]), a => a.Action == "force-released");
        await session.Disconnect(default);
    }
    [Fact]
    public async Task ExpiryRestartAndDeniedConstantsAreConfirmedBySharedServices()
    {
        await using var f = await Fixture.Create(); using var session = f.Session(); await session.Connect(default); await session.Cycle(default);
        await session.Command("engineering.force", Context(session, new { signalId = "output-bank", value = true, seconds = 2 }), default); await session.Cycle(default);
        f.Clock.Advance(3); await session.Cycle(default); Assert.Equal(0, session.Read().ForceCount); Assert.All(session.Read().Outputs, o => Assert.False(o.Value));
        var denied = await Assert.ThrowsAsync<HttpRequestException>(() => session.Command("engineering.force", Context(session, new { signalId = "delay-seconds", value = 5, seconds = 10 }), default));
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode); Assert.Equal(3, Signal(session, "delay-seconds").NormalValue.GetInt32());
        await session.Command("engineering.force", Context(session, new { signalId = "output-bank", value = true, seconds = 30 }), default);
        var previous = session.Read().InstanceId; await session.Command("demo.reset", Args(), default); await session.Cycle(default);
        Assert.NotEqual(previous, session.Read().InstanceId); Assert.Equal(0, session.Read().ForceCount); Assert.All(session.Read().Outputs, o => Assert.False(o.Value));
        using var runtimeOnly = f.Client(1);
        Assert.Equal(HttpStatusCode.Forbidden, (await runtimeOnly.PostAsJsonAsync("/api/apps/v1/engineering/force", new ApplicationSignalForceRequest(session.Read().InstanceId, SampleConnectedSession.Revision, "output-bank", Args(true), 10))).StatusCode);
        await session.Disconnect(default);
    }
    [Fact]
    public async Task RevokedIoAuthorityFaultsTheConnectedRuntimeWithoutReacquiring()
    {
        await using var f = await Fixture.Create(); using var session = f.Session(); await session.Connect(default); await session.Cycle(default);
        var users = f.Factory.Services.GetRequiredService<UserStore>(); var secret = Totp.NewSecret(); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        users.Add("test-owner", password, secret, "review", "Owner", true, -1);
        var user = users.Authenticate("test-owner", password, Totp.Code(secret, f.Clock.GetUtcNow().ToUnixTimeSeconds() / 30), f.Clock.GetUtcNow())!;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Name, user.Username), new(ClaimTypes.Role, user.Role), new("tenant", user.TenantId), new("stamp", user.Stamp), new("expires", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString())], "test"));
        f.Factory.Services.GetRequiredService<ApplicationCredentialVault>().Revoke(principal, password, Totp.Code(secret, f.Clock.GetUtcNow().ToUnixTimeSeconds() / 30 + 1), "ui-sample", "sample-io");
        f.Factory.Services.GetRequiredService<ApplicationIoService>().Sweep();
        await Assert.ThrowsAsync<HttpRequestException>(() => session.Cycle(default)); Assert.Equal("unavailable", session.Read().Status);
        await session.Cycle(default); Assert.Equal("unavailable", session.Read().Status);
        await session.Disconnect(default);
    }
    [Fact]
    public async Task BridgeCommandArgumentsAreDeduplicatedAndFenceAnOldDisplayedRuntime()
    {
        await using var f = await Fixture.Create(); using var session = f.Session(); await session.Connect(default); await session.Cycle(default);
        var runtime = new SampleRuntime(new(null), f.Clock, session);
        var force = new ApplicationUiCommand(1, Guid.NewGuid(), "engineering.force", Context(session, new { signalId = "output-bank", value = true, seconds = 30 }));
        var accepted = await runtime.CommandAsync(force, default); Assert.True(accepted.Succeeded);
        foreach (var credential in f.Factory.Credentials) Assert.DoesNotContain(credential.Token, JsonSerializer.Serialize(accepted, StateFiles.Json));
        var forceId = Signal(session, "output-bank").Force!.Id;
        Assert.True((await runtime.CommandAsync(force, default)).Succeeded); Assert.Equal(forceId, Signal(session, "output-bank").Force!.Id);
        Assert.False((await runtime.CommandAsync(force with { Name = "engineering.release-all" }, default)).Succeeded);
        await session.Command("demo.reset", Args(), default); await session.Cycle(default);
        Assert.False((await runtime.CommandAsync(force with { CommandId = Guid.NewGuid() }, default)).Succeeded);
        Assert.Equal(0, session.Read().ForceCount); await session.Disconnect(default);
    }
    [Theory]
    [InlineData("pause")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("failure")]
    [InlineData("broker_unavailable")]
    public async Task LifecycleAccessRevokesThroughRealApisAndFreshStartupBeginsCleared(string change)
    {
        await using var f = await Fixture.Create(bound:true); using var session = f.Session();
        await session.Connect(default); await session.Cycle(default);
        var oldInstance = session.Read().InstanceId;
        await session.Command("engineering.force", Context(session, new {signalId="output-bank",value=true,seconds=60}), default);
        await session.Cycle(default); Assert.All(session.Read().Outputs, o => Assert.True(o.Value));
        if (change == "pause") f.Access!.Paused = true;
        else if (change == "restart") f.Access!.Delivery = Guid.NewGuid();
        else if (change == "broker_unavailable") f.Access!.Unavailable = true;
        else f.Access!.Allowed = false;
        await f.Factory.Services.GetRequiredService<ApplicationRuntimeAccessMonitor>().Refresh(default,force:true);
        var io = f.Factory.Services.GetRequiredService<ApplicationIoService>().Activity("ui-sample");
        Assert.Equal(0, io.Leases); Assert.True(io.OutputsAtFallback);
        Assert.Equal((0,0,true), f.Factory.Services.GetRequiredService<ApplicationOverrideService>().Activity("ui-sample"));
        await Assert.ThrowsAsync<HttpRequestException>(() => session.Cycle(default));
        f.Access!.Paused=false; f.Access.Allowed=true; f.Access.Unavailable=false; f.Access.Delivery=Guid.NewGuid();
        await f.Factory.Services.GetRequiredService<ApplicationRuntimeAccessMonitor>().Refresh(default,force:true);
        using var fresh = f.Session(); await fresh.Connect(default); await fresh.Cycle(default);
        Assert.NotEqual(oldInstance,fresh.Read().InstanceId); Assert.Equal(0,fresh.Read().ForceCount); Assert.All(fresh.Read().Outputs,o=>Assert.False(o.Value));
        Assert.DoesNotContain(f.Access.Delivery.ToString(), JsonSerializer.Serialize(fresh.Read(),StateFiles.Json));
        using var unbound=f.Factory.CreateClient(new(){BaseAddress=new("https://localhost")});
        unbound.DefaultRequestHeaders.Authorization=new("Bearer",f.Factory.Credentials[0].Token);
        Assert.Equal(HttpStatusCode.Forbidden,(await unbound.PostAsJsonAsync("/api/apps/v1/io/leases",new ApplicationIoLeaseRequest(["sim:bcm17"]))).StatusCode);
        await fresh.Disconnect(default);
    }
    [Fact]
    public async Task IdleHostDetectionRevokesWithoutAnyFurtherAppRequest()
    {
        await using var f = await Fixture.Create(bound:true); using var session = f.Session();
        await session.Connect(default); await session.Cycle(default);
        await session.Command("engineering.force", Context(session, new {signalId="output-bank",value=true,seconds=60}), default); await session.Cycle(default);
        f.Access!.Allowed=false; f.Clock.Advance(0.5);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var io = f.Factory.Services.GetRequiredService<ApplicationIoService>();
        // No session cycle, cleanup or explicit monitor refresh: only the actual idle hosted poll can see this change.
        while (io.Activity("ui-sample").Leases != 0) await Task.Delay(25, deadline.Token);
        Assert.True(io.Activity("ui-sample").OutputsAtFallback);
        Assert.Equal((0,0,true), f.Factory.Services.GetRequiredService<ApplicationOverrideService>().Activity("ui-sample"));
    }
    private sealed class AccessState
    {
        public Guid Delivery=Guid.NewGuid(); private readonly Guid coordinator=Guid.NewGuid();
        public bool Paused, Unavailable, Allowed=true;
        public ApplicationRuntimeAccessSnapshot Snapshot()=>new(coordinator,Paused,[new("ui-sample",Delivery,Allowed,"fixture")]);
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks; private DateTimeOffset utc = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(double seconds) { ticks += TimeSpan.FromSeconds(seconds).Ticks; utc += TimeSpan.FromSeconds(seconds); }
    }
    private sealed class Plenty : ICapacityProbe { public StorageCapacity Read(string path, long bytes) => new(32L << 30, 18L << 30, bytes); }
    private sealed class Factory(string root, string socket, Clock clock, AccessState? access = null) : WebApplicationFactory<PiDock.Appliance.Web.ApplianceMarker>
    {
        public readonly ApplicationCredentialFile[] Credentials = [Credential("sample-io", ["io.read", "io.write", "io.simulate"]), Credential("sample-runtime", ["signals.runtime"]), Credential("sample-engineer", ["signals.engineer"])];
        private static ApplicationCredentialFile Credential(string id, string[] scopes) => new(1, "ui-sample", id, 1, "platform", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow.AddHours(1), scopes);
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development").UseSetting("PiDock:StateDirectory", root).UseSetting("Logging:LogLevel:Default", "Warning").UseSetting("Broker:SocketPath", socket);
            var settings = new Dictionary<string, string?> { ["PiDock:Apps:Io:Enabled"] = "true", ["PiDock:Engineering:Enabled"] = "true", ["PiDock:Apps:RuntimeAccess:Enabled"] = (access is not null).ToString() };
            for (int i = 0; i < SampleConnectedSession.Aliases.Length; i++)
            { var prefix = "PiDock:Apps:Io:Grants:" + i + ":"; settings[prefix + "AppId"] = "ui-sample"; settings[prefix + "Alias"] = SampleConnectedSession.Aliases[i]; settings[prefix + "Direction"] = i < 2 ? "Input" : "Output"; }
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock); services.AddSingleton<ICapacityProbe, Plenty>(); services.RemoveAll<ApplicationCredentialVault>();
                services.AddSingleton(sp =>
                {
                    var protection = sp.GetRequiredService<IDataProtectionProvider>();
                    StateFiles.WriteAtomic(Path.Combine(root, "app-credentials.protected"), protection.CreateProtector("PiDock.ApplicationCredentials.v1").Protect(JsonSerializer.Serialize(Credentials, StateFiles.Json)));
                    return new ApplicationCredentialVault(protection, sp.GetRequiredService<StateDirectory>(), sp.GetRequiredService<UserStore>(), clock);
                });
            });
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pce-" + Guid.NewGuid().ToString("N")));
        private WebApplication broker = null!;
        public Factory Factory { get; private set; } = null!;
        public Clock Clock { get; } = new();
        public AccessState? Access { get; private set; }
        public static async Task<Fixture> Create(bool bound = false)
        {
            var f = new Fixture { Access = bound ? new AccessState() : null }; var socket = Path.Combine(f.root, "b.sock");
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socket));
            f.broker = builder.Build(); f.broker.MapGet("/applications/orchestration/status", () => new ApplicationOrchestrationStatus(f.Access?.Paused ?? false, "fixture", "active", 0, 0));
            f.broker.MapGet("/applications/runtime-access", () => f.Access?.Unavailable == true ? (Microsoft.AspNetCore.Http.IResult)Microsoft.AspNetCore.Http.Results.StatusCode(503) : Microsoft.AspNetCore.Http.Results.Ok(f.Access?.Snapshot())); await f.broker.StartAsync();
            f.Factory = new(f.root, socket, f.Clock, f.Access); return f;
        }
        public HttpClient Client(int credential)
        { var client = Factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Factory.Credentials[credential].Token); if (Access is not null) ApplicationRuntimeAccess.Bind(client, Access.Delivery); return client; }
        public SampleConnectedSession Session() => new(Client(0), Client(1), Client(2), Clock);
        public async ValueTask DisposeAsync() { await Factory.DisposeAsync(); await broker.StopAsync(); await broker.DisposeAsync(); Directory.Delete(root, true); }
    }
}
