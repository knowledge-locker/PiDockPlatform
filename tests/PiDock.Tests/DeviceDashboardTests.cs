using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PiDock.Agent;
using PiDock.Cloud.Web;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class DeviceDashboardTests : IDisposable
{
    private readonly string root = StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-device-tests-" + Guid.NewGuid()));
    private readonly Clock clock = new();
    private static readonly string Container = new('a', 64);
    private static ClaimsPrincipal User(string tenant = "alpha", string role = "Owner") => new(new ClaimsIdentity([new Claim("tenant",tenant), new Claim(ClaimTypes.Name,"owner"), new Claim(ClaimTypes.Role,role)], "test"));
    private TelemetryEnvelope Frame(string tenant = "alpha", string device = "pi5", bool allowed = true)
    {
        var sample = new HostSnapshot(clock.GetUtcNow(), "test-pi", "Linux ARM64", 25, 8L<<30, 6L<<30, 45, 90000,
            DataPolicyCalculator.Evaluate(new(),new(32L<<30,16L<<30,0)), [], "Test clock");
        return new(1,tenant,device,Guid.NewGuid(),Guid.NewGuid(),1,sample,Applications:new(clock.GetUtcNow(),allowed,"Ready",[new(Container,["managed-app"],"trusted/image","running","Up")],[]));
    }
    private IConfiguration Settings => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["PiDock:StateDirectory"]=root,["Mqtt:Enabled"]="true",["Mqtt:Host"]="broker",["Mqtt:TenantId"]="alpha",["Mqtt:DeviceId"]="pi5"
    }).Build();
    private ApplicationCommand Command(string action = "stop") => new(Guid.NewGuid(),"alpha","pi5",action,Container,clock.GetUtcNow(),clock.GetUtcNow().AddMinutes(2),"owner");

    [Theory]
    [InlineData(0,"Just now")][InlineData(20,"20s ago")][InlineData(90,"1m ago")][InlineData(7200,"2h ago")][InlineData(172800,"2d ago")]
    public void RelativeContactTimeIsReadable(int seconds,string expected) => Assert.Equal(expected,Format.Ago(clock.GetUtcNow().AddSeconds(-seconds),clock.GetUtcNow()));
    [Fact]
    public void RemotePolicyUsesTheSameResolvedStateDirectoryAsAuthentication()
    {
        using var mqtt=new MqttConnection(Settings);using var agent=new RemoteApplicationAgent(new ConfigurationBuilder().Build(),new Runtime(),mqtt,clock,root);
        agent.SetEnabled(true);Assert.True(File.Exists(Path.Combine(root,"remote-applications.json")));
    }
    [Fact]
    public async Task DeviceOptInAndPersistentDedupProtectRuntimeOperations()
    {
        using var mqtt = new MqttConnection(Settings); var runtime = new Runtime();
        using(var agent = new RemoteApplicationAgent(Settings,runtime,mqtt,clock)) {
            await agent.ProcessAsync(Command()); Assert.Equal(0,runtime.Calls); Assert.Equal("Rejected",Assert.Single(agent.Current.Results).State);
            agent.SetEnabled(true); var command = Command(); await agent.ProcessAsync(command); await agent.ProcessAsync(command);
            Assert.Equal(1,runtime.Calls);
            using var restarted = new RemoteApplicationAgent(Settings,runtime,mqtt,clock); await restarted.ProcessAsync(command);
            Assert.Equal(1,runtime.Calls); Assert.True(restarted.Current.ControlEnabled);
        }
    }
    [Fact]
    public async Task WrongIdentityExpiredAndUnsupportedCommandsCannotTouchRuntime()
    {
        using var mqtt = new MqttConnection(Settings); var runtime = new Runtime(); using var agent = new RemoteApplicationAgent(Settings,runtime,mqtt,clock); agent.SetEnabled(true);
        var command = Command();
        foreach(var bad in new[] { command with { TenantId="other" },command with { DeviceId="other" },command with { ExpiresAt=clock.GetUtcNow().AddSeconds(-1) },command with { Action="shell" }, command with { ContainerId="../docker.sock" },command with { ExpiresAt=clock.GetUtcNow().AddHours(1) } }) await agent.ProcessAsync(bad);
        Assert.Equal(0,runtime.Calls); Assert.Empty(agent.Current.Results);
    }
    [Fact]
    public void CloudOperationsRequireTenantRoleLiveInventoryAndScopeReceipts()
    {
        var fleet=new FleetStore(new(root)); var frame=Frame(); Assert.True(fleet.Accept(IdentityRules.Topic("alpha","pi5"),frame).Stored);
        var commands=new ApplicationCommandStore(new(root),fleet,clock);
        Assert.Throws<UnauthorizedAccessException>(()=>commands.Queue(User(role:"Viewer"),"pi5","stop",Container));
        Assert.Throws<UnauthorizedAccessException>(()=>commands.Queue(User("other"),"pi5","stop",Container));
        Assert.Throws<InvalidOperationException>(()=>commands.Queue(User(),"pi5","stop",new string('b',64)));
        var command=commands.Queue(User(),"pi5","stop",Container);
        Assert.Equal(command.Id,commands.ForReceipt(frame)!.Id);
        Assert.Null(commands.ForReceipt(frame with { IsReplay=true })); Assert.Null(commands.ForReceipt(frame with { TenantId="other" }));
        Assert.Throws<InvalidOperationException>(()=>commands.Queue(User(),"pi5","restart",Container));
        var result=new ApplicationCommandResult(command.Id,"Completed","Stopped",clock.GetUtcNow());
        commands.AcceptResults(frame with { TenantId="other",Applications=frame.Applications! with { Results=[result] } });
        Assert.Null(Assert.Single(commands.For(User(),"pi5")).Result);
        commands.AcceptResults(frame with { Applications=frame.Applications! with { Results=[result] } });
        Assert.Equal("Completed",Assert.Single(commands.For(User(),"pi5")).Result!.State);
        Assert.Null(commands.ForReceipt(frame));
    }
    [Fact]
    public void OfflineAndLocallyDisabledDevicesCannotReceiveQueuedOperations()
    {
        foreach(bool disabled in new[] {true,false}) {
            var state=StateFiles.PrepareDirectory(Path.Combine(root,Guid.NewGuid().ToString()));var fleet=new FleetStore(new(state));var frame=Frame(allowed:!disabled);
            fleet.Accept(IdentityRules.Topic("alpha","pi5"),frame);if(!disabled)clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Throws<InvalidOperationException>(()=>new ApplicationCommandStore(new(state),fleet,clock).Queue(User(),"pi5","start",Container));
        }
    }
    [Fact]
    public void HistoryIsOrderedScopedAndDownloadsProtectSpreadsheetCells()
    {
        var fleet=new FleetStore(new(root));var first=Frame(); var second=Frame() with { Sequence=2,Snapshot=first.Snapshot with { ObservedAt=clock.GetUtcNow().AddMinutes(-5),Hostname="=dangerous-formula" },IsReplay=true };
        fleet.Accept(IdentityRules.Topic("alpha","pi5"),first);fleet.Accept(IdentityRules.Topic("alpha","pi5"),second);
        var history=fleet.RangeFor(User(),"pi5",clock.GetUtcNow().AddHours(-1),clock.GetUtcNow());Assert.Equal(2,history.Count);Assert.Equal(second.MessageId,history[0].MessageId);
        Assert.Empty(fleet.RangeFor(User("other"),"pi5",clock.GetUtcNow().AddHours(-1),clock.GetUtcNow()));
        var csv=DeviceHistory.Csv(history,["cpu","memory"]); Assert.Contains("cpu_percent,memory_percent",csv);Assert.Contains("\"'=dangerous-formula\"",csv);Assert.Contains("25,25",csv);
        Assert.Equal(2,DeviceHistory.Paths(history,"cpu",clock.GetUtcNow().AddHours(-1),clock.GetUtcNow(),100).Count);
    }
    [Theory]
    [InlineData("\n=SUM(1,2)")][InlineData(" \t=SUM(1,2)")][InlineData("\ufeff+SUM(1,2)")]
    public void CsvNeutralizesFormulasHiddenBehindWhitespace(string hostname)
    {
        var frame=Frame();var csv=DeviceHistory.Csv([frame with { Snapshot=frame.Snapshot with { Hostname=hostname } }],["cpu"]);
        Assert.Contains("\"'"+hostname.Replace("\"","\"\"")+"\"",csv);
    }
    [Fact]
    public void OptionalInventoryDoesNotChangeLegacyTelemetryCanonicalJson()
    {
        var legacy=Frame() with { Applications=null };
        Assert.DoesNotContain("\"applications\"",JsonSerializer.Serialize(legacy,StateFiles.Json));
        Assert.Null(JsonSerializer.Deserialize<TelemetryEnvelope>(JsonSerializer.Serialize(legacy,StateFiles.Json),StateFiles.Json)!.Applications);
        var fleet=new FleetStore(new(root));Assert.True(fleet.Accept(IdentityRules.Topic("alpha","pi5"),legacy).Stored);Assert.True(fleet.Accept(IdentityRules.Topic("alpha","pi5"),legacy).Stored);
    }
    public void Dispose()=>Directory.Delete(root,true);
    private sealed class Clock : TimeProvider { private DateTimeOffset now=DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow()=>now;public void Advance(TimeSpan amount)=>now+=amount; }
    private sealed class Runtime : IApplicationRuntime {
        public bool Configured=>true; public int Calls;
        public Task<ApplicationSummary[]> List(CancellationToken ct=default)=>Task.FromResult(new[]{new ApplicationSummary(Container,["app"],"image","running","Up")});
        public Task ChangeState(string id,bool start,CancellationToken ct=default){if(id!=Container)throw new ArgumentException();Calls++;return Task.CompletedTask;}
        public Task Restart(string id,CancellationToken ct=default)=>ChangeState(id,true,ct);
    }
}
