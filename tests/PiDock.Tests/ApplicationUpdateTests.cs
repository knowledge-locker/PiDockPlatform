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

public sealed class ApplicationUpdateTests
{
    [Fact]
    public async Task SelectedRuntimeCanInitializeIoWhileUpdateAndRollbackVerifyReadiness()
    {
        using var f=new Fixture();await f.Queue();
        Assert.True(Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Allowed);
        await f.Step();Assert.Equal("staged",f.Plan.Phase);
        Assert.True(Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Allowed);
        await f.Step();Assert.Equal("verifying",f.Plan.Phase);
        Assert.False(Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Allowed); // No usable health yet.
        var starting=f.Store.Find(f.Plan.ActiveId)!;
        f.Store.Save(starting with{Health=new("starting",false,true,f.Clock.Now,f.Clock.Now,0,"not_ready",false)});
        Assert.True(Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Allowed); // I/O can initialize before Ready.
        f.Ready();Assert.True(Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Allowed);
        var verifying=f.Plan;f.Updates.Save(verifying with{Phase="switching"});
        Assert.False(Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Allowed);
        f.Updates.Save(verifying);await f.Step();f.Clock.Advance(5);f.Ready();await f.Step();
        Assert.Equal("committed",f.Plan.Phase);
        await f.Docker.RequestRollback(f.Plan.ActiveId,new(f.Plan.OperationId),default);await f.Step();
        Assert.Equal("rollback_verifying",f.Plan.Phase);f.Ready();
        Assert.True(Assert.Single((await f.Docker.RuntimeAccess(default)).Apps).Allowed);
    }
    [Fact]
    public async Task HealthyReplacementCommitsAfterStableReadyAndRetainsOneHiddenPreviousVersion()
    {
        using var f=new Fixture();await f.Queue();await f.Step();Assert.Equal("staged",f.Plan.Phase);Assert.Equal("running",f.Engine.Containers[f.OriginalId].State);Assert.Empty(f.Engine.Stops);
        Assert.Single(await f.Docker.List(default));Assert.DoesNotContain(f.Token,f.Engine.LastCreate!);
        using(var body=JsonDocument.Parse(f.Engine.LastCreate!)){Assert.True(body.RootElement.GetProperty("hostConfig").GetProperty("mounts")[0].GetProperty("volumeOptions").GetProperty("noCopy").GetBoolean());}
        await f.Step();Assert.Equal("verifying",f.Plan.Phase);Assert.Equal("exited",f.Engine.Containers[f.OriginalId].State);
        var visible=Assert.Single(await f.Docker.List(default));Assert.Equal(f.Plan.CandidateId,visible.Id);
        f.Ready();await f.Step();Assert.Equal("verifying",f.Plan.Phase);
        f.Clock.Advance(5);f.Ready();await f.Step();Assert.Equal("committed",f.Plan.Phase);
        var status=Assert.Single(await f.Docker.UpdateStatus(default));Assert.True(status.CanRollback);Assert.Equal(f.Old,status.PreviousImage);Assert.Equal(f.New,status.CurrentImage);
        Assert.Equal(2,f.Engine.Containers.Count);Assert.DoesNotContain("/volumes/",string.Join('\n',f.Engine.Deletes));
    }
    [Fact]
    public async Task UnreadyReplacementRollsBackWithFreshCredentialsAndCleansOnlyFailedContainer()
    {
        using var f=new Fixture(credentials:true);await f.Queue();await f.Step();
        var fresh=f.Credential with{Version=2,Token=new string('e',64)};await f.Step([fresh]);Assert.Equal(fresh.Token,f.Delivery.Packets.Last().Credentials[0].Token);
        f.Clock.Advance(31);await f.Step([fresh]);Assert.Equal("rollback_pending",f.Plan.Phase);
        var rotated=fresh with{Version=3,Token=new string('f',64)};await f.Step([rotated]);Assert.Equal("rollback_verifying",f.Plan.Phase);Assert.Equal(rotated.Token,f.Delivery.Packets.Last().Credentials[0].Token);
        f.Ready();await f.Step([rotated]);f.Clock.Advance(5);f.Ready();await f.Step([rotated]);await f.Step([rotated]);
        Assert.Equal("rolled_back",f.Plan.Phase);Assert.Equal(f.OriginalId,Assert.Single(await f.Docker.List(default)).Id);Assert.Single(f.Engine.Containers);
        Assert.DoesNotContain(rotated.Token,JsonSerializer.Serialize(await f.Docker.UpdateStatus(default)));
        Assert.DoesNotContain(rotated.Token,File.ReadAllText(Path.Combine(f.Root,"updates","app.protected")));
    }
    [Fact]
    public async Task PullFailureLeavesCurrentVersionAndDependentsRunning()
    {
        using var f=new Fixture();f.AddChild();f.Engine.PullError=true;await f.Queue();await f.Step();Assert.Equal("stage_cleanup",f.Plan.Phase);await f.Step();
        Assert.Equal("failed",f.Plan.Phase);Assert.Empty(f.Engine.Stops);Assert.Equal("running",f.Engine.Containers[f.OriginalId].State);Assert.Equal("running",f.Engine.Containers[f.ChildId!].State);
    }
    [Fact]
    public async Task StopCanAbortAnInFlightPullAndNeverStartsEitherVersion()
    {
        using var f=new Fixture();f.Engine.PullGate=new(TaskCreationOptions.RunContinuationsAsynchronously);await f.Queue();
        var advancing=f.Step();await f.Engine.PullEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.Docker.ChangeState(f.OriginalId,false,default);Assert.False(f.Store.Find(f.OriginalId)!.DesiredRunning);
        f.Engine.PullGate.SetResult();await advancing;await f.Step();Assert.Empty(f.Delivery.Packets);Assert.Single(f.Engine.Containers);Assert.Equal("rolled_back",f.Plan.Phase);
    }
    [Fact]
    public async Task ProviderUpdateStopsChildBeforeProviderAndBlocksChildUntilCommit()
    {
        using var f=new Fixture();f.AddChild();await f.Queue();await f.Step();
        Assert.True(Assert.Single((await f.Docker.LifecycleStatus(default)).Single(s=>s.AppId=="child").Dependencies!).Satisfied);
        await f.Step();Assert.Equal(new[]{f.ChildId!,f.OriginalId},f.Engine.Stops);
        Assert.Equal("updating",Assert.Single((await f.Docker.LifecycleStatus(default)).Single(s=>s.AppId=="child").Dependencies!).Reason);
        f.Ready();await f.Step();f.Clock.Advance(5);f.Ready();await f.Step();
        var child=(await f.Docker.LifecycleStatus(default)).Single(s=>s.AppId=="child");Assert.True(Assert.Single(child.Dependencies!).Satisfied);Assert.True(child.PendingStart);
    }
    [Fact]
    public async Task FailedRollbackStaysBlockedUntilOwnerRetryOrStop()
    {
        using var f=new Fixture();await f.Queue();await f.Step();await f.Step();f.Clock.Advance(31);await f.Step();
        f.Delivery.FailImage=f.Old;await f.Step();Assert.Equal("rollback_failed",f.Plan.Phase);var count=f.Delivery.Packets.Count;
        await f.Step();Assert.Equal(count,f.Delivery.Packets.Count);Assert.True(Assert.Single(await f.Docker.UpdateStatus(default)).CanRollback);
        await f.Docker.ChangeState(f.Plan.ActiveId,false,default);Assert.False(f.Store.Find(f.OriginalId)!.DesiredRunning);Assert.Equal("rollback_cleanup",f.Plan.Phase);
    }
    [Fact]
    public async Task OwnerRollbackPreservesStoppedIntentAndNeverBootstraps()
    {
        using var f=new Fixture();await f.Commit();var count=f.Delivery.Packets.Count;
        await f.Docker.ChangeState(f.Plan.ActiveId,false,default);await f.Docker.RequestRollback(f.Plan.ActiveId,new(f.Plan.OperationId),default);
        await f.Step();await f.Step();Assert.Equal("rolled_back",f.Plan.Phase);Assert.Equal(count,f.Delivery.Packets.Count);Assert.False(f.Store.Find(f.OriginalId)!.DesiredRunning);
    }
    [Fact]
    public async Task DiscardRequiresSelectedVersionAndRetainsAppDataBeforeAnotherUpdate()
    {
        using var f=new Fixture();await f.Commit();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Docker.QueueUpdate(f.Plan.ActiveId,new(Guid.NewGuid(),f.New,f.Bad),default));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Docker.DiscardPrevious(f.Plan.ActiveId,new(Guid.NewGuid()),default));
        await f.Docker.DiscardPrevious(f.Plan.ActiveId,new(f.Plan.OperationId),default);await f.Step();Assert.Equal("accepted",f.Plan.Phase);Assert.Single(f.Engine.Containers);
        Assert.All(f.Engine.Deletes.Where(p=>p.Contains("/containers/",StringComparison.Ordinal)),p=>Assert.Contains("v=0",p));
        f.Ready();await f.Docker.QueueUpdate(f.Plan.ActiveId,new(Guid.NewGuid(),f.New,f.Bad),default);Assert.Equal("queued",f.Plan.Phase);
    }
    [Fact]
    public async Task MaintenanceAbortsUpdateRestoresStoppedOldVersionAndKeepsStartupBarrier()
    {
        using var f=new Fixture();await f.Queue();await f.Step();await f.Step();await f.Docker.PauseApplications(default);
        Assert.Equal(f.OriginalId,f.Plan.ActiveId);Assert.True(f.Docker.OrchestrationStatus().Paused);Assert.True(f.Store.Find(f.OriginalId)!.PendingStart);
        var count=f.Delivery.Packets.Count;await f.Step();Assert.Equal(count,f.Delivery.Packets.Count);
        await f.Docker.ResumeApplications(default);await f.Step();Assert.Equal("rolled_back",f.Plan.Phase);
    }
    [Fact]
    public async Task ProtectedJournalReopensAndInterruptedStartupRollsBackRatherThanAssumingReadiness()
    {
        using var f=new Fixture();await f.Queue();await f.Step();await f.Step();
        var candidate=f.Store.Find(f.Plan.ActiveId)!;f.Store.Save(candidate with{Outcome="starting",StableSince=null});
        var reopened=new ApplicationUpdateStore(f.Protection,Path.Combine(f.Root,"updates"));var docker=new DockerOperations(f.Http,f.Store,f.Delivery,f.Clock,true,true,f.Shutdown,true,reopened);
        var status=Assert.Single(await docker.UpdateStatus(default));await docker.AdvanceUpdate("app",new(status.OperationId!.Value,status.Revision,[]),default);
        Assert.Equal("rollback_pending",f.Plan.Phase);Assert.Equal(f.OriginalId,f.Plan.OriginalId);
    }
    [Fact]
    public async Task CredentialsUnavailableWaitsWithoutStoppingCurrentVersion()
    {
        using var f=new Fixture(credentials:true);await f.Queue();await f.Step();await f.Step(null);
        Assert.Equal("staged",f.Plan.Phase);Assert.Equal("credential_unavailable",f.Plan.Outcome);Assert.Empty(f.Engine.Stops);
        f.Clock.Advance(31);await f.Step([f.Credential]);Assert.Equal("verifying",f.Plan.Phase);
    }
    [Fact]
    public async Task DeadlineAndObservationGapCannotCommitLateOrUnobservedReadiness()
    {
        using var f=new Fixture();await f.Queue();await f.Step();await f.Step();f.Ready();await f.Step();
        f.Clock.Advance(21);f.Ready();await f.Step();Assert.Equal("verifying",f.Plan.Phase);Assert.Equal(f.Clock.Now,f.Plan.ReadySince);
        f.Clock.Advance(10);f.Ready();await f.Step();Assert.Equal("rollback_pending",f.Plan.Phase);
    }
    [Fact]
    public async Task OptInImmutabilityStaleRequestsAndOperationIdsAreEnforced()
    {
        using var f=new Fixture();var disabled=new DockerOperations(f.Http,f.Store,f.Delivery,f.Clock,true,true,f.Shutdown,false,f.Updates);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>disabled.QueueUpdate(f.OriginalId,new(Guid.NewGuid(),f.Old,f.New),default));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Docker.QueueUpdate(f.OriginalId,new(Guid.NewGuid(),f.Bad,f.New),default));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Docker.QueueUpdate(f.OriginalId,new(Guid.NewGuid(),f.Old,"image:latest"),default));
        var id=Guid.NewGuid();await f.Docker.QueueUpdate(f.OriginalId,new(id,f.Old,f.New),default);await f.Docker.QueueUpdate(f.OriginalId,new(id,f.Old,f.New),default);
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Docker.QueueUpdate(f.OriginalId,new(id,f.Old,f.Bad),default));Assert.Equal(0,f.Engine.Pulls);
    }
    [Fact]
    public async Task MissingCandidateRemainsVisibleToWorkerAndRestoresPreviousContainer()
    {
        using var f=new Fixture();await f.Queue();await f.Step();await f.Step();f.Engine.Containers.Remove(f.Plan.CandidateId!);
        Assert.Empty(await f.Docker.List(default));Assert.Single(await f.Docker.UpdateStatus(default));
        await f.Step();await f.Step();Assert.Equal("rollback_verifying",f.Plan.Phase);Assert.Equal(f.OriginalId,Assert.Single(await f.Docker.List(default)).Id);
    }
    [Fact]
    public async Task ExistingHealthAppCanOptInAndPolicyIsFrozenDuringAnUpdate()
    {
        using var f=new Fixture();var original=f.Store.Find(f.OriginalId)!;
        f.Store.Save(original with{Manifest=original.Manifest with{Resources=original.Manifest.Resources! with{Updates=null}}});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Queue());
        await f.Docker.ConfigureUpdatePolicy(f.OriginalId,new(new("backward-compatible",30,5)),default);await f.Queue();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Docker.ConfigureUpdatePolicy(f.OriginalId,new(null),default));
        Assert.Throws<ArgumentException>(()=>new ApplicationUpdatePolicy("destructive").Validate());
    }
    [Fact]
    public async Task SavedActivePointerAndUpdateBarrierRemainWhenFeatureIsDisabled()
    {
        using var f=new Fixture();await f.Queue();await f.Step();await f.Step();
        var disabled=new DockerOperations(f.Http,f.Store,f.Delivery,f.Clock,true,true,f.Shutdown,false,f.Updates);
        Assert.Equal(f.Plan.CandidateId,Assert.Single(await disabled.List(default)).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>disabled.Lifecycle(f.Plan.ActiveId,true,false,[],default));
        await disabled.ChangeState(f.Plan.ActiveId,false,default);Assert.False(f.Store.Find(f.OriginalId)!.DesiredRunning);
    }
    [Fact]
    public async Task RuntimeRecoveryPolicyIsPreservedInsteadOfResetToInitialManifestDefault()
    {
        using var f=new Fixture();var original=f.Store.Find(f.OriginalId)!;
        f.Store.Save(original with{RecoveryEnabled=false});await f.Queue();await f.Step();await f.Step();Assert.False(f.Store.Find(f.Plan.ActiveId)!.RecoveryEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Docker.SetRecovery(f.Plan.ActiveId,true,default));
    }
    [Fact]
    public async Task HostWorkerUsesFreshVaultMaterialOverPrivateUnixTransport()
    {
        using var f=new Fixture(credentials:true);await f.Queue();
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();var socket=Path.Combine(f.Root,"u.sock");builder.WebHost.ConfigureKestrel(k=>k.ListenUnixSocket(socket));
        await using var server=builder.Build();server.MapGet("/applications/updates/support",()=>f.Docker.UpdateSupport());server.MapGet("/applications/updates",(CancellationToken ct)=>f.Docker.UpdateStatus(ct));
        server.MapPost("/applications/updates/{app}/advance",async(string app,ApplicationUpdateAdvance request,CancellationToken ct)=>{await f.Docker.AdvanceUpdate(app,request,ct);return Microsoft.AspNetCore.Http.Results.Ok();});await server.StartAsync();
        var source=new Source{Value=f.Credential};using var broker=new BrokerClient(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Broker:SocketPath"]=socket}).Build(),source);
        var worker=new ApplicationUpdateService(broker,f.Clock,NullLogger<ApplicationUpdateService>.Instance);await worker.Reconcile(default);Assert.Equal("staged",f.Plan.Phase);
        source.Value=f.Credential with{Version=4,Token=new string('e',64)};await worker.Reconcile(default);
        Assert.Equal(4,f.Delivery.Packets.Last().Credentials[0].Version);Assert.Equal(source.Value.Token,f.Delivery.Packets.Last().Credentials[0].Token);
        await server.StopAsync();
    }
    private sealed class Source:IApplicationCredentialSource{public ApplicationCredentialFile? Value;public ApplicationCredentialResolution Resolve(string app,string id)=>new(true,Value);}
    private sealed class Clock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;public void Advance(int seconds)=>Now=Now.AddSeconds(seconds);}
    private sealed record Container(string Id,string Name,string Image,string State,string Network,Dictionary<string,string> Labels);
    private sealed class Engine:HttpMessageHandler
    {
        public Dictionary<string,Container> Containers=[];public Dictionary<string,Dictionary<string,string>> Networks=[];public Dictionary<string,string> NetworkNames=[];
        public List<string> Stops=[],Deletes=[];public string? LastCreate;public bool PullError;public int Pulls;private int sequence=10;
        public TaskCompletionSource? PullGate;public TaskCompletionSource PullEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var uri=request.RequestUri!;var path=uri.AbsolutePath;object result=new{};var code=HttpStatusCode.OK;
            if(path.EndsWith("/containers/json",StringComparison.Ordinal))result=Containers.Values.Select(c=>new ApplicationSummary(c.Id,[c.Name],c.Image,c.State,c.State,c.Labels)).ToArray();
            else if(path.EndsWith("/images/create",StringComparison.Ordinal)){Pulls++;PullEntered.TrySetResult();if(PullGate is not null)await PullGate.Task.WaitAsync(ct);if(PullError)result=new{error="Test registry error"};}
            else if(path.EndsWith("/networks/create",StringComparison.Ordinal))
            {var body=JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));var id=(sequence++).ToString("x64");Networks[id]=body.GetProperty("labels").Deserialize<Dictionary<string,string>>()!;NetworkNames[body.GetProperty("name").GetString()!]=id;result=new{Id=id};}
            else if(path.EndsWith("/containers/create",StringComparison.Ordinal))
            {
                LastCreate=await request.Content!.ReadAsStringAsync(ct);var body=JsonSerializer.Deserialize<JsonElement>(LastCreate);var id=(sequence++).ToString("x64");var name=Uri.UnescapeDataString(uri.Query.Split("name=")[1]);
                Containers[id]=new(id,name,body.GetProperty("image").GetString()!,"created",body.GetProperty("hostConfig").GetProperty("networkMode").GetString()!,body.GetProperty("labels").Deserialize<Dictionary<string,string>>()!);result=new{Id=id};
            }
            else if(path.Contains("/containers/",StringComparison.Ordinal))
            {
                var key=path.Split("/containers/")[1].Split('/')[0];var item=Containers.Values.SingleOrDefault(c=>c.Id==key||c.Name==key);
                if(item is null)code=HttpStatusCode.NotFound;
                else if(request.Method==HttpMethod.Delete){Deletes.Add(uri.PathAndQuery);Containers.Remove(item.Id);}
                else if(path.EndsWith("/json",StringComparison.Ordinal))result=new{Id=item.Id,Config=new{Image=item.Image,Labels=item.Labels},HostConfig=new{NetworkMode=item.Network}};
                else if(path.EndsWith("/stop",StringComparison.Ordinal)){Stops.Add(item.Id);Containers[item.Id]=item with{State="exited"};}
                else if(path.EndsWith("/start",StringComparison.Ordinal))Containers[item.Id]=item with{State="running"};
            }
            else if(path.Contains("/networks/",StringComparison.Ordinal))
            {
                var key=path.Split("/networks/")[1];var id=NetworkNames.GetValueOrDefault(key)??key;
                if(!Networks.TryGetValue(id,out var labels))code=HttpStatusCode.NotFound;
                else if(request.Method==HttpMethod.Delete){Deletes.Add(uri.PathAndQuery);Networks.Remove(id);}
                else result=new{Id=id,Driver="bridge",Labels=labels};
            }
            else if(path.Contains("/volumes/",StringComparison.Ordinal))result=new{Driver="local",Labels=new Dictionary<string,string>{["ai.knowledgelocker.pidock.managed"]="true",["ai.knowledgelocker.pidock.managed.app"]="app"},Options=new{}};
            return new(code){Content=new StringContent(JsonSerializer.Serialize(result),Encoding.UTF8,"application/json")};
        }
    }
    private sealed class Delivery(Engine engine):IApplicationBootstrapDelivery
    {
        public List<ApplicationBootstrapPacket> Packets=[];public string? FailImage;
        public async Task Deliver(string id,ApplicationBootstrapPacket packet,Func<Task> start,CancellationToken ct){Packets.Add(packet);await start();if(engine.Containers[id].Image==FailImage)throw new IOException("Fixture bootstrap failed.");}
    }
    private sealed class Fixture:IDisposable
    {
        public string Root {get;}=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pu-"+Guid.NewGuid().ToString("N"));public string OriginalId {get;}=new('a',64);public string? ChildId;
        public string Old {get;}="registry/app@sha256:"+new string('a',64);public string New {get;}="registry/app@sha256:"+new string('b',64);public string Bad {get;}="registry/app@sha256:"+new string('c',64);
        public string Token {get;}=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        public ApplicationCredentialFile Credential=>new(1,"app","bridge",1,"ui-bridge",Token,DateTimeOffset.UtcNow.AddDays(1),[]);
        public Clock Clock {get;}=new();public Engine Engine {get;}=new();public HttpClient Http {get;}public IDataProtectionProvider Protection {get;}=new EphemeralDataProtectionProvider();
        public ManagedDeploymentStore Store {get;}public IApplicationUpdateStore Updates {get;}public IApplicationShutdownStore Shutdown {get;}public Delivery Delivery {get;}public DockerOperations Docker {get;}
        public ApplicationUpdateRecord Plan=>Assert.Single(Updates.ReadAll());private readonly bool credentials;
        public Fixture(bool credentials=false)
        {
            this.credentials=credentials;Http=new(Engine){BaseAddress=new("http://localhost")};Store=new(Protection,Path.Combine(Root,"apps"));Updates=new ApplicationUpdateStore(Protection,Path.Combine(Root,"updates"));Shutdown=new ApplicationShutdownStore(Protection,Path.Combine(Root,"shutdown"));Delivery=new(Engine);Docker=new(Http,Store,Delivery,Clock,true,true,Shutdown,true,Updates);
            var labels=new Dictionary<string,string>{["ai.knowledgelocker.pidock.managed"]="true",["ai.knowledgelocker.pidock.managed.resources"]="v1"};var network=new string('c',64);Engine.Networks[network]=new(){["ai.knowledgelocker.pidock.managed"]="true"};
            Engine.Containers[OriginalId]=new(OriginalId,"pidock-app",Old,"running",network,labels);
            Store.Save(new(new("app",Old,Resources:new(true,credentials?["bridge"]:[],Health:new(15,15,3),Updates:new("backward-compatible",30,5))),OriginalId,credentials?[Credential]:[],Clock.Now,true,true,1,Outcome:"observing",LastDeliveryId:Guid.NewGuid(),Health:Healthy()));
        }
        private ApplicationHealthStatus Healthy()=>new("ready",true,true,Clock.Now,Clock.Now,0,"ready",false);
        public void Ready(){var id=Updates.ReadAll().FirstOrDefault()?.ActiveId??OriginalId;var record=Store.Find(id)!;Store.Save(record with{Health=Healthy(),Revision=record.Revision+1,Outcome="observing"});}
        public void AddChild(){ChildId=new string('d',64);Engine.Containers[ChildId]=new(ChildId,"pidock-child",Old,"running",new string('d',64),new(){["ai.knowledgelocker.pidock.managed"]="true",["ai.knowledgelocker.pidock.managed.resources"]="v1"});Store.Save(new(new("child",Old,Resources:new(ManagedLifecycle:true,Dependencies:[new("app")])),ChildId,[],Clock.Now,true,false,1,Outcome:"observing"));}
        public async Task Queue()=>await Docker.QueueUpdate(OriginalId,new(Guid.NewGuid(),Old,New),default);
        public async Task Step()=>await Step(credentials?[Credential]:[]);
        public async Task Step(ApplicationCredentialFile[]? material){var status=(await Docker.UpdateStatus(default)).Single(s=>s.AppId=="app");await Docker.AdvanceUpdate("app",new(status.OperationId!.Value,status.Revision,material),default);}
        public async Task Commit(){await Queue();await Step();await Step();Ready();await Step();Clock.Advance(5);Ready();await Step();Assert.Equal("committed",Plan.Phase);}
        public void Dispose(){Http.Dispose();Directory.Delete(Root,true);}
    }
}
