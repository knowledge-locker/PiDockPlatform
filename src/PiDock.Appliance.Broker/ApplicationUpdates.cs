using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

public sealed partial class DockerOperations
{
    private readonly SemaphoreSlim updateStepLock=new(1);
    public ApplicationUpdateSupport UpdateSupport()=>new(1,updatesEnabled&&OrchestrationSupport().Enabled&&updateStore is not null,"image-only",1,
        ["Requires declared health and backward-compatible data; rollback restores an image, never a data snapshot or migration.",
         "One active update globally and one retained previous container per app. Discard the retained version before the next update.",
         "Settings, credentials, ports, app identity and dependency declarations remain unchanged. Current credentials are redelivered.",
         "A failed rollback remains blocked for Owner attention. Image signatures, package rollout and data/schema rollback remain separate work."]);
    private ApplicationUpdateRecord? UpdateFor(string app)=>updateStore?.ReadAll().SingleOrDefault(p=>p.AppId==app);
    private bool UpdateBlocks(string app)=>UpdateFor(app) is {} plan&&ApplicationUpdateRules.BlocksLifecycle(plan.Phase);
    private bool UpdateBlocksDependency(string app)=>UpdateFor(app) is {} plan&&ApplicationUpdateRules.BlocksDependencies(plan.Phase);
    public async Task<ApplicationSummary[]> List(CancellationToken ct)
    {
        var raw=await RawList(ct);var plans=updateStore?.ReadAll()??[];
        var active=plans.Select(p=>p.ActiveId).ToHashSet(StringComparer.Ordinal);
        var hidden=plans.SelectMany(p=>new[]{p.OriginalId,p.CandidateId}).Where(id=>id is not null&&!active.Contains(id)).ToHashSet(StringComparer.Ordinal);
        return raw.Where(a=>!hidden.Contains(a.Id)&&(!(a.Labels?.TryGetValue(Label+".update",out var marker)==true&&Guid.TryParseExact(marker,"N",out _))||active.Contains(a.Id))).ToArray();
    }
    public async Task<ApplicationUpdateStatus[]> UpdateStatus(CancellationToken ct)
    {
        {
            var result=new List<ApplicationUpdateStatus>();
            foreach(var app in await List(ct))
            {
                var record=store?.Find(app.Id);if(record is null)continue;
                var plan=UpdateFor(record.Manifest.Name);var policy=record.Manifest.Resources?.Updates;
                bool idle=plan is null||plan.Phase is "accepted" or "rolled_back" or "failed";
                result.Add(new(record.Manifest.Name,app.Id,record.Manifest.Image,plan?.Phase=="committed"?plan.OriginalManifest.Image:null,
                    plan?.TargetManifest.Image,plan?.OperationId,plan?.Revision??0,plan?.Phase??"idle",plan?.Outcome??"idle",plan?.NextCheckAt,
                    UpdateSupport().Enabled&&idle&&policy is not null&&record.DesiredRunning&&ProjectHealth(record,app.State).Ready&&!Paused,
                    UpdateSupport().Enabled&&plan?.Phase is "committed" or "rollback_failed",UpdateSupport().Enabled&&plan?.Phase=="committed",
                    plan is not null&&ApplicationUpdateRules.CanAdvance(plan.Phase),record.Manifest.Resources!.CredentialIds,UpdateSupport().Enabled&&idle&&policy is null&&record.Manifest.Resources.Health is not null&&!Paused));
            }
            foreach(var plan in updateStore?.ReadAll()??[])
            {
                if(result.Any(s=>s.AppId==plan.AppId))continue;
                result.Add(new(plan.AppId,plan.ActiveId,plan.ActiveId==plan.OriginalId?plan.OriginalManifest.Image:plan.TargetManifest.Image,
                    plan.Phase=="committed"?plan.OriginalManifest.Image:null,plan.TargetManifest.Image,plan.OperationId,plan.Revision,plan.Phase,plan.Outcome,plan.NextCheckAt,false,
                    UpdateSupport().Enabled&&plan.Phase is "committed" or "rollback_failed",UpdateSupport().Enabled&&plan.Phase=="committed",ApplicationUpdateRules.CanAdvance(plan.Phase),plan.OriginalManifest.Resources!.CredentialIds));
            }
            return result.ToArray();
        }
    }
    public async Task ConfigureUpdatePolicy(string id,ApplicationUpdatePolicyRequest request,CancellationToken ct)
    {
        await deploymentLock.WaitAsync(ct);
        try
        {
            RequireUpdates();EnsureOpen();
            if(!(await List(ct)).Any(a=>a.Id==id))throw new ArgumentException("Unknown active application.");
            var record=store!.Find(id)??throw new InvalidOperationException("Startup record unavailable.");
            if(UpdateFor(record.Manifest.Name) is {} plan&&plan.Phase is not ("accepted" or "rolled_back" or "failed"))throw new InvalidOperationException("Finish the current version operation before changing update policy.");
            var manifest=record.Manifest with{Resources=record.Manifest.Resources! with{Updates=request.Policy}};manifest.Validate();
            store.Save(record with{Manifest=manifest,Revision=checked(record.Revision+1)});
        }
        finally{deploymentLock.Release();}
    }
    public async Task QueueUpdate(string id,ApplicationUpdateRequest request,CancellationToken ct)
    {
        await deploymentLock.WaitAsync(ct);
        try
        {
            RequireUpdates();EnsureOpen();
            var existing=updateStore!.ReadAll().SingleOrDefault(p=>p.OperationId==request.OperationId);
            if(existing is not null){if(existing.ActiveId!=id&&existing.OriginalId!=id||existing.TargetManifest.Image!=request.Image||existing.OriginalManifest.Image!=request.ExpectedImage)throw new ArgumentException("Update operation ID conflict.");return;}
            if(request.OperationId==Guid.Empty)throw new ArgumentException("An update operation ID is required.");
            var app=(await List(ct)).SingleOrDefault(a=>a.Id==id)??throw new ArgumentException("Unknown active application.");
            var record=store!.Find(id)??throw new InvalidOperationException("Managed startup record unavailable.");
            if(record.Manifest.Resources?.Updates is null||!record.DesiredRunning||!ProjectHealth(record,app.State).Ready||!(await DependenciesReady(record,ct)))throw new InvalidOperationException("Update requires a Ready, requested-running app with an explicit compatible-data update policy.");
            if(record.Manifest.Image!=request.ExpectedImage||record.Manifest.Image==request.Image)throw new ArgumentException("The current image changed or the requested image is already active.");
            if(updateStore.ReadAll().Any(p=>ApplicationUpdateRules.BlocksLifecycle(p.Phase)||ApplicationUpdateRules.CanAdvance(p.Phase)))throw new InvalidOperationException("Another app update or cleanup is active.");
            if(UpdateFor(record.Manifest.Name) is {Phase:"committed"})throw new InvalidOperationException("Discard the retained previous version before another update.");
            var target=record.Manifest with{Image=request.Image};target.Validate();
            RequireInstallApproval(target);
            var network=await ContainerNetwork(id,ct);
            updateStore.Save(new(request.OperationId,record.Manifest.Name,"queued",id,id,record.Manifest,target,1,Now,record.DesiredRunning,OriginalNetworkId:network,RecoveryEnabled:record.RecoveryEnabled));
        }
        finally{deploymentLock.Release();}
    }
    public async Task RequestRollback(string id,ApplicationVersionRequest request,CancellationToken ct)
    {
        await deploymentLock.WaitAsync(ct);
        try
        {
            RequireUpdates();EnsureOpen();var plan=FindVersion(id,request);
            if(plan.Phase is not ("committed" or "rollback_failed"))throw new InvalidOperationException("No retained version is available for rollback.");
            if(updateStore!.ReadAll().Any(p=>p.AppId!=plan.AppId&&(ApplicationUpdateRules.BlocksLifecycle(p.Phase)||ApplicationUpdateRules.CanAdvance(p.Phase))))throw new InvalidOperationException("Another app update is active.");
            var active=store!.Find(plan.ActiveId)??throw new InvalidOperationException("Active startup record unavailable.");
            SaveUpdate(plan with{Phase="rollback_pending",DesiredRunning=active.DesiredRunning,RecoveryEnabled=active.RecoveryEnabled,Deadline=null,ReadySince=null,NextCheckAt=null,Outcome="owner_rollback"});
        }
        finally{deploymentLock.Release();}
    }
    public async Task DiscardPrevious(string id,ApplicationVersionRequest request,CancellationToken ct)
    {
        await deploymentLock.WaitAsync(ct);
        try {RequireUpdates();var plan=FindVersion(id,request);if(updateStore!.ReadAll().Any(p=>p.AppId!=plan.AppId&&(ApplicationUpdateRules.BlocksLifecycle(p.Phase)||ApplicationUpdateRules.CanAdvance(p.Phase))))throw new InvalidOperationException("Another app version operation is active.");if(plan.Phase=="discarding")return;if(plan.Phase!="committed")throw new InvalidOperationException("No retained version to discard.");SaveUpdate(plan with{Phase="discarding",NextCheckAt=null,Outcome="discarding_previous"});}
        finally{deploymentLock.Release();}
    }
    private ApplicationUpdateRecord FindVersion(string id,ApplicationVersionRequest request)=>updateStore!.ReadAll().SingleOrDefault(p=>p.ActiveId==id&&p.OperationId==request.OperationId)??throw new ArgumentException("Version selection is stale.");
    private void RequireUpdates(){if(!UpdateSupport().Enabled)throw new InvalidOperationException("App updates are disabled.");}
    private void SaveUpdate(ApplicationUpdateRecord plan)=>updateStore!.Save(plan with{Revision=checked(plan.Revision+1)});

