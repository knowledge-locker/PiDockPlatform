using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiDock.Agent;
using PiDock.Appliance.Broker;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationOrchestrationTests
{
    [Fact]
    public void GraphOrdersDiamondsAndRejectsCyclesAmbiguousIdsAndSelfReferences()
    {
        var nodes=new[]{Node("leaf",["left","right"]),Node("right",["root"]),Node("left",["root"]),Node("root",[])};
        Assert.Equal(new[]{"root","left","right","leaf"},ApplicationDependencyGraph.Order(nodes).Select(n=>n.AppId));
        Assert.Equal(new[]{"leaf","right","left"},ApplicationDependencyGraph.Dependents(nodes,"root").Select(n=>n.AppId));
        Assert.Throws<ArgumentException>(()=>ApplicationDependencyGraph.Order([Node("a",["b"]),Node("b",["a"])]));
        Assert.Throws<ArgumentException>(()=>ApplicationDependencyGraph.Order([Node("a",[]),Node("a",[])]));
        Assert.Throws<ArgumentException>(()=>ApplicationDependencyGraph.Order([Node("a",["a"])]));
        Assert.Throws<ArgumentException>(()=>new ApplicationResources(Dependencies:[new("app","url")]).Validate());
    }
    [Fact]
    public async Task MissingOrNotReadyDependenciesGateManualStartupWithoutSpendingAttempts()
    {
        using var f=new Fixture();var child=f.Add("child",[new("provider")],"exited");
        await f.Docker.Lifecycle(child.ContainerId,true,false,[],default);
        Assert.True(f.Record("child").PendingStart);Assert.Equal(0,f.Record("child").Attempts);Assert.Empty(f.Delivery.Started);
        Assert.Equal("missing",Assert.Single((await f.Status("child")).Dependencies!).Reason);
        f.Add("provider",[],"running",ready:false);
        var waiting=await f.Status("child");await f.Docker.Recover(child.ContainerId,new(waiting.Revision,[]),default);
        Assert.Equal(0,f.Record("child").Attempts);Assert.Empty(f.Delivery.Started);
        f.Ready("provider");var eligible=await f.Status("child");await f.Docker.Recover(child.ContainerId,new(eligible.Revision,[]),default);
        Assert.Equal(new[]{"child"},f.Delivery.Started);Assert.False(f.Record("child").PendingStart);Assert.Equal(1,f.Record("child").Attempts);
    }
    [Fact]
    public async Task RunningConditionIsExplicitAndReadinessExpires()
    {
        using var f=new Fixture();f.Add("provider",[],"running",health:false);var child=f.Add("child",[new("provider")],"exited");
        Assert.Equal("health_not_configured",Assert.Single((await f.Status("child")).Dependencies!).Reason);
        f.Store.Save(child with{Manifest=child.Manifest with{Resources=child.Manifest.Resources! with{Dependencies=[new("provider","running")]}}});
        await f.Docker.Lifecycle(child.ContainerId,true,false,[],default);Assert.Equal(new[]{"child"},f.Delivery.Started);
        using var readyFixture=new Fixture();readyFixture.Add("provider",[],"running");readyFixture.Add("child",[new("provider")],"running");
        readyFixture.Clock.Now=readyFixture.Clock.Now.AddSeconds(16);
        Assert.False(Assert.Single((await readyFixture.Status("child")).Dependencies!).Satisfied);
        await readyFixture.Docker.ReconcileOrchestration(default);Assert.Equal(new[]{"child"},readyFixture.Engine.Stopped);
    }
    [Fact]
    public async Task ProviderStopStopsTransitiveDependentsFirstAndNeverResurrectsStopIntent()
    {
        using var f=new Fixture();var root=f.Add("root",[],"running");f.Add("middle",[new("root")],"running");f.Add("leaf",[new("middle")],"running");
        var stale=await f.Status("leaf");
        await f.Docker.ChangeState(root.ContainerId,false,default);
        Assert.Equal(new[]{"leaf","middle","root"},f.Engine.Stopped);Assert.False(f.Record("root").DesiredRunning);Assert.True(f.Record("leaf").DesiredRunning);Assert.True(f.Record("leaf").PendingStart);
        await f.Docker.Recover(f.Record("leaf").ContainerId,new(stale.Revision,[]),default);Assert.Empty(f.Delivery.Started);
        await f.Docker.ReconcileOrchestration(default);Assert.Empty(f.Delivery.Started);
        await f.Docker.Lifecycle(root.ContainerId,true,false,[],default);f.Ready("root");
        var middle=await f.Status("middle");await f.Docker.Recover(middle.ContainerId,new(middle.Revision,[]),default);f.Ready("middle");
        var leaf=await f.Status("leaf");await f.Docker.Recover(leaf.ContainerId,new(leaf.Revision,[]),default);
        Assert.Equal(new[]{"root","middle","leaf"},f.Delivery.Started);
    }
    [Fact]
    public async Task DependencyLossSuspendsOnlyDependentsAndPreservesExplicitStops()
    {
        using var f=new Fixture();f.Add("provider",[],"running",ready:false);f.Add("child",[new("provider")],"running");f.Add("unrelated",[],"running");var stopped=f.Add("stopped",[new("provider")],"exited",desired:false);
        await f.Docker.ReconcileOrchestration(default);Assert.Equal(new[]{"child"},f.Engine.Stopped);Assert.True(f.Record("child").PendingStart);Assert.False(f.Record("stopped").PendingStart);
        f.Ready("provider");Assert.True(Assert.Single((await f.Status("child")).Dependencies!).Satisfied);
        await f.Docker.Recover(stopped.ContainerId,new(stopped.Revision,[]),default);Assert.Empty(f.Delivery.Started);
    }
    [Fact]
    public async Task FailedDependentStopRetainsProviderAndCanBeRetriedFromSavedIntent()
    {
        using var f=new Fixture();var provider=f.Add("provider",[],"running");f.Add("child",[new("provider")],"running");f.Engine.FailStop="child";
        await Assert.ThrowsAsync<HttpRequestException>(()=>f.Docker.ChangeState(provider.ContainerId,false,default));
        Assert.False(f.Record("provider").DesiredRunning);Assert.Equal("running",f.Engine.Apps["provider"].State);Assert.DoesNotContain("provider",f.Engine.Stopped);
        f.Engine.FailStop=null;await f.Docker.ReconcileOrchestration(default);Assert.Equal(new[]{"child","provider"},f.Engine.Stopped);
    }
    [Fact]
    public async Task MaintenanceJournalSurvivesBrokerRestartAndRequiresExplicitResume()
    {
        using var f=new Fixture();f.Add("provider",[],"running");f.Add("child",[new("provider")],"running");f.Engine.FailStop="provider";
        await Assert.ThrowsAsync<HttpRequestException>(()=>f.Docker.PauseApplications(default));Assert.Equal(1,f.Journal.Read()!.NextIndex);Assert.True(f.Docker.OrchestrationStatus().Paused);
        f.Engine.FailStop=null;var restarted=f.Restart();await restarted.ReconcileOrchestration(default);
        Assert.Equal("paused",f.Journal.Read()!.Phase);Assert.Equal(new[]{"child","provider"},f.Engine.Stopped);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>restarted.Lifecycle(f.Record("provider").ContainerId,true,false,[],default));
        await restarted.ReconcileOrchestration(default);Assert.True(restarted.OrchestrationStatus().Paused);Assert.Empty(f.Delivery.Started);
        await restarted.ResumeApplications(default);Assert.False(restarted.OrchestrationStatus().Paused);
        var root=(await restarted.LifecycleStatus(default)).Single(a=>a.AppId=="provider");await restarted.Recover(root.ContainerId,new(root.Revision,[]),default);Assert.Equal(new[]{"provider"},f.Delivery.Started);
        var bytes=File.ReadAllText(Path.Combine(f.Root,"journal","shutdown.protected"));Assert.DoesNotContain("maintenance",bytes);Assert.DoesNotContain(root.ContainerId,bytes);
    }
    [Fact]
    public async Task HostShutdownRetainsRequestsAndAutoResumesOnlyOnNewBrokerInstance()
    {
        using var f=new Fixture();f.Add("provider",[],"running");f.Add("child",[new("provider")],"running");f.Add("off",[],"exited",desired:false);
        await f.Docker.HostShutdown(default);Assert.True(f.Docker.OrchestrationStatus().Paused);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Docker.ResumeApplications(default));
        var restarted=f.Restart();await restarted.ReconcileOrchestration(default);Assert.False(restarted.OrchestrationStatus().Paused);
        Assert.True(f.Record("provider").PendingStart);Assert.True(f.Record("child").PendingStart);Assert.False(f.Record("off").PendingStart);
        Assert.Equal(new[]{"child","provider"},f.Engine.Stopped.Where(a=>a!="off"));
    }
    [Fact]
    public async Task ExplicitMaintenancePauseOverridesPendingHostAutoResume()
    {
        using var f=new Fixture();f.Add("app",[],"running");await f.Docker.HostShutdown(default);
        var restarted=f.Restart();await restarted.PauseApplications(default);await restarted.ReconcileOrchestration(default);
        Assert.Equal("maintenance",f.Journal.Read()!.Reason);Assert.True(restarted.OrchestrationStatus().Paused);
    }
    [Fact]
    public async Task QueuedStartupAndDependencyRecoveryWorkWithoutCrashRecoveryPermission()
    {
        using var f=new Fixture(recovery:false);f.Add("provider",[],"running");var child=f.Add("child",[new("provider")],"exited");
        f.Ready("provider",false);await f.Docker.Lifecycle(child.ContainerId,true,false,[],default);
        f.Ready("provider");var waiting=await f.Status("child");await f.Docker.Recover(child.ContainerId,new(waiting.Revision,[]),default);
        Assert.Equal(new[]{"child"},f.Delivery.Started);Assert.False(f.Record("child").PendingStart);
        f.Engine.Apps["child"]=f.Engine.Apps["child"] with{State="exited"};var exited=await f.Status("child");
        await f.Docker.Recover(child.ContainerId,new(exited.Revision,[]),default);Assert.Single(f.Delivery.Started);
    }
    [Fact]
    public async Task InterruptedPendingBootstrapIsStoppedBeforeRedelivery()
    {
        using var f=new Fixture(recovery:false);var app=f.Add("app",[],"running");f.Store.Save(app with{PendingStart=true,StableSince=null,Outcome="starting"});
        await f.Docker.ReconcileOrchestration(default);Assert.Equal("exited",f.Engine.Apps["app"].State);
        var pending=await f.Status("app");await f.Docker.Recover(app.ContainerId,new(pending.Revision,[]),default);Assert.Equal(new[]{"app"},f.Delivery.Started);
    }
    [Fact]
    public async Task NewCycleIsRejectedBeforePullAndMissingDependencyDeploymentQueues()
    {
        using var f=new Fixture();f.Add("a",[new("b")],"created");
        var b=new ApplicationManifest("b","registry/app@sha256:"+new string('b',64),Resources:new(Dependencies:[new("a")]));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Docker.DeployPrepared(new(b,[]),default));Assert.Equal(0,f.Engine.Pulls);
        var queued=new ApplicationManifest("queued","registry/app@sha256:"+new string('b',64),Resources:new(Dependencies:[new("missing")]));
        await f.Docker.DeployPrepared(new(queued,[]),default);Assert.True(f.Record("queued").PendingStart);Assert.Empty(f.Delivery.Started);
    }
    [Fact]
    public async Task ShutdownConfirmsDependentHasStoppedBeforeTouchingProvider()
    {
        using var f=new Fixture();var root=f.Add("root",[],"running");f.Add("child",[new("root")],"running");f.Engine.AcknowledgeOnly="child";
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Docker.ChangeState(root.ContainerId,false,default));
        Assert.Equal("running",f.Engine.Apps["root"].State);Assert.DoesNotContain("root",f.Engine.Stopped);
    }
    [Fact]
    public async Task CoordinatorConvergesQueuedStartsOverUnixTransportWithoutCrashRecovery()
    {
        using var f=new Fixture(recovery:false);f.Add("provider",[],"running",ready:false);var child=f.Add("child",[new("provider")],"exited");
        await f.Docker.Lifecycle(child.ContainerId,true,false,[],default);f.Ready("provider");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();var socket=Path.Combine(f.Root,"o.sock");builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(socket));
        await using var server=builder.Build();
        server.MapGet("/applications/health/support",()=>f.Docker.HealthSupport() with{Enabled=false});
        server.MapGet("/applications/lifecycle/support",()=>f.Docker.LifecycleSupport());
        server.MapGet("/applications/lifecycle",(CancellationToken ct)=>f.Docker.LifecycleStatus(ct));
        server.MapGet("/applications/orchestration/support",()=>f.Docker.OrchestrationSupport());
        server.MapGet("/applications/orchestration/status",()=>f.Docker.OrchestrationStatus());
        server.MapPost("/applications/orchestration/{action}",async(string action,CancellationToken ct)=>{if(action=="pause")await f.Docker.PauseApplications(ct);else if(action=="resume")await f.Docker.ResumeApplications(ct);else await f.Docker.ReconcileOrchestration(ct);return Microsoft.AspNetCore.Http.Results.Ok();});
        server.MapPost("/applications/{id}/converge",async(string id,ApplicationRecoveryRequest request,CancellationToken ct)=>{await f.Docker.Recover(id,request,ct);return Microsoft.AspNetCore.Http.Results.Ok();});
        await server.StartAsync();
        using var broker=new BrokerClient(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Broker:SocketPath"]=socket}).Build());
        var service=new ApplicationRecoveryService(broker,f.Clock,NullLogger<ApplicationRecoveryService>.Instance);await service.Reconcile(default);
        Assert.Equal(new[]{"child"},f.Delivery.Started);
        await broker.OrchestrationControl("pause");Assert.True((await broker.OrchestrationStatus())!.Paused);
        await service.Reconcile(default);Assert.Single(f.Delivery.Started);
        await broker.OrchestrationControl("resume");await service.Reconcile(default);Assert.Equal(new[]{"child","provider"},f.Delivery.Started);
        await server.StopAsync();
    }

    [Fact]
    public async Task DisablingOrchestrationCannotBypassDeclaredDependenciesOrSavedPause()
    {
        using var f=new Fixture();f.Add("provider",[],"running");var child=f.Add("child",[new("provider")],"exited");
        var disabled=new DockerOperations(f.Http,f.Store,f.Delivery,f.Clock,true,false,f.Journal);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>disabled.Lifecycle(child.ContainerId,true,false,[],default));
        f.Store.Save(child with{RecoveryEnabled=true});var waiting=await f.Status("child");await disabled.Recover(child.ContainerId,new(waiting.Revision,[]),default);Assert.Empty(f.Delivery.Started);
        await f.Docker.PauseApplications(default);
        var provider=f.Record("provider");await Assert.ThrowsAsync<InvalidOperationException>(()=>disabled.Lifecycle(provider.ContainerId,true,false,[],default));
        await disabled.Recover(provider.ContainerId,new(provider.Revision,[]),default);Assert.Empty(f.Delivery.Started);
    }

    [Fact]
    public async Task DependencyGapCannotResetRunningProcessRetryBudget()
    {
        using var f=new Fixture();f.Add("provider",[],"running",ready:false);var child=f.Add("child",[new("provider")],"running",health:false);
        f.Store.Save(child with{Attempts=2,StableSince=f.Clock.Now.AddSeconds(-65)});
        var waiting=await f.Status("child");Assert.Equal(2,waiting.Attempts);Assert.Null(waiting.StableSince);Assert.Equal("dependencies_waiting",waiting.Outcome);
        f.Ready("provider");Assert.Equal(2,(await f.Status("child")).Attempts);
        f.Store.Save(f.Record("child") with{StableSince=f.Clock.Now.AddSeconds(-65)});f.Store.Forget(f.Record("provider").ContainerId);
        var unknown=await f.Status("child");Assert.Equal(2,unknown.Attempts);Assert.Null(unknown.StableSince);Assert.Equal("dependency_catalog_unavailable",unknown.Outcome);
    }

    [Fact]
    public async Task RuntimeAuthorityProjectionUsesIntentActualStateBootstrapHealthAndMaintenance()
    {
        using var f = new Fixture(); var app = f.Add("provider", [], "running", health:false);
        Assert.True(Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Allowed);
        f.Store.Save(app with { DesiredRunning = false });
        Assert.Equal("requested_stopped", Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Reason);
        f.Store.Save(app with { LastDeliveryId = null });
        Assert.Equal("bootstrap_incomplete", Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Reason);
        f.Store.Save(app); f.Engine.Apps["provider"] = f.Engine.Apps["provider"] with { State = "exited" };
        Assert.Equal("not_running", Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Reason);
        f.Engine.Apps["provider"] = f.Engine.Apps["provider"] with { State = "running" };
        var unhealthy = app with { Manifest = app.Manifest with { Resources = new(Health:new()) }, Health = new("unhealthy",false,false,f.Clock.Now,f.Clock.Now,3,"liveness_failed",true) };
        f.Store.Save(unhealthy); Assert.Equal("health_unavailable", Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Reason);
        f.Store.Save(unhealthy with { Health = unhealthy.Health! with { State = "starting", Failures = 1 } });
        Assert.Equal("health_unavailable", Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Reason);
        f.Store.Save(app); await f.Docker.PauseApplications(default);
        var paused = await f.Docker.RuntimeAccess(default); Assert.True(paused.Paused); Assert.False(Assert.Single(paused.Apps).Allowed);
    }

    private static ApplicationDependencyNode Node(string id,string[] dependencies)=>new(id,id,dependencies.Select(d=>new ApplicationDependency(d)).ToArray());
    private sealed class Clock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Delivery:IApplicationBootstrapDelivery
    {
        public List<string> Started=[];
        public async Task Deliver(string id,ApplicationBootstrapPacket packet,Func<Task> start,CancellationToken ct){await start();Started.Add(packet.AppId);}
    }
    private sealed class Engine:HttpMessageHandler
    {
        public Dictionary<string,ApplicationSummary> Apps=new(StringComparer.Ordinal);public List<string> Stopped=[];public string? FailStop;public string? AcknowledgeOnly;public int Pulls;
        public string Add(string name,string state){var id=Apps.Count.ToString("x64");Apps[name]=new(id,["pidock-"+name],"image",state,state,new(){["ai.knowledgelocker.pidock.managed.resources"]="v1"});return id;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;object body=new{};
            if(path.EndsWith("/containers/json",StringComparison.Ordinal))body=Apps.Values.ToArray();
            if(path.EndsWith("/images/create",StringComparison.Ordinal))Pulls++;
            if(path.EndsWith("/networks/create",StringComparison.Ordinal))body=new{Id="network"};
            if(path.EndsWith("/containers/create",StringComparison.Ordinal))
            { var json=JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));var name=request.RequestUri.Query.Split("pidock-")[1];body=new{Id=Add(name,"created")};Assert.Equal("SIGTERM",json.GetProperty("stopSignal").GetString()); }
            foreach(var name in Apps.Keys.ToArray())
            {
                if(!path.Contains("/containers/"+Apps[name].Id+"/",StringComparison.Ordinal))continue;
                if(path.EndsWith("/stop",StringComparison.Ordinal))
                {if(FailStop==name)return new(HttpStatusCode.InternalServerError);if(AcknowledgeOnly==name)return new(HttpStatusCode.OK);Stopped.Add(name);Apps[name]=Apps[name] with{State="exited"};}
                if(path.EndsWith("/start",StringComparison.Ordinal))Apps[name]=Apps[name] with{State="running"};
            }
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json")};
        }
    }
    private sealed class Fixture:IDisposable
    {
        public string Root {get;}=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"po-"+Guid.NewGuid().ToString("N"));
        public Clock Clock {get;}=new();public Engine Engine {get;}=new();public HttpClient Http {get;}public ManagedDeploymentStore Store {get;}public IApplicationShutdownStore Journal {get;}public Delivery Delivery {get;}public DockerOperations Docker {get;}private readonly IDataProtectionProvider protection=new EphemeralDataProtectionProvider();private readonly bool recovery;
        public Fixture(bool recovery=true){this.recovery=recovery;Http=new(Engine){BaseAddress=new("http://localhost")};Store=new(protection,Path.Combine(Root,"records"));Journal=new ApplicationShutdownStore(protection,Path.Combine(Root,"journal"));Delivery=new();Docker=Restart();}
        public DockerOperations Restart()=>new(Http,Store,Delivery,Clock,recovery,true,new ApplicationShutdownStore(protection,Path.Combine(Root,"journal")));
        public ManagedDeployment Add(string name,ApplicationDependency[] dependencies,string state,bool ready=true,bool health=true,bool desired=true)
        {
            var id=Engine.Add(name,state);var record=new ManagedDeployment(new(name,"registry/app@sha256:"+new string('b',64),Resources:new(Health:health?new():null,Dependencies:dependencies,PersistentData:true)),id,[],Clock.Now,desired,false,1,StableSince:ready?Clock.Now:null,Outcome:"observing",LastDeliveryId:Guid.NewGuid(),Health:health?new(ready?"ready":"not_ready",ready,true,Clock.Now,Clock.Now,0,"test",false):null);
            Store.Save(record);return record;
        }
        public void Ready(string app,bool ready=true){var record=Record(app);Store.Save(record with{Health=new(ready?"ready":"not_ready",ready,true,Clock.Now,Clock.Now,0,"test",false),Revision=record.Revision+1,Outcome="observing"});}
        public ManagedDeployment Record(string name)=>Store.Find(Engine.Apps[name].Id)!;
        public async Task<ApplicationLifecycleStatus> Status(string name)=>(await Docker.LifecycleStatus(default)).Single(a=>a.AppId==name);
        public void Dispose(){Http.Dispose();Directory.Delete(Root,true);}
    }
}
