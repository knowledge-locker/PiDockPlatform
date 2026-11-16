using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PiDock.Core;

public static class ApplicationDataRestore
{
    private sealed record Journal(Guid OperationId,string Phase,string[] OldNames,string[] NewNames,ApplicationDataManifest Manifest);
    public static void Apply(string root,Guid operation,bool finish=false)
    {
        if(operation==Guid.Empty)throw new ArgumentException("Operation required.");
        Check(root,true);string id=operation.ToString("N"),stage=Path.Combine(root,".pidock-data-stage-"+id),safety=Path.Combine(root,".pidock-data-safety-"+id),state=Path.Combine(root,".pidock-data-state-"+id+".json"),done=Path.Combine(root,".pidock-data-done-"+id+".json");
        if(finish)
        {
            if(File.Exists(done)){Check(done,false);if(Read<Guid>(done)!=operation)throw new IOException("Wrong completion marker.");}
            else if(File.Exists(state)||Directory.Exists(stage)||Directory.Exists(safety))throw new IOException("Restore is incomplete.");
            File.Delete(state);File.Delete(done);StateFiles.SynchronizeDirectory(root);return;
        }
        if(File.Exists(done)){Check(done,false);if(Read<Guid>(done)!=operation)throw new IOException("Wrong completion marker.");return;}
        Journal journal;
        if(File.Exists(state))
        {
            journal=Read<Journal>(state);if(journal.OperationId!=operation||journal.Phase is not("moving_old" or "moving_new" or "committing"))throw new IOException("Invalid restore state.");Validate(journal.Manifest,operation);ValidateNames(journal.OldNames);ValidateNames(journal.NewNames);
        }
        else
        {
            Check(stage,true);var manifest=Read<ApplicationDataManifest>(Path.Combine(stage,".pidock-data-manifest.json"));Validate(manifest,operation);Verify(stage,manifest);SynchronizeTree(stage);
            var oldNames=Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Select(n=>n!).Where(n=>n!=Path.GetFileName(stage)).ToArray();ValidateNames(oldNames);
            var newNames=manifest.Files.Select(f=>f.Path.Split('/')[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if(Directory.Exists(safety))throw new IOException("Unexpected restore staging directory.");
            journal=new(operation,"moving_old",oldNames,newNames,manifest);Save();
        }
        if(journal.Phase=="moving_old")
        {
            if(!Directory.Exists(safety)){Directory.CreateDirectory(safety);StateFiles.SynchronizeDirectory(root);}Check(safety,true);
            foreach(string name in journal.OldNames)Move(Path.Combine(root,name),Path.Combine(safety,name));
            journal=journal with{Phase="moving_new"};Save();
        }
        if(journal.Phase=="moving_new")
        {
            foreach(string name in journal.NewNames)Move(Path.Combine(stage,name),Path.Combine(root,name));
            Verify(root,journal.Manifest,allowControl:true);
            journal=journal with{Phase="committing"};Save();
        }
        Verify(root,journal.Manifest,allowControl:true);
        DeleteTree(safety);DeleteTree(stage);StateFiles.WriteAtomic(done,JsonSerializer.Serialize(operation,StateFiles.Json));File.Delete(state);StateFiles.SynchronizeDirectory(root);
        void Save()=>StateFiles.WriteAtomic(state,JsonSerializer.Serialize(journal,StateFiles.Json));
    }
    private static T Read<T>(string path)
    {
        Check(path,false);if(new FileInfo(path).Length>1024*1024)throw new IOException("Restore metadata limit.");return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path),StateFiles.Json)??throw new IOException("Missing restore metadata.");
    }
    private static void Validate(ApplicationDataManifest manifest,Guid id)
    {
        if(manifest.OperationId!=id||manifest.Files.Length>ApplicationBackupRules.MaximumEntries||manifest.Files.Sum(f=>f.Bytes)>ApplicationBackupRules.MaximumDataBytes)throw new IOException("Invalid staged manifest.");
        var paths=new HashSet<string>(StringComparer.Ordinal);
        foreach(var file in manifest.Files)
        {
            ApplicationBackupRules.ValidatePath(file.Path);if(!paths.Add(file.Path)||file.Bytes<0||file.Bytes>ApplicationBackupRules.MaximumDataBytes||file.Directory&&(file.Bytes!=0||file.Sha256!="")||!file.Directory&&(file.Sha256.Length!=64||!file.Sha256.All(char.IsAsciiHexDigit)))throw new IOException("Invalid staged file.");
            if(manifest.Files.Any(other=>!other.Directory&&file.Path.StartsWith(other.Path+"/",StringComparison.Ordinal)))throw new IOException("Conflicting staged paths.");
        }
    }
    private static void ValidateNames(string[] names)
    {
        if(names.Length>ApplicationBackupRules.MaximumEntries||names.Distinct(StringComparer.Ordinal).Count()!=names.Length)throw new IOException("Invalid restore names.");foreach(string name in names){ApplicationBackupRules.ValidatePath(name);if(name.Contains('/'))throw new IOException("Invalid restore root name.");}
    }
    private static void Verify(string root,ApplicationDataManifest manifest,bool allowControl=false)
    {
        var expected=new HashSet<string>(manifest.Files.Select(f=>f.Path),StringComparer.Ordinal);
        foreach(var file in manifest.Files)
        {
            string path=root;foreach(string part in file.Path.Split('/')){path=Path.Combine(path,part);Check(path,Directory.Exists(path));}
            if(file.Directory){Check(path,true);continue;}
            Check(path,false);using var stream=File.OpenRead(path);if(stream.Length!=file.Bytes||Convert.ToHexStringLower(SHA256.HashData(stream))!=file.Sha256)throw new IOException("Staged file integrity check failed.");
        }
        foreach(string relative in Enumerate(root,root))
        {
            if(relative==".pidock-data-manifest.json"&&!allowControl)continue;
            if(allowControl&&relative.Split('/')[0].StartsWith(".pidock-data-",StringComparison.Ordinal))continue;
            if(!expected.Contains(relative)&&!manifest.Files.Any(f=>f.Path.StartsWith(relative+"/",StringComparison.Ordinal)))throw new IOException("Unexpected staged data.");
        }
    }
    private static IEnumerable<string> Enumerate(string root,string directory)
    {
        foreach(string path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes=File.GetAttributes(path);if(attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("Links are unsupported.");
            string relative=Path.GetRelativePath(root,path).Replace(Path.DirectorySeparatorChar,'/');yield return relative;
            if(attributes.HasFlag(FileAttributes.Directory))foreach(string nested in Enumerate(root,path))yield return nested;
        }
    }
    private static void Check(string path,bool directory)
    {
        var attributes=File.GetAttributes(path);if(attributes.HasFlag(FileAttributes.ReparsePoint)||attributes.HasFlag(FileAttributes.Directory)!=directory)throw new IOException("Unexpected app data entry.");
    }
    private static void Move(string source,string destination)
    {
        bool original=File.Exists(source)||Directory.Exists(source),moved=File.Exists(destination)||Directory.Exists(destination);
        if(!original&&moved)return;if(!original||moved)throw new IOException("Ambiguous restore move.");
        if(!OperatingSystem.IsLinux()){if(Directory.Exists(source))Directory.Move(source,destination);else File.Move(source,destination);}
        else if(Rename(source,destination)!=0)throw new IOException("Restore move failed.");
        StateFiles.SynchronizeDirectory(Path.GetDirectoryName(source)!);StateFiles.SynchronizeDirectory(Path.GetDirectoryName(destination)!);
    }
    private static void SynchronizeTree(string path)
    {
        foreach(string child in Directory.EnumerateFileSystemEntries(path))
        {
            if(Directory.Exists(child))SynchronizeTree(child);
            else if(OperatingSystem.IsLinux()){using var handle=File.OpenHandle(child,FileMode.Open,FileAccess.Read,FileShare.Read);RandomAccess.FlushToDisk(handle);}
        }
        StateFiles.SynchronizeDirectory(path);
    }
    private static void DeleteTree(string path)
    {
        if(!Directory.Exists(path)&&!File.Exists(path))return;
        var attributes=File.GetAttributes(path);
        if(attributes.HasFlag(FileAttributes.ReparsePoint)||!attributes.HasFlag(FileAttributes.Directory)){File.Delete(path);return;}
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        foreach(string child in Directory.EnumerateFileSystemEntries(path))DeleteTree(child);Directory.Delete(path);StateFiles.SynchronizeDirectory(Path.GetDirectoryName(path)!);
    }
    [DllImport("libc",EntryPoint="rename",SetLastError=true)]private static extern int Rename(string source,string destination);
}
