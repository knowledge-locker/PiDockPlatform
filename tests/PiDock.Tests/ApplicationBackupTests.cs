using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Appliance.Broker;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationBackupTests
{
    [Fact]public void EmptyArchivesRemainRestorable(){var data=ApplicationDataArchive.Read(ApplicationDataArchive.Write([]));Assert.Empty(data.Entries);Assert.Equal(1024,data.Archive.Length);}
    [Fact]public void CanonicalArchiveIsRepeatableAndAddsBoundedParentDirectories()
    {
        var data=ApplicationDataArchive.Read(ApplicationDataArchive.Write([new("config/program.json",false,"program"u8.ToArray())]));
        Assert.Contains(data.Entries,e=>e.Path=="config"&&e.Directory);Assert.Equal(data.Sha256,ApplicationDataArchive.Read(data.Archive).Sha256);
        Assert.Equal(7,data.DataBytes);Assert.Equal("program",Encoding.UTF8.GetString(data.Entries.Single(e=>e.Path.EndsWith(".json")).Data));
    }
    [Theory][InlineData("../escape")][InlineData("/absolute")][InlineData("normal/../escape")][InlineData(".pidock-data-state-foo")][InlineData("a/.pidock-data-manifest.json")][InlineData("a\\b")]
    public void DangerousPathsAreRejected(string path)=>Assert.Throws<ArgumentException>(()=>ApplicationDataArchive.Read(ApplicationDataArchive.Write([new(path,false,[])])));
    [Fact]public void LinksConflictsAndDeclaredOversizeAreRejected()
    {
        using var output=new MemoryStream();using(var writer=new TarWriter(output,TarEntryFormat.Pax,true))writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink,"link"){LinkName="/etc/passwd"});
        Assert.Throws<ArgumentException>(()=>ApplicationDataArchive.Read(output.ToArray()));
        Assert.Throws<ArgumentException>(()=>ApplicationDataArchive.Read(ApplicationDataArchive.Write([new("a",false,[]),new("a/b",false,[])])));
        Assert.Throws<ArgumentException>(()=>ApplicationDataArchive.Read(ApplicationDataArchive.Write([new("a",false,[]),new("a",false,[])])));
        byte[] bytes=new byte[512];Encoding.ASCII.GetBytes("77777777777").CopyTo(bytes,124);Assert.Throws<ArgumentException>(()=>ApplicationDataArchive.Read(bytes));
    }
    [Fact]public void PaxDeclaredLengthCannotTriggerAnUnboundedFileAllocation()
    {
        string field="size=9999999999\n",record="";int length=field.Length+3;while(true){record=length+" "+field;if(record.Length==length)break;length=record.Length;}
        byte[] archive=new byte[2560];Header(archive.AsSpan(0,512),"PaxHeaders/program",record.Length,(byte)'x');Encoding.ASCII.GetBytes(record).CopyTo(archive,512);Header(archive.AsSpan(1024,512),"program",0,(byte)'0');
        Assert.ThrowsAny<ArgumentException>(()=>ApplicationDataArchive.Read(archive));
        static void Header(Span<byte> header,string name,int size,byte kind)
        {
            Encoding.ASCII.GetBytes(name).CopyTo(header);Encoding.ASCII.GetBytes("0000600\0").CopyTo(header[100..]);Encoding.ASCII.GetBytes(Convert.ToString(size,8).PadLeft(11,'0')+"\0").CopyTo(header[124..]);header.Slice(148,8).Fill(32);header[156]=kind;Encoding.ASCII.GetBytes("ustar\000").CopyTo(header[257..]);int sum=0;foreach(byte value in header)sum+=value;Encoding.ASCII.GetBytes(Convert.ToString(sum,8).PadLeft(6,'0')+"\0 ").CopyTo(header[148..]);
        }
    }
    [Fact]public void StoreAuthenticatesEncryptsScopesAndReservesSafetyCapacity()
    {
        using var f=new Fixture();var store=f.Store();var data=Data("secrets inside app data");var first=Info(data);
        store.Save(first,data);Assert.Equal(first,Assert.Single(store.List()));Assert.Equal(data.Sha256,store.Read(first.Id).Data.Sha256);
        byte[] protectedBytes=File.ReadAllBytes(Path.Combine(f.Root,first.Id.ToString("N")+".backup"));Assert.DoesNotContain("secrets inside app data",Encoding.UTF8.GetString(protectedBytes));
        for(int i=0;i<3;i++)store.Save(Info(data),data);
        Assert.Throws<InvalidOperationException>(()=>store.Save(Info(data),data));
        var restore=new ApplicationDataRestoreStatus(Guid.NewGuid(),"test",new string('a',64),first.Id,null,first.Image,"applying",DateTimeOffset.UtcNow,"interrupted");store.SaveRestore(restore);
        Assert.Throws<InvalidOperationException>(()=>store.Delete(first.Id));Assert.True(ApplicationBackupRules.BlocksStartup(f.Store().RestoreStatus()));
        protectedBytes[^10]^=0xff;File.WriteAllBytes(Path.Combine(f.Root,first.Id.ToString("N")+".backup"),protectedBytes);Assert.Throws<System.IO.IOException>(()=>store.Read(first.Id));
    }
    [Fact]public void CapacityFailureLeavesNoSnapshotAndCorruptRestoreJournalFailsClosed()
    {
        using var f=new Fixture();var store=f.Store(new NoSpace());var data=Data("payload");Assert.Throws<IOException>(()=>store.Save(Info(data),data));Assert.Empty(store.List());
        File.WriteAllText(Path.Combine(f.Root,"restore.protected"),"corrupt");Assert.Throws<IOException>(()=>store.RestoreStatus());
    }
    [Fact]public void StagedRestoreReplacesWholeTreePreservesEmptyDirectoriesAndIsIdempotent()
    {
        using var f=new Fixture();string volume=Path.Combine(f.Root,"volume");Directory.CreateDirectory(volume);Directory.CreateDirectory(Path.Combine(volume,"old"));File.WriteAllText(Path.Combine(volume,"old","stale"),"remove me");File.WriteAllText(Path.Combine(volume,"program"),"old");
        var data=ApplicationDataArchive.Read(ApplicationDataArchive.Write([new("program",false,"new"u8.ToArray()),new("config/empty",true,[])]));Guid id=Guid.NewGuid();Stage(volume,id,data);
        ApplicationDataRestore.Apply(volume,id);Assert.Equal("new",File.ReadAllText(Path.Combine(volume,"program")));Assert.False(Directory.Exists(Path.Combine(volume,"old")));Assert.True(Directory.Exists(Path.Combine(volume,"config","empty")));
        ApplicationDataRestore.Apply(volume,id);ApplicationDataRestore.Apply(volume,id,true);ApplicationDataRestore.Apply(volume,id,true);Assert.DoesNotContain(Directory.GetFileSystemEntries(volume),p=>Path.GetFileName(p).StartsWith(".pidock-data-"));
    }
    [Theory][InlineData("moving_old")][InlineData("moving_new")][InlineData("committing")]
    public void InterruptedMovesResumeFromDurableJournal(string phase)
    {
        using var f=new Fixture();string root=Path.Combine(f.Root,"volume");Directory.CreateDirectory(root);Guid id=Guid.NewGuid();var data=Data("restored");Stage(root,id,data);
        string stage=Path.Combine(root,".pidock-data-stage-"+id.ToString("N")),safety=Path.Combine(root,".pidock-data-safety-"+id.ToString("N"));Directory.CreateDirectory(safety);File.WriteAllText(Path.Combine(safety,"stale"),"old");
        if(phase=="committing")File.Move(Path.Combine(stage,"program"),Path.Combine(root,"program"));
        var journal=new{OperationId=id,Phase=phase,OldNames=new[]{"stale"},NewNames=new[]{"program"},Manifest=ApplicationDataArchive.Manifest(id,data)};
        StateFiles.WriteAtomic(Path.Combine(root,".pidock-data-state-"+id.ToString("N")+".json"),JsonSerializer.Serialize(journal,StateFiles.Json));
        ApplicationDataRestore.Apply(root,id);Assert.Equal("restored",File.ReadAllText(Path.Combine(root,"program")));ApplicationDataRestore.Apply(root,id,true);Assert.Single(Directory.GetFileSystemEntries(root));
    }
    [Fact]public void AlteredStageIsRejectedBeforeTouchingCurrentData()
    {
        using var f=new Fixture();string root=Path.Combine(f.Root,"volume");Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"current"),"keep");Guid id=Guid.NewGuid();Stage(root,id,Data("expected"));File.WriteAllText(Path.Combine(root,".pidock-data-stage-"+id.ToString("N"),"program"),"altered");
        Assert.Throws<IOException>(()=>ApplicationDataRestore.Apply(root,id));Assert.Equal("keep",File.ReadAllText(Path.Combine(root,"current")));
    }
    private static void Stage(string root,Guid id,ApplicationDataSnapshot data)
    {
        byte[] archive=ApplicationDataArchive.Write(data.Entries,".pidock-data-stage-"+id.ToString("N"),ApplicationDataArchive.Manifest(id,data));using var stream=new MemoryStream(archive);TarFile.ExtractToDirectory(stream,root,true);
    }
    private static ApplicationDataSnapshot Data(string text)=>ApplicationDataArchive.Read(ApplicationDataArchive.Write([new("program",false,Encoding.UTF8.GetBytes(text))]));
    private static ApplicationBackupInfo Info(ApplicationDataSnapshot data)=>new(Guid.NewGuid(),"test","registry/test@sha256:"+new string('b',64),DateTimeOffset.UtcNow,"manual",data.DataBytes,data.Entries.Length,data.Sha256,data.Archive.LongLength);
    private sealed class Plenty:IApplicationBackupCapacity{public void Require(string directory,long bytes){}}
    private sealed class NoSpace:IApplicationBackupCapacity{public void Require(string directory,long bytes)=>throw new IOException("No space.");}
    private sealed class Fixture:IDisposable
    {
        public string Root{get;}=StateFiles.PrepareDirectory(Path.Combine(Path.GetTempPath().Replace("/var/","/private/var/",StringComparison.Ordinal),"pidock-backups-"+Guid.NewGuid().ToString("N")));private readonly IDataProtectionProvider protection=new EphemeralDataProtectionProvider();
        public ApplicationBackupStore Store(IApplicationBackupCapacity? capacity=null)=>new(protection,Root,capacity??new Plenty());public void Dispose()=>Directory.Delete(Root,true);
    }
}
