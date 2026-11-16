using System.Buffers.Binary;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiDock.Appliance.Broker;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class ApplicationResourceTests
{
    private const string Password = "app-credential-test-password";
    [Fact]
    public async Task VaultEncryptsAndScopesCredentialsAndImmediatelyInvalidatesRotationAndRevocation()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var users = factory.Services.GetRequiredService<UserStore>(); var vault = factory.Services.GetRequiredService<ApplicationCredentialVault>();
        var secret = Totp.NewSecret(); users.Add("owner",Password,secret,"local","Owner",true,-1);
        var login = users.Authenticate("owner",Password,Totp.Code(secret,factory.Clock.Now.ToUnixTimeSeconds()/30),factory.Clock.Now)!;
        var owner = users.Principal(login);
        string NextCode() { factory.Clock.Now = factory.Clock.Now.AddSeconds(30); return Totp.Code(secret,factory.Clock.Now.ToUnixTimeSeconds()/30); }
        var code = NextCode();
        Assert.Throws<UnauthorizedAccessException>(() => vault.Issue(new ClaimsPrincipal(),Password,code,"app-one","services","platform",factory.Clock.Now.AddDays(1),["logs.read"]));
        Assert.Throws<ArgumentException>(() => vault.Issue(owner,Password,code,"app-one","services","platform",factory.Clock.Now.AddDays(1),["gpio.write"]));
        vault.Issue(owner,Password,code,"app-one","services","platform",factory.Clock.Now.AddDays(1),["logs.read"]);
        var first = vault.Resolve("app-one","services").Credential!;
        Assert.False(vault.Resolve("app-two","services").Managed);
        Assert.DoesNotContain(first.Token,JsonSerializer.Serialize(vault.Read()));
        Assert.DoesNotContain(first.Token,File.ReadAllText(Path.Combine(factory.Root,"app-credentials.protected")));
        var reopened = new ApplicationCredentialVault(factory.Services.GetRequiredService<IDataProtectionProvider>(),new(factory.Root),users,factory.Clock);
        Assert.Equal(first.Token,reopened.Resolve("app-one","services").Credential!.Token);
        Assert.Throws<UnauthorizedAccessException>(() => vault.Revoke(owner,Password,code,"app-one","services"));
        client.DefaultRequestHeaders.Authorization = new("Bearer",first.Token);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/apps/v1/logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/apps/v1/system")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/platform/v1/discovery")).StatusCode);
        vault.Issue(owner,Password,NextCode(),"app-one","services","platform",factory.Clock.Now.AddDays(1),["system.read"]);
        var second = vault.Resolve("app-one","services").Credential!; Assert.Equal(2,second.Version);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/apps/v1/logs")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer",second.Token);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/apps/v1/system")).StatusCode);
        vault.Revoke(owner,Password,NextCode(),"app-one","services");
        Assert.True(vault.Resolve("app-one","services").Managed); Assert.Null(vault.Resolve("app-one","services").Credential);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/apps/v1/system")).StatusCode);
        vault.Issue(owner,Password,NextCode(),"app-one","bridge","ui-bridge",factory.Clock.Now.AddMinutes(1),[]);
        Assert.Null(vault.Authenticate(vault.Resolve("app-one","bridge").Credential!.Token));
        factory.Clock.Now = factory.Clock.Now.AddMinutes(2); Assert.Null(vault.Resolve("app-one","bridge").Credential);
    }
    [Fact]
    public async Task BrokerUsesOnlyNamedVolumeAndPrivateStartupTransportAndRefreshesOnRestart()
    {
        using var fixture = new BrokerFixture();
        var first = Credential(); var manifest = Manifest();
        var id = await fixture.Docker.DeployPrepared(new(manifest,[first]),default);
        var body = fixture.Handler.Body!; Assert.DoesNotContain(first.Token,body);
        using var json = JsonDocument.Parse(body); var host = json.RootElement.GetProperty("hostConfig");
        Assert.False(host.TryGetProperty("binds",out _)); Assert.Equal("no",host.GetProperty("restartPolicy").GetProperty("name").GetString());
        Assert.Equal("pidock-app-app-one-data",host.GetProperty("mounts")[0].GetProperty("source").GetString());
        Assert.Contains("uid=1654",host.GetProperty("tmpfs").GetProperty(ApplicationResourceRules.CredentialDirectory).GetString());
        Assert.Equal(first.Token,fixture.Delivery.Packet!.Credentials[0].Token);
        Assert.DoesNotContain(first.Token,File.ReadAllText(Path.Combine(fixture.Root,id+".protected")));
        var second = first with { Version=2, Token=new string('c',64) };
        await fixture.Docker.Lifecycle(id,true,true,[second],default);
        Assert.Equal(second.Token,fixture.Delivery.Packet!.Credentials[0].Token);
        await fixture.Docker.ChangeState(id,false,default);
        Assert.DoesNotContain(fixture.Handler.Deleted,p=>p.Contains("/volumes/",StringComparison.Ordinal));
        Assert.Equal(new[]{"bridge"},(await fixture.Docker.Resources(id,default))!.CredentialIds);
        fixture.Store.Forget(id);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>fixture.Docker.ChangeState(id,true,default));
    }
    [Fact]
    public async Task BootstrapFailureCleansContainerButRetainsDataAndUnmanagedVolumesAreRejected()
    {
        using var fixture = new BrokerFixture(); fixture.Delivery.Fail=true;
        await Assert.ThrowsAsync<IOException>(()=>fixture.Docker.DeployPrepared(new(Manifest(),[Credential()]),default));
        Assert.Contains(fixture.Handler.Deleted,p=>p.Contains("/containers/",StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Handler.Deleted,p=>p.Contains("/volumes/",StringComparison.Ordinal));
        fixture.Handler.ForeignVolume = true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>fixture.Docker.DeployPrepared(new(Manifest(),[Credential()]),default));
    }
    [Fact]
    public async Task ResourcesRequireOptInAndExactLiveCredentialAssignments()
    {
        using var fixture = new BrokerFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new DockerOperations(fixture.Http).DeployPrepared(new(Manifest(),[Credential()]),default));
        await Assert.ThrowsAsync<ArgumentException>(()=>fixture.Docker.DeployPrepared(new(Manifest(),[]),default));
        await Assert.ThrowsAsync<ArgumentException>(()=>fixture.Docker.DeployPrepared(new(Manifest(),[Credential() with { AppId="app-two" }]),default));
        await Assert.ThrowsAsync<ArgumentException>(()=>fixture.Docker.DeployPrepared(new(Manifest(),[Credential() with { ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1) }]),default));
        Assert.Throws<ArgumentException>(()=>new ApplicationResources(Credentials:["../key"]).Validate());
    }
    [Fact]
    public async Task StartupPacketsAndMultiplexedAcknowledgementsAreBoundedAndCorrelated()
    {
        var packet = new ApplicationBootstrapPacket(1,Guid.NewGuid(),"app-one",true,[Credential()]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet,StateFiles.Json); var size=new byte[4];BinaryPrimitives.WriteInt32BigEndian(size,bytes.Length);
        using var input = new MemoryStream(size.Concat(bytes).ToArray()); Assert.Equal(packet.DeliveryId,(await ApplicationBootstrap.ReadPacket(input,default)).DeliveryId);
        BinaryPrimitives.WriteInt32BigEndian(size,32769); using var oversized = new MemoryStream(size);
        await Assert.ThrowsAsync<IOException>(()=>ApplicationBootstrap.ReadPacket(oversized,default));
        static MemoryStream Frame(ApplicationBootstrapAck ack)
        { var body=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ack,StateFiles.Json)+"\nApp started\n");var header=new byte[8];header[0]=1;BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),body.Length);return new(header.Concat(body).ToArray()); }
        using var valid=Frame(new(1,packet.DeliveryId,packet.AppId,true));await ApplicationBootstrapDelivery.ReadAcknowledgement(valid,packet,default);
        using var wrong=Frame(new(1,Guid.NewGuid(),packet.AppId,true));await Assert.ThrowsAsync<IOException>(()=>ApplicationBootstrapDelivery.ReadAcknowledgement(wrong,packet,default));
        using var rejected=Frame(new(1,packet.DeliveryId,packet.AppId,false));await Assert.ThrowsAsync<IOException>(()=>ApplicationBootstrapDelivery.ReadAcknowledgement(rejected,packet,default));
    }
    private static ApplicationManifest Manifest()=>new("app-one","registry/app@sha256:"+new string('a',64),Resources:new(true,["bridge"]));
    private static ApplicationCredentialFile Credential()=>new(1,"app-one","bridge",1,"ui-bridge",new string('b',64),DateTimeOffset.UtcNow.AddDays(1),[]);
    private sealed class Delivery:IApplicationBootstrapDelivery
    {
        public ApplicationBootstrapPacket? Packet; public bool Fail;
        public async Task Deliver(string id,ApplicationBootstrapPacket packet,Func<Task> start,CancellationToken ct) { Packet=packet;await start();if(Fail)throw new IOException("Test failure."); }
    }
    private sealed class DockerHandler:HttpMessageHandler
    {
        public string? Body; public bool ForeignVolume; public List<string> Deleted=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;object result=new{};
            if(request.Method==HttpMethod.Delete)Deleted.Add(path);
            if(path.EndsWith("/containers/json",StringComparison.Ordinal))result=new[]{new ApplicationSummary(new string('d',64),["pidock-app-one"],"image","running","running",new(){["ai.knowledgelocker.pidock.managed.resources"]="v1"})};
            if(path.EndsWith("/networks/create",StringComparison.Ordinal))result=new{Id="test-network"};
            if(path.EndsWith("/containers/create",StringComparison.Ordinal)){Body=await request.Content!.ReadAsStringAsync(ct);result=new{Id=new string('d',64)};}
            if(path.Contains("/volumes/",StringComparison.Ordinal)) result=new{Driver="local",Labels=new Dictionary<string,string>{["ai.knowledgelocker.pidock.managed"]="true",["ai.knowledgelocker.pidock.managed.app"]=ForeignVolume?"other":"app-one"},Options=new{}};
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(result),Encoding.UTF8,"application/json")};
        }
    }
    private sealed class BrokerFixture:IDisposable
    {
        public readonly string Root=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-resources-test-"+Guid.NewGuid());
        public readonly DockerHandler Handler=new();public readonly HttpClient Http;public readonly ManagedDeploymentStore Store;public readonly Delivery Delivery=new();public readonly DockerOperations Docker;
        public BrokerFixture(){Http=new(Handler){BaseAddress=new("http://localhost")};Store=new(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Root,"keys"))),Root);Docker=new(Http,Store,Delivery);}
        public void Dispose(){Http.Dispose();Directory.Delete(Root,true);}
    }
    private sealed class Clock:TimeProvider { public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now; }
    private sealed class Factory:WebApplicationFactory<PiDock.Appliance.Web.ApplianceMarker>
    {
        public string Root {get;}=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-vault-test-"+Guid.NewGuid());
        public Clock Clock {get;}=new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {builder.UseEnvironment("Development").UseSetting("PiDock:StateDirectory",Root).UseSetting("Logging:LogLevel:Default","Warning");builder.ConfigureServices(s=>{s.RemoveAll<TimeProvider>();s.AddSingleton<TimeProvider>(Clock);});}
        protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing&&Directory.Exists(Root))Directory.Delete(Root,true);}
    }
}
