using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

public sealed record ManagedDeployment(ApplicationManifest Manifest,string ContainerId,ApplicationCredentialFile[] Credentials,DateTimeOffset LastDeliveredAt,
    bool DesiredRunning = false, bool RecoveryEnabled = false, long Revision = 0, int Attempts = 0,
    DateTimeOffset? NextAttemptAt = null, DateTimeOffset? StableSince = null, string Outcome = "unconfigured", Guid? LastDeliveryId = null, ApplicationHealthStatus? Health = null, long HealthSequence = 0, DateTimeOffset? FirstReadyAt = null, ApplicationHealthReport? LastHealthReport = null, bool PendingStart = false);
public interface IManagedDeploymentStore
{
    ManagedDeployment? Find(string containerId);
    void Save(ManagedDeployment deployment);
    void Forget(string containerId);
}
public sealed class ManagedDeploymentStore(IDataProtectionProvider protection,string root):IManagedDeploymentStore
{
    private readonly string directory=StateFiles.PrepareDirectory(root);
    private readonly IDataProtector protector=protection.CreateProtector("PiDock.ManagedAppBootstrap.v1");
    private string PathFor(string id)
    {
        if(id.Length!=64||!id.All(char.IsAsciiHexDigit))throw new ArgumentException("Invalid managed container ID.");
        return Path.Combine(directory,id+".protected");
    }
    public ManagedDeployment? Find(string id)
    {
        var path=PathFor(id);if(!File.Exists(path))return null;
        if(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)||new FileInfo(path).Length>131072)throw new IOException("Invalid managed app record.");
        try
        {
            var record = JsonSerializer.Deserialize<ManagedDeployment>(protector.Unprotect(File.ReadAllText(path)),StateFiles.Json) ?? throw new IOException("Invalid managed app record.");
            if (record.ContainerId != id || record.Manifest?.Resources?.Requested != true || record.Credentials is not { Length: <= 8 }) throw new IOException("Invalid managed app record.");
            record.Manifest.Validate();
            return record;
        }
        catch(Exception e)when(e is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException){throw new IOException("Managed app record unavailable.");}
    }
    public void Save(ManagedDeployment deployment)=>StateFiles.WriteAtomic(PathFor(deployment.ContainerId),protector.Protect(JsonSerializer.Serialize(deployment,StateFiles.Json)));
    public void Forget(string id)=>File.Delete(PathFor(id));
}
