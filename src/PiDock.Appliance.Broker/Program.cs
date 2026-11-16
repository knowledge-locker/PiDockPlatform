using System.Net;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Core;
using PiDock.Appliance.Broker;
if(args.Contains("--usb-node")) {await UsbNodeHost.Run(args);return;}
var builder = WebApplication.CreateBuilder(args);
var socket = builder.Configuration["Broker:SocketPath"] ?? "/run/pidock/broker.sock";
if (!Path.IsPathFullyQualified(socket)) throw new InvalidOperationException("Broker socket must be an absolute path.");
var socketDirectory = StateFiles.PrepareDirectory(Path.GetDirectoryName(socket)!);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(socketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
if (File.Exists(socket)) throw new InvalidOperationException("Broker socket already exists; stop the other broker or remove a verified stale socket locally.");
builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 65536; k.ListenUnixSocket(socket); });
var resourcesEnabled = builder.Configuration.GetValue<bool>("Broker:EnableAppResources");
var installTrustEnabled = builder.Configuration.GetValue<bool>("Broker:RequireAppImageApproval");
if (installTrustEnabled && !resourcesEnabled) throw new InvalidOperationException("App image approvals require protected managed resource state.");
var orchestrationEnabled = builder.Configuration.GetValue<bool>("Broker:EnableAppOrchestration");
var removalsEnabled = builder.Configuration.GetValue<bool>("Broker:EnableAppRemoval");
if (removalsEnabled && !orchestrationEnabled) throw new InvalidOperationException("App removal requires managed app orchestration.");
if(orchestrationEnabled && (!resourcesEnabled || !(builder.Configuration.GetValue<bool>("Broker:EnableLifecycle") || builder.Configuration.GetValue<bool>("Broker:EnableDeployments"))))throw new InvalidOperationException("App orchestration requires resources and lifecycle operations.");
var updatesEnabled=builder.Configuration.GetValue<bool>("Broker:EnableAppUpdates");
if(updatesEnabled&&!orchestrationEnabled)throw new InvalidOperationException("App updates require app orchestration.");
var backupsEnabled=builder.Configuration.GetValue<bool>("Broker:EnableAppBackups");
var dataHelperImage=builder.Configuration["Broker:AppDataHelperImage"];
if(backupsEnabled&&(!orchestrationEnabled||string.IsNullOrWhiteSpace(dataHelperImage)))throw new InvalidOperationException("App backups require orchestration and a trusted pinned helper image.");
var recoveryEnabled = builder.Configuration.GetValue<bool>("Broker:EnableAppRecovery");
if (recoveryEnabled && !(builder.Configuration.GetValue<bool>("Broker:EnableLifecycle") || builder.Configuration.GetValue<bool>("Broker:EnableDeployments"))) throw new InvalidOperationException("App recovery requires lifecycle operations to be enabled.");
if (recoveryEnabled && !resourcesEnabled) throw new InvalidOperationException("App recovery requires managed app resources.");
if (resourcesEnabled)
{
    var state = builder.Configuration["Broker:StateDirectory"];
    if (state is null || !Path.IsPathFullyQualified(state)) throw new InvalidOperationException("Managed app resources require an absolute private Broker:StateDirectory.");
    state = StateFiles.PrepareDirectory(state);
    builder.Services.AddDataProtection().SetApplicationName("PiDock.Broker").PersistKeysToFileSystem(new DirectoryInfo(StateFiles.PrepareDirectory(Path.Combine(state,"keys"))));
    builder.Services.AddSingleton<IManagedDeploymentStore>(sp => new ManagedDeploymentStore(sp.GetRequiredService<IDataProtectionProvider>(),Path.Combine(state,"apps")));
    builder.Services.AddSingleton<IApplicationUpdateStore>(sp=>new ApplicationUpdateStore(sp.GetRequiredService<IDataProtectionProvider>(),Path.Combine(state,"updates")));
    builder.Services.AddSingleton<IApplicationBackupStore>(sp=>new ApplicationBackupStore(sp.GetRequiredService<IDataProtectionProvider>(),Path.Combine(state,"backups")));
    builder.Services.AddSingleton<IApplicationInstallTrustStore>(sp => new ApplicationInstallTrustStore(sp.GetRequiredService<IDataProtectionProvider>(), Path.Combine(state, "install-trust")));
    builder.Services.AddSingleton<IApplicationRemovalStore>(sp => new ApplicationRemovalStore(sp.GetRequiredService<IDataProtectionProvider>(), Path.Combine(state, "removal")));
    builder.Services.AddSingleton<IApplicationShutdownStore>(sp=>new ApplicationShutdownStore(sp.GetRequiredService<IDataProtectionProvider>(),Path.Combine(state,"orchestration")));
    builder.Services.AddSingleton<IApplicationBootstrapDelivery>(new ApplicationBootstrapDelivery(builder.Configuration["Broker:DockerSocket"] ?? "/var/run/docker.sock"));
}
builder.Services.AddSingleton(sp => new DockerOperations(UnixHttp.Connect(builder.Configuration["Broker:DockerSocket"] ?? "/var/run/docker.sock", TimeSpan.FromMinutes(3)),sp.GetService<IManagedDeploymentStore>(),sp.GetService<IApplicationBootstrapDelivery>(),TimeProvider.System,recoveryEnabled,orchestrationEnabled,sp.GetService<IApplicationShutdownStore>(),updatesEnabled,sp.GetService<IApplicationUpdateStore>(),backupsEnabled,sp.GetService<IApplicationBackupStore>(),dataHelperImage,installTrustEnabled,sp.GetService<IApplicationInstallTrustStore>(),removalsEnabled,sp.GetService<IApplicationRemovalStore>())
{ StorageConfiguration = builder.Configuration.GetSection("Broker:AppStorage").Get<ApplicationStorageConfiguration>() });
builder.Services.AddSingleton<IDigitalIoDriver, LinuxDigitalIo>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DigitalIoService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DigitalIoService>());
builder.Services.AddSingleton<MainNodeService>();
builder.Services.AddHostedService(sp=>sp.GetRequiredService<MainNodeService>());
var installedRelease = OsUpdateRules.Installed(AppContext.BaseDirectory);
var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (ArgumentException e) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
    { app.Logger.LogWarning("Broker operation failed: {Type}", e.GetType().Name); context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "Host operation unavailable; check the runtime and broker logs." }); }
});
app.MapGet("/health/update", () => new OsUpdateHealth(installedRelease?.Digest, true));
app.MapGet("/applications/runtime-access", (DockerOperations docker, CancellationToken ct) => docker.RuntimeAccess(ct));
app.MapGet("/applications/storage/support", (DockerOperations docker) => docker.StorageSupport());
app.MapGet("/applications/removal/support", (DockerOperations docker) => docker.RemovalSupport());
app.MapGet("/applications/removal/status", (DockerOperations docker) => docker.RemovalStatus() is { } status ? Results.Ok(status) : Results.NoContent());
if (removalsEnabled)
{
    app.MapPost("/applications/{id}/remove", async (string id, ApplicationRemovalRequest request, DockerOperations docker, CancellationToken ct) => { await docker.RemoveApplication(id, request, ct); return Results.Ok(); });
    app.MapPost("/applications/removal/complete", async (ApplicationRemovalCompleteRequest request, DockerOperations docker, CancellationToken ct) => { await docker.CompleteRemoval(request, ct); return Results.Ok(); });
}
app.MapGet("/applications/storage", (DockerOperations docker, CancellationToken ct) => docker.StorageStatus(ct));
app.MapGet("/applications/install-trust/support", (DockerOperations docker) => docker.InstallTrustSupport());
app.MapGet("/applications/install-trust/approvals", (DockerOperations docker) => docker.InstallApprovals());
if (installTrustEnabled)
{
    app.MapPost("/applications/install-trust/approvals", (ApplicationInstallApprovalRequest request, DockerOperations docker, CancellationToken ct) => docker.ApproveInstall(request, ct));
    app.MapDelete("/applications/install-trust/approvals/{hash}", async (string hash, DockerOperations docker, CancellationToken ct) => { await docker.RevokeInstallApproval(hash, ct); return Results.Ok(); });
}
app.MapGet("/applications/backups/support",(DockerOperations docker)=>docker.BackupSupport());
app.MapGet("/applications/backups",(DockerOperations docker)=>docker.Backups());
app.MapGet("/applications/backups/restore",(DockerOperations docker)=>docker.DataRestoreStatus() is {} status?Results.Ok(status):Results.NoContent());
if(backupsEnabled)
{
    app.MapPost("/applications/{id}/backup",(string id,ApplicationBackupRequest request,DockerOperations docker,CancellationToken ct)=>docker.CreateBackup(id,request,ct));
    app.MapPost("/applications/{id}/restore",async(string id,ApplicationRestoreRequest request,DockerOperations docker,CancellationToken ct)=>{await docker.RestoreBackup(id,request,ct);return Results.Ok();});
    app.MapDelete("/applications/backups/{id:guid}",async(Guid id,DockerOperations docker,CancellationToken ct)=>{await docker.DeleteBackup(id,ct);return Results.Ok();});
}
app.MapGet("/applications/updates/support",(DockerOperations docker)=>docker.UpdateSupport());
app.MapGet("/applications/updates",(DockerOperations docker,CancellationToken ct)=>docker.UpdateStatus(ct));
if(updatesEnabled)
{
    app.MapPost("/applications/{id}/update-policy",async(string id,ApplicationUpdatePolicyRequest request,DockerOperations docker,CancellationToken ct)=>{await docker.ConfigureUpdatePolicy(id,request,ct);return Results.Ok();});
    app.MapPost("/applications/{id}/update",async(string id,ApplicationUpdateRequest request,DockerOperations docker,CancellationToken ct)=>{await docker.QueueUpdate(id,request,ct);return Results.Accepted();});
    app.MapPost("/applications/{id}/rollback",async(string id,ApplicationVersionRequest request,DockerOperations docker,CancellationToken ct)=>{await docker.RequestRollback(id,request,ct);return Results.Accepted();});
    app.MapPost("/applications/{id}/discard-previous",async(string id,ApplicationVersionRequest request,DockerOperations docker,CancellationToken ct)=>{await docker.DiscardPrevious(id,request,ct);return Results.Accepted();});
    app.MapPost("/applications/updates/{appId}/advance",async(string appId,ApplicationUpdateAdvance request,DockerOperations docker,CancellationToken ct)=>{await docker.AdvanceUpdate(appId,request,ct);return Results.Ok();});
}
app.MapGet("/applications/orchestration/support",(DockerOperations docker)=>docker.OrchestrationSupport());
app.MapGet("/applications/orchestration/status",(DockerOperations docker)=>docker.OrchestrationStatus());
if(orchestrationEnabled)
{
    app.MapPost("/applications/orchestration/reconcile",async(DockerOperations docker,CancellationToken ct)=>{await docker.ReconcileOrchestration(ct);return Results.Ok();});
    app.MapPost("/applications/orchestration/pause",async(DockerOperations docker,CancellationToken ct)=>{await docker.PauseApplications(ct);return Results.Ok();});
    app.MapPost("/applications/orchestration/resume",async(DockerOperations docker,CancellationToken ct)=>{await docker.ResumeApplications(ct);return Results.Ok();});
    app.MapPost("/applications/{id}/converge",async(string id,ApplicationRecoveryRequest request,DockerOperations docker,CancellationToken ct)=>{await docker.Recover(id,request,ct);return Results.Ok();});
    if(!recoveryEnabled)app.MapPost("/applications/{id}/recovery-blocked",async(string id,ApplicationRecoveryBlocked request,DockerOperations docker,CancellationToken ct)=>{await docker.RecoveryBlocked(id,request.Revision,ct);return Results.Ok();});
}
app.MapGet("/applications/health/support", (DockerOperations docker) => docker.HealthSupport());
if (resourcesEnabled) app.MapPost("/applications/health/check", async (DockerOperations docker, CancellationToken ct) => { await docker.CheckHealth(ct); return Results.Ok(); });
app.MapGet("/applications/lifecycle/support", (DockerOperations docker) => docker.LifecycleSupport());
app.MapGet("/applications/lifecycle", (DockerOperations docker, CancellationToken ct) => docker.LifecycleStatus(ct));
app.MapGet("/applications/resources", () => new ApplicationResourceSupport(1,resourcesEnabled,ApplicationResourceRules.DataDirectory,ApplicationResourceRules.CredentialDirectory,"pidock-stdin-v1",["PiDockOS-managed start/restart required; optional supervised recovery is advertised by lifecycle/support. Docker automatic restart is disabled.","Data volumes are retained; discover optional app backups separately. Data quotas, purge and migration are not implemented."]));
app.MapGet("/applications/{id}/resources", async (string id, DockerOperations docker, CancellationToken ct) =>
{
    var state = await docker.Resources(id,ct);
    return state is null ? (IResult)Results.NotFound() : Results.Ok(state);
});
app.MapGet("/nodes/status",(MainNodeService nodes)=>new {enabled=nodes.Enabled});
app.MapGet("/nodes",(MainNodeService nodes)=>nodes.Current);
app.MapGet("/nodes/audit",(MainNodeService nodes)=>nodes.Audit);
app.MapPost("/nodes/adopt",async(NodeAction request,MainNodeService nodes,CancellationToken ct)=>{await nodes.Adopt(request,ct);return Results.Ok();});
app.MapPut("/nodes/configuration",async(NodeConfigureAction request,MainNodeService nodes,CancellationToken ct)=>{await nodes.Configure(request,ct);return Results.Ok();});
app.MapPost("/nodes/release",async(NodeReleaseAction request,MainNodeService nodes,CancellationToken ct)=>{await nodes.Release(request,ct);return Results.Ok();});
app.MapGet("/applications", (DockerOperations docker, CancellationToken ct) => docker.List(ct));
app.MapGet("/gpio", (DigitalIoService gpio) => gpio.Status());
app.MapPost("/gpio/leases", (DigitalLeaseRequest request, DigitalIoService gpio) => { gpio.Claim(request); return Results.Ok(); });
app.MapPost("/gpio/policy", (DigitalPinPermission permission,DigitalIoService gpio)=>{gpio.ConfigurePin(permission);return Results.Ok();});
app.MapDelete("/gpio/leases/{session:guid}", (Guid session, DigitalIoService gpio) => { gpio.Release(session); return Results.Ok(); });
if (builder.Configuration.GetValue<bool>("Broker:EnableDeployments"))
{
    if (resourcesEnabled) app.MapPost("/applications/prepared", async (ApplicationDeploymentRequest request, DockerOperations docker, CancellationToken ct) => Results.Ok(new { id = await docker.DeployPrepared(request,ct) }));
    app.MapPost("/applications", async (ApplicationManifest manifest, DockerOperations docker, CancellationToken ct) => Results.Ok(new { id = await docker.Deploy(manifest, ct) }));
}
if (builder.Configuration.GetValue<bool>("Broker:EnableLifecycle") || builder.Configuration.GetValue<bool>("Broker:EnableDeployments"))
{
    if (resourcesEnabled)
    {
        app.MapPost("/applications/{id}/recovery-policy", async (string id, ApplicationRecoveryPolicy policy, DockerOperations docker, CancellationToken ct) => { await docker.SetRecovery(id,policy.Enabled,ct); return Results.Ok(); });
        if (recoveryEnabled)
        {
            app.MapPost("/applications/lifecycle/reconcile", async (DockerOperations docker, CancellationToken ct) => { await docker.ReconcileLifecycle(ct); return Results.Ok(); });
            app.MapPost("/applications/{id}/recover", async (string id, ApplicationRecoveryRequest request, DockerOperations docker, CancellationToken ct) => { await docker.Recover(id,request,ct); return Results.Ok(); });
            app.MapPost("/applications/{id}/recovery-blocked", async (string id, ApplicationRecoveryBlocked request, DockerOperations docker, CancellationToken ct) => { await docker.RecoveryBlocked(id,request.Revision,ct); return Results.Ok(); });
        }
    }
    if (resourcesEnabled) app.MapPost("/applications/{id}/bootstrap/{action}", async (string id, string action, ApplicationCredentialFile[] credentials, DockerOperations docker, CancellationToken ct) =>
    { if (action is not ("start" or "restart")) return Results.BadRequest(); await docker.Lifecycle(id,true,action == "restart",credentials,ct); return Results.Ok(); });
    app.MapPost("/applications/{id}/restart", async (string id, DockerOperations docker, CancellationToken ct) => { await docker.Restart(id, ct); return Results.Ok(); });
    app.MapPost("/applications/{id}/start", async (string id, DockerOperations docker, CancellationToken ct) => { await docker.ChangeState(id, true, ct); return Results.Ok(); });
    app.MapPost("/applications/{id}/stop", async (string id, DockerOperations docker, CancellationToken ct) => { await docker.ChangeState(id, false, ct); return Results.Ok(); });
}
await app.StartAsync();
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
try { await app.WaitForShutdownAsync(); }
finally
{
    try
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(180));
        await app.Services.GetRequiredService<DockerOperations>().HostShutdown(deadline.Token);
    }
    catch(Exception e)when(e is HttpRequestException or IOException or InvalidOperationException or ArgumentException or OperationCanceledException or System.Security.Cryptography.CryptographicException)
    { app.Logger.LogWarning("Coordinated app shutdown incomplete: {ErrorType}; retained journal will be retried.",e.GetType().Name); }
    finally{File.Delete(socket);}
}
