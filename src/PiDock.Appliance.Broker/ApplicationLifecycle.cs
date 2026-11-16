using PiDock.Core;

namespace PiDock.Appliance.Broker;

public sealed partial class DockerOperations
{
    public ApplicationLifecycleSupport LifecycleSupport() => new(1, recoveryEnabled && store is not null && delivery is not null,
        ApplicationRecoveryRules.MaximumAttempts, ApplicationRecoveryRules.StableSeconds, "pidock-host",
        ["Requires the PiDockOS host service, broker and Docker; no browser or cloud connection required.",
         "Only resource-enabled containers are supervised. Docker automatic restart remains disabled for them.",
         "Process recovery and optional health-driven recovery; discover app-health separately. Bootstrap acknowledgement is delivery readiness only.",
         "Missing containers/data, updates, rollback and uninstall are not repaired automatically."]);

    public async Task<ApplicationLifecycleStatus[]> LifecycleStatus(CancellationToken ct)
    {
        await deploymentLock.WaitAsync(ct);
        try
        {
            var result = new List<ApplicationLifecycleStatus>();
            Catalog? catalog=null;
            if(orchestrationEnabled) { try { catalog=await ReadCatalog(ct); } catch(Exception e)when(e is IOException or InvalidOperationException or ArgumentException) { } }
            foreach (var app in (await List(ct)).Take(16))
            {
                if (app.Labels?.GetValueOrDefault(Label + ".resources") != "v1") continue;
                ManagedDeployment? record;
                try { record = store?.Find(app.Id); }
                catch (IOException) { record = null; }
                if (record is null)
                {
                    result.Add(new(app.Names.FirstOrDefault() ?? app.Id, app.Id, app.State, false, false, 0, 0, null, null, "startup_record_unavailable", []));
                    continue;
                }
                bool dependencyBlocked=orchestrationEnabled && (catalog is null || DependencyStatuses(record,catalog).Any(d=>!d.Satisfied));
                var observed=dependencyBlocked ? record.StableSince is null ? record : record with{StableSince=null,Revision=checked(record.Revision+1)} : Observe(record,app.State);
                if (observed != record) store!.Save(observed);
                var status=Project(observed,app.State) with {PendingStart=observed.PendingStart,OrchestrationPaused=Paused,UpdateInProgress=UpdateBlocks(observed.Manifest.Name)};
                if(!orchestrationEnabled && observed.Manifest.Resources!.RequiredApps.Length>0)
                    status=status with {Dependencies=observed.Manifest.Resources.RequiredApps.Select(d=>new ApplicationDependencyStatus(d.AppId,d.Condition,false,"orchestration_disabled")).ToArray(),Outcome="orchestration_disabled"};
                if(orchestrationEnabled)status=status with {Dependencies=catalog is null ? [] : DependencyStatuses(observed,catalog),PendingStart=observed.PendingStart,OrchestrationPaused=Paused,Outcome=catalog is null ? "dependency_catalog_unavailable" : DependencyStatuses(observed,catalog).Any(d=>!d.Satisfied) ? "dependencies_waiting" : status.Outcome};
                result.Add(status);
            }
            return catalog is null ? result.ToArray() : result.OrderBy(status=>Array.FindIndex(catalog.Order,n=>n.ContainerId==status.ContainerId)).ToArray();
        }
        finally { deploymentLock.Release(); }
    }
    private ManagedDeployment Observe(ManagedDeployment record, string runtimeState)
    {
        if (runtimeState == "running")
        {
            if (Incomplete(record)) return record;
            if (!record.DesiredRunning) return record; // Never undo an explicit Stop, even if Docker's stop failed.
            if (record.Manifest.Resources?.Health is not null && !ProjectHealth(record,runtimeState).Ready)
                return record.StableSince is not null ? record with { StableSince = null, Revision = checked(record.Revision + 1), Outcome = "waiting_for_health" } : record;
            if (record.StableSince is null) return record with { StableSince = Now, Revision = checked(record.Revision + 1), Outcome = "observing" };
            if (Now - record.StableSince >= TimeSpan.FromSeconds(ApplicationRecoveryRules.StableSeconds) && (record.Attempts != 0 || record.Outcome != "stable"))
                return record with { Attempts = 0, NextAttemptAt = null, Revision = checked(record.Revision + 1), Outcome = "stable" };
            return record;
        }
        if (record.DesiredRunning && record.RecoveryEnabled && record.Attempts >= ApplicationRecoveryRules.MaximumAttempts && record.Outcome != "suspended")
            return record with { StableSince = null, Revision = checked(record.Revision + 1), Outcome = "suspended" };
        return record.StableSince is not null ? record with { StableSince = null, Revision = checked(record.Revision + 1), Outcome = record.Attempts >= ApplicationRecoveryRules.MaximumAttempts ? "suspended" : "waiting" } : record;
    }
    private ApplicationLifecycleStatus Project(ManagedDeployment record, string state) => new(record.Manifest.Name,
        record.ContainerId, state, record.DesiredRunning, record.RecoveryEnabled, record.Revision, record.Attempts,
        record.NextAttemptAt, record.StableSince, state == "running" && Incomplete(record) ? "startup_incomplete" : record.Outcome, record.Manifest.Resources!.CredentialIds, ProjectHealth(record,state));

