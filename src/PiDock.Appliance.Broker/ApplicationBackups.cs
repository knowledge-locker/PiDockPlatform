using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

public sealed partial class DockerOperations
{
    private bool RestoreBlocksStartup=>ApplicationBackupRules.BlocksStartup(backupStore?.RestoreStatus());
    public ApplicationBackupSupport BackupSupport()=>new(1,backupsEnabled&&backupStore is not null&&OrchestrationSupport().Enabled&&ValidHelperImage(appDataHelperImage),ApplicationBackupRules.MaximumSnapshotsPerApp,ApplicationBackupRules.MaximumDataBytes,ApplicationBackupRules.MaximumEntries,
        ["Local encrypted stopped-app snapshots; completed maintenance pause is required and remains in place after restore.","Restore requires the same app and exact installed image, a pre-restore safety-backup slot, and a preloaded trusted digest-pinned ARM64 helper image.","Only regular files/directories are supported; links and reserved .pidock-data- paths are rejected. No off-device export, schedules, data migrations or key recovery.","Incomplete restores block all app startup/resume, including when backup operations are disabled. Retry the same operation after repairing storage/runtime."]);
    public ApplicationBackupInfo[] Backups()=>backupStore?.List()??[];
    public ApplicationDataRestoreStatus? DataRestoreStatus()=>backupStore?.RestoreStatus();
    private void RequireBackups(){if(!BackupSupport().Enabled)throw new InvalidOperationException("App data backups are disabled or the trusted helper is not configured.");}
    private async Task<ManagedDeployment> BackupTarget(string id,CancellationToken ct)
    {
        if(shutdownStore?.Read() is not{Reason:"maintenance",Phase:"paused"})throw new InvalidOperationException("Finish a maintenance pause before backing up or restoring app data.");
        var app=(await List(ct)).SingleOrDefault(a=>a.Id==id)??throw new ArgumentException("Unknown managed application.");
        var record=store?.Find(id)??throw new InvalidOperationException("Managed app metadata unavailable.");RequireResourceRecord(app,record);
        if(record.Manifest.Resources?.PersistentData!=true||UpdateBlocks(record.Manifest.Name))throw new InvalidOperationException("App data is unavailable or an image update is active.");
        if((await RawList(ct)).Any(a=>a.Labels?.GetValueOrDefault(Label+".app")==record.Manifest.Name&&a.State is "running" or "restarting" or "paused"))throw new InvalidOperationException("All containers using this app data must be stopped.");
        await EnsureVolume(record.Manifest.Name,ct);return record;
    }
    public async Task<ApplicationBackupInfo> CreateBackup(string id,ApplicationBackupRequest request,CancellationToken ct)
    {
        RequireBackups();if(request.OperationId==Guid.Empty)throw new ArgumentException("Operation ID required.");await deploymentLock.WaitAsync(ct);
        try
        {
            if(RestoreBlocksStartup || RemovalBlocksStartup)throw new InvalidOperationException("Complete the active restore or removal before creating another snapshot.");
            var record=await BackupTarget(id,ct);var existing=backupStore!.List().SingleOrDefault(b=>b.Id==request.OperationId);
            if(existing is not null){if(existing.AppId!=record.Manifest.Name||existing.Image!=record.Manifest.Image||existing.Kind!="manual")throw new ArgumentException("Backup ID already used.");return existing;}
            return await Capture(record,request.OperationId,"manual",ct);
        }
        finally{deploymentLock.Release();}
    }
    private async Task<ApplicationBackupInfo> Capture(ManagedDeployment record,Guid snapshotId,string kind,CancellationToken ct)
    {
        using var response=await engine.GetAsync($"{Api}/containers/{record.ContainerId}/archive?path={Uri.EscapeDataString(ApplicationResourceRules.DataDirectory)}",HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();var buffer=new byte[65536];int count;
        while((count=await stream.ReadAsync(buffer,ct))>0){if(output.Length+count>ApplicationBackupRules.MaximumArchiveBytes)throw new ArgumentException("App data exceeds the backup archive limit.");output.Write(buffer,0,count);}
        var snapshot=ApplicationDataArchive.FromDocker(output.ToArray());
        return backupStore!.Save(new(snapshotId,record.Manifest.Name,record.Manifest.Image,Now,kind,snapshot.DataBytes,snapshot.Entries.Length,snapshot.Sha256,snapshot.Archive.LongLength),snapshot);
    }
    public async Task DeleteBackup(Guid id,CancellationToken ct)
    {
        RequireBackups();await deploymentLock.WaitAsync(ct);try{backupStore!.Delete(id);}finally{deploymentLock.Release();}
    }
    public async Task RestoreBackup(string id,ApplicationRestoreRequest request,CancellationToken ct)
    {
        RequireBackups();if(request.OperationId==Guid.Empty||request.SnapshotId==Guid.Empty)throw new ArgumentException("Restore and snapshot IDs required.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(150));ct=deadline.Token;
        await deploymentLock.WaitAsync(ct);ApplicationDataRestoreStatus? operation=null;
        try
        {
            if (RemovalBlocksStartup) throw new InvalidOperationException("Complete the active removal before restoring app data.");
            var active=backupStore!.RestoreStatus();
            if(active?.OperationId==request.OperationId&&active.Phase=="completed"){if(active.SnapshotId!=request.SnapshotId||active.ContainerId!=id)throw new ArgumentException("Restore operation ID conflict.");return;}
            if(ApplicationBackupRules.BlocksStartup(active)&&(active!.OperationId!=request.OperationId||active.SnapshotId!=request.SnapshotId||active.ContainerId!=id))throw new InvalidOperationException("Another restore must be completed first.");
            var record=await BackupTarget(id,ct);var snapshot=backupStore.Read(request.SnapshotId);
            if(snapshot.Info.AppId!=record.Manifest.Name||snapshot.Info.Image!=record.Manifest.Image)throw new ArgumentException("Restore requires the same app and exact installed image as the snapshot.");
            if(!ApplicationBackupRules.BlocksStartup(active)&&backupStore.List().Count(b=>b.AppId==record.Manifest.Name)>=ApplicationBackupRules.MaximumSnapshotsPerApp)throw new InvalidOperationException("Restore needs a free safety-backup slot.");
            await CheckHelperImage(ct);
            operation=ApplicationBackupRules.BlocksStartup(active)?active!:new(request.OperationId,record.Manifest.Name,id,request.SnapshotId,Guid.NewGuid(),record.Manifest.Image,"preparing",Now,"in_progress");
            backupStore.SaveRestore(operation with{Outcome="in_progress"});
            if(operation.Phase=="preparing")
            {
                if(!backupStore.List().Any(b=>b.Id==operation.SafetySnapshotId))await Capture(record,operation.SafetySnapshotId!.Value,"pre_restore",ct);
                operation=operation with{Phase="staging"};backupStore.SaveRestore(operation);
            }
            if(operation.Phase=="staging")
            {
                string stage=".pidock-data-stage-"+operation.OperationId.ToString("N");
                var archive=ApplicationDataArchive.Write(snapshot.Data.Entries,stage,ApplicationDataArchive.Manifest(operation.OperationId,snapshot.Data));
                using var content=new ByteArrayContent(archive);content.Headers.ContentType=new("application/x-tar");
                using var upload=await engine.PutAsync($"{Api}/containers/{id}/archive?path={Uri.EscapeDataString(ApplicationResourceRules.DataDirectory)}",content,ct);upload.EnsureSuccessStatusCode();
                operation=operation with{Phase="applying"};backupStore.SaveRestore(operation);
            }
            if(operation.Phase=="applying")
            {
                await RunDataHelper(operation,"apply",ct);
                operation=operation with{Phase="finishing"};backupStore.SaveRestore(operation);
            }
            if(operation.Phase=="finishing")
            {
                await RunDataHelper(operation,"finish",ct);
                operation=operation with{Phase="completed",Outcome="completed"};backupStore.SaveRestore(operation);
                await RemoveDataHelper(operation,"apply",ct);await RemoveDataHelper(operation,"finish",ct);
            }
        }
        catch
        {
            if(operation is not null&&operation.Phase!="completed")backupStore!.SaveRestore(operation with{Outcome="interrupted"});throw;
        }
        finally{deploymentLock.Release();}
    }
    private static bool ValidHelperImage(string? value)=>value is not null&&(value.StartsWith("sha256:",StringComparison.Ordinal)&&value.Length==71&&value[7..].All(char.IsAsciiHexDigit)||value.Contains("@sha256:",StringComparison.Ordinal)&&value.Length<=256&&value[(value.LastIndexOf(':')+1)..] is{Length:64} hex&&hex.All(char.IsAsciiHexDigit)&&!value.Any(char.IsWhiteSpace));
    private async Task CheckHelperImage(CancellationToken ct)
    {
        using var response=await engine.GetAsync($"{Api}/images/{Uri.EscapeDataString(appDataHelperImage!)}/json",ct);response.EnsureSuccessStatusCode();var image=await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if(image.GetProperty("Os").GetString()!="linux"||image.GetProperty("Architecture").GetString()!="arm64")throw new InvalidOperationException("Preload a trusted ARM64 app-data helper image.");
    }
    private static string HelperName(ApplicationDataRestoreStatus operation,string mode)=>"pidock-data-"+operation.OperationId.ToString("N")+"-"+mode;
    private async Task RunDataHelper(ApplicationDataRestoreStatus operation,string mode,CancellationToken ct)
    {
        string name=HelperName(operation,mode);
        using(var check=await engine.GetAsync($"{Api}/containers/{name}/json",ct))
        {
            if(check.StatusCode==HttpStatusCode.NotFound)
            {
                var request=new{Image=appDataHelperImage,User="1654:1654",Entrypoint=new[]{"dotnet","/app/PiDock.AppData.Helper.dll"},Cmd=new[]{mode,operation.OperationId.ToString("N")},Labels=new Dictionary<string,string>{{Label,"data-helper"},{Label+".operation",operation.OperationId.ToString("N")},{Label+".app",operation.AppId},{Label+".mode",mode}},HostConfig=new{ReadonlyRootfs=true,NetworkMode="none",CapDrop=new[]{"ALL"},SecurityOpt=new[]{"no-new-privileges:true"},PidsLimit=64,Memory=256L*1024*1024,NanoCpus=500_000_000L,RestartPolicy=new{Name="no"},Mounts=new[]{new{Type="volume",Source="pidock-app-"+operation.AppId+"-data",Target="/data",VolumeOptions=new{NoCopy=true}}},Tmpfs=new Dictionary<string,string>{{"/tmp","rw,noexec,nosuid,nodev,size=16777216"}}}};
                using var create=await engine.PostAsJsonAsync($"{Api}/containers/create?name={name}",request,ct);create.EnsureSuccessStatusCode();
            }
            else
            {
                check.EnsureSuccessStatusCode();var inspected=await check.Content.ReadFromJsonAsync<JsonElement>(ct);ValidateHelper(inspected,operation,mode);
                var state=inspected.GetProperty("State");if(state.GetProperty("Status").GetString()=="exited"&&state.GetProperty("ExitCode").GetInt32()==0)return;
            }
        }
        // Starting an already running owned helper returns 304. A failed invocation can resume its durable move journal.
        using(var start=await engine.PostAsync($"{Api}/containers/{name}/start",null,ct)){if(start.StatusCode!=HttpStatusCode.NotModified)start.EnsureSuccessStatusCode();}
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(65));
        try
        {
            while(true)
            {
                using var response=await engine.GetAsync($"{Api}/containers/{name}/json",timeout.Token);response.EnsureSuccessStatusCode();var inspected=await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);ValidateHelper(inspected,operation,mode);var state=inspected.GetProperty("State");
                if(!state.GetProperty("Running").GetBoolean()){if(state.GetProperty("Status").GetString()!="exited"||state.GetProperty("ExitCode").GetInt32()!=0)throw new IOException("App-data helper failed; maintenance remains paused.");return;}
                await Task.Delay(250,timeout.Token);
            }
        }
        catch
        {
            using var stopDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));using var stop=await engine.PostAsync($"{Api}/containers/{name}/stop?t=2",null,stopDeadline.Token);throw;
        }
    }
    private void ValidateHelper(JsonElement value,ApplicationDataRestoreStatus operation,string mode)
    {
        var config=value.GetProperty("Config");var labels=config.GetProperty("Labels");
        if(config.GetProperty("Image").GetString()!=appDataHelperImage||labels.GetProperty(Label).GetString()!="data-helper"||labels.GetProperty(Label+".operation").GetString()!=operation.OperationId.ToString("N")||labels.GetProperty(Label+".app").GetString()!=operation.AppId||labels.GetProperty(Label+".mode").GetString()!=mode)throw new InvalidOperationException("Refusing an unowned app-data helper container.");
    }
    private async Task RemoveDataHelper(ApplicationDataRestoreStatus operation,string mode,CancellationToken ct)
    {
        string name=HelperName(operation,mode);using var check=await engine.GetAsync($"{Api}/containers/{name}/json",ct);if(check.StatusCode==HttpStatusCode.NotFound)return;check.EnsureSuccessStatusCode();ValidateHelper(await check.Content.ReadFromJsonAsync<JsonElement>(ct),operation,mode);using var remove=await engine.DeleteAsync($"{Api}/containers/{name}",ct);remove.EnsureSuccessStatusCode();
    }
}
