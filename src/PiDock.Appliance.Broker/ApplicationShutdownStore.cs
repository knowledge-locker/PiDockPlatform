using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

public interface IApplicationShutdownStore { ApplicationShutdownPlan? Read(); void Save(ApplicationShutdownPlan plan); }
public sealed class ApplicationShutdownStore(IDataProtectionProvider protection,string root):IApplicationShutdownStore
{
    private readonly string path=Path.Combine(StateFiles.PrepareDirectory(root),"shutdown.protected");
    private readonly IDataProtector protector=protection.CreateProtector("PiDock.ApplicationShutdown.v1");
    public ApplicationShutdownPlan? Read()
    {
        if(!File.Exists(path))return null;
        if(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)||new FileInfo(path).Length>16384)throw new IOException("Invalid shutdown journal.");
        try { var plan=JsonSerializer.Deserialize<ApplicationShutdownPlan>(protector.Unprotect(File.ReadAllText(path)),StateFiles.Json)??throw new IOException("Missing shutdown journal.");Validate(plan);return plan; }
        catch(Exception e)when(e is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException){throw new IOException("Shutdown journal unavailable.");}
    }
    public void Save(ApplicationShutdownPlan plan){Validate(plan);StateFiles.WriteAtomic(path,protector.Protect(JsonSerializer.Serialize(plan,StateFiles.Json)));}
    private static void Validate(ApplicationShutdownPlan plan)
    {
        if(plan.OperationId==Guid.Empty || plan.Reason is not ("maintenance" or "host_shutdown") || plan.Phase is not ("stopping" or "paused" or "resumed") || plan.Order is not {Length:<=16} || plan.NextIndex<0 || plan.NextIndex>plan.Order.Length || plan.Order.Any(id=>id is not {Length:64}||!id.All(char.IsAsciiHexDigit)) || plan.Order.Distinct(StringComparer.Ordinal).Count()!=plan.Order.Length)
            throw new ArgumentException("Invalid shutdown journal.");
    }
}
