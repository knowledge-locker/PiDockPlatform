using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

public interface IApplicationBackupStore
{
    ApplicationBackupInfo[] List();
    (ApplicationBackupInfo Info,ApplicationDataSnapshot Data) Read(Guid id);
    ApplicationBackupInfo Save(ApplicationBackupInfo info,ApplicationDataSnapshot data);
    void Delete(Guid id);
    ApplicationDataRestoreStatus? RestoreStatus();
    void SaveRestore(ApplicationDataRestoreStatus status);
}
public interface IApplicationBackupCapacity {void Require(string directory,long additionalBytes);}
public sealed class ApplicationBackupCapacity:IApplicationBackupCapacity
{
    public void Require(string directory,long additionalBytes)
    {
        var full=Path.GetFullPath(directory);
        var drive=DriveInfo.GetDrives().Where(d=>d.IsReady&&(full==d.Name.TrimEnd(Path.DirectorySeparatorChar)||full.StartsWith(d.Name.EndsWith(Path.DirectorySeparatorChar)?d.Name:d.Name+Path.DirectorySeparatorChar,StringComparison.Ordinal))).OrderByDescending(d=>d.Name.Length).FirstOrDefault()??throw new IOException("Backup filesystem capacity unavailable.");
        if(drive.AvailableFreeSpace<Math.Max(512L*1024*1024,drive.TotalSize/20)+additionalBytes)throw new IOException("Insufficient reserved backup storage.");
    }
}
public sealed class ApplicationBackupStore(IDataProtectionProvider protection,string root,IApplicationBackupCapacity? capacity=null):IApplicationBackupStore
{
    private readonly string directory=StateFiles.PrepareDirectory(root);
    private readonly IDataProtector payload=protection.CreateProtector("PiDock.AppDataBackup.v1"),journal=protection.CreateProtector("PiDock.AppDataRestore.v1");
    private readonly object gate=new();
    public ApplicationBackupInfo[] List(){lock(gate)return Files().Select(ReadEnvelope).Select(v=>v.Info).OrderByDescending(v=>v.CreatedAt).ToArray();}
    public (ApplicationBackupInfo Info,ApplicationDataSnapshot Data) Read(Guid id)
    {
        lock(gate){var value=ReadEnvelope(PathFor(id));var data=ApplicationDataArchive.Read(value.Archive);if(data.Sha256!=value.Info.ArchiveSha256||data.Archive.LongLength!=value.Info.ArchiveBytes||data.DataBytes!=value.Info.DataBytes||data.Entries.Length!=value.Info.EntryCount)throw new IOException("Backup integrity check failed.");return(value.Info,data);}
    }
    public ApplicationBackupInfo Save(ApplicationBackupInfo info,ApplicationDataSnapshot data)
    {
        lock(gate)
        {
            Validate(info);if(data.Sha256!=info.ArchiveSha256||data.Archive.LongLength!=info.ArchiveBytes||data.DataBytes!=info.DataBytes||data.Entries.Length!=info.EntryCount)throw new ArgumentException("Backup metadata mismatch.");
            var files=Files();if(files.Contains(PathFor(info.Id))){var existing=Read(info.Id);if(existing.Info.AppId!=info.AppId||existing.Info.Image!=info.Image||existing.Info.Kind!=info.Kind)throw new ArgumentException("Backup identity already used.");return existing.Info;}
            if(files.Select(ReadEnvelope).Count(v=>v.Info.AppId==info.AppId)>=ApplicationBackupRules.MaximumSnapshotsPerApp)throw new InvalidOperationException("Delete an older backup before creating another; restore needs a safety-backup slot.");
            byte[] metadata=JsonSerializer.SerializeToUtf8Bytes(info,StateFiles.Json);var envelope=new byte[checked(metadata.Length+data.Archive.Length+4)];BinaryPrimitives.WriteInt32LittleEndian(envelope,metadata.Length);metadata.CopyTo(envelope,4);data.Archive.CopyTo(envelope,metadata.Length+4);
            byte[] encrypted=payload.Protect(envelope);
            if(files.Sum(f=>new FileInfo(f).Length)+encrypted.LongLength>ApplicationBackupRules.MaximumStoreBytes)throw new IOException("Backup store capacity exceeded.");
            (capacity??new ApplicationBackupCapacity()).Require(directory,encrypted.LongLength+ApplicationBackupRules.MaximumArchiveBytes);
            StateFiles.WriteAtomicBytes(PathFor(info.Id),encrypted);return info;
        }
    }
    public void Delete(Guid id){lock(gate){var active=RestoreStatus();if(ApplicationBackupRules.BlocksStartup(active)&&(active!.SnapshotId==id||active.SafetySnapshotId==id))throw new InvalidOperationException("Backup is required by an incomplete restore.");Read(id);File.Delete(PathFor(id));StateFiles.SynchronizeDirectory(directory);}}
    public ApplicationDataRestoreStatus? RestoreStatus()
    {
        lock(gate)
        {
            string path=Path.Combine(directory,"restore.protected");if(!File.Exists(path))return null;
            CheckFile(path,16384);
            try{var status=JsonSerializer.Deserialize<ApplicationDataRestoreStatus>(journal.Unprotect(File.ReadAllText(path)),StateFiles.Json)??throw new IOException("Restore journal unavailable.");ValidateStatus(status);return status;}
            catch(Exception e)when(e is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException){throw new IOException("Restore journal unavailable.");}
        }
    }
    public void SaveRestore(ApplicationDataRestoreStatus status){lock(gate){ValidateStatus(status);StateFiles.WriteAtomic(Path.Combine(directory,"restore.protected"),journal.Protect(JsonSerializer.Serialize(status,StateFiles.Json)));}}
    private string[] Files(){var files=Directory.GetFiles(directory,"*.backup");if(files.Length>64||files.Sum(f=>new FileInfo(f).Length)>ApplicationBackupRules.MaximumStoreBytes)throw new IOException("Backup store limit exceeded.");return files;}
    private string PathFor(Guid id)=>id==Guid.Empty?throw new ArgumentException("Backup ID required."):Path.Combine(directory,id.ToString("N")+".backup");
    private (ApplicationBackupInfo Info,byte[] Archive) ReadEnvelope(string path)
    {
        CheckFile(path,ApplicationBackupRules.MaximumArchiveBytes+32768);
        try
        {
            byte[] bytes=payload.Unprotect(File.ReadAllBytes(path));if(bytes.Length<4)throw new IOException("Invalid backup.");int length=BinaryPrimitives.ReadInt32LittleEndian(bytes);if(length is <1 or >8192||length>bytes.Length-4)throw new IOException("Invalid backup metadata.");
            var info=JsonSerializer.Deserialize<ApplicationBackupInfo>(bytes.AsSpan(4,length),StateFiles.Json)??throw new IOException("Invalid backup metadata.");Validate(info);
            if(Path.GetFileName(path)!=info.Id.ToString("N")+".backup"||bytes.Length-4-length!=info.ArchiveBytes)throw new IOException("Invalid backup identity.");return(info,bytes[(4+length)..]);
        }
        catch(Exception e)when(e is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException){throw new IOException("Backup unavailable or unauthenticated.");}
    }
    private static void CheckFile(string path,long max){if(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)||new FileInfo(path).Length>max)throw new IOException("Invalid backup file.");}
    private static void Validate(ApplicationBackupInfo info)
    {
        new ApplicationManifest(info.AppId,info.Image).Validate();
        if(info.Id==Guid.Empty||!IdentityRules.IsId(info.AppId)||info.AppId.Length>40||info.Kind is not("manual" or "pre_restore")||info.ArchiveBytes is <1024 or >ApplicationBackupRules.MaximumArchiveBytes||info.DataBytes is <0 or >ApplicationBackupRules.MaximumDataBytes||info.EntryCount is <0 or >ApplicationBackupRules.MaximumEntries||info.ArchiveSha256 is not{Length:64}||!info.ArchiveSha256.All(char.IsAsciiHexDigit))throw new ArgumentException("Invalid backup metadata.");
    }
    private static void ValidateStatus(ApplicationDataRestoreStatus status)
    {
        if(status.OperationId==Guid.Empty||status.SnapshotId==Guid.Empty||!IdentityRules.IsId(status.AppId)||status.AppId.Length>40||status.ContainerId is not{Length:64}||!status.ContainerId.All(char.IsAsciiHexDigit)||status.Phase is not("preparing" or "staging" or "applying" or "finishing" or "completed")||status.Outcome is not("in_progress" or "interrupted" or "completed")||status.Image.Length>273)throw new ArgumentException("Invalid restore journal.");
    }
}
