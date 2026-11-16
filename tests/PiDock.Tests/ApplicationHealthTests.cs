using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiDock.Agent;
using PiDock.Appliance.Broker;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationHealthTests
{
    [Fact]
    public async Task ReadinessIsSeparateFromBootstrapAndLaterNotReadyDoesNotMeanDead()
    {
        using var f=new Fixture();
        Assert.Equal("unknown",(await f.Status()).Health!.State);
        f.Report(true,false);await f.Docker.CheckHealth(default);Assert.Equal("starting",(await f.Status()).Health!.State);
        f.Advance();f.Report(true,true);await f.Docker.CheckHealth(default);
        var ready=(await f.Status()).Health!;Assert.True(ready.Ready);Assert.True(ready.Live);Assert.Equal("ready",ready.State);
        f.Clock.Advance(70);f.Report(true,false);await f.Docker.CheckHealth(default);
        var waiting=(await f.Status()).Health!;Assert.Equal("not_ready",waiting.State);Assert.True(waiting.Live);Assert.False(waiting.RecoveryRequired);
        await f.Docker.ReconcileLifecycle(default);Assert.Equal("running",f.Engine.State);
    }
    [Fact]
    public async Task MissingReportsHaveGraceAndDistinctThrottledFailuresBeforeRecovery()
    {
        using var f=new Fixture();f.Engine.Code=HttpStatusCode.NotFound;
        await f.Docker.CheckHealth(default);Assert.Equal("starting",f.Record.Health!.State);Assert.Equal(0,f.Record.Health.Failures);
        f.Clock.Advance(16);
        await f.Docker.CheckHealth(default);Assert.Equal(1,f.Record.Health!.Failures);
        var calls=f.Engine.Checks;await f.Docker.CheckHealth(default);await f.Status();Assert.Equal(calls,f.Engine.Checks);Assert.Equal(1,f.Record.Health.Failures);
        f.Advance();await f.Docker.CheckHealth(default);Assert.False(f.Record.Health.RecoveryRequired);
        f.Advance();await f.Docker.CheckHealth(default);Assert.True(f.Record.Health.RecoveryRequired);
        await f.Docker.ReconcileLifecycle(default);Assert.Equal("exited",f.Engine.State);
    }
    [Fact]
    public async Task StartupReadinessTimeoutIsRecoverableButFreshReadyClearsFailures()
    {
        using var f=new Fixture();f.Clock.Advance(16);
        for(int i=0;i<3;i++){f.Report(true,false);await f.Docker.CheckHealth(default);f.Advance();}
        Assert.Equal("readiness_timeout",f.Record.Health!.Reason);Assert.True(f.Record.Health.RecoveryRequired);
        f.Report(true,true);await f.Docker.CheckHealth(default);
        Assert.Equal(0,f.Record.Health!.Failures);Assert.False(f.Record.Health.RecoveryRequired);Assert.True(f.Record.Health.Ready);
        await f.Docker.ReconcileLifecycle(default);Assert.Equal("running",f.Engine.State);
    }
    [Fact]
    public async Task ReportsMustMatchStartupIdentityTimeSequenceAndLiveness()
    {
        using var f=new Fixture();f.Clock.Advance(16);
        f.Report(true,true);await f.Docker.CheckHealth(default);var accepted=f.Engine.Report!;
        foreach(var bad in new[]{accepted with{DeliveryId=Guid.NewGuid()},accepted with{Sequence=0},accepted with{SchemaVersion=2},accepted with{ReportedAt=f.Clock.Now.AddMinutes(1)},accepted with{ReportedAt=f.Clock.Now.AddMinutes(-1)},accepted with{Live=false,Ready=true}})
        {f.Advance();f.Engine.Report=bad;await f.Docker.CheckHealth(default);Assert.False(f.Record.Health!.Ready);}
        f.Advance();f.Report(true,true);await f.Docker.CheckHealth(default);Assert.True(f.Record.Health!.Ready);
        f.Advance();f.Engine.Report=f.Engine.Report! with {Sequence=1,ReportedAt=f.Clock.Now};await f.Docker.CheckHealth(default);Assert.False(f.Record.Health!.Ready);
    }
    [Fact]
    public async Task DuplicateReadIsAllowedButChangedContentNeedsNewSequence()
    {
        using var f=new Fixture();f.Report(true,true);await f.Docker.CheckHealth(default);
        f.Advance();await f.Docker.CheckHealth(default);Assert.True(f.Record.Health!.Ready);
        f.Advance();f.Engine.Report=f.Engine.Report! with {ReportedAt=f.Clock.Now};await f.Docker.CheckHealth(default);
        Assert.False(f.Record.Health!.Ready);Assert.Equal("report_stale_or_invalid",f.Record.Health.Reason);
        f.Advance();f.Report(true,true);await f.Docker.CheckHealth(default);Assert.True(f.Record.Health!.Ready);
    }
    [Fact]
    public async Task StaleObservationsAndUnobservedGapsCannotResetRetryBudget()
    {
        using var f=new Fixture();f.Store.Save(f.Record with{Attempts=2});
        f.Report(true,true);await f.Docker.CheckHealth(default);var initial=f.Record.StableSince;
        f.Clock.Advance(61);var expired=await f.Status();Assert.False(expired.Health!.Ready);Assert.Equal("unknown",expired.Health.State);Assert.Equal(2,f.Record.Attempts);
        f.Report(true,true);await f.Docker.CheckHealth(default);Assert.NotEqual(initial,f.Record.StableSince);Assert.Equal(2,(await f.Status()).Attempts);
        for(int i=0;i<12;i++){f.Advance();f.Report(true,true);await f.Docker.CheckHealth(default);}
        Assert.Equal(0,(await f.Status()).Attempts);
        f.Clock.Advance(16);await f.Docker.CheckHealth(default);Assert.False(f.Record.Health!.Ready);Assert.Equal("report_stale_or_invalid",f.Record.Health.Reason);
    }
    [Fact]
    public async Task RuntimeCheckFailureIsUnknownAndNeverAnAppFailure()
    {
        using var f=new Fixture();f.Clock.Advance(16);f.Report(false,false);await f.Docker.CheckHealth(default);Assert.Equal(1,f.Record.Health!.Failures);
        f.Advance();f.Engine.Code=HttpStatusCode.InternalServerError;await f.Docker.CheckHealth(default);
        Assert.Equal("unknown",f.Record.Health!.State);Assert.Equal("check_unavailable",f.Record.Health.Reason);Assert.Equal(0,f.Record.Health.Failures);
        await f.Docker.ReconcileLifecycle(default);Assert.Equal("running",f.Engine.State);
        f.Advance();f.Engine.ThrowTransport=true;await f.Docker.CheckHealth(default);Assert.Equal("unknown",f.Record.Health.State);
    }
    [Fact]
    public async Task ProbeStreamsAndReportsAreBoundedAndNeverExposePayloadContents()
    {
        using var f=new Fixture();f.Clock.Advance(16);
        var invalid=new List<byte[]> {
            Frame(Encoding.UTF8.GetBytes("{\"private\":\"do-not-expose-this-value\"}")),
            Frame(new byte[1025]),Frame(Encoding.UTF8.GetBytes("{}"),stderr:true),
            Frame(Encoding.UTF8.GetBytes("{}"),extra:true),new byte[8193],Encoding.UTF8.GetBytes("not a tar"),Frame(Encoding.UTF8.GetBytes("{\"nested\":{\"a\":{\"b\":{\"c\":{}}}}}")) };
        foreach(var bytes in invalid)
        {f.Engine.Bytes=bytes;await f.Docker.CheckHealth(default);var state=await f.Status();Assert.False(state.Health!.Ready);Assert.DoesNotContain("do-not-expose",JsonSerializer.Serialize(state));f.Advance();}
    }
    [Theory]
    [InlineData(false,true,true)]
    [InlineData(true,false,true)]
    [InlineData(true,true,false)]
    public async Task HealthRestartNeedsHealthPolicyAppRecoveryAndPlatformRecovery(bool healthRestart,bool appRecovery,bool platformRecovery)
    {
        using var f=new Fixture(healthRestart,appRecovery,platformRecovery);f.Clock.Advance(16);
        for(int i=0;i<3;i++){f.Report(false,false);await f.Docker.CheckHealth(default);f.Advance();}
        await f.Docker.ReconcileLifecycle(default);Assert.Equal("running",f.Engine.State);Assert.Equal(0,f.Engine.Stops);
    }
    [Fact]
    public async Task HealthRestartsUseExistingBudgetAndNewStartupIdentityAndRespectStop()
    {
        using var f=new Fixture();f.Store.Save(f.Record with{Attempts=4});f.Clock.Advance(16);
        for(int i=0;i<3;i++){f.Report(false,false);await f.Docker.CheckHealth(default);f.Advance();}
        var oldIdentity=f.Record.LastDeliveryId;
        await f.Docker.ReconcileLifecycle(default);Assert.Equal("exited",f.Engine.State);
        var queued=await f.Status();await f.Docker.Recover(f.Id,new(queued.Revision,[]),default);
        Assert.Equal(5,f.Record.Attempts);Assert.NotEqual(oldIdentity,f.Record.LastDeliveryId);Assert.Null(f.Record.Health);Assert.Null(f.Record.FirstReadyAt);
        f.Clock.Advance(16);
        for(int i=0;i<3;i++){f.Report(false,false);await f.Docker.CheckHealth(default);f.Advance();}
        await f.Docker.ReconcileLifecycle(default);Assert.Equal("suspended",f.Record.Outcome);Assert.Equal("exited",f.Engine.State);
        queued=await f.Status();await f.Docker.Recover(f.Id,new(queued.Revision,[]),default);Assert.Equal(1,f.Engine.Starts);
        await f.Docker.Lifecycle(f.Id,true,true,[],default);
        var stale=await f.Status();await f.Docker.ChangeState(f.Id,false,default);
        await f.Docker.Recover(f.Id,new(stale.Revision,[]),default);Assert.Equal(2,f.Engine.Starts);Assert.False(f.Record.DesiredRunning);
    }
    [Fact]
    public async Task NoHealthContractDoesNotInventReadinessOrReadContainerFiles()
    {
        using var f=new Fixture();f.Store.Save(f.Record with{Manifest=f.Record.Manifest with{Resources=new(true)}});
        await f.Docker.CheckHealth(default);Assert.Equal(0,f.Engine.Checks);Assert.Equal("not_configured",(await f.Status()).Health!.State);Assert.False((await f.Status()).Health!.Ready);
    }
    [Fact]
    public async Task HostChecksHealthEvenWhenAutomaticRecoveryIsDisabled()
    {
        using var f=new Fixture(platformRecovery:false);f.Report(true,true);
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();var socket=Path.Combine(f.Root,"h.sock");builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(socket));
        await using var server=builder.Build();
        server.MapGet("/applications/health/support",()=>f.Docker.HealthSupport());
        server.MapPost("/applications/health/check",async(CancellationToken ct)=>{await f.Docker.CheckHealth(ct);return Microsoft.AspNetCore.Http.Results.Ok();});
        server.MapGet("/applications/lifecycle/support",()=>f.Docker.LifecycleSupport());await server.StartAsync();
        using var broker=new BrokerClient(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Broker:SocketPath"]=socket}).Build());
        await new ApplicationRecoveryService(broker,f.Clock,NullLogger<ApplicationRecoveryService>.Instance).Reconcile(default);
        Assert.True(f.Record.Health!.Ready);Assert.Equal(1,f.Engine.Checks);await server.StopAsync();
    }
    [Fact]
    public async Task CheckUsesOnlyFixedUnprivilegedReaderWithoutShellOrUserArguments()
    {
        using var f=new Fixture();f.Report(true,true);await f.Docker.CheckHealth(default);
        var config=f.Engine.ProbeCreate!.Value;
        Assert.Equal(new[]{ApplicationHealthRules.ProbePath},config.GetProperty("cmd").EnumerateArray().Select(v=>v.GetString()));
        Assert.Equal("1654:1654",config.GetProperty("user").GetString());Assert.False(config.GetProperty("privileged").GetBoolean());Assert.False(config.GetProperty("tty").GetBoolean());
    }
    [Theory]
    [InlineData(14,15,3)] [InlineData(301,15,3)] [InlineData(60,9,3)] [InlineData(60,121,3)] [InlineData(60,15,0)] [InlineData(60,15,11)]
    public void HealthPolicyRejectsUnboundedOrOveractiveSettings(int startup,int freshness,int threshold) => Assert.Throws<ArgumentException>(()=>new ApplicationHealthPolicy(startup,freshness,threshold).Validate());
    private static byte[] Frame(byte[] data,bool stderr=false,bool extra=false)
    {
        var header = new byte[8];header[0]=stderr ? (byte)2 : (byte)1;BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),data.Length);
        var bytes = header.Concat(data).ToArray();return extra ? bytes.Concat(bytes).ToArray() : bytes;
    }
    private sealed class Clock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;public void Advance(int seconds)=>Now=Now.AddSeconds(seconds);}
    private sealed class Delivery:IApplicationBootstrapDelivery {public async Task Deliver(string id,ApplicationBootstrapPacket packet,Func<Task> start,CancellationToken ct)=>await start();}
    private sealed class Engine(string id):HttpMessageHandler
    {
        public string State="running";public int Starts,Stops,Checks;public HttpStatusCode Code=HttpStatusCode.OK;public bool ThrowTransport;public byte[]? Bytes;public ApplicationHealthReport? Report;
        public JsonElement? ProbeCreate;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("/exec",StringComparison.Ordinal))
            {
                Checks++;if(ThrowTransport)throw new HttpRequestException("Test transport failure.");
                ProbeCreate=JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
                return new(Code == HttpStatusCode.NotFound ? HttpStatusCode.OK : Code){Content=new StringContent(JsonSerializer.Serialize(new{Id=new string('c',64)}),Encoding.UTF8,"application/json")};
            }
            if(path.Contains("/exec/",StringComparison.Ordinal))
            {
                if(path.EndsWith("/start",StringComparison.Ordinal)) return new(HttpStatusCode.OK){Content=new ByteArrayContent(Bytes??Frame(Code==HttpStatusCode.NotFound ? [] : JsonSerializer.SerializeToUtf8Bytes(Report,StateFiles.Json)))};
                return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{Running=false,ExitCode=Code==HttpStatusCode.NotFound ? 2 : 0}),Encoding.UTF8,"application/json")};
            }
            if(path.EndsWith("/start",StringComparison.Ordinal)){Starts++;State="running";}
            if(path.EndsWith("/stop",StringComparison.Ordinal)){Stops++;State="exited";}
            object body=path.EndsWith("/containers/json",StringComparison.Ordinal)?new[]{new ApplicationSummary(id,["health-test"],"image",State,State,new(){["ai.knowledgelocker.pidock.managed.resources"]="v1"})}:new{};
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json")};
        }
    }
    private sealed class Fixture:IDisposable
    {
        public string Root {get;}=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"ph-"+Guid.NewGuid().ToString("N"));public string Id {get;}=new('a',64);
        public Clock Clock {get;}=new();public ManagedDeploymentStore Store {get;}public Engine Engine {get;}public HttpClient Http {get;}public DockerOperations Docker {get;}private long sequence;
        public ManagedDeployment Record=>Store.Find(Id)!;
        public Fixture(bool healthRestart=true,bool appRecovery=true,bool platformRecovery=true)
        {
            Store=new(new EphemeralDataProtectionProvider(),Root);Engine=new(Id);Http=new(Engine){BaseAddress=new("http://localhost")};Docker=new(Http,Store,new Delivery(),Clock,platformRecovery);
            Store.Save(new(new("health-test","registry/app@sha256:"+new string('b',64),Resources:new(Health:new(15,15,3,healthRestart))),Id,[],Clock.Now,true,appRecovery,1,Outcome:"observing",LastDeliveryId:Guid.NewGuid()));
        }
        public void Advance()=>Clock.Advance(5);
        public void Report(bool live,bool ready)=>Engine.Report=new(1,Record.LastDeliveryId!.Value,++sequence,Clock.Now,live,ready);
        public async Task<ApplicationLifecycleStatus> Status()=>Assert.Single(await Docker.LifecycleStatus(default));
        public void Dispose(){Http.Dispose();Directory.Delete(Root,true);}
    }
}
