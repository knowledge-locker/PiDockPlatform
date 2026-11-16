using System.Net;
using System.Security.Cryptography;
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

public sealed class ApplicationLifecycleTests
{
    [Fact]
    public async Task RecoveryRedeliversCurrentCredentialsAndPreservesBackoffAcrossBrokerRestart()
    {
        using var f = new Fixture();
        var initial = await f.Status();
        await f.Docker.Recover(f.Id,new(initial.Revision,[f.Credential with { Version=2,Token=new string('c',64) }]),default);
        Assert.Equal(2,f.Delivery.Packet!.Credentials[0].Version);
        Assert.Equal(1,f.Record.Attempts); Assert.Equal(f.Clock.Now.AddSeconds(5),f.Record.NextAttemptAt);
        var reopened = new ManagedDeploymentStore(f.Protection,f.Root);
        Assert.Equal(1,reopened.Find(f.Id)!.Attempts);
        f.Engine.State = "exited";
        var restarted = new DockerOperations(f.Http,reopened,f.Delivery,f.Clock,true);
        var beforeDue = Assert.Single(await restarted.LifecycleStatus(default));
        await restarted.Recover(f.Id,new(beforeDue.Revision,[f.Credential]),default);
        Assert.Equal(1,f.Engine.Starts);
        f.Clock.Advance(6);
        var due = Assert.Single(await restarted.LifecycleStatus(default));
        await restarted.Recover(f.Id,new(due.Revision,[f.Credential]),default);
        Assert.Equal(2,f.Engine.Starts); Assert.Equal(2,f.Record.Attempts);
    }
    [Fact]
    public async Task PersistedStopAndPolicyChangesInvalidateQueuedRecovery()
    {
        using var f = new Fixture(); var queued = await f.Status();
        await f.Docker.ChangeState(f.Id,false,default);
        await f.Docker.Recover(f.Id,new(queued.Revision,[f.Credential]),default);
        Assert.Equal(0,f.Engine.Starts); Assert.False(f.Record.DesiredRunning);
        var restarted = new DockerOperations(f.Http,new ManagedDeploymentStore(f.Protection,f.Root),f.Delivery,f.Clock,true);
        Assert.False(Assert.Single(await restarted.LifecycleStatus(default)).DesiredRunning);
        await restarted.SetRecovery(f.Id,true,default);
        Assert.False(f.Record.DesiredRunning);
        await restarted.Lifecycle(f.Id,true,false,[f.Credential],default);
        Assert.True(f.Record.DesiredRunning); Assert.Equal(0,f.Record.Attempts);
        f.Engine.State="exited"; queued=await f.Status();
        await f.Docker.SetRecovery(f.Id,false,default);
        await f.Docker.Recover(f.Id,new(queued.Revision,[f.Credential]),default);
        Assert.Equal(1,f.Engine.Starts);
        await f.Docker.SetRecovery(f.Id,true,default);
        await f.Docker.Recover(f.Id,new(queued.Revision,[f.Credential]),default);
        Assert.Equal(1,f.Engine.Starts); // Old revisions cannot become valid again after a toggle.
    }
    [Fact]
    public async Task FailedStartupAndFastExitsExhaustPersistentBudgetUntilManualStart()
    {
        using var f=new Fixture(); f.Delivery.Fail=true;
        for (var attempt=1;attempt<=5;attempt++)
        {
            var state=await f.Status();
            await Assert.ThrowsAsync<IOException>(()=>f.Docker.Recover(f.Id,new(state.Revision,[f.Credential]),default));
            Assert.Equal(attempt,f.Record.Attempts); Assert.Equal("exited",f.Engine.State);
            f.Clock.Now=f.Record.NextAttemptAt!.Value.AddSeconds(1);
        }
        var suspended=await f.Status(); Assert.Equal("suspended",suspended.Outcome);
        await f.Docker.Recover(f.Id,new(suspended.Revision,[f.Credential]),default); Assert.Equal(5,f.Engine.Starts);
        f.Delivery.Fail=false;
        await f.Docker.Lifecycle(f.Id,true,true,[f.Credential],default);
        Assert.Equal(0,f.Record.Attempts); Assert.Equal(6,f.Engine.Starts);
        // An acknowledged startup that exits quickly still counts against recovery.
        for(var attempt=1;attempt<=5;attempt++)
        {
            f.Engine.State="exited";var state=await f.Status();
            await f.Docker.Recover(f.Id,new(state.Revision,[f.Credential]),default);
            Assert.Equal(attempt,f.Record.Attempts); f.Clock.Now=f.Record.NextAttemptAt!.Value.AddSeconds(1);
        }
        f.Engine.State="exited";Assert.Equal("suspended",(await f.Status()).Outcome);
    }
    [Fact]
    public async Task BudgetResetsOnlyAfterSustainedRunningObservation()
    {
        using var f=new Fixture();var state=await f.Status();
        await f.Docker.Recover(f.Id,new(state.Revision,[f.Credential]),default);
        f.Clock.Advance(59);Assert.Equal(1,(await f.Status()).Attempts);
        f.Clock.Advance(1);var stable=await f.Status();Assert.Equal("stable",stable.Outcome);Assert.Equal(0,stable.Attempts);
        f.Engine.State="exited";var exited=await f.Status();Assert.Null(exited.StableSince);
        await f.Docker.Recover(f.Id,new(exited.Revision,[f.Credential]),default);Assert.Equal(1,f.Record.Attempts);
    }
    [Fact]
    public async Task UnavailableCredentialsWaitWithoutConsumingStartupAttemptsAndNeverUseSavedMaterial()
    {
        using var f=new Fixture();var state=await f.Status();
        await f.Docker.RecoveryBlocked(f.Id,state.Revision,default);
        Assert.Equal("credential_unavailable",f.Record.Outcome);Assert.Equal(0,f.Record.Attempts);Assert.Equal(0,f.Engine.Starts);
        state=await f.Status();await f.Docker.Recover(f.Id,new(state.Revision,[f.Credential]),default);Assert.Equal(0,f.Engine.Starts);
        f.Clock.Advance(31);state=await f.Status();
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Docker.Recover(f.Id,new(state.Revision,[]),default));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Docker.Recover(f.Id,new(state.Revision,[f.Credential with { ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1) }]),default));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Docker.Recover(f.Id,new(state.Revision,[f.Credential with { AppId="different-app" }]),default));
        Assert.Equal(0,f.Record.Attempts);Assert.Equal(0,f.Engine.Starts);
    }
    [Fact]
    public async Task OptInMissingRecordsAndForeignContainersFailClosedButStopRemainsAvailable()
    {
        using var f=new Fixture();var disabled=new DockerOperations(f.Http,f.Store,f.Delivery,f.Clock);
        Assert.False(disabled.LifecycleSupport().Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>disabled.SetRecovery(f.Id,true,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>disabled.Recover(f.Id,new(1,[f.Credential]),default));
        f.Engine.Managed=false;
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Docker.Recover(f.Id,new(1,[f.Credential]),default));Assert.Empty(await f.Docker.LifecycleStatus(default));
        f.Engine.Managed=true;f.Store.Forget(f.Id);
        Assert.Equal("startup_record_unavailable",(await f.Status()).Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Docker.Recover(f.Id,new(1,[f.Credential]),default));
        await f.Docker.ChangeState(f.Id,false,default);Assert.Equal(0,f.Engine.Starts);
        Assert.DoesNotContain(f.Engine.Requests,p=>p.Contains("/volumes/",StringComparison.Ordinal));
    }
    [Fact]
    public async Task HostCoordinatorUsesCurrentVaultAndRunsWithoutBrowserOrCloud()
    {
        using var f=new Fixture();
        var socket=Path.Combine(f.Root,"b.sock");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(socket));
        await using var server=builder.Build();
        server.MapGet("/applications/lifecycle/support",()=>f.Docker.LifecycleSupport());
        server.MapPost("/applications/lifecycle/reconcile",async(CancellationToken ct)=>{await f.Docker.ReconcileLifecycle(ct);return Microsoft.AspNetCore.Http.Results.Ok();});
        server.MapGet("/applications/lifecycle",(CancellationToken ct)=>f.Docker.LifecycleStatus(ct));
        server.MapPost("/applications/{id}/recover",async(string id,ApplicationRecoveryRequest request,CancellationToken ct)=>{await f.Docker.Recover(id,request,ct);return Microsoft.AspNetCore.Http.Results.Ok();});
        server.MapPost("/applications/{id}/recovery-blocked",async(string id,ApplicationRecoveryBlocked request,CancellationToken ct)=>{await f.Docker.RecoveryBlocked(id,request.Revision,ct);return Microsoft.AspNetCore.Http.Results.Ok();});
        await server.StartAsync();
        var source=new Source();
        using var client=new BrokerClient(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Broker:SocketPath"]=socket}).Build(),source);
        var coordinator=new ApplicationRecoveryService(client,f.Clock,NullLogger<ApplicationRecoveryService>.Instance);
        await coordinator.Reconcile(default);Assert.Equal("credential_unavailable",f.Record.Outcome);Assert.Equal(0,f.Engine.Starts);
        source.Value=f.Credential with { Version=3,Token=new string('c',64) };f.Clock.Advance(31);
        await coordinator.Reconcile(default);Assert.Equal(3,f.Delivery.Packet!.Credentials[0].Version);Assert.Equal(1,f.Engine.Starts);
        await f.Docker.ChangeState(f.Id,false,default);await coordinator.Reconcile(default);Assert.Equal(1,f.Engine.Starts);
        await server.StopAsync();
    }
    [Fact]
    public async Task InterruptedBootstrapIsStoppedAndRedeliveredRatherThanAssumedReady()
    {
        using var f = new Fixture();
        f.Store.Save(f.Record with { Attempts = 1, Outcome = "recovering", StableSince = null });
        f.Engine.State = "running";
        var incomplete = await f.Status(); Assert.Equal("startup_incomplete",incomplete.Outcome); Assert.Null(incomplete.StableSince);
        await f.Docker.ReconcileLifecycle(default); Assert.Equal("exited",f.Engine.State); Assert.Equal(1,f.Record.Attempts);
        var ready = await f.Status();
        await f.Docker.Recover(f.Id,new(ready.Revision,[f.Credential]),default);Assert.Equal(2,f.Record.Attempts);Assert.Equal("observing",f.Record.Outcome);
        // Even with an exhausted budget, an unacknowledged leftover process is stopped.
        f.Store.Save(f.Record with { Attempts = 5, Outcome = "recovering", StableSince = null });
        await f.Docker.ReconcileLifecycle(default); Assert.Equal("exited",f.Engine.State);Assert.Equal("suspended",f.Record.Outcome);
    }

    [Theory]
    [InlineData("running", true)]
    [InlineData("exited", false)]
    public async Task LegacyRecordsRequireExplicitOptInAndAdoptCurrentIntentOnce(string state,bool desired)
    {
        using var f = new Fixture();
        var oldJson = JsonSerializer.Serialize(new { Manifest = new { Name = "test-app", Image = "registry/app@sha256:"+new string('b',64), Resources = new { PersistentData = true, Credentials = new[]{"bridge"} } }, ContainerId = f.Id, Credentials = new[]{f.Credential}, LastDeliveredAt = DateTimeOffset.UtcNow },StateFiles.Json);
        StateFiles.WriteAtomic(Path.Combine(f.Root,f.Id+".protected"),f.Protection.CreateProtector("PiDock.ManagedAppBootstrap.v1").Protect(oldJson));
        f.Engine.State = state;
        Assert.False(f.Record.RecoveryEnabled);Assert.Equal(0,f.Record.Revision);
        await f.Docker.SetRecovery(f.Id,true,default);
        Assert.Equal(desired,f.Record.DesiredRunning);Assert.True(f.Record.RecoveryEnabled);
        await f.Docker.ChangeState(f.Id,false,default);
        await f.Docker.SetRecovery(f.Id,true,default);Assert.False(f.Record.DesiredRunning);
    }

    private sealed class Source:IApplicationCredentialSource
    {public ApplicationCredentialFile? Value;public ApplicationCredentialResolution Resolve(string app,string id)=>new(true,Value);}
    private sealed class Clock:TimeProvider
    {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;public void Advance(int seconds)=>Now=Now.AddSeconds(seconds);}
    private sealed class Delivery:IApplicationBootstrapDelivery
    {
        public bool Fail;public ApplicationBootstrapPacket? Packet;
        public async Task Deliver(string id,ApplicationBootstrapPacket packet,Func<Task> start,CancellationToken ct)
        {Packet=packet;await start();if(Fail)throw new IOException("Test acknowledgement failure.");}
    }
    private sealed class Engine(string id):HttpMessageHandler
    {
        public string State="exited";public int Starts;public bool Managed=true;public List<string> Requests=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;Requests.Add(path);object result=new{};var code=HttpStatusCode.OK;
            if(path.EndsWith("/containers/json",StringComparison.Ordinal))result=new[]{new ApplicationSummary(id,["pidock-test"],"image",State,State,Managed?new(){["ai.knowledgelocker.pidock.managed.resources"]="v1"}:[])};
            if(path.EndsWith("/start",StringComparison.Ordinal)){State="running";Starts++;}
            if(path.EndsWith("/stop",StringComparison.Ordinal)){if(State=="exited")code=HttpStatusCode.NotModified;State="exited";}
            return Task.FromResult(new HttpResponseMessage(code){Content=new StringContent(JsonSerializer.Serialize(result),Encoding.UTF8,"application/json")});
        }
    }
    private sealed class Fixture:IDisposable
    {
        public string Root {get;}=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pl-"+Guid.NewGuid().ToString("N"));
        public string Id {get;}=new('a',64);public Clock Clock {get;}=new();public Delivery Delivery {get;}=new();
        public IDataProtectionProvider Protection {get;}=new EphemeralDataProtectionProvider();
        public ManagedDeploymentStore Store {get;}public Engine Engine {get;}public HttpClient Http {get;}public DockerOperations Docker {get;}
        public ApplicationCredentialFile Credential {get;}=new(1,"test-app","bridge",1,"ui-bridge",new string('b',64),DateTimeOffset.UtcNow.AddDays(1),[]);
        public ManagedDeployment Record=>Store.Find(Id)!;
        public Fixture()
        {
            Store=new(Protection,Root);Engine=new(Id);Http=new(Engine){BaseAddress=new("http://localhost")};Docker=new(Http,Store,Delivery,Clock,true);
            Store.Save(new(new("test-app","registry/app@sha256:"+new string('b',64),Resources:new(true,["bridge"],true)),Id,[Credential],DateTimeOffset.MinValue,true,true,1,Outcome:"waiting"));
        }
        public async Task<ApplicationLifecycleStatus> Status()=>Assert.Single(await Docker.LifecycleStatus(default));
        public void Dispose(){Http.Dispose();Directory.Delete(Root,true);}
    }
}
