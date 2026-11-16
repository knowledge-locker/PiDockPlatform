using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using PiDock.Core;
namespace PiDock.Appliance.Broker;

public sealed record MainNodeRecord(NodeDiscovery Device,string Key,Guid PendingRelease=default);
public sealed class MainNodeService : BackgroundService
{
    private readonly string directory,path,mainId;private readonly UsbNodeTransport transport=new();
    private readonly SemaphoreSlim gate=new(1,1);private readonly object viewGate=new();
    private readonly ConcurrentDictionary<string,(long Next,long Revision,NodeMeasurement Value)> samples=new();
    private MainNodeRecord[] records;private NodeView[] views=[];private NodeAudit[] audit;
    public bool Enabled {get;}
    public MainNodeService(IConfiguration configuration)
    {
        var configured=configuration["Nodes:StateDirectory"];
        if(configured is null){directory=path=mainId="";records=[];audit=[];return;}
        if(!Path.IsPathFullyQualified(configured))throw new InvalidOperationException("Node state requires an absolute persistent path.");
        Enabled=true;directory=StateFiles.PrepareDirectory(configured);
        path=Path.Combine(directory,"members.json");
        records=File.Exists(path)?JsonSerializer.Deserialize<MainNodeRecord[]>(File.ReadAllText(path),StateFiles.Json)??throw new IOException("Invalid node inventory."):[];
        string identity=Path.Combine(directory,"main-id");if(!File.Exists(identity))StateFiles.WriteAtomic(identity,Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        mainId=File.ReadAllText(identity);if(!NodeSecurity.Digest(mainId))throw new IOException("Invalid Main identity.");
        string log=Path.Combine(directory,"audit.json");audit=File.Exists(log)?JsonSerializer.Deserialize<NodeAudit[]>(File.ReadAllText(log),StateFiles.Json)??[]:[];
        if(records.Length>32||records.Any(r=>!NodeSecurity.Digest(r.Device.Id)||r.Key!=r.Device.Id)||records.Select(r=>r.Device.Id).Distinct().Count()!=records.Length)throw new IOException("Invalid node membership state.");
    }
    public NodeView[] Current {get{lock(viewGate)return views;}}
    public NodeAudit[] Audit {get{lock(viewGate)return audit.TakeLast(40).Reverse().ToArray();}}
    private void Record(NodeAction action,string operation,string result) {
        if(!IdentityRules.IsId(action.Actor))throw new ArgumentException("Invalid Main actor.");
        var next=audit.TakeLast(1999).Append(new NodeAudit(DateTimeOffset.UtcNow,action.Actor,action.Id,operation,result)).ToArray();
        StateFiles.WriteAtomic(Path.Combine(directory,"audit.json"),JsonSerializer.Serialize(next,StateFiles.Json));lock(viewGate)audit=next;
    }
    private void Save(MainNodeRecord[] next) {StateFiles.WriteAtomic(path,JsonSerializer.Serialize(next,StateFiles.Json));records=next;}
    private string Credential(string id)=>Path.Combine(directory,id+".identity");
    private UsbNodeLink Target(NodeAction action) {
        if(!IdentityRules.IsId(action.Actor)||!NodeSecurity.Digest(action.Id)||action.KeyFingerprint!=action.Id)throw new ArgumentException("Invalid node selection.");
        var matches=Current.Where(n=>n.Device.Id==action.Id&&n.Connected).ToArray();
        if(matches.Length!=1||matches[0].Interface!=action.Interface||matches[0].Status.StartsWith("Duplicate",StringComparison.Ordinal))throw new IOException("Node attachment is unavailable or ambiguous. Refresh discovery.");
        return UsbNodeTransport.Links().SingleOrDefault(n=>n.Name==action.Interface)??throw new IOException("USB link disconnected.");
    }
    public async Task Adopt(NodeAction action,CancellationToken ct) {
        await gate.WaitAsync(ct);try {
            var link=Target(action);var (discovered,pin)=await transport.Discover(link,ct);
            if(discovered.Id!=action.Id||pin!=action.KeyFingerprint)throw new UnauthorizedAccessException("Node identity changed.");
            if(discovered.MainId is not null&&discovered.MainId!=mainId)throw new UnauthorizedAccessException("This node belongs to a different Main. Release it from that Main before adoption.");
            if(records.Length>=32&&!records.Any(r=>r.Device.Id==action.Id))throw new InvalidOperationException("Main node limit reached.");
            if(records.Any(r=>r.Device.Id==action.Id&&r.PendingRelease!=Guid.Empty))throw new InvalidOperationException("Complete the pending node release before adoption.");
            using var identity=NodeSecurity.LoadOrCreate(Credential(action.Id),"pidock-main.local");
            // Retain the identity and pin before sending. A retry after interruption uses the same key.
            if(!records.Any(r=>r.Device.Id==action.Id))Save([..records,new(discovered,pin)]);
            Record(action,"AdoptionRequested","USB identity reviewed by Main owner");
            using var http=transport.Connect(link,pin,identity);
            using var response=await http.PostAsJsonAsync("/adopt",new NodeAdoption(action.Id,discovered.Challenge??"",mainId),StateFiles.Json,ct);response.EnsureSuccessStatusCode();
            Record(action,"Adopted","Main service identity accepted");
        }finally{gate.Release();}
        await Refresh(ct);
    }
    public async Task Configure(NodeConfigureAction request,CancellationToken ct) {
        if(request.Configuration is null)throw new ArgumentException("Missing node settings.");request.Configuration.Validate();await gate.WaitAsync(ct);try {
            var link=Target(request.Target);var record=records.SingleOrDefault(r=>r.Device.Id==request.Target.Id)??throw new UnauthorizedAccessException("Adopt the node first.");
            using var identity=NodeSecurity.ReadIdentity(Credential(record.Device.Id));using var http=transport.Connect(link,record.Key,identity);
            Record(request.Target,"ConfigurationRequested","Main owner confirmed settings");
            using var response=await http.PutAsJsonAsync("/configuration",new NodeConfigurationRequest(request.Operation,request.Configuration,request.Revision),StateFiles.Json,ct);response.EnsureSuccessStatusCode();
            Record(request.Target,"Configured","Node acknowledged settings");
        }finally{gate.Release();}
        await Refresh(ct);
    }
    public async Task Release(NodeReleaseAction request,CancellationToken ct) {
        await gate.WaitAsync(ct);try {
            var link=Target(request.Target);var record=records.SingleOrDefault(r=>r.Device.Id==request.Target.Id)??throw new UnauthorizedAccessException("Node is not a member.");
            using var identity=NodeSecurity.ReadIdentity(Credential(record.Device.Id));using var http=transport.Connect(link,record.Key,identity);
            if(request.Operation==Guid.Empty)throw new ArgumentException("A release operation is required.");
            if(record.PendingRelease==Guid.Empty){record=record with {PendingRelease=request.Operation};Save(records.Select(r=>r.Device.Id==record.Device.Id?record:r).ToArray());}
            Record(request.Target,"ReleaseRequested","Main owner confirmed removal");
            var (current,pin)=await transport.Discover(link,ct);
            if(current.Id!=record.Device.Id||pin!=record.Key)throw new UnauthorizedAccessException("Node identity changed.");
            if(current.MainId==mainId) {
                using var response=await http.PostAsJsonAsync("/release",new NodeReleaseRequest(record.PendingRelease),StateFiles.Json,ct);response.EnsureSuccessStatusCode();
            }
            // A pinned node already reporting no ownership by this Main also acknowledges completed removal.
            Record(request.Target,"Released","Node no longer belongs to this Main");
            Save(records.Where(r=>r.Device.Id!=record.Device.Id).ToArray());File.Delete(Credential(record.Device.Id));
        }finally{gate.Release();}
        await Refresh(ct);
    }
    public async Task Refresh(CancellationToken ct) {
        if(!Enabled)return;
        await gate.WaitAsync(ct);try {
            var scanned=new ConcurrentBag<NodeView>();
            await Parallel.ForEachAsync(UsbNodeTransport.Links(),new ParallelOptions {MaxDegreeOfParallelism=4,CancellationToken=ct},async(link,token)=> {
                try {
                    var (device,pin)=await transport.Discover(link,ct);var record=records.SingleOrDefault(r=>r.Device.Id==device.Id);
                    if(record is null){scanned.Add(new(device,link.Name,pin,false,true,device.MainId is null?"Discovered · awaiting owner adoption":"Owned by another Main"));return;}
                    if(pin!=record.Key){scanned.Add(new(device,link.Name,pin,false,true,"Identity mismatch · access blocked"));return;}
                    if(record.PendingRelease!=Guid.Empty) {scanned.Add(new(device,link.Name,pin,true,true,"Release awaiting confirmation · retry from Main"));return;}
                    if(device.MainId is null) {scanned.Add(new(device,link.Name,pin,false,true,"Adoption pending · confirm in Main"));return;}
                    if(device.MainId!=mainId) {scanned.Add(new(device,link.Name,pin,false,true,"Ownership changed · access blocked"));return;}
                    using var identity=NodeSecurity.ReadIdentity(Credential(device.Id));using var http=transport.Connect(link,record.Key,identity);
                    using var configResponse=await http.GetAsync("/configuration",HttpCompletionOption.ResponseHeadersRead,ct);
                    var config=await UsbNodeTransport.Read<NodeConfigurationSnapshot>(configResponse,ct);if(config.Configuration is null||config.Revision<0)throw new IOException("Invalid node configuration.");config.Configuration.Validate();
                    NodeMeasurement? measurement=null;
                    if(config.Configuration.Monitoring) {
                        if(samples.TryGetValue(device.Id,out var cached)&&cached.Revision==config.Revision&&cached.Next>Environment.TickCount64)measurement=cached.Value;
                        else {
                            using var measured=await http.GetAsync("/measurement",HttpCompletionOption.ResponseHeadersRead,ct);measurement=await UsbNodeTransport.Read<NodeMeasurement>(measured,ct);
                            if(measurement.CpuCount is <1 or >1024||measurement.MemoryBytes is <0 or >1125899906842624||!double.IsFinite(measurement.UptimeSeconds)||measurement.UptimeSeconds<0)throw new IOException("Invalid node measurement.");
                            samples[device.Id]=(Environment.TickCount64+config.Configuration.SampleSeconds*1000L,config.Revision,measurement);
                        }
                    } else samples.TryRemove(device.Id,out _);
                    scanned.Add(new(device,link.Name,pin,true,true,"Connected · authenticated Main identity",config.Configuration,measurement,config.Revision));
                }catch(Exception e) when(e is IOException or HttpRequestException or CryptographicException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException) { }
            });
            var next=scanned.ToList();
            foreach(var duplicate in next.GroupBy(n=>n.Device.Id).Where(g=>g.Count()>1).ToArray())
                foreach(var entry in duplicate){next.Remove(entry);next.Add(entry with {Adopted=false,Status="Duplicate identity · disconnect ambiguous nodes"});}
            foreach(var record in records.Where(r=>!next.Any(n=>n.Device.Id==r.Device.Id)))next.Add(new(record.Device,"",record.Key,true,false,"Disconnected · credentials retained for authenticated reconnection"));
            lock(viewGate)views=next.ToArray();
        }finally{gate.Release();}
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try{while(!stoppingToken.IsCancellationRequested){await Refresh(stoppingToken);await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken);}}
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
    }
}
