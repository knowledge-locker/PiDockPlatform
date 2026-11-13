using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using PiDock.Appliance.Broker;
using PiDock.Core;
namespace PiDock.Tests;

public sealed class UsbNodeTlsTests
{
    [Fact] public async Task RealTlsRequiresOwningMainPrivateKeyAndRevokesAnExistingConnectionAfterRelease()
    {
        string root=StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-node-tls-"+Guid.NewGuid()));
        try {
            using var node=new UsbNodeState(root);using var main=NodeSecurity.CreateIdentity("pidock-main.local");using var other=NodeSecurity.CreateIdentity("pidock-main.local");
            var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0,listen=>listen.UseHttps(https=>UsbNodeHost.ConfigureHttps(https,node))));
            await using var app=builder.Build();UsbNodeHost.MapEndpoints(app,node,requireUsb:false);await app.StartAsync();
            var origin=new Uri(app.Urls.Single());
            HttpClient Client(X509Certificate2? identity=null) {
                var handler=new SocketsHttpHandler {UseProxy=false,AllowAutoRedirect=false,SslOptions=new SslClientAuthenticationOptions {
                    RemoteCertificateValidationCallback=(_,certificate,_,_)=>{using var cert=new X509Certificate2(certificate!);return NodeSecurity.KeyId(cert)==node.Id;},
                    ClientCertificates=identity is null?null:new X509CertificateCollection {identity}
                }};
                return new(handler){BaseAddress=origin,Timeout=TimeSpan.FromSeconds(5)};
            }
            using var anonymous=Client();using var owner=Client(main);using var stranger=Client(other);
            var discovery=await anonymous.GetFromJsonAsync<NodeDiscovery>("/discovery",StateFiles.Json);Assert.NotNull(discovery);
            using(var response=await anonymous.GetAsync("/configuration"))Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);
            string first=NodeSecurity.KeyId(main),second=NodeSecurity.KeyId(other);
            using(var response=await owner.PostAsJsonAsync("/adopt",new NodeAdoption(node.Id,discovery.Challenge!,first),StateFiles.Json))Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            using(var response=await stranger.GetAsync("/configuration"))Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);
            var configuration=await owner.GetFromJsonAsync<NodeConfigurationSnapshot>("/configuration",StateFiles.Json);Assert.NotNull(configuration);
            using(var response=await owner.PutAsJsonAsync("/configuration",new NodeConfigurationRequest(Guid.NewGuid(),new("USB test node"),configuration.Revision),StateFiles.Json))Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            using(var response=await owner.PostAsJsonAsync("/release",new NodeReleaseRequest(Guid.NewGuid()),StateFiles.Json))Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            var unclaimed=await anonymous.GetFromJsonAsync<NodeDiscovery>("/discovery",StateFiles.Json);
            using(var response=await stranger.PostAsJsonAsync("/adopt",new NodeAdoption(node.Id,unclaimed!.Challenge!,second),StateFiles.Json))Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            // Reuse the old Main's HttpClient and TLS connection: authorization must still observe new ownership.
            using(var response=await owner.GetAsync("/configuration"))Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);
            using(var response=await stranger.GetAsync("/configuration"))Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            await app.StopAsync();
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task ProductionEndpointMappingRejectsNonUsbPeersEvenForDiscovery()
    {
        string root=StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-node-scope-"+Guid.NewGuid()));
        try {
            using var node=new UsbNodeState(root);var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
            await using var app=builder.Build();UsbNodeHost.MapEndpoints(app,node);await app.StartAsync();
            using var client=new HttpClient {BaseAddress=new Uri(app.Urls.Single())};using var response=await client.GetAsync("/discovery");Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);await app.StopAsync();
        }finally{Directory.Delete(root,true);}
    }
}
