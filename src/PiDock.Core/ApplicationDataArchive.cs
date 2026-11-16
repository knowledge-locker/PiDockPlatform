using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;

namespace PiDock.Core;

public sealed record ApplicationDataEntry(string Path,bool Directory,byte[] Data);
public sealed record ApplicationDataFile(string Path,bool Directory,long Bytes,string Sha256);
public sealed record ApplicationDataManifest(Guid OperationId,ApplicationDataFile[] Files);
public sealed record ApplicationDataSnapshot(byte[] Archive,ApplicationDataEntry[] Entries)
{
    public long DataBytes=>Entries.Sum(e=>(long)e.Data.Length);
    public string Sha256=>Convert.ToHexStringLower(SHA256.HashData(Archive));
}
public static class ApplicationDataArchive
{
    // Docker prefixes the exported mount's final path component. No links, devices or special files are admitted.
    public static ApplicationDataSnapshot FromDocker(byte[] archive)=>Read(archive,true);
    public static ApplicationDataSnapshot Read(byte[] archive,bool docker=false)
    {
        if(archive.LongLength>ApplicationBackupRules.MaximumArchiveBytes)throw new ArgumentException("App data archive limit exceeded.");
        Preflight(archive);
        var entries=new List<ApplicationDataEntry>();long total=0;
        using var reader=new TarReader(new MemoryStream(archive));
        while(reader.GetNextEntry(copyData:false) is {} entry)
        {
            string name=entry.Name.TrimEnd('/');
            if(docker)
            {
                if(name=="app-data"){if(entry.EntryType!=TarEntryType.Directory)throw new ArgumentException("Invalid app data root.");continue;}
                if(!name.StartsWith("app-data/",StringComparison.Ordinal))throw new ArgumentException("Unexpected app data root.");
                name=name[9..];
            }
            ApplicationBackupRules.ValidatePath(name);
            if(entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))throw new ArgumentException("Only regular app data files and directories are supported.");
            bool directory=entry.EntryType==TarEntryType.Directory;
            if(entry.Length<0||entry.Length>ApplicationBackupRules.MaximumDataBytes||directory&&entry.Length!=0||entries.Count>=ApplicationBackupRules.MaximumEntries)throw new ArgumentException("App data limits exceeded.");
            total+=entry.Length;if(total>ApplicationBackupRules.MaximumDataBytes)throw new ArgumentException("App data size limit exceeded.");
            if(entries.Any(e=>e.Path==name||!e.Directory&&name.StartsWith(e.Path+"/",StringComparison.Ordinal)||!directory&&e.Path.StartsWith(name+"/",StringComparison.Ordinal)))throw new ArgumentException("Conflicting app data paths.");
            var bytes=directory?[]:new byte[checked((int)entry.Length)];entry.DataStream?.ReadExactly(bytes);
            entries.Add(new(name,directory,bytes));
        }
        foreach(var item in entries.ToArray())
        {
            string parent=item.Path;
            while(parent.Contains('/'))
            {
                parent=parent[..parent.LastIndexOf('/')];if(entries.All(e=>e.Path!=parent))entries.Add(new(parent,true,[]));
                if(entries.Count>ApplicationBackupRules.MaximumEntries)throw new ArgumentException("App data path count limit exceeded.");
            }
        }
        return new(Write(entries),entries.ToArray());
    }
    // Bound declared TAR sizes (including extended metadata) before TarReader can allocate them.
    private static void Preflight(byte[] archive)
    {
        long position=0;
        while(position+512<=archive.Length)
        {
            var header=archive.AsSpan((int)position,512);if(header.IndexOfAnyExcept((byte)0)<0)return;
            long size=0;foreach(byte b in header.Slice(124,12)){if(b is 0 or 32)continue;if(b is <48 or >55)throw new ArgumentException("Unsupported archive size encoding.");size=checked(size*8+b-48);if(size>ApplicationBackupRules.MaximumArchiveBytes)throw new ArgumentException("Archive entry too large.");}
            byte kind=header[156];if(kind is (byte)'x' or (byte)'g' && size>16384)throw new ArgumentException("Archive metadata too large.");
            position=checked(position+512+((size+511)/512)*512);if(position>archive.Length)throw new ArgumentException("Truncated app data archive.");
        }
        if(position!=archive.Length)throw new ArgumentException("Truncated app data archive.");
    }
    public static byte[] Write(IEnumerable<ApplicationDataEntry> entries,string? prefix=null,ApplicationDataManifest? manifest=null)
    {
        using var output=new MemoryStream();using(var writer=new TarWriter(output,TarEntryFormat.Pax,true))
        {
            if(prefix is not null)Add(prefix,true,[]);
            foreach(var item in entries.OrderBy(e=>e.Path,StringComparer.Ordinal))Add(prefix is null?item.Path:prefix+"/"+item.Path,item.Directory,item.Data);
            if(manifest is not null)Add(prefix+"/.pidock-data-manifest.json",false,JsonSerializer.SerializeToUtf8Bytes(manifest,StateFiles.Json));
            void Add(string path,bool directory,byte[] bytes)
            {
                var entry=new PaxTarEntry(directory?TarEntryType.Directory:TarEntryType.RegularFile,path,[new("atime","0"),new("ctime","0")]){Uid=1654,Gid=1654,Mode=directory?UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute:UnixFileMode.UserRead|UnixFileMode.UserWrite,ModificationTime=DateTimeOffset.UnixEpoch};
                if(!directory)entry.DataStream=new MemoryStream(bytes,false);writer.WriteEntry(entry);entry.DataStream?.Dispose();
            }
        }
        if(output.Length==0)output.Write(new byte[1024]);
        if(output.Length>ApplicationBackupRules.MaximumArchiveBytes)throw new ArgumentException("Canonical app data archive too large.");return output.ToArray();
    }
    public static ApplicationDataManifest Manifest(Guid id,ApplicationDataSnapshot snapshot)=>new(id,snapshot.Entries.Select(e=>new ApplicationDataFile(e.Path,e.Directory,e.Data.LongLength,e.Directory?"":Convert.ToHexStringLower(SHA256.HashData(e.Data)))).ToArray());
}
