using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PiDock.Core;
namespace PiDock.Appliance.Broker;

public sealed record UsbNodeOwnership(string MainId,string ClientKey);
public sealed record UsbNodePersistent(UsbNodeOwnership? Owner,NodeConfiguration Configuration,Guid LastConfiguration,Guid LastRelease,string? ReleasedKey,long Revision=0);
public sealed class UsbNodeState : IDisposable
{
    private readonly object gate=new();private readonly string path;private readonly TimeProvider time;
    private string challenge=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));private long issued;
    private UsbNodePersistent state;
    public X509Certificate2 Identity {get;}
    public string Id=>NodeSecurity.KeyId(Identity);
    public UsbNodeState(string directory,TimeProvider? clock=null) {
        time=clock??TimeProvider.System;issued=time.GetTimestamp();directory=StateFiles.PrepareDirectory(directory);
        Identity=NodeSecurity.LoadOrCreate(Path.Combine(directory,"identity.protected"),"pidock-node.local");
        path=Path.Combine(directory,"membership.json");
        state=File.Exists(path)?JsonSerializer.Deserialize<UsbNodePersistent>(File.ReadAllText(path),StateFiles.Json)??throw new IOException("Invalid node membership."):new(null,new("PiDockOS node"),Guid.Empty,Guid.Empty,null);
    }
    public NodeDiscovery Discovery() {lock(gate) {if(state.Owner is null&&time.GetElapsedTime(issued)>TimeSpan.FromMinutes(10))RotateChallenge();return new(1,Id,Board(),Version(),state.Owner?.MainId,state.Owner is null?challenge:null,["monitoring","configuration"]);}}
    private void RotateChallenge(){challenge=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));issued=time.GetTimestamp();}
    private static string Board() {const string file="/proc/device-tree/model";return File.Exists(file)?new string(File.ReadAllText(file).Where(c=>!char.IsControl(c)).Take(128).ToArray()):"PiDockOS USB node";}
    private static string Version() {const string file="/etc/pidock/image-profile.json";if(!File.Exists(file))return "unavailable";using var json=JsonDocument.Parse(File.ReadAllText(file));return json.RootElement.GetProperty("version").GetString()??"unavailable";}
    private void Save(UsbNodePersistent next) {StateFiles.WriteAtomic(path,JsonSerializer.Serialize(next,StateFiles.Json));state=next;}
    public void Adopt(NodeAdoption request,string clientKey) {lock(gate) {
        if(request.Id!=Id||!NodeSecurity.Digest(clientKey)||!NodeSecurity.Digest(request.MainId))throw new UnauthorizedAccessException("Invalid adoption identity.");
        if(state.Owner is {} owner) {if(owner.MainId==request.MainId&&owner.ClientKey==clientKey)return;throw new UnauthorizedAccessException("Node already belongs to another Main.");}
        if(time.GetElapsedTime(issued)>TimeSpan.FromMinutes(10)||request.Challenge!=challenge)throw new UnauthorizedAccessException("Discovery challenge expired. Reconnect the node and refresh discovery.");
        Save(state with {Owner=new(request.MainId,clientKey),Revision=checked(state.Revision+1)});
    }}
    public bool Authorized(string? clientKey) {lock(gate)return clientKey is not null&&state.Owner?.ClientKey==clientKey;}
    private void Require(string clientKey) {if(!Authorized(clientKey))throw new UnauthorizedAccessException("This node accepts only its Main identity.");}
    public NodeConfigurationSnapshot Configuration(string clientKey) {lock(gate){Require(clientKey);return new(state.Configuration,state.Revision);}}
    public void Configure(NodeConfigurationRequest request,string clientKey) {lock(gate) {
        Require(clientKey);if(request.Operation==Guid.Empty)throw new ArgumentException("An operation identifier is required.");if(request.Configuration is null)throw new ArgumentException("Missing node configuration.");request.Configuration.Validate();
        if(state.LastConfiguration==request.Operation) {if(state.Configuration!=request.Configuration)throw new ArgumentException("Operation already used with different settings.");return;}
        if(request.Revision!=state.Revision)throw new InvalidOperationException("Node configuration changed; refresh before applying settings.");
        Save(state with {Configuration=request.Configuration,LastConfiguration=request.Operation,Revision=checked(state.Revision+1)});
    }}
    public void Release(NodeReleaseRequest request,string clientKey) {lock(gate) {
        if(request.Operation==Guid.Empty)throw new ArgumentException("An operation identifier is required.");
        if(state.LastRelease==request.Operation&&state.ReleasedKey==clientKey)return;
        Require(clientKey);Save(state with {Owner=null,Configuration=new("PiDockOS node"),LastConfiguration=Guid.Empty,LastRelease=request.Operation,ReleasedKey=clientKey,Revision=checked(state.Revision+1)});RotateChallenge();
    }}
    public NodeMeasurement? Measurement(string clientKey) {lock(gate) {
        Require(clientKey);if(!state.Configuration.Monitoring)return null;
        long memory=0;const string file="/proc/meminfo";
        if(File.Exists(file)){var fields=File.ReadLines(file).FirstOrDefault(l=>l.StartsWith("MemTotal:",StringComparison.Ordinal))?.Split(' ',StringSplitOptions.RemoveEmptyEntries);if(fields is {Length:>=2}&&long.TryParse(fields[1],out var kb))memory=kb*1024;}
        double uptime=0;if(File.Exists("/proc/uptime"))double.TryParse(File.ReadAllText("/proc/uptime").Split(' ')[0],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out uptime);
        var boot=File.Exists("/proc/sys/kernel/random/boot_id")?Guid.Parse(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()):Guid.Empty;
        return new(boot,DateTimeOffset.UtcNow,Environment.ProcessorCount,memory,uptime);
    }}
    public void Dispose()=>Identity.Dispose();
}
