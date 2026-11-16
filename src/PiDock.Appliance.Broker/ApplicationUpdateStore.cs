using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

public sealed record ApplicationUpdateRecord(Guid OperationId,string AppId,string Phase,string ActiveId,string OriginalId,
    ApplicationManifest OriginalManifest,ApplicationManifest TargetManifest,long Revision,DateTimeOffset RequestedAt,bool DesiredRunning,
    string? CandidateId=null,string? CandidateNetworkId=null,string? OriginalNetworkId=null,DateTimeOffset? Deadline=null,DateTimeOffset? ReadySince=null,
    DateTimeOffset? NextCheckAt=null,string Outcome="queued",bool RecoveryEnabled=false);
public interface IApplicationUpdateStore
{
    ApplicationUpdateRecord[] ReadAll();
    void Save(ApplicationUpdateRecord record);
    void Forget(string appId) => throw new InvalidOperationException("Update metadata removal is unavailable.");
}
public sealed class ApplicationUpdateStore(IDataProtectionProvider protection,string root):IApplicationUpdateStore
{
    private readonly string directory=StateFiles.PrepareDirectory(root);
    private readonly IDataProtector protector=protection.CreateProtector("PiDock.ApplicationUpdates.v1");
    public ApplicationUpdateRecord[] ReadAll()
    {
        var files=System.IO.Directory.GetFiles(directory,"*.protected").Take(17).ToArray();
        if(files.Length>16)throw new IOException("Update journal capacity exceeded.");
        try
        {
            return files.Select(path=>{
                if(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)||new FileInfo(path).Length>32768)throw new IOException("Invalid update journal.");
                var record=JsonSerializer.Deserialize<ApplicationUpdateRecord>(protector.Unprotect(File.ReadAllText(path)),StateFiles.Json)??throw new IOException("Invalid update journal.");
                Validate(record);if(Path.GetFileName(path)!=record.AppId+".protected")throw new IOException("Invalid update journal identity.");return record;
            }).ToArray();
        }
        catch(Exception e)when(e is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException){throw new IOException("Update journal unavailable.");}
    }
    public void Save(ApplicationUpdateRecord record){Validate(record);StateFiles.WriteAtomic(Path.Combine(directory,record.AppId+".protected"),protector.Protect(JsonSerializer.Serialize(record,StateFiles.Json)));}
    public void Forget(string appId)
    {
        if (!IdentityRules.IsId(appId) || appId.Length > 40) throw new ArgumentException("Invalid app identity.");
        _ = ReadAll();
        File.Delete(Path.Combine(directory, appId + ".protected"));
        StateFiles.SynchronizeDirectory(directory);
    }
    private static void Validate(ApplicationUpdateRecord record)
    {
        static bool Id(string? value)=>value is {Length:64}&&value.All(char.IsAsciiHexDigit);
        if(record.OperationId==Guid.Empty||!IdentityRules.IsId(record.AppId)||record.AppId.Length>40||record.Revision<1||!Id(record.ActiveId)||!Id(record.OriginalId)||record.CandidateId is not null&&!Id(record.CandidateId)||record.Phase is not ("queued" or "staging" or "staged" or "switching" or "verifying" or "rollback_pending" or "rollback_verifying" or "rollback_failed" or "rollback_cleanup" or "committed" or "rolled_back" or "failed" or "discarding" or "accepted" or "stage_cleanup"))throw new ArgumentException("Invalid app update journal.");
        record.OriginalManifest.Validate();record.TargetManifest.Validate();
        if(record.CandidateNetworkId is not null&&!Id(record.CandidateNetworkId)||record.OriginalNetworkId is not null&&!Id(record.OriginalNetworkId)||
            JsonSerializer.Serialize(record.OriginalManifest with{Image=record.TargetManifest.Image},StateFiles.Json)!=JsonSerializer.Serialize(record.TargetManifest,StateFiles.Json))throw new ArgumentException("Update settings must remain unchanged.");
        if(record.OriginalManifest.Name!=record.AppId||record.TargetManifest.Name!=record.AppId||record.OriginalManifest.Resources?.Updates is null||record.TargetManifest.Resources?.Updates is null)throw new ArgumentException("Invalid update manifests.");
    }
}
