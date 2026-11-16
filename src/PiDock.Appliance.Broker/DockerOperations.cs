using System.Net.Http.Json;
using System.Text.Json;
using PiDock.Core;
namespace PiDock.Appliance.Broker;

public sealed partial class DockerOperations(HttpClient engine, IManagedDeploymentStore? store = null, IApplicationBootstrapDelivery? delivery = null, TimeProvider? clock = null, bool recoveryEnabled = false, bool orchestrationEnabled = false, IApplicationShutdownStore? shutdownStore = null, bool updatesEnabled = false, IApplicationUpdateStore? updateStore = null, bool backupsEnabled = false, IApplicationBackupStore? backupStore = null, string? appDataHelperImage = null, bool installTrustEnabled = false, IApplicationInstallTrustStore? installTrustStore = null, bool removalsEnabled = false, IApplicationRemovalStore? removalStore = null)
{
    private const string Label = "ai.knowledgelocker.pidock.managed";
    private const string Api = "/v1.47";
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private readonly SemaphoreSlim deploymentLock = new(1);
    private async Task<ApplicationSummary[]> RawList(CancellationToken ct)
    {
        var filter = Uri.EscapeDataString(JsonSerializer.Serialize(new { label = new[] { Label + "=true" } }));
        return await engine.GetFromJsonAsync<ApplicationSummary[]>($"{Api}/containers/json?all=1&filters={filter}", StateFiles.Json, ct) ?? [];
    }
    public async Task<string> Deploy(ApplicationManifest manifest, CancellationToken ct)
    {
        return await DeployPrepared(new(manifest, []), ct);
    }
    public async Task<string> DeployPrepared(ApplicationDeploymentRequest prepared, CancellationToken ct)
    {
        var manifest = prepared.Manifest; manifest.Validate(); ValidateMaterial(manifest, prepared.Credentials);
        if (manifest.Resources?.Requested == true && (store is null || delivery is null)) throw new InvalidOperationException("Managed app resources are disabled.");
        if (manifest.Resources?.AutomaticRecovery == true && !recoveryEnabled) throw new InvalidOperationException("Automatic app recovery is disabled.");
        await deploymentLock.WaitAsync(ct);
        string? networkId = null, containerId = null; bool ready = false;
        try
        {
            if(RestoreBlocksStartup || RemovalBlocksStartup)throw new InvalidOperationException("Complete the active app data restore or removal before deployment.");
            if(manifest.Resources?.Updates is not null && !UpdateSupport().Enabled)throw new InvalidOperationException("App updates are disabled.");
            if (manifest.Resources?.Requested == true && Paused) throw new InvalidOperationException("Application startup is paused for coordinated shutdown.");
            if (orchestrationEnabled) { EnsureOpen(); await ValidateDeploymentGraph(manifest,ct); }
            else if (manifest.Resources?.RequiredApps.Length > 0) throw new InvalidOperationException("Application orchestration is disabled.");
            if ((await List(ct)).Length >= 16) throw new InvalidOperationException("Preview is limited to 16 managed containers.");
            RequireInstallApproval(manifest);
            await PullImage(manifest.Image,ct);
            var resourceApp=manifest.Resources?.Requested==true;
            string id=await CreateContainer(manifest,"pidock-"+manifest.Name,null,value=>networkId=value,ct);containerId=id;
            if (resourceApp)
            {
                var record = new ManagedDeployment(manifest, id, prepared.Credentials, DateTimeOffset.MinValue, DesiredRunning: true, RecoveryEnabled: manifest.Resources!.AutomaticRecovery, Revision: 1, Outcome: "starting", PendingStart: orchestrationEnabled);
                store!.Save(record);
                if (orchestrationEnabled && !(await DependenciesReady(record,ct))) store.Save(record with { PendingStart = true, Outcome = "dependencies_waiting" });
                else await Bootstrap(record, ct);
            }
            else { using var start = await engine.PostAsync($"{Api}/containers/{id}/start", null, ct); start.EnsureSuccessStatusCode(); }
            ready = true; return id;
        }
        finally
        {
            try
            {
                if (!ready)
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    if (containerId is not null) { using var remove = await engine.DeleteAsync($"{Api}/containers/{containerId}?force=1", cleanupTimeout.Token); if (remove.IsSuccessStatusCode) store?.Forget(containerId); }
                    if (networkId is not null) { using var cleanup = await engine.DeleteAsync($"{Api}/networks/{networkId}", cleanupTimeout.Token); }
                }
            }
            finally { deploymentLock.Release(); }
        }
    }
    private async Task PullImage(string image,CancellationToken ct)
    {
            using var pullRequest = new HttpRequestMessage(HttpMethod.Post, $"{Api}/images/create?fromImage={Uri.EscapeDataString(image)}&platform=linux%2Farm64");
            using var pull = await engine.SendAsync(pullRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            pull.EnsureSuccessStatusCode();
            // Docker can return HTTP 200 with an error in its progress stream.
            using var stream = await pull.Content.ReadAsStreamAsync(ct); using var reader = new StreamReader(stream);
            int size = 0;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                size += line.Length; if (size > 10_000_000) throw new InvalidOperationException("Image pull progress exceeded the preview limit.");
                using var json = JsonDocument.Parse(line); if (json.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("Image pull failed. Check registry access and ARM64 support.");
            }
    }
    private static Dictionary<string,string> NetworkLabels(string app,Guid? operation)
    {
        var labels=new Dictionary<string,string>{[Label]="true",[Label+".app"]=app};if(operation is not null)labels[Label+".update"]=operation.Value.ToString("N");return labels;
    }
    private static Dictionary<string,string> ContainerLabels(string app,bool resourceApp,Guid? operation)
    {var labels=NetworkLabels(app,operation);labels[Label+".resources"]=resourceApp?"v1":"none";labels[Label+".update"]=operation?.ToString("N")??"none";return labels;}
    private async Task<string> CreateContainer(ApplicationManifest manifest,string name,Guid? operation,Action<string> networkCreated,CancellationToken ct)
    {
        await ValidateInstallImage(manifest,ct);
        string? networkId=null;
        if(operation is not null)
        {
            var existing=await FindCandidate(manifest,name,operation.Value,ct);if(existing is not null)return existing;
            if((await RawList(ct)).Length>=32)throw new InvalidOperationException("Retained container capacity reached.");
        }
            if(operation is not null)networkId=await FindUpdateNetwork(manifest.Name,operation.Value,ct);
            if(networkId is null)
            {
            using var network = await engine.PostAsJsonAsync(Api + "/networks/create", new { Name = operation is null ? "pidock-" + manifest.Name + "-" + Guid.NewGuid().ToString("N") : "pidock-u-" + operation.Value.ToString("N"), Driver = "bridge", Labels = NetworkLabels(manifest.Name,operation) }, ct);
            network.EnsureSuccessStatusCode(); networkId = (await network.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("Id").GetString()!; networkCreated(networkId);
            }
            else networkCreated(networkId);
            var ports = manifest.ContainerPort is { } port ? new Dictionary<string, object> { [$"{port}/tcp"] = new { } } : [];
            var bindings = manifest.ContainerPort is { } cp ? new Dictionary<string, object> { [$"{cp}/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = manifest.HostPort!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) } } } : [];
            var resourceApp = manifest.Resources?.Requested == true;
            var tmpfs = new Dictionary<string,string> { ["/tmp"] = "rw,noexec,nosuid,nodev,size=16777216" };
            if (resourceApp) tmpfs[ApplicationResourceRules.CredentialDirectory] = "rw,noexec,nosuid,nodev,size=1048576,uid=1654,gid=1654,mode=0700";
            if (manifest.Resources?.Health is not null) tmpfs[ApplicationHealthRules.Directory] = "rw,noexec,nosuid,nodev,size=65536,uid=1654,gid=1654,mode=0700";
            var environment = new List<string>();
            if (resourceApp) environment.AddRange(["PIDOCK_BOOTSTRAP_PROTOCOL=1", "PIDOCK_APP_ID=" + manifest.Name]);
            if (manifest.Resources?.Health is not null) environment.Add("PIDOCK_HEALTH_REPORTING=1");
            var mounts = new List<object>();
            if (manifest.Resources?.PersistentData == true)
            {
                var volume = await EnsureVolume(manifest.Name, ct);
                mounts.Add(new { Type = "volume", Source = volume, Target = ApplicationResourceRules.DataDirectory, ReadOnly = false, VolumeOptions = new { NoCopy = operation is not null } });
            }
            var request = new
            {
                manifest.Image,
                User = "1654:1654", StopSignal = "SIGTERM", StopTimeout = manifest.Resources?.ShutdownSeconds ?? 10,
                OpenStdin = resourceApp, StdinOnce = false, AttachStdin = resourceApp, Tty = false,
                Env = environment,
                Labels = ContainerLabels(manifest.Name,resourceApp,operation),
                ExposedPorts = ports,
                HostConfig = new
                {
                    ReadonlyRootfs = true,
                    Privileged = false,
                    CapDrop = new[] { "ALL" },
                    SecurityOpt = new[] { "no-new-privileges:true" },
                    Memory = (long)manifest.MemoryMiB << 20,
                    NanoCpus = (long)(manifest.CpuLimit * 1_000_000_000),
                    PidsLimit = 128,
                    NetworkMode = networkId,
                    PortBindings = bindings,
                    Tmpfs = tmpfs,
                    Mounts = mounts,
                    LogConfig = new { Type = "local", Config = new Dictionary<string, string> { ["max-size"] = "5m", ["max-file"] = "2" } },
                    RestartPolicy = new { Name = resourceApp ? "no" : "unless-stopped" }
                }
            };
            using var container = await engine.PostAsJsonAsync($"{Api}/containers/create?name={Uri.EscapeDataString(name)}", request, ct);
            container.EnsureSuccessStatusCode();
            string id = (await container.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("Id").GetString()!; return id;
    }
    public Task Restart(string id, CancellationToken ct) => Lifecycle(id, true, true, null, ct);
    public Task ChangeState(string id, bool start, CancellationToken ct) => Lifecycle(id, start, false, null, ct);
    public async Task<ApplicationResourceState?> Resources(string id, CancellationToken ct)
    {
        var app = (await List(ct)).SingleOrDefault(c => c.Id == id) ?? throw new ArgumentException("Unknown managed application.");
        var record = store?.Find(id);
        RequireResourceRecord(app,record);
        return record is null ? null : new(record.Manifest.Name, id, record.Manifest.Resources!.PersistentData, record.Manifest.Resources.CredentialIds, record.LastDeliveredAt);
    }
    public async Task Lifecycle(string id, bool start, bool restart, ApplicationCredentialFile[]? credentials, CancellationToken ct)
    {
        await deploymentLock.WaitAsync(ct);
        try
        {
            if (id.Length != 64 || !id.All(char.IsAsciiHexDigit)) throw new ArgumentException("Unknown managed application.");
            var app = (await List(ct)).SingleOrDefault(c => c.Id == id) ?? throw new ArgumentException("Unknown managed application.");
            ManagedDeployment? record;
            // A corrupt record must not prevent an operator from stopping a known managed app.
            if (start) record = store?.Find(id);
            else
            {
                try { record = store?.Find(id); }
                catch (IOException) { record = null; }
            }
            if (start) { RequireResourceRecord(app,record);if(RestoreBlocksStartup || RemovalBlocksStartup)throw new InvalidOperationException("Complete the active app data restore or removal before startup."); }
            if(record is not null && UpdateBlocks(record.Manifest.Name))
            {
                if(start)throw new InvalidOperationException("App update is active; Stop aborts it.");
                await AbortUpdate(UpdateFor(record.Manifest.Name)!,false,"operator_stop",ct);return;
            }
            if (start && record is not null && (Paused || record.Manifest.Resources!.RequiredApps.Length>0 && !OrchestrationSupport().Enabled))
                throw new InvalidOperationException("Application startup requires enabled orchestration and an unpaused shutdown journal.");
            if (start && record is not null)
            {
                var updated = record with { Credentials = credentials ?? record.Credentials, DesiredRunning = true, Revision = checked(record.Revision + 1), Attempts = 0, NextAttemptAt = null, StableSince = null, Outcome = "starting", PendingStart = false };
                ValidateMaterial(updated.Manifest, updated.Credentials);
                if (orchestrationEnabled)
                {
                    EnsureOpen();
                    updated = updated with {PendingStart=true};store!.Save(updated);
                    await StopDependents(record.Manifest.Name,ct);
                    if (!(await DependenciesReady(updated,ct)))
                    {
                        store!.Save(updated with { PendingStart = true, Outcome = "dependencies_waiting" });
                        await StopContainer(updated,ct); return;
                    }
                }
                store!.Save(updated);
                // Always stop first: a managed start must never inject another packet into a running app.
                using var stop = await engine.PostAsync($"{Api}/containers/{id}/stop?t={updated.Manifest.Resources?.ShutdownSeconds ?? 10}", null, ct); if (stop.StatusCode != System.Net.HttpStatusCode.NotModified) stop.EnsureSuccessStatusCode();
                try { await Bootstrap(updated, ct); }
                catch { store!.Save(updated with { Outcome = "start_failed", PendingStart = false }); throw; }
            }
            else
            {
                if (!start && record is not null)
                    store!.Save(record with { DesiredRunning = false, Revision = checked(record.Revision + 1), Attempts = 0, NextAttemptAt = null, StableSince = null, Outcome = "stopped_by_operator", PendingStart = false });
                if (!start && record is not null && orchestrationEnabled) { await StopDependents(record.Manifest.Name,ct); await StopContainer(record,ct); return; }
                if (credentials is { Length: > 0 }) throw new ArgumentException("App has no managed resource record.");
                using var result = await engine.PostAsync($"{Api}/containers/{id}/{(restart ? "restart?t=10" : start ? "start" : "stop?t=10")}", null, ct); if (result.StatusCode != System.Net.HttpStatusCode.NotModified) result.EnsureSuccessStatusCode();
            }
        }
        finally { deploymentLock.Release(); }
    }
    private static void RequireResourceRecord(ApplicationSummary app, ManagedDeployment? record)
    {
        if (app.Labels?.GetValueOrDefault(Label + ".resources") == "v1" && record is null)
            throw new InvalidOperationException("Managed app startup record unavailable; restore broker state before starting.");
    }
    private void ValidateMaterial(ApplicationManifest manifest, ApplicationCredentialFile[] credentials)
    {
        ApplicationResourceRules.Validate(new(1, Guid.NewGuid(), manifest.Name, manifest.Resources?.PersistentData == true, credentials), Now);
        if (!(manifest.Resources?.CredentialIds ?? []).Order(StringComparer.Ordinal).SequenceEqual(credentials.Select(c => c.Id).Order(StringComparer.Ordinal)))
            throw new ArgumentException("Credential delivery must exactly match the manifest references.");
    }
    private async Task Bootstrap(ManagedDeployment record, CancellationToken ct)
    {
        if (delivery is null) throw new InvalidOperationException("App bootstrap unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        record = record with { LastDeliveryId = Guid.NewGuid(), Health = null, HealthSequence = 0, FirstReadyAt = null, LastHealthReport = null };
        store!.Save(record);
        try
        {
            await delivery.Deliver(record.ContainerId, new(1, record.LastDeliveryId.Value, record.Manifest.Name, record.Manifest.Resources!.PersistentData, record.Credentials), async () =>
            { using var response = await engine.PostAsync($"{Api}/containers/{record.ContainerId}/start", null, timeout.Token); response.EnsureSuccessStatusCode(); }, timeout.Token);
            store!.Save(record with { LastDeliveredAt = Now, StableSince = record.Manifest.Resources?.Health is null ? Now : null, Outcome = "observing", PendingStart = false });
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var stopped = await engine.PostAsync($"{Api}/containers/{record.ContainerId}/stop?t=10", null, cleanup.Token);
            throw;
        }
    }
    private async Task<string> EnsureVolume(string app, CancellationToken ct)
    {
        var name = "pidock-app-" + app + "-data";
        using var existing = await engine.GetAsync($"{Api}/volumes/{name}", ct);
        if (existing.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            RequireStorageAdmission();
            using var created = await engine.PostAsJsonAsync(Api + "/volumes/create", new { Name = name, Driver = "local", Labels = new Dictionary<string,string> { [Label] = "true", [Label + ".app"] = app } }, ct);
            created.EnsureSuccessStatusCode();
        }
        else existing.EnsureSuccessStatusCode();
        using var checkedVolume = await engine.GetAsync($"{Api}/volumes/{name}", ct); checkedVolume.EnsureSuccessStatusCode();
        var volume = await checkedVolume.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (volume.GetProperty("Driver").GetString() != "local" ||
            !volume.TryGetProperty("Labels", out var labels) || labels.ValueKind != JsonValueKind.Object ||
            !labels.TryGetProperty(Label, out var managed) || managed.GetString() != "true" ||
            !labels.TryGetProperty(Label + ".app", out var owner) || owner.GetString() != app ||
            (volume.TryGetProperty("Options", out var options) && options.ValueKind != JsonValueKind.Null && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any())))
            throw new InvalidOperationException("Refusing unmanaged or externally backed app volume.");
        return name;
    }
}
