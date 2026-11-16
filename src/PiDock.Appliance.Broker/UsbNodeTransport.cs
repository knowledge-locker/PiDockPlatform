using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PiDock.Core;
namespace PiDock.Appliance.Broker;

public sealed record UsbNodeLink(string Name,int Index);
public sealed class UsbNodeTransport
{
    public static UsbNodeLink[] Links()
    {
        if(!OperatingSystem.IsLinux())return [];
        var result=new List<UsbNodeLink>();
        foreach(var nic in NetworkInterface.GetAllNetworkInterfaces()) {
            try {if(nic.OperationalStatus==OperationalStatus.Up&&UsbDevice(nic.Name)&&nic.GetIPProperties().GetIPv6Properties() is {} ip)result.Add(new(nic.Name,ip.Index));}
            catch(Exception e) when(e is IOException or NetworkInformationException or UnauthorizedAccessException) { }
            if(result.Count>=32)break;
        }
        return result.Where(n=>n.Index>0).ToArray();
    }
    private static bool UsbDevice(string name)
    {
        if(!name.StartsWith("pn",StringComparison.Ordinal)||name.Length>15||!name.All(c=>char.IsAsciiLetterOrDigit(c)))return false;
        var link=new DirectoryInfo("/sys/class/net/"+name+"/device");
        var target=link.ResolveLinkTarget(true)?.FullName;
        if(target is null||!target.StartsWith("/sys/devices/",StringComparison.Ordinal))return false;
        for(var directory=new DirectoryInfo(target);directory is not null&&directory.FullName!="/sys";directory=directory.Parent) {
            string Read(string file)=>File.Exists(Path.Combine(directory.FullName,file))?File.ReadAllText(Path.Combine(directory.FullName,file)).Trim():"";
            if(Read("idVendor")=="1d6b"&&Read("idProduct")=="0104"&&Read("manufacturer")=="PiDock"&&Read("product")=="PiDockOS Node")return true;
        }
        return false;
    }
    public static async Task<T> Read<T>(HttpResponseMessage response,CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>16384)throw new IOException("Oversized node response.");
        using var input=await response.Content.ReadAsStreamAsync(ct);var bytes=new byte[16385];int length=0;
        while(length<bytes.Length){int n=await input.ReadAsync(bytes.AsMemory(length),ct);if(n==0)break;length+=n;}
        if(length>16384)throw new IOException("Oversized node response.");
        return JsonSerializer.Deserialize<T>(bytes.AsSpan(0,length),StateFiles.Json)??throw new IOException("Invalid node response.");
    }
    public HttpClient Connect(UsbNodeLink link,string? pin,X509Certificate2? identity,Action<string>? observed=null)
    {
        if(!Links().Contains(link))throw new IOException("The USB link changed. Refresh discovery.");
        var handler=new SocketsHttpHandler {UseProxy=false,UseCookies=false,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(2)};
        handler.ConnectCallback=async(_,ct)=>{
            var socket=new Socket(AddressFamily.InterNetworkV6,SocketType.Stream,ProtocolType.Tcp);
            try {await socket.ConnectAsync(new IPEndPoint(new IPAddress(IPAddress.Parse("fe80::1").GetAddressBytes(),link.Index),7446),ct);return new NetworkStream(socket,true);}
            catch{socket.Dispose();throw;}
        };
        handler.SslOptions=new SslClientAuthenticationOptions {
            RemoteCertificateValidationCallback=(_,cert,_,_)=>{
                if(cert is null)return false;
                try {using var leaf=new X509Certificate2(cert);string actual=NodeSecurity.KeyId(leaf);if(pin is not null&&actual!=pin)return false;observed?.Invoke(actual);return true;}
                catch(System.Security.Cryptography.CryptographicException){return false;}
            },
            ClientCertificates=identity is null?null:new X509CertificateCollection {identity}
        };
        return new HttpClient(handler){BaseAddress=new Uri("https://pidock-node.local:7446"),Timeout=TimeSpan.FromSeconds(5)};
    }
    public async Task<(NodeDiscovery Device,string Pin)> Discover(UsbNodeLink link,CancellationToken ct)
    {
        string? pin=null;using var http=Connect(link,null,null,key=>pin=key);
        using var response=await http.GetAsync("/discovery",HttpCompletionOption.ResponseHeadersRead,ct);
        var device=await Read<NodeDiscovery>(response,ct);
        if(device.Schema!=1||!NodeSecurity.Digest(device.Id)||pin!=device.Id||device.Board is not {Length:>0 and <=128}||device.Board.Any(char.IsControl)||device.Version is not {Length:>0 and <=64}||device.Version.Any(char.IsControl)||
            device.MainId is not null&&!NodeSecurity.Digest(device.MainId)||device.MainId is null&&!NodeSecurity.Digest(device.Challenge)||device.Capabilities is not {Length:<=8}||device.Capabilities.Any(x=>x is not ("monitoring" or "configuration")))
            throw new IOException("Invalid USB node discovery.");
        return (device,pin);
    }
}