    public async Task AdvanceUpdate(string app,ApplicationUpdateAdvance request,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(210));ct=deadline.Token;
        await updateStepLock.WaitAsync(ct);
        try
        {
            ApplicationUpdateRecord? snapshot;
            await deploymentLock.WaitAsync(ct);
            try
            {
                RequireUpdates();snapshot=UpdateFor(app);
                if(snapshot is null||snapshot.OperationId!=request.OperationId||snapshot.Revision!=request.Revision||!ApplicationUpdateRules.CanAdvance(snapshot.Phase)||snapshot.NextCheckAt>Now||Paused)return;
                if(snapshot.Phase=="queued"){SaveUpdate(snapshot with{Phase="staging",Outcome="pulling_image"});snapshot=UpdateFor(app)!;}
            }
            finally{deploymentLock.Release();}
            if(snapshot.Phase=="staging")
            {
                try {using var pull=CancellationTokenSource.CreateLinkedTokenSource(ct);pull.CancelAfter(TimeSpan.FromSeconds(120));await PullImage(snapshot.TargetManifest.Image,pull.Token);}
                catch(Exception e)when(e is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException or JsonException)
                {
                    using var repair=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await deploymentLock.WaitAsync(repair.Token);
                    try {var current=UpdateFor(app);if(current?.OperationId==snapshot.OperationId&&current.Revision==snapshot.Revision)SaveUpdate(current with{Phase="stage_cleanup",Outcome="image_pull_failed"});}
                    finally{deploymentLock.Release();}return;
                }
            }
            await deploymentLock.WaitAsync(ct);
            try
            {
                var plan=UpdateFor(app)!;
                if(plan.OperationId!=snapshot.OperationId||plan.Revision!=snapshot.Revision||Paused)return;
                try { await AdvanceCore(plan,request.Credentials,ct); }
                catch(Exception e)when(e is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException or ArgumentException or JsonException)
                {
                    var current=UpdateFor(app)!;
                    if(current.Phase is "rollback_pending" or "rollback_verifying")SaveUpdate(current with{Phase="rollback_failed",Outcome="rollback_needs_attention",NextCheckAt=null});
                    else if(current.Phase=="staging")SaveUpdate(current with{Phase="stage_cleanup",Outcome="staging_failed",NextCheckAt=null});
                    else if(current.Phase is "switching" or "verifying")SaveUpdate(current with{Phase="rollback_pending",Outcome="replacement_failed",NextCheckAt=null,ReadySince=null});
                    else SaveUpdate(current with{NextCheckAt=Now.AddSeconds(10),Outcome="cleanup_pending"});
                }
            }
            finally{deploymentLock.Release();}
        }
        finally{updateStepLock.Release();}
    }
    private async Task AdvanceCore(ApplicationUpdateRecord plan,ApplicationCredentialFile[]? credentials,CancellationToken ct)
    {
        if(plan.Phase=="stage_cleanup")
        {
            var candidate=plan.CandidateId??await FindCandidate(plan.TargetManifest,CandidateName(plan),plan.OperationId,ct);
            var network=plan.CandidateNetworkId??await FindUpdateNetwork(plan.AppId,plan.OperationId,ct);
            if(candidate is not null)await RemoveRetained(candidate,network,ct);
            else if(network is not null)await RemoveNetwork(network,ct);
            SaveUpdate(plan with{Phase="failed",CandidateId=null,CandidateNetworkId=null,NextCheckAt=null});return;
        }
        if(plan.Phase=="staging")
        {
            var id=await CreateContainer(plan.TargetManifest,CandidateName(plan),plan.OperationId,network=>{plan=plan with{CandidateNetworkId=network};updateStore!.Save(plan);},ct);
            store!.Save(new(plan.TargetManifest,id,[],DateTimeOffset.MinValue,DesiredRunning:plan.DesiredRunning,RecoveryEnabled:plan.RecoveryEnabled,Revision:1,Outcome:"update_staged"));
            SaveUpdate(plan with{CandidateId=id,Phase="staged",Outcome="staged"});return;
        }
        if(plan.Phase=="staged")
        {
            if(credentials is null){SaveUpdate(plan with{NextCheckAt=Now.AddSeconds(30),Outcome="credential_unavailable"});return;}
            ValidateMaterial(plan.TargetManifest,credentials);
            SaveUpdate(plan with{Phase="switching",NextCheckAt=null,Outcome="switching"});
            await AdvanceCore(UpdateFor(plan.AppId)!,credentials,ct);return;
        }
        if(plan.Phase=="switching")
        {
            if(credentials is null){SaveUpdate(plan with{NextCheckAt=Now.AddSeconds(30),Outcome="credential_unavailable"});return;}
            ValidateMaterial(plan.TargetManifest,credentials);
            await StopDependents(plan.AppId,ct);await StopRaw(plan.OriginalId,plan.OriginalManifest.Resources!.ShutdownSeconds,ct);
            var original=store!.Find(plan.OriginalId)!;store.Save(original with{DesiredRunning=false,PendingStart=false,Revision=original.Revision+1,StableSince=null});
            plan=plan with{ActiveId=plan.CandidateId!,Phase="verifying",Deadline=Now.AddSeconds(plan.TargetManifest.Resources!.Updates!.ReadinessSeconds),Outcome="starting_replacement"};SaveUpdate(plan);plan=UpdateFor(plan.AppId)!;
            var candidate=store.Find(plan.CandidateId!)! with{Credentials=credentials,DesiredRunning=true,PendingStart=false,Outcome="starting",Revision=2};
            await Bootstrap(candidate,ct);SaveUpdate(plan with{Outcome="waiting_for_ready"});return;
        }
        if(plan.Phase is "verifying" or "rollback_verifying")
        {
            var runtime=(await RawList(ct)).SingleOrDefault(a=>a.Id==plan.ActiveId);var record=store!.Find(plan.ActiveId);
            bool ready=runtime is not null&&record is not null&&ProjectHealth(record,runtime.State).Ready&&await DependenciesReady(record,ct);
            var policy=(plan.Phase=="verifying"?plan.TargetManifest:plan.OriginalManifest).Resources!.Updates!;
            var health=record is null||runtime is null?null:ProjectHealth(record,runtime.State);
            if(runtime?.State!="running"||record is null||Incomplete(record)||Now>=plan.Deadline||health?.State=="unhealthy"&&health.Failures>=record.Manifest.Resources!.Health!.FailureThreshold)
            {SaveUpdate(plan with{Phase=plan.Phase=="verifying"?"rollback_pending":"rollback_failed",ReadySince=null,NextCheckAt=null,Outcome=plan.Phase=="verifying"?"readiness_failed":"rollback_needs_attention"});return;}
            if(ready)
            {
                var since=plan.ReadySince is not null && (plan.NextCheckAt is null || Now-(plan.NextCheckAt.Value-TimeSpan.FromSeconds(5))<=TimeSpan.FromSeconds(record!.Manifest.Resources!.Health!.FreshnessSeconds)) ? plan.ReadySince.Value : Now;
                if(Now-since>=TimeSpan.FromSeconds(policy.StableSeconds))
                {SaveUpdate(plan with{Phase=plan.Phase=="verifying"?"committed":"rollback_cleanup",ReadySince=since,NextCheckAt=null,Outcome=plan.Phase=="verifying"?"update_committed":"previous_version_ready"});return;}
                SaveUpdate(plan with{ReadySince=since,NextCheckAt=Now.AddSeconds(5),Outcome="observing_ready"});return;
            }
            if(runtime?.State!="running"||record is null||Incomplete(record)||Now>=plan.Deadline)
            {SaveUpdate(plan with{Phase=plan.Phase=="verifying"?"rollback_pending":"rollback_failed",ReadySince=null,NextCheckAt=null,Outcome=plan.Phase=="verifying"?"readiness_failed":"rollback_needs_attention"});return;}
            SaveUpdate(plan with{ReadySince=null,NextCheckAt=Now.AddSeconds(5),Outcome="waiting_for_ready"});return;
        }
        if(plan.Phase=="rollback_pending")
        {
            await RestoreStopped(plan,plan.DesiredRunning,"restoring_previous",ct);plan=UpdateFor(plan.AppId)!;
            if(!plan.DesiredRunning){SaveUpdate(plan with{Phase="rollback_cleanup",Outcome="previous_version_stopped"});return;}
            if(credentials is null){SaveUpdate(plan with{NextCheckAt=Now.AddSeconds(30),Outcome="credential_unavailable"});return;}
            ValidateMaterial(plan.OriginalManifest,credentials);
            var original=store!.Find(plan.OriginalId)! with{Credentials=credentials,DesiredRunning=true,PendingStart=false,Outcome="starting",Revision=store.Find(plan.OriginalId)!.Revision+1};
            SaveUpdate(plan with{Phase="rollback_verifying",Deadline=Now.AddSeconds(plan.OriginalManifest.Resources!.Updates!.ReadinessSeconds),ReadySince=null,NextCheckAt=null,Outcome="starting_previous"});
            await Bootstrap(original,ct);return;
        }
        if(plan.Phase is "rollback_cleanup" or "discarding")
        {
            bool discard=plan.Phase=="discarding";
            var id=discard?plan.OriginalId:plan.CandidateId;var network=discard?plan.OriginalNetworkId:plan.CandidateNetworkId;
            if(id is not null&&id!=plan.ActiveId)await RemoveRetained(id,network,ct);
            SaveUpdate(plan with{Phase=discard?"accepted":"rolled_back",CandidateId=discard?plan.CandidateId:null,CandidateNetworkId=discard?plan.CandidateNetworkId:null,NextCheckAt=null,Outcome=discard?"previous_version_discarded":"previous_version_restored"});
        }
    }
    private async Task RestoreStopped(ApplicationUpdateRecord plan,bool desired,string outcome,CancellationToken ct)
    {
        await StopDependents(plan.AppId,ct);
        var candidate=plan.CandidateId??await FindCandidate(plan.TargetManifest,CandidateName(plan),plan.OperationId,ct);
        if(candidate is not null)await StopRaw(candidate,plan.TargetManifest.Resources!.ShutdownSeconds,ct,allowMissing:true);
        await StopRaw(plan.OriginalId,plan.OriginalManifest.Resources!.ShutdownSeconds,ct);
        var original=store!.Find(plan.OriginalId)??throw new InvalidOperationException("Retained version startup record unavailable.");
        store.Save(original with{DesiredRunning=desired,RecoveryEnabled=plan.RecoveryEnabled,PendingStart=false,StableSince=null,Revision=original.Revision+1,Outcome="update_restored"});
        SaveUpdate(plan with{ActiveId=plan.OriginalId,CandidateId=candidate,DesiredRunning=desired,Outcome=outcome});
    }
    private async Task AbortUpdate(ApplicationUpdateRecord plan,bool desired,string reason,CancellationToken ct)
    {
        plan=plan with{Phase="rollback_pending",DesiredRunning=desired,Outcome=reason};SaveUpdate(plan);plan=UpdateFor(plan.AppId)!;
        await RestoreStopped(plan,desired,reason,ct);plan=UpdateFor(plan.AppId)!;
        var original=store!.Find(plan.OriginalId)!;store.Save(original with{PendingStart=desired,Outcome=desired?"waiting":"stopped_by_operator",Revision=original.Revision+1});
        SaveUpdate(plan with{Phase="rollback_cleanup",Outcome=reason,NextCheckAt=null});
    }
    private async Task InterruptUpdates(CancellationToken ct)
    {
        foreach(var plan in updateStore?.ReadAll().Where(p=>ApplicationUpdateRules.BlocksLifecycle(p.Phase)).ToArray()??[])
            await AbortUpdate(plan,plan.DesiredRunning,"shutdown_interrupted_update",ct);
    }
    private async Task StopRaw(string id,int seconds,CancellationToken ct,bool allowMissing=false)
    {
        var app=(await RawList(ct)).SingleOrDefault(a=>a.Id==id);if(app is null){if(allowMissing)return;throw new InvalidOperationException("Update container unavailable.");}
        if(app.State is not ("running" or "restarting" or "paused"))return;
        using var response=await engine.PostAsync($"{Api}/containers/{id}/stop?t={seconds}",null,ct);if(response.StatusCode!=HttpStatusCode.NotModified)response.EnsureSuccessStatusCode();
        if((await RawList(ct)).Any(a=>a.Id==id&&a.State is "running" or "restarting" or "paused"))throw new InvalidOperationException("Update container did not stop.");
    }
    private static string CandidateName(ApplicationUpdateRecord plan)=>"pidock-"+plan.AppId+"-u-"+plan.OperationId.ToString("N");
    private async Task<string?> FindCandidate(ApplicationManifest manifest,string name,Guid operation,CancellationToken ct)
    {
        using var response=await engine.GetAsync($"{Api}/containers/{Uri.EscapeDataString(name)}/json",ct);if(response.StatusCode==HttpStatusCode.NotFound)return null;response.EnsureSuccessStatusCode();
        var json=await response.Content.ReadFromJsonAsync<JsonElement>(ct);var config=json.GetProperty("Config");var labels=config.GetProperty("Labels");
        if(config.GetProperty("Image").GetString()!=manifest.Image||labels.GetProperty(Label+".update").GetString()!=operation.ToString("N")||labels.GetProperty(Label+".app").GetString()!=manifest.Name||labels.GetProperty(Label).GetString()!="true")throw new InvalidOperationException("Refusing to adopt an unrelated candidate.");
        return json.GetProperty("Id").GetString()!;
    }
    private async Task<string?> FindUpdateNetwork(string app,Guid operation,CancellationToken ct)
    {
        using var response=await engine.GetAsync($"{Api}/networks/pidock-u-{operation:N}",ct);if(response.StatusCode==HttpStatusCode.NotFound)return null;response.EnsureSuccessStatusCode();
        var json=await response.Content.ReadFromJsonAsync<JsonElement>(ct);var labels=json.GetProperty("Labels");
        if(json.GetProperty("Driver").GetString()!="bridge"||labels.GetProperty(Label).GetString()!="true"||labels.GetProperty(Label+".update").GetString()!=operation.ToString("N")||labels.GetProperty(Label+".app").GetString()!=app)throw new InvalidOperationException("Refusing to adopt an unrelated network.");
        return json.GetProperty("Id").GetString()!;
    }
    private async Task<string?> ContainerNetwork(string id,CancellationToken ct)
    {
        using var response=await engine.GetAsync($"{Api}/containers/{id}/json",ct);response.EnsureSuccessStatusCode();var json=await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var network=json.GetProperty("HostConfig").GetProperty("NetworkMode").GetString();return network is {Length:64}&&network.All(char.IsAsciiHexDigit)?network:null;
    }
    private async Task RemoveRetained(string id,string? network,CancellationToken ct)
    {
        var app=(await RawList(ct)).SingleOrDefault(a=>a.Id==id);
        if(app is not null)
        {
            if(app.State is "running" or "restarting" or "paused")throw new InvalidOperationException("A running retained version cannot be discarded.");
            using var removed=await engine.DeleteAsync($"{Api}/containers/{id}?force=0&v=0",ct);removed.EnsureSuccessStatusCode();
        }
        if(network is not null)await RemoveNetwork(network,ct);
        store!.Forget(id);
    }
    private async Task RemoveNetwork(string network,CancellationToken ct)
    {
        using var inspected=await engine.GetAsync($"{Api}/networks/{network}",ct);
        if(inspected.StatusCode==HttpStatusCode.NotFound)return;
        inspected.EnsureSuccessStatusCode();var json=await inspected.Content.ReadFromJsonAsync<JsonElement>(ct);
        if(json.GetProperty("Labels").GetProperty(Label).GetString()!="true")throw new InvalidOperationException("Unmanaged network retained.");
        using var removed=await engine.DeleteAsync($"{Api}/networks/{network}",ct);removed.EnsureSuccessStatusCode();
    }

}