    private static bool Incomplete(ManagedDeployment record) => record.StableSince is null && record.Outcome is "starting" or "recovering" or "start_failed";
    // An interrupted delivery must not be mistaken for a completed readiness acknowledgement.
    public async Task ReconcileLifecycle(CancellationToken ct)
    {
        if (!LifecycleSupport().Enabled || Paused) return;
        await deploymentLock.WaitAsync(ct);
        try
        {
            foreach (var app in (await List(ct)).Take(16))
            {
                if (app.State != "running" || app.Labels?.GetValueOrDefault(Label + ".resources") != "v1") continue;
                ManagedDeployment? record;
                try { record = store?.Find(app.Id); } catch (IOException) { continue; }
                if (record is not null && UpdateBlocks(record.Manifest.Name))continue;
                if (record is null || !record.DesiredRunning || !record.RecoveryEnabled || !(Incomplete(record) || ProjectHealth(record,app.State).RecoveryRequired)) continue;
                if(orchestrationEnabled) { if(Paused)continue;await StopDependents(record.Manifest.Name,ct); }
                using var stopped = await engine.PostAsync($"{Api}/containers/{app.Id}/stop?t={record.Manifest.Resources!.ShutdownSeconds}",null,ct); if (stopped.StatusCode != System.Net.HttpStatusCode.NotModified) stopped.EnsureSuccessStatusCode();
                store!.Save(record with { Revision = checked(record.Revision + 1), Outcome = record.Attempts >= ApplicationRecoveryRules.MaximumAttempts ? "suspended" : "waiting", StableSince = null });
            }
        }
        finally { deploymentLock.Release(); }
    }

    public async Task SetRecovery(string id, bool enabled, CancellationToken ct)
    {
        await deploymentLock.WaitAsync(ct);
        try
        {
            if (enabled && !LifecycleSupport().Enabled) throw new InvalidOperationException("Automatic app recovery is disabled.");
            var app = await ManagedApp(id, ct);
            var record = store?.Find(id) ?? throw new InvalidOperationException("Managed app startup record unavailable.");
            if(UpdateBlocks(record.Manifest.Name))throw new InvalidOperationException("Change recovery policy after the app update completes.");
            // Old records had no persisted operator intent. Enabling adopts their current runtime state once.
            store!.Save(record with { RecoveryEnabled = enabled, DesiredRunning = record.Revision == 0 ? app.State == "running" : record.DesiredRunning,
                Revision = checked(record.Revision + 1), Outcome = enabled ? "recovery_enabled" : "recovery_disabled" });
        }
        finally { deploymentLock.Release(); }
    }
    public async Task Recover(string id, ApplicationRecoveryRequest request, CancellationToken ct)
    {
        await deploymentLock.WaitAsync(ct);
        try
        {
            if (!LifecycleSupport().Enabled && !OrchestrationSupport().Enabled) throw new InvalidOperationException("Application startup coordination is disabled.");
            var app = await ManagedApp(id, ct);
            var record = store?.Find(id) ?? throw new InvalidOperationException("Managed app startup record unavailable.");
            if (UpdateBlocks(record.Manifest.Name))return;
            if (!Eligible(record, request.Revision, app.State) || Paused || record.Manifest.Resources!.RequiredApps.Length>0 && !OrchestrationSupport().Enabled) return;
            if(orchestrationEnabled && !(await DependenciesReady(record,ct)))
            { if(!Paused && record.Outcome!="dependencies_waiting")store!.Save(record with{Revision=checked(record.Revision+1),Outcome="dependencies_waiting"});return; }
            ValidateMaterial(record.Manifest, request.Credentials); // Never recover with retained/expired credential snapshots.
            var attempts = checked(record.Attempts + 1);
            var attempting = record with { Credentials = request.Credentials, Revision = checked(record.Revision + 1), Attempts = attempts,
                NextAttemptAt = Now + ApplicationRecoveryRules.Backoff(attempts), StableSince = null, Outcome = "recovering", PendingStart = orchestrationEnabled && record.PendingStart };
            // Persist before starting: broker/host power loss cannot erase this attempt or its backoff.
            store!.Save(attempting);
            try { await Bootstrap(attempting, ct); }
            catch
            {
                store.Save(attempting with { Outcome = attempts >= ApplicationRecoveryRules.MaximumAttempts ? "suspended" : "start_failed", PendingStart = false });
                throw;
            }
        }
        finally { deploymentLock.Release(); }
    }
    public async Task RecoveryBlocked(string id, long revision, CancellationToken ct)
    {
        await deploymentLock.WaitAsync(ct);
        try
        {
            var app = await ManagedApp(id, ct);
            var record = store?.Find(id) ?? throw new InvalidOperationException("Managed app startup record unavailable.");
            if(UpdateBlocks(record.Manifest.Name))return;
            if ((!LifecycleSupport().Enabled && !OrchestrationSupport().Enabled) || !Eligible(record, revision, app.State) || Paused || record.Manifest.Resources!.RequiredApps.Length>0 && !OrchestrationSupport().Enabled) return;
            if(orchestrationEnabled && (Paused || !(await DependenciesReady(record,ct))))return;
            store!.Save(record with { Revision = checked(record.Revision + 1), NextAttemptAt = Now.AddSeconds(30), StableSince = null, Outcome = "credential_unavailable" });
        }
        finally { deploymentLock.Release(); }
    }
    private bool Eligible(ManagedDeployment record, long revision, string state) => record.Revision == revision && record.DesiredRunning && ((LifecycleSupport().Enabled && record.RecoveryEnabled) || (OrchestrationSupport().Enabled && record.PendingStart)) &&
        state is "exited" or "created" or "dead" && record.Attempts < ApplicationRecoveryRules.MaximumAttempts && (record.NextAttemptAt is null || record.NextAttemptAt <= Now);
    private async Task<ApplicationSummary> ManagedApp(string id, CancellationToken ct)
    {
        if (id.Length != 64 || !id.All(char.IsAsciiHexDigit)) throw new ArgumentException("Unknown managed application.");
        return (await List(ct)).SingleOrDefault(app => app.Id == id && app.Labels?.GetValueOrDefault(Label + ".resources") == "v1") ?? throw new ArgumentException("Unknown resource-enabled application.");
    }
}
