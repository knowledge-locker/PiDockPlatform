using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Appliance.Broker;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationBackupBrokerTests
{
    [Fact]public async Task BackupRestoreReplacesDataCreatesSafetySnapshotAndKeepsMaintenancePaused()
    {
        using var f=new Fixture();File.WriteAllText(Path.Combine(f.Volume,"program"),"first");var snapshot=await f.Docker.CreateBackup(f.Id,new(Guid.NewGuid()),default);File.WriteAllText(Path.Combine(f.Volume,"program"),"changed");File.WriteAllText(Path.Combine(f.Volume,"stale"),"delete");
        var request=new ApplicationRestoreRequest(Guid.NewGuid(),snapshot.Id);await f.Docker.RestoreBackup(f.Id,request,default);
        Assert.Equal("first",File.ReadAllText(Path.Combine(f.Volume,"program")));Assert.False(File.Exists(Path.Combine(f.Volume,"stale")));Assert.True(f.Docker.OrchestrationStatus().Paused);Assert.Equal("completed",f.Backups.RestoreStatus()!.Phase);
        var safety=Assert.Single(f.Backups.List(),b=>b.Kind=="pre_restore");Assert.Equal("changed",System.Text.Encoding.UTF8.GetString(f.Backups.Read(safety.Id).Data.Entries.Single(e=>e.Path=="program").Data));
        await f.Docker.RestoreBackup(f.Id,request,default);Assert.Equal(2,f.Backups.List().Length);await Assert.ThrowsAsync<ArgumentException>(()=>f.Docker.RestoreBackup(f.Id,request with{SnapshotId=Guid.NewGuid()},default));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(f.Volume),p=>Path.GetFileName(p).StartsWith(".pidock-data-"));
    }
    [Fact]public async Task FailedHelperHoldsBarrierAcrossDisabledFeatureAndRetriesSameOperation()
    {
        using var f=new Fixture();File.WriteAllText(Path.Combine(f.Volume,"program"),"saved");var snapshot=await f.Docker.CreateBackup(f.Id,new(Guid.NewGuid()),default);File.WriteAllText(Path.Combine(f.Volume,"program"),"changed");f.Engine.FailHelper=true;var request=new ApplicationRestoreRequest(Guid.NewGuid(),snapshot.Id);
        await Assert.ThrowsAsync<IOException>(()=>f.Docker.RestoreBackup(f.Id,request,default));Assert.Equal("applying",f.Backups.RestoreStatus()!.Phase);Assert.Equal("interrupted",f.Backups.RestoreStatus()!.Outcome);
        var disabled=f.NewDocker(false);await Assert.ThrowsAsync<InvalidOperationException>(()=>disabled.ResumeApplications(default));await Assert.ThrowsAsync<InvalidOperationException>(()=>disabled.ChangeState(f.Id,true,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Docker.RestoreBackup(f.Id,new(Guid.NewGuid(),snapshot.Id),default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Docker.DeleteBackup(snapshot.Id,default));
        f.Engine.FailHelper=false;await f.Docker.RestoreBackup(f.Id,request,default);Assert.Equal("saved",File.ReadAllText(Path.Combine(f.Volume,"program")));Assert.Equal("completed",f.Backups.RestoreStatus()!.Phase);
    }
    [Fact]public async Task RestoreRequiresSafetySlotBeforeCreatingItsBarrier()
    {
        using var f=new Fixture();File.WriteAllText(Path.Combine(f.Volume,"program"),"saved");var snapshots=new List<ApplicationBackupInfo>();for(int i=0;i<4;i++)snapshots.Add(await f.Docker.CreateBackup(f.Id,new(Guid.NewGuid()),default));
        var request=new ApplicationRestoreRequest(Guid.NewGuid(),snapshots[0].Id);await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Docker.RestoreBackup(f.Id,request,default));Assert.Null(f.Backups.RestoreStatus());
        await f.Docker.DeleteBackup(snapshots[3].Id,default);await f.Docker.RestoreBackup(f.Id,request,default);Assert.Equal(4,f.Backups.List().Length);
    }
    [Fact]public async Task RunningUnpausedCrossAppAndDifferentImageAreRejectedBeforeWrites()
    {
        using var f=new Fixture();f.Shutdown.Save(f.Shutdown.Read()! with{Phase="resumed"});await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Docker.CreateBackup(f.Id,new(Guid.NewGuid()),default));f.Shutdown.Save(f.Shutdown.Read()! with{Phase="paused"});f.Engine.State="running";await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Docker.CreateBackup(f.Id,new(Guid.NewGuid()),default));f.Engine.State="exited";
        var snapshot=await f.Docker.CreateBackup(f.Id,new(Guid.NewGuid()),default);var record=f.Apps.Find(f.Id)!;f.Apps.Save(record with{Manifest=record.Manifest with{Image="registry/app@sha256:"+new string('d',64)}});await Assert.ThrowsAsync<ArgumentException>(()=>f.Docker.RestoreBackup(f.Id,new(Guid.NewGuid(),snapshot.Id),default));Assert.Null(f.Backups.RestoreStatus());
        f.Apps.Save(record with{Manifest=record.Manifest with{Name="other"}});f.Engine.App="other";await Assert.ThrowsAsync<ArgumentException>(()=>f.Docker.RestoreBackup(f.Id,new(Guid.NewGuid(),snapshot.Id),default));Assert.Null(f.Backups.RestoreStatus());
    }
    [Fact]public async Task IncompleteRemovalBlocksNewBackupAndRestoreBeforeDataWork()
    {
        using var f=new Fixture();var snapshot=await f.Docker.CreateBackup(f.Id,new(Guid.NewGuid()),default);
        var removal=new HeldRemoval(new(Guid.NewGuid(),"other",new string('d',64),"registry/other@sha256:"+new string('e',64),"awaiting_external_cleanup",[],DateTimeOffset.UtcNow,"external_cleanup_required",[],["Revoke credentials."]));
        var blocked=f.NewDocker(removal:removal);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>blocked.CreateBackup(f.Id,new(Guid.NewGuid()),default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>blocked.RestoreBackup(f.Id,new(Guid.NewGuid(),snapshot.Id),default));
        Assert.Null(f.Backups.RestoreStatus());Assert.Single(f.Backups.List());
    }
    private sealed class HeldRemoval(ApplicationRemovalStatus status):IApplicationRemovalStore
    {public ApplicationRemovalStatus? Read()=>status;public void Save(ApplicationRemovalStatus value)=>throw new InvalidOperationException();}
    private sealed class Fixture:IDisposable
    {
        public string Root{get;}=StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pb-"+Guid.NewGuid().ToString("N")));public string Id{get;}=new('a',64);public string Volume{get;}
        public Engine Engine{get;}public ManagedDeploymentStore Apps{get;}public ApplicationBackupStore Backups{get;}public ApplicationShutdownStore Shutdown{get;}private readonly HttpClient http;public DockerOperations Docker{get;}
        public Fixture()
        {
            Volume=Path.Combine(Root,"volume");Directory.CreateDirectory(Volume);var protection=new EphemeralDataProtectionProvider();Apps=new(protection,Path.Combine(Root,"apps"));Backups=new(protection,Path.Combine(Root,"backups"),new Plenty());Shutdown=new(protection,Path.Combine(Root,"shutdown"));Shutdown.Save(new(Guid.NewGuid(),"maintenance","paused",[Id],1,DateTimeOffset.UtcNow));
            Apps.Save(new(new("app","registry/app@sha256:"+new string('b',64),Resources:new(PersistentData:true)),Id,[],DateTimeOffset.UtcNow,true,false,1));Engine=new(Id,Volume);http=new(Engine){BaseAddress=new("http://localhost")};Docker=NewDocker();
        }
        public DockerOperations NewDocker(bool enabled=true,IApplicationRemovalStore? removal=null)=>new(http,Apps,new Delivery(),orchestrationEnabled:true,shutdownStore:Shutdown,backupsEnabled:enabled,backupStore:Backups,appDataHelperImage:"sha256:"+new string('c',64),removalStore:removal);
        public void Dispose(){http.Dispose();Directory.Delete(Root,true);}
    }
    private sealed class Plenty:IApplicationBackupCapacity{public void Require(string directory,long bytes){}}
    private sealed class Delivery:IApplicationBootstrapDelivery{public Task Deliver(string id,ApplicationBootstrapPacket packet,Func<Task> start,CancellationToken ct)=>start();}
    private sealed class Engine(string id,string volume):HttpMessageHandler
    {
        private const string Label="ai.knowledgelocker.pidock.managed";public string State="exited";public string App="app";public bool FailHelper;private readonly Dictionary<string,JsonElement> helpers=[];private readonly Dictionary<string,int> exits=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("/containers/json"))return Json(new[]{new ApplicationSummary(id,["pidock-app"],"app",State,State,new(){[Label]="true",[Label+".resources"]="v1",[Label+".app"]=App})});
            if(path.Contains("/volumes/"))return Json(new{Driver="local",Labels=new Dictionary<string,string>{{Label,"true"},{Label+".app",App}},Options=new{}});
            if(path.Contains("/images/"))return Json(new{Os="linux",Architecture="arm64"});
            if(path.EndsWith("/archive"))
            {
                if(request.Method==HttpMethod.Get)
                {
                    var list=new List<ApplicationDataEntry>();foreach(string item in Directory.GetFileSystemEntries(volume,"*",SearchOption.AllDirectories)){string relative=Path.GetRelativePath(volume,item);list.Add(new(relative,Directory.Exists(item),Directory.Exists(item)?[]:File.ReadAllBytes(item)));}
                    var archive=ApplicationDataArchive.Write(list,"app-data");return new(HttpStatusCode.OK){Content=new ByteArrayContent(archive)};
                }
                using var stream=await request.Content!.ReadAsStreamAsync(ct);TarFile.ExtractToDirectory(stream,volume,true);return Json(new{});
            }
            if(path.EndsWith("/containers/create"))
            {
                string name=Uri.UnescapeDataString(request.RequestUri.Query[6..]);helpers[name]=await request.Content!.ReadFromJsonAsync<JsonElement>(ct);exits[name]=-1;return Json(new{Id=name});
            }
            string[] parts=path.Split('/');string nameId=parts.Length>3?parts[3]:"";
            if(path.EndsWith("/json"))
            {
                if(!helpers.TryGetValue(nameId,out var config))return new(HttpStatusCode.NotFound);
                return Json(new{Config=new{Image=config.GetProperty("image"),Labels=config.GetProperty("labels")},State=new{Status=exits[nameId]==-1?"created":"exited",ExitCode=Math.Max(0,exits[nameId]),Running=false}});
            }
            if(path.EndsWith("/start"))
            {
                var config=helpers[nameId];var args=config.GetProperty("cmd").EnumerateArray().Select(v=>v.GetString()!).ToArray();
                if(FailHelper)exits[nameId]=1;else{ApplicationDataRestore.Apply(volume,Guid.ParseExact(args[1],"N"),args[0]=="finish");exits[nameId]=0;}return Json(new{});
            }
            if(request.Method==HttpMethod.Delete){helpers.Remove(nameId);return Json(new{});}
            if(path.EndsWith("/stop"))return Json(new{});
            throw new InvalidOperationException("Unexpected fake Docker request: "+path);
        }
        private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=JsonContent.Create(value,options:new JsonSerializerOptions())};
    }
}
