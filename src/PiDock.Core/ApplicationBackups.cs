namespace PiDock.Core;

public sealed record ApplicationBackupSupport(int SchemaVersion,bool Enabled,int MaximumSnapshotsPerApp,long MaximumDataBytes,int MaximumEntries,string[] Limitations);
public sealed record ApplicationBackupInfo(Guid Id,string AppId,string Image,DateTimeOffset CreatedAt,string Kind,long DataBytes,int EntryCount,string ArchiveSha256,long ArchiveBytes);
public sealed record ApplicationBackupRequest(Guid OperationId);
public sealed record ApplicationRestoreRequest(Guid OperationId,Guid SnapshotId);
public sealed record ApplicationDataRestoreStatus(Guid OperationId,string AppId,string ContainerId,Guid SnapshotId,Guid? SafetySnapshotId,string Image,string Phase,DateTimeOffset RequestedAt,string Outcome);
public static class ApplicationBackupRules
{
    public const int MaximumSnapshotsPerApp=4, MaximumEntries=1024, MaximumPath=240;
    public const long MaximumDataBytes=16*1024*1024, MaximumArchiveBytes=20*1024*1024, MaximumStoreBytes=256*1024*1024;
    public static bool BlocksStartup(ApplicationDataRestoreStatus? state)=>state is not null && state.Phase!="completed";
    public static bool IsReserved(string name)=>name.Split('/').Any(part=>part.StartsWith(".pidock-data-",StringComparison.Ordinal));
    public static void ValidatePath(string path)
    {
        if(string.IsNullOrEmpty(path)||path.Length>MaximumPath||path.StartsWith('/')||path.Contains('\\')||path.Any(c=>char.IsControl(c)||c=='\ufffd')||path.Split('/').Any(p=>p is "" or "." or "..")||IsReserved(path))throw new ArgumentException("Unsupported app data path.");
    }
}
