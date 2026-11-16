using System.Net;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using PiDock.Core;
namespace PiDock.Appliance.Broker;

public static class UsbNodeHost
{
    public static async Task Run(string[] args)
    {
        if(!OperatingSystem.IsLinux()||!File.Exists("/var/lib/pidock/node-mode"))throw new InvalidOperationException("USB node service requires a provisioned Node image.");
        var builder=WebApplication.CreateBuilder(args);
        using var node=new UsbNodeState("/var/lib/pidock/node");
        IPAddress? address=null;
        // Never listen on a LAN wildcard. Carrier/address setup may arrive after a headless boot.
        while(address is null) {
            var nic=NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n=>n.Name=="usb0");
            if(nic is not null) {
                int index=nic.GetIPProperties().GetIPv6Properties()?.Index??0;
                if(index>0&&nic.GetIPProperties().UnicastAddresses.Any(a=>a.Address.IsIPv6LinkLocal&&a.Address.GetAddressBytes().SequenceEqual(IPAddress.Parse("fe80::1").GetAddressBytes())))address=new IPAddress(IPAddress.Parse("fe80::1").GetAddressBytes(),index);
            }
            if(address is null)await Task.Delay(2000);
        }
        builder.WebHost.ConfigureKestrel(k=>{
            k.Limits.MaxRequestBodySize=8192;k.Limits.MaxConcurrentConnections=16;k.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(5);
            k.Listen(address,7446,listen=>listen.UseHttps(https=>ConfigureHttps(https,node)));
        });
        var app=builder.Build();
        MapEndpoints(app,node);
        await app.RunAsync();
    }
    internal static void ConfigureHttps(HttpsConnectionAdapterOptions https,UsbNodeState node)
    {
        https.ServerCertificate=node.Identity;https.ClientCertificateMode=ClientCertificateMode.AllowCertificate;https.HandshakeTimeout=TimeSpan.FromSeconds(5);
        // TLS proves possession. Route authorization pins the owning Main key; discovery remains anonymous.
        https.ClientCertificateValidation=(cert,_,_)=>{try{return NodeSecurity.Digest(NodeSecurity.KeyId(cert));}catch(System.Security.Cryptography.CryptographicException){return false;}};
    }
    internal static void MapEndpoints(WebApplication app,UsbNodeState node,bool requireUsb=true)
    {
        app.Use(async(context,next)=>{
            context.Response.Headers.CacheControl="no-store";
            if(requireUsb&&context.Connection.RemoteIpAddress is not {IsIPv6LinkLocal:true}){context.Response.StatusCode=403;return;}
            try{await next();}
            catch(UnauthorizedAccessException){context.Response.StatusCode=403;}
            catch(ArgumentException){context.Response.StatusCode=400;}
            catch(InvalidOperationException){context.Response.StatusCode=409;}
            catch(IOException){context.Response.StatusCode=503;}
        });
        static string Client(HttpContext c)=>c.Connection.ClientCertificate is {} cert?NodeSecurity.KeyId(cert):throw new UnauthorizedAccessException();
        app.MapGet("/discovery",()=>node.Discovery());
        app.MapPost("/adopt",(NodeAdoption request,HttpContext context)=>{node.Adopt(request,Client(context));return Results.Ok();});
        app.MapGet("/configuration",(HttpContext context)=>node.Configuration(Client(context)));
        app.MapPut("/configuration",(NodeConfigurationRequest request,HttpContext context)=>{node.Configure(request,Client(context));return Results.Ok();});
        app.MapGet("/measurement",(HttpContext context)=>node.Measurement(Client(context)));
        app.MapPost("/release",(NodeReleaseRequest request,HttpContext context)=>{node.Release(request,Client(context));return Results.Ok();});
    }
}
