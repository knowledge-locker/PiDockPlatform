using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using PiDock.Core;
namespace PiDock.Agent;

public interface IApplicationRuntime
{
    bool Configured { get; }
    Task<ApplicationSummary[]> List(CancellationToken ct = default);
    Task ChangeState(string id, bool start, CancellationToken ct = default);
    Task Restart(string id, CancellationToken ct = default);
}
public sealed class BrokerClient(IConfiguration configuration, IApplicationCredentialSource? credentials = null) : IDisposable, IApplicationRuntime
{
    private readonly HttpClient? http = configuration["Broker:SocketPath"] is { Length: > 0 } path ? UnixHttp.Connect(path, TimeSpan.FromMinutes(3)) : null;
    private readonly HttpClient? updateHttp=configuration["Broker:SocketPath"] is {Length:>0} path?UnixHttp.Connect(path,TimeSpan.FromMinutes(4)):null;
    public bool Configured => http is not null;
    public async Task<ApplicationRuntimeAccessSnapshot?> RuntimeAccess(CancellationToken ct = default)
    {
        if (http is null) return null;
        using var response = await http.GetAsync("/applications/runtime-access", HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 65536) throw new IOException("Private runtime observation exceeded its limit.");
        using var source = await response.Content.ReadAsStreamAsync(ct); using var bytes = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        { if (bytes.Length + count > 65536) throw new IOException("Private runtime observation exceeded its limit."); bytes.Write(buffer, 0, count); }
        return System.Text.Json.JsonSerializer.Deserialize<ApplicationRuntimeAccessSnapshot>(bytes.ToArray(), new System.Text.Json.JsonSerializerOptions(StateFiles.Json) { MaxDepth = 16 });
    }
    public async Task<ApplicationStorageSupport?> StorageSupport(CancellationToken ct = default)
    {
        if (http is null) return null;
        using var response = await http.GetAsync("/applications/storage/support", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<ApplicationStorageSupport>(StateFiles.Json, ct);
    }
    public async Task<ApplicationStorageStatus?> StorageStatus(CancellationToken ct = default)
    {
        if (http is null) return null;
        using var response = await http.GetAsync("/applications/storage", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<ApplicationStorageStatus>(StateFiles.Json, ct);
    }
    public async Task<ApplicationSummary[]> List(CancellationToken ct = default) => http is null ? [] : await http.GetFromJsonAsync<ApplicationSummary[]>("/applications", StateFiles.Json, ct) ?? [];
    public async Task Deploy(ApplicationManifest manifest)
    {
        manifest.Validate(); if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = manifest.Resources?.Requested == true
            ? await http.PostAsJsonAsync("/applications/prepared",new ApplicationDeploymentRequest(manifest,Resolve(manifest.Name,manifest.Resources.CredentialIds)))
            : await http.PostAsJsonAsync("/applications", manifest); response.EnsureSuccessStatusCode();
    }
    public async Task ChangeState(string id, bool start, CancellationToken ct = default)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        await Control(id,start ? "start" : "stop",ct);
    }
    public async Task Restart(string id, CancellationToken ct = default)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        await Control(id,"restart",ct);
    }
    public async Task<ApplicationRemovalSupport?> RemovalSupport(CancellationToken ct = default)
    {
        if (http is null) return null;
        using var response = await http.GetAsync("/applications/removal/support", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<ApplicationRemovalSupport>(StateFiles.Json, ct);
    }
    public async Task<ApplicationRemovalStatus?> RemovalStatus(CancellationToken ct = default)
    {
        if (http is null) return null;
        using var response = await http.GetAsync("/applications/removal/status", ct);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<ApplicationRemovalStatus>(StateFiles.Json, ct);
    }
    public async Task RemoveApplication(string id, ApplicationRemovalRequest request, CancellationToken ct = default)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.PostAsJsonAsync("/applications/" + Uri.EscapeDataString(id) + "/remove", request, StateFiles.Json, ct);
        response.EnsureSuccessStatusCode();
    }
    public async Task CompleteRemoval(ApplicationRemovalCompleteRequest request, CancellationToken ct = default)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.PostAsJsonAsync("/applications/removal/complete", request, StateFiles.Json, ct);
        response.EnsureSuccessStatusCode();
    }
    public async Task<ApplicationResourceSupport?> ResourceSupport(CancellationToken ct = default) => http is null ? null : await http.GetFromJsonAsync<ApplicationResourceSupport>("/applications/resources",StateFiles.Json,ct);
    public async Task<ApplicationInstallSupport?> InstallTrustSupport(CancellationToken ct = default)
    {
        if (http is null) return null;
        using var response = await http.GetAsync("/applications/install-trust/support", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<ApplicationInstallSupport>(StateFiles.Json, ct);
    }
    public async Task<ApplicationInstallApproval[]> InstallApprovals(CancellationToken ct = default)
    {
        if (http is null) return [];
        using var response = await http.GetAsync("/applications/install-trust/approvals", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<ApplicationInstallApproval[]>(StateFiles.Json, ct) ?? [];
    }
    public async Task ApproveInstall(ApplicationInstallApprovalRequest request, CancellationToken ct = default)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.PostAsJsonAsync("/applications/install-trust/approvals", request, StateFiles.Json, ct); response.EnsureSuccessStatusCode();
    }
    public async Task RevokeInstallApproval(string hash, CancellationToken ct = default)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.DeleteAsync("/applications/install-trust/approvals/" + Uri.EscapeDataString(hash), ct); response.EnsureSuccessStatusCode();
    }
    public async Task<ApplicationBackupSupport?> BackupSupport(CancellationToken ct=default)
    {
        if(http is null)return null;using var response=await http.GetAsync("/applications/backups/support",ct);
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return null;response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<ApplicationBackupSupport>(StateFiles.Json,ct);
    }
    public async Task<ApplicationBackupInfo[]> Backups(CancellationToken ct=default)
    {
        if(http is null)return [];using var response=await http.GetAsync("/applications/backups",ct);
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return [];response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<ApplicationBackupInfo[]>(StateFiles.Json,ct)??[];
    }
    public async Task<ApplicationDataRestoreStatus?> DataRestoreStatus(CancellationToken ct=default)
    {
        if(http is null)return null;using var response=await http.GetAsync("/applications/backups/restore",ct);
        if(response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NoContent)return null;response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<ApplicationDataRestoreStatus>(StateFiles.Json,ct);
    }
    public async Task CreateBackup(string id,ApplicationBackupRequest request,CancellationToken ct=default)
    {
        if(http is null)throw new InvalidOperationException("Host broker is not configured.");using var response=await http.PostAsJsonAsync("/applications/"+Uri.EscapeDataString(id)+"/backup",request,ct);response.EnsureSuccessStatusCode();
    }
    public async Task RestoreBackup(string id,ApplicationRestoreRequest request,CancellationToken ct=default)
    {
        if(http is null)throw new InvalidOperationException("Host broker is not configured.");using var response=await http.PostAsJsonAsync("/applications/"+Uri.EscapeDataString(id)+"/restore",request,ct);response.EnsureSuccessStatusCode();
    }
    public async Task DeleteBackup(Guid id,CancellationToken ct=default)
    {
        if(http is null)throw new InvalidOperationException("Host broker is not configured.");using var response=await http.DeleteAsync("/applications/backups/"+id.ToString("D"),ct);response.EnsureSuccessStatusCode();
    }
    public async Task<ApplicationUpdateSupport?> UpdateSupport(CancellationToken ct=default)
    {
        if(http is null)return null;using var response=await http.GetAsync("/applications/updates/support",ct);
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<ApplicationUpdateSupport>(StateFiles.Json,ct);
    }
    public async Task<ApplicationUpdateStatus[]> UpdateStatus(CancellationToken ct=default)
    {
        if(http is null)return [];using var response=await http.GetAsync("/applications/updates",ct);
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return [];
        response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<ApplicationUpdateStatus[]>(StateFiles.Json,ct)??[];
    }
    public async Task ConfigureUpdatePolicy(string id,ApplicationUpdatePolicy? policy,CancellationToken ct=default)
    {
        if(http is null)throw new InvalidOperationException("Host broker is not configured.");
        using var response=await http.PostAsJsonAsync("/applications/"+Uri.EscapeDataString(id)+"/update-policy",new ApplicationUpdatePolicyRequest(policy),ct);response.EnsureSuccessStatusCode();
    }
    public async Task Update(string id,ApplicationUpdateRequest request,CancellationToken ct=default)
    {
        if(http is null)throw new InvalidOperationException("Host broker is not configured.");
        using var response=await http.PostAsJsonAsync("/applications/"+Uri.EscapeDataString(id)+"/update",request,ct);response.EnsureSuccessStatusCode();
    }
    public async Task VersionControl(string id,string action,Guid operationId,CancellationToken ct=default)
    {
        if(http is null)throw new InvalidOperationException("Host broker is not configured.");
        if(action is not ("rollback" or "discard-previous"))throw new ArgumentException("Invalid version action.");
        using var response=await http.PostAsJsonAsync("/applications/"+Uri.EscapeDataString(id)+"/"+action,new ApplicationVersionRequest(operationId),ct);response.EnsureSuccessStatusCode();
    }
    public async Task AdvanceUpdate(ApplicationUpdateStatus status,CancellationToken ct=default)
    {
        if(updateHttp is null||status.OperationId is null)throw new InvalidOperationException("App update is unavailable.");
        ApplicationCredentialFile[]? material=null;
        try {material=Resolve(status.AppId,status.CredentialIds);}catch(InvalidOperationException){ }
        using var response=await updateHttp.PostAsJsonAsync("/applications/updates/"+Uri.EscapeDataString(status.AppId)+"/advance",new ApplicationUpdateAdvance(status.OperationId.Value,status.Revision,material),ct);response.EnsureSuccessStatusCode();
    }
    public async Task<ApplicationOrchestrationSupport?> OrchestrationSupport(CancellationToken ct=default)
    {
        if(http is null)return null;
        using var response=await http.GetAsync("/applications/orchestration/support",ct);
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<ApplicationOrchestrationSupport>(StateFiles.Json,ct);
    }
    public async Task<ApplicationOrchestrationStatus?> OrchestrationStatus(CancellationToken ct=default)
    {
        if(http is null)return null;
        using var response=await http.GetAsync("/applications/orchestration/status",ct);
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<ApplicationOrchestrationStatus>(StateFiles.Json,ct);
    }
    public async Task OrchestrationControl(string action,CancellationToken ct=default)
    {
        if(http is null)throw new InvalidOperationException("Host broker is not configured.");
        if(action is not ("reconcile" or "pause" or "resume"))throw new ArgumentException("Invalid orchestration action.");
        using var response=await http.PostAsync("/applications/orchestration/"+action,null,ct);response.EnsureSuccessStatusCode();
    }
    public async Task<ApplicationHealthSupport?> HealthSupport(CancellationToken ct = default)
    {
        if (http is null) return null;
        using var response = await http.GetAsync("/applications/health/support",ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<ApplicationHealthSupport>(StateFiles.Json,ct);
    }
    public async Task CheckHealth(CancellationToken ct = default)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.PostAsync("/applications/health/check",null,ct); response.EnsureSuccessStatusCode();
    }
    public async Task<ApplicationLifecycleSupport?> LifecycleSupport(CancellationToken ct = default)
    {
        if (http is null) return null;
        using var response = await http.GetAsync("/applications/lifecycle/support",ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<ApplicationLifecycleSupport>(StateFiles.Json,ct);
    }
    public async Task<ApplicationLifecycleStatus[]> LifecycleStatus(CancellationToken ct = default)
    {
        if (http is null) return [];
        using var response = await http.GetAsync("/applications/lifecycle",ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<ApplicationLifecycleStatus[]>(StateFiles.Json,ct) ?? [];
    }
    public async Task ReconcileLifecycle(CancellationToken ct = default)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.PostAsync("/applications/lifecycle/reconcile",null,ct); response.EnsureSuccessStatusCode();
    }
    public async Task SetRecovery(string id,bool enabled,CancellationToken ct = default)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.PostAsJsonAsync("/applications/"+Uri.EscapeDataString(id)+"/recovery-policy",new ApplicationRecoveryPolicy(enabled),ct); response.EnsureSuccessStatusCode();
    }
    public async Task Recover(ApplicationLifecycleStatus app,CancellationToken ct = default)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        ApplicationCredentialFile[] material;
        try { material = Resolve(app.AppId,app.CredentialIds); }
        catch (InvalidOperationException)
        {
            using var blocked = await http.PostAsJsonAsync("/applications/"+Uri.EscapeDataString(app.ContainerId)+"/recovery-blocked",new ApplicationRecoveryBlocked(app.Revision),ct); blocked.EnsureSuccessStatusCode(); return;
        }
        using var response = await http.PostAsJsonAsync("/applications/"+Uri.EscapeDataString(app.ContainerId)+(app.PendingStart ? "/converge" : "/recover"),new ApplicationRecoveryRequest(app.Revision,material),ct); response.EnsureSuccessStatusCode();
    }
    private ApplicationCredentialFile[] Resolve(string app,string[] ids) => ids.Select(id => credentials?.Resolve(app,id).Credential ?? throw new InvalidOperationException("A requested app credential is unavailable, revoked or expired.")).ToArray();
    private async Task Control(string id,string action,CancellationToken ct)
    {
        var path = "/applications/" + Uri.EscapeDataString(id);
        // Older brokers remain usable for applications without managed resources.
        ApplicationResourceState? resources = null;
        if (action != "stop")
        {
            using var metadata = await http!.GetAsync(path + "/resources",ct);
            if (metadata.IsSuccessStatusCode) resources = await metadata.Content.ReadFromJsonAsync<ApplicationResourceState>(StateFiles.Json,ct);
            else if (metadata.StatusCode != System.Net.HttpStatusCode.NotFound) metadata.EnsureSuccessStatusCode();
        }
        using var response = resources is null ? await http!.PostAsync(path + "/" + action,null,ct) : await http!.PostAsJsonAsync(path + "/bootstrap/" + action,Resolve(resources.AppId,resources.CredentialIds),ct);
        response.EnsureSuccessStatusCode();
    }
    public void Dispose() {http?.Dispose();updateHttp?.Dispose();}
}
