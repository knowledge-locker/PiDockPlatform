using PiDock.Core;

namespace PiDock.Appliance.Broker;

public sealed partial class DockerOperations
{
    private bool shuttingDown;
    private sealed record Catalog(Dictionary<string,ManagedDeployment> Records, Dictionary<string,ApplicationSummary> Runtime, ApplicationDependencyNode[] Order);
    public ApplicationOrchestrationSupport OrchestrationSupport() => new(1,orchestrationEnabled && store is not null && delivery is not null && shutdownStore is not null,16,8,180,
        ["Dependencies require known resource-enabled app identities; references do not grant service or hardware authority.",
         "Ready requires fresh declared health; running requires completed bootstrap. Dependencies never start an explicitly stopped provider.",
         "Shutdown uses reverse dependency order and 1–10 seconds per app before Docker may kill it. New containers use SIGTERM; existing containers retain their configured signal.",
         "Broker shutdown pauses resource apps and resumes their saved running requests after broker restart; maintenance pause requires explicit resume.",
         "Power loss, runtime failure, corrupt metadata and forced service termination cannot guarantee graceful application cleanup."]);
    private bool Paused => RemovalBlocksStartup || RestoreBlocksStartup || shuttingDown || shutdownStore?.Read() is { Phase: not "resumed" };
    public ApplicationOrchestrationStatus OrchestrationStatus()
    {
        var plan=shutdownStore?.Read();
        return new(Paused,plan?.Reason,plan?.Phase,plan?.NextIndex??0,plan?.Order.Length??0);
    }
    private void RequireOrchestration() { if(!OrchestrationSupport().Enabled)throw new InvalidOperationException("Application orchestration is disabled."); }
    private void EnsureOpen() { RequireOrchestration();if(Paused)throw new InvalidOperationException("Application startup is paused for coordinated shutdown."); }
    private async Task<Catalog> ReadCatalog(CancellationToken ct)
    {
        var runtime=(await List(ct)).Where(app=>app.Labels?.GetValueOrDefault(Label+".resources")=="v1").ToArray();
        if(runtime.Length>16)throw new InvalidOperationException("Application catalog exceeds the preview limit.");
        var records=new Dictionary<string,ManagedDeployment>(StringComparer.Ordinal);var states=new Dictionary<string,ApplicationSummary>(StringComparer.Ordinal);
        foreach(var app in runtime)
        {
            var record=store?.Find(app.Id)??throw new InvalidOperationException("Application ordering requires readable startup records.");
            if(!records.TryAdd(record.Manifest.Name,record))throw new InvalidOperationException("Application identities are ambiguous.");
            states.Add(record.Manifest.Name,app);
        }
        var ordered=ApplicationDependencyGraph.Order(records.Values.Select(r=>new ApplicationDependencyNode(r.Manifest.Name,r.ContainerId,r.Manifest.Resources!.RequiredApps)));
        return new(records,states,ordered);
    }
    private async Task ValidateDeploymentGraph(ApplicationManifest manifest,CancellationToken ct)
    {
        var catalog=await ReadCatalog(ct);
        var nodes=catalog.Order.AsEnumerable();
        if(manifest.Resources?.Requested==true)nodes=nodes.Append(new(manifest.Name,"pending",manifest.Resources.RequiredApps));
        ApplicationDependencyGraph.Order(nodes);
    }
    private ApplicationDependencyStatus[] DependencyStatuses(ManagedDeployment record,Catalog catalog)
    {
        var memo=new Dictionary<(string,string),string>();
        return record.Manifest.Resources!.RequiredApps.Select(d=>{var reason=Reason(d.AppId,d.Condition);return new ApplicationDependencyStatus(d.AppId,d.Condition,reason=="satisfied",reason);}).ToArray();
        string Reason(string app,string condition)
        {
            if(memo.TryGetValue((app,condition),out var existing))return existing;
            string result;
            if(!catalog.Records.TryGetValue(app,out var target))result="missing";
            else if(UpdateBlocksDependency(app))result="updating";
            else if(!target.DesiredRunning && target.Revision>0)result="requested_stopped";
            else if(catalog.Runtime[app].State!="running")result="not_running";
            else if(Incomplete(target))result="bootstrap_incomplete";
            else if(target.Manifest.Resources!.RequiredApps.Any(d=>Reason(d.AppId,d.Condition)!="satisfied"))result="dependency_unavailable";
            else if(condition=="ready" && target.Manifest.Resources.Health is null)result="health_not_configured";
            else if(condition=="ready" && !ProjectHealth(target,"running").Ready)result="not_ready";
            else result="satisfied";
            memo[(app,condition)]=result;return result;
        }
    }
    private async Task<bool> DependenciesReady(ManagedDeployment record,CancellationToken ct) => !Paused && DependencyStatuses(record,await ReadCatalog(ct)).All(d=>d.Satisfied);
    private async Task StopContainer(ManagedDeployment record,CancellationToken ct,bool allowMissing=false)
    {
        var current=(await List(ct)).SingleOrDefault(a=>a.Id==record.ContainerId);
        if(current is null){if(allowMissing)return;throw new InvalidOperationException("Managed application disappeared during shutdown.");}
        if(current.State is not ("running" or "restarting" or "paused"))return;
        using var stopped=await engine.PostAsync($"{Api}/containers/{record.ContainerId}/stop?t={record.Manifest.Resources!.ShutdownSeconds}",null,ct);
        if(stopped.StatusCode!=System.Net.HttpStatusCode.NotModified)stopped.EnsureSuccessStatusCode();
        var actual=(await List(ct)).SingleOrDefault(a=>a.Id==record.ContainerId);
        if(actual?.State is "running" or "restarting" or "paused")throw new InvalidOperationException("Dependent application did not stop; provider retained.");
    }
    private async Task StopDependents(string appId,CancellationToken ct)
    {
        var catalog=await ReadCatalog(ct);
        foreach(var node in ApplicationDependencyGraph.Dependents(catalog.Order,appId))
        {
            var record=store!.Find(node.ContainerId)!;
            if(catalog.Runtime[node.AppId].State is not ("running" or "restarting" or "paused"))continue;
            store.Save(record with{PendingStart=record.DesiredRunning,StableSince=null,Revision=checked(record.Revision+1),Outcome="dependencies_waiting"});
            await StopContainer(record,ct);
        }
    }
    public async Task ReconcileOrchestration(CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(180));ct=deadline.Token;
        RequireOrchestration();await deploymentLock.WaitAsync(ct);
        try
        {
            if(shuttingDown)return;
            var plan=shutdownStore!.Read();
            if(plan is {Phase:not "resumed"})
            {
                await ContinuePlan(ct);
                plan=shutdownStore.Read()!;
                if(!RestoreBlocksStartup && !RemovalBlocksStartup && plan.Reason=="host_shutdown" && plan.Phase=="paused")shutdownStore.Save(plan with{Phase="resumed"});
                else return;
            }
            var catalog=await ReadCatalog(ct);
            foreach(var node in catalog.Order.Reverse())
            {
                var record=store!.Find(node.ContainerId)!;
                if(catalog.Runtime[node.AppId].State is not ("running" or "restarting" or "paused"))continue;
                if(UpdateBlocks(node.AppId))continue;
                if(record.DesiredRunning && !(record.PendingStart && Incomplete(record)) && DependencyStatuses(record,catalog).All(d=>d.Satisfied))continue;
                if(record.Revision==0 && !record.DesiredRunning)continue; // Legacy records have no saved Stop intent.
                store.Save(record with{PendingStart=record.DesiredRunning,StableSince=null,Revision=checked(record.Revision+1),Outcome=record.DesiredRunning ? "dependencies_waiting" : "stopped_by_operator"});
                await StopContainer(record,ct);
            }
        }
        finally{deploymentLock.Release();}
    }
    public async Task PauseApplications(CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(180));ct=deadline.Token;
        RequireOrchestration();await deploymentLock.WaitAsync(ct);
        try { await BeginPlan("maintenance",ct);await ContinuePlan(ct); }
        finally{deploymentLock.Release();}
    }
    public async Task ResumeApplications(CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(180));ct=deadline.Token;
        RequireOrchestration();await deploymentLock.WaitAsync(ct);
        try
        {
            if(shuttingDown)throw new InvalidOperationException("Broker is shutting down.");
            if(RestoreBlocksStartup || RemovalBlocksStartup)throw new InvalidOperationException("Complete or repair the app data restore or removal before resuming apps.");
            if(shutdownStore!.Read() is not {Phase:not "resumed"})return;
            await ContinuePlan(ct);var plan=shutdownStore.Read()!;shutdownStore.Save(plan with{Phase="resumed"});
        }
        finally{deploymentLock.Release();}
    }
    public async Task HostShutdown(CancellationToken ct)
    {
        if(!OrchestrationSupport().Enabled)return;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(180));ct=deadline.Token;
        await deploymentLock.WaitAsync(ct);
        try { shuttingDown=true;await BeginPlan("host_shutdown",ct);await ContinuePlan(ct); }
        finally{deploymentLock.Release();}
    }
    private async Task BeginPlan(string reason,CancellationToken ct)
    {
        if(shutdownStore!.Read() is {Phase:not "resumed"} active)
        {
            if(reason=="maintenance" && active.Reason=="host_shutdown")shutdownStore.Save(active with{Reason="maintenance"});
            return;
        }
        await InterruptUpdates(ct);
        var catalog=await ReadCatalog(ct);
        shutdownStore.Save(new(Guid.NewGuid(),reason,"stopping",catalog.Order.Reverse().Select(n=>n.ContainerId).ToArray(),0,Now));
    }
    private async Task ContinuePlan(CancellationToken ct)
    {
        var plan=shutdownStore!.Read();if(plan is null || plan.Phase!="stopping")return;
        while(plan.NextIndex<plan.Order.Length)
        {
            ct.ThrowIfCancellationRequested();
            var record=store!.Find(plan.Order[plan.NextIndex])??throw new InvalidOperationException("Shutdown startup record unavailable.");
            bool desired=record.Revision==0 ? (await List(ct)).Any(a=>a.Id==record.ContainerId&&a.State=="running") : record.DesiredRunning;
            store.Save(record with{DesiredRunning=desired,PendingStart=desired,StableSince=null,Revision=checked(record.Revision+1),Outcome="shutdown_paused"});
            await StopContainer(record,ct,allowMissing:true);
            plan=plan with{NextIndex=plan.NextIndex+1};shutdownStore.Save(plan);
        }
        shutdownStore.Save(plan with{Phase="paused"});
    }
}
