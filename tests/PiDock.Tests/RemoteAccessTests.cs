using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiDock.Agent;
using PiDock.Cloud.Web;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Tests;

[Collection("Remote access loopback")]
public sealed class RemoteAccessTests
{
    [Fact]
    public async Task RelayProvesDeviceIdentityScopesBrowserGrantFiltersCookiesAndCarriesHttpAndWebSockets()
    {
        await using var f = new Fixture(); await f.Connect();
        Assert.True(f.Remote.Connected("alpha", f.Device));
        var grant = f.Remote.Begin(f.Identity.User, f.Device); var launch = f.Remote.Launch(grant.Id, f.Identity.User)!;
        Assert.Null(f.Remote.Consume("other.access.example.test", launch.LaunchCode));
        using var client = f.Client();
        var bootstrap = new HttpRequestMessage(HttpMethod.Post, "https://" + launch.Host + "/_pidock/session") { Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["code"] = launch.LaunchCode }) };
        bootstrap.Headers.Add("Origin", "https://cloud.example.test");
        var result = await client.SendAsync(bootstrap); Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        var cookie = result.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        Assert.Null(f.Remote.Consume(launch.Host, launch.LaunchCode));
        var pump = f.Pump();
        using var get = new HttpRequestMessage(HttpMethod.Get, "https://" + launch.Host + "/");
        get.Headers.Add("Cookie", cookie + "; PiDock.Cloud=never-forward; PiDock.Appliance=test-appliance-cookie");
        var page = await client.SendAsync(get); Assert.Equal("appliance reached", await page.Content.ReadAsStringAsync());
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        Assert.DoesNotContain(page.Headers.GetValues("Set-Cookie"), s => s.Contains("PiDock.Cloud"));
        Assert.Contains(page.Headers.GetValues("Set-Cookie"), s => s.StartsWith("PiDock.Appliance=") && !s.Contains("domain=", StringComparison.OrdinalIgnoreCase));
        var forwarded = f.Requests.Single(); Assert.Contains("PiDock.Appliance=test-appliance-cookie", forwarded); Assert.DoesNotContain("never-forward", forwarded); Assert.DoesNotContain("__Host-PiDockRemote", forwarded);
        using var denied = new HttpRequestMessage(HttpMethod.Post, "https://" + launch.Host + "/auth/logout"); denied.Headers.Add("Cookie", cookie); denied.Headers.Add("Origin", "https://attacker.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(denied)).StatusCode);
        using var localOnly = new HttpRequestMessage(HttpMethod.Get, "https://" + launch.Host + "/remote-access"); localOnly.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(localOnly)).StatusCode);
        var wsClient = f.Server.CreateWebSocketClient(); wsClient.ConfigureRequest = r => { r.Headers["Cookie"] = cookie; r.Headers["Origin"] = "https://" + launch.Host; };
        using var browser = await wsClient.ConnectAsync(new("wss://" + launch.Host + "/_blazor"), CancellationToken.None);
        await browser.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        var bytes = new byte[32]; var echo = await browser.ReceiveAsync(bytes, CancellationToken.None); Assert.Equal("hello", Encoding.UTF8.GetString(bytes, 0, echo.Count));
        f.Remote.Revoke(f.Identity.User, grant.Id);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { var closed = await browser.ReceiveAsync(bytes, stop.Token); Assert.Equal(WebSocketMessageType.Close, closed.MessageType); } catch (Exception e) when (e is WebSocketException or IOException) { }
        Assert.False(stop.IsCancellationRequested);
        Assert.Null(f.Remote.Authenticate(launch.Host, cookie.Split('=')[1]));
        Assert.Contains(f.Remote.Audit(f.Identity.User, f.Device), e => e.Event == "SessionClosed");
        await f.StopPump(pump);
    }
    [Fact]
    public async Task PermissionOffExpiredSignedOutAndWrongTenantCannotOpenOrRetainAccess()
    {
        await using var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => f.Remote.Begin(f.Identity.User, f.Device));
        await f.Connect();
        var grant = f.Remote.Begin(f.Identity.User, f.Device); var launch = f.Remote.Launch(grant.Id, f.Identity.User)!;
        Assert.Null(f.Remote.Launch(grant.Id, new ClaimsPrincipal()));
        var code = launch.LaunchCode; Assert.NotNull(f.Remote.Consume(launch.Host, code));
        Assert.Throws<UnauthorizedAccessException>(() => f.Remote.Begin(new ClaimsPrincipal(), f.Device));
        f.Clock.Now += TimeSpan.FromMinutes(6);
        Assert.Null(f.Remote.Authenticate(launch.Host, launch.Cookie));
        f.Clock.Now = DateTimeOffset.UtcNow;
        var next = f.Remote.Begin(f.Identity.User, f.Device); var active = f.Remote.Launch(next.Id, f.Identity.User)!; f.Remote.Consume(active.Host, active.LaunchCode);
        f.Users.RevokeSession(f.Identity.User);
        Assert.Null(f.Remote.Authenticate(active.Host, active.Cookie));
        Assert.Empty(f.Remote.Sessions(f.Identity.User, f.Device));
    }
    [Fact]
    public async Task ChallengeReplayForeignCertificatesAndCrossOriginCloudSocketsAreRejected()
    {
        await using var f = new Fixture();
        using var key = RSA.Create(2048);
        var signature = Convert.ToBase64String(f.Key.SignData(RemoteProtocol.ProofBytes(new string('A',64)), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        Assert.Null(f.Enrollments.AuthenticateRemoteDevice(f.Reply.CertificatePem, new string('B',64), signature));
        Assert.Null(f.Enrollments.AuthenticateRemoteDevice(f.Reply.CertificatePem, new string('A',64), Convert.ToBase64String(key.SignData(RemoteProtocol.ProofBytes(new string('A',64)), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
        Assert.NotNull(f.Enrollments.AuthenticateRemoteDevice(f.Reply.CertificatePem, new string('A',64), signature));
        using var client = f.Client(); using var request = new HttpRequestMessage(HttpMethod.Get,"https://cloud.example.test/_blazor"); request.Headers.Add("Origin","https://untrusted.access.example.test");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(request)).StatusCode);
        Assert.False(f.Remote.KnownHost("arbitrary.access.example.test"));
    }
    [Fact]
    public void LocalPermissionDefaultsOffPersistsAuditsAndIsSeparateFromCloudUpdates()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-remote-policy-"+Guid.NewGuid());
        try {
            var mqtt = new MqttConnection(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),root); var enrollment = new CloudEnrollmentClient(root,mqtt);
            using var first = new RemoteInterfaceAgent(enrollment,root,NullLogger<RemoteInterfaceAgent>.Instance); Assert.False(first.Allowed);
            first.SetAllowed(true,"local-owner");
            using var reopened = new RemoteInterfaceAgent(enrollment,root,NullLogger<RemoteInterfaceAgent>.Instance); Assert.True(reopened.Allowed); Assert.Equal(2,reopened.Events.Length);
            reopened.SetAllowed(false,"local-owner"); Assert.False(reopened.Allowed); Assert.Equal(4,reopened.Events.Length);
            Assert.False(File.Exists(Path.Combine(root,"updater-state.json")));
        } finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
    [Fact]
    public async Task RealPiAgentUsesPinnedLoopbackTlsAndLocalRevocationClosesCloudAccess()
    {
        await using var f = new Fixture();
        var directory = Path.Combine(f.Root, "pi-agent"); Directory.CreateDirectory(directory);
        using var key = RSA.Create(2048); var csr = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); csr.CertificateExtensions.Add(san.Build());
        csr.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var certificate = csr.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificatePath = Path.Combine(directory, "local.crt"); File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        StateFiles.WriteAtomic(Path.Combine(directory, "cloud-connection.json"), System.Text.Json.JsonSerializer.Serialize(new CloudConnection("https://cloud.example.test/", "alpha", f.Device, f.Reply.CertificatePem, f.Key.ExportPkcs8PrivateKeyPem(), f.Reply.CaPem, "mqtt.example.test", 8883, f.Reply.ExpiresAt), StateFiles.Json));
        using var mqtt = new MqttConnection(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), directory);
        var enrollment = new CloudEnrollmentClient(directory, mqtt);
        async Task<WebSocket> Connect(Uri uri, string? token, CancellationToken ct) { var client = f.Server.CreateWebSocketClient(); if (token is not null) client.ConfigureRequest = r => r.Headers["Authorization"] = "Bearer " + token; return await client.ConnectAsync(uri, ct); }
        using var agent = new RemoteInterfaceAgent(enrollment, directory, NullLogger<RemoteInterfaceAgent>.Instance, certificatePath, Connect);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 7241, o => o.UseHttps(certificate)));
        await using var local = builder.Build();
        local.MapGet("/", (HttpContext c) => c.Request.Headers["X-PiDock-Remote-Proof"] == agent.LocalProof && Guid.TryParse(c.Request.Headers["X-PiDock-Remote-Id"], out _) ? Results.Text("real appliance over TLS") : Results.StatusCode(403));
        await local.StartAsync(); agent.SetAllowed(true,"local-owner"); await agent.StartAsync(CancellationToken.None);
        try {
            for (int i = 0; i < 100 && !agent.Connected; i++) await Task.Delay(20);
            Assert.True(agent.Connected);
            var view = f.Remote.Begin(f.Identity.User, f.Device); var grant = f.Remote.Launch(view.Id, f.Identity.User)!;
            Assert.NotNull(f.Remote.Consume(grant.Host, grant.LaunchCode));
            using var client = f.Client(); using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + grant.Host + "/"); request.Headers.Add("Cookie", "__Host-PiDockRemote=" + grant.Cookie);
            var response = await client.SendAsync(request); Assert.Equal("real appliance over TLS", await response.Content.ReadAsStringAsync());
            File.WriteAllText(certificatePath, f.Reply.CaPem); // A different trusted root must not authorize the loopback server.
            using var badTrust = new HttpRequestMessage(HttpMethod.Get, "https://" + grant.Host + "/"); badTrust.Headers.Add("Cookie", "__Host-PiDockRemote=" + grant.Cookie);
            Assert.Equal(HttpStatusCode.BadGateway, (await client.SendAsync(badTrust)).StatusCode);
            Assert.True(agent.Connected);
            agent.SetAllowed(false,"local-owner");
            for (int i = 0; i < 100 && f.Remote.Connected("alpha",f.Device); i++) await Task.Delay(20);
            Assert.False(f.Remote.Connected("alpha",f.Device)); Assert.Null(f.Remote.Authenticate(grant.Host, grant.Cookie));
            Assert.Contains(agent.Events,e=>e.Event=="SessionConnected"); Assert.Contains(agent.Events,e=>e.Event=="SessionClosed");
        } finally { await agent.StopAsync(CancellationToken.None); await local.StopAsync(); }
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow()=>Now; }
    public sealed class TestIdentity { public ClaimsPrincipal User { get; set; } = new(); }
    public sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, Microsoft.Extensions.Logging.ILoggerFactory logging, System.Text.Encodings.Web.UrlEncoder encoder, TestIdentity identity) : AuthenticationHandler<AuthenticationSchemeOptions>(options,logging,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()=>Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(identity.User,Scheme.Name)));
    }
    private sealed class Fixture : WebApplicationFactory<PiDock.Cloud.Web.CloudMarker>, IAsyncDisposable
    {
        public string Root => root;
        private readonly string root=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-relay-test-"+Guid.NewGuid());
        public Clock Clock { get; }=new(); public TestIdentity Identity { get; }=new(); public RSA Key { get; }=RSA.Create(2048);
        private WebSocket? control; private readonly CancellationTokenSource stop=new(); private readonly ConcurrentBag<Task> bridges=[];
        public ConcurrentQueue<string> Requests { get; }=new(); public RemoteAccessService Remote=>Services.GetRequiredService<RemoteAccessService>();
        public UserStore Users=>Services.GetRequiredService<UserStore>(); public EnrollmentStore Enrollments=>Services.GetRequiredService<EnrollmentStore>();
        public string Device { get; } public EnrollmentReply Reply { get; }
        public Fixture() {
            Directory.CreateDirectory(root);
            using var caKey=RSA.Create(2048); var caRequest=new CertificateRequest("CN=test-relay-ca",caKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true)); caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign,true));
            using var ca=caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(30)); File.WriteAllText(Path.Combine(root,"ca.crt"),ca.ExportCertificatePem()); File.WriteAllText(Path.Combine(root,"ca.key"),caKey.ExportPkcs8PrivateKeyPem());
            using var client=Client(); var secret=Totp.NewSecret(); Users.Add("remote-owner","remote-test-password",secret,"alpha","Owner",true,-1);
            var user=Users.Authenticate("remote-owner","remote-test-password",Totp.Code(secret,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30),DateTimeOffset.UtcNow)!; var session=Users.CreateSession(user,true,DateTimeOffset.UtcNow);
            Identity.User=new(new ClaimsIdentity([new Claim(ClaimTypes.Name,user.Username),new Claim(ClaimTypes.Role,user.Role),new Claim("tenant",user.TenantId),new Claim("stamp",user.Stamp),new Claim("session",session.Id),new Claim("expires",session.ExpiresAt.ToUnixTimeSeconds().ToString())],"test"));
            var code=Enrollments.Create(Identity.User,"Test Pi");Device=code.DeviceId;
            Reply=Enrollments.Redeem(new(code.Code,new CertificateRequest("CN=pending",Key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1).CreateSigningRequestPem()))!;
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.UseEnvironment("Development").UseSetting("AllowedHosts","cloud.example.test;*.access.example.test").UseSetting("PiDock:StateDirectory",root).UseSetting("Logging:LogLevel:Default","Warning").UseSetting("RemoteAccess:CloudOrigin","https://cloud.example.test").UseSetting("RemoteAccess:Domain","access.example.test")
                .UseSetting("Enrollment:IssuerCertificatePath",Path.Combine(root,"ca.crt")).UseSetting("Enrollment:IssuerKeyPath",Path.Combine(root,"ca.key")).UseSetting("Enrollment:BrokerCaPath",Path.Combine(root,"ca.crt")).UseSetting("Enrollment:MqttHost","mqtt.example.test");
            builder.ConfigureServices(services=>{services.AddSingleton<TimeProvider>(Clock);services.AddSingleton(Identity);services.AddAuthentication(o=>{o.DefaultAuthenticateScheme="test";o.DefaultChallengeScheme="test";}).AddScheme<AuthenticationSchemeOptions,TestAuthentication>("test",_=>{});});
        }
        public HttpClient Client()=>CreateClient(new WebApplicationFactoryClientOptions{AllowAutoRedirect=false,HandleCookies=false,BaseAddress=new("https://cloud.example.test")});
        public async Task Connect() {
            control=await Server.CreateWebSocketClient().ConnectAsync(new("wss://cloud.example.test/api/remote/agent"),stop.Token);
            var challenge=await RemoteProtocol.Read<RemoteTunnelCommand>(control,stop.Token);
            await RemoteProtocol.Send(control,new RemoteDeviceProof(Reply.CertificatePem,Convert.ToBase64String(Key.SignData(RemoteProtocol.ProofBytes(challenge.Token),HashAlgorithmName.SHA256,RSASignaturePadding.Pss)),new string('A',64)),stop.Token);
            Assert.Equal("ready",(await RemoteProtocol.Read<RemoteTunnelCommand>(control,stop.Token)).Type);
        }
        public async Task Pump() {
            try { while(!stop.IsCancellationRequested) {
                var command=await RemoteProtocol.Read<RemoteTunnelCommand>(control!,stop.Token);
                if(command.Type!="open")continue;
                var client=Server.CreateWebSocketClient();client.ConfigureRequest=r=>r.Headers["Authorization"]="Bearer "+command.Token;
                var data=await client.ConnectAsync(new("wss://cloud.example.test/api/remote/data/"+command.Channel),stop.Token);bridges.Add(Serve(data));
            } } catch(Exception e) when(e is OperationCanceledException or WebSocketException or IOException) { }
        }
        private async Task Serve(WebSocket data) {
            using(data)using(var stream=new RemoteSocketStream(data)) {
                try {
                    var header=new List<byte>();var one=new byte[1];
                    while(header.Count<32768) { if(await stream.ReadAsync(one,stop.Token)==0)return;header.Add(one[0]);if(header.Count>=4&&Encoding.ASCII.GetString(header.TakeLast(4).ToArray())=="\r\n\r\n")break; }
                    var text=Encoding.ASCII.GetString(header.ToArray());Requests.Enqueue(text);
                    if(text.Contains("Upgrade: websocket",StringComparison.OrdinalIgnoreCase)) {
                        string key=text.Split("\r\n").Single(l=>l.StartsWith("Sec-WebSocket-Key:",StringComparison.OrdinalIgnoreCase)).Split(':',2)[1].Trim();
                        string accept=Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "+accept+"\r\n\r\n"),stop.Token);
                        using var ws=WebSocket.CreateFromStream(stream,true,null,Timeout.InfiniteTimeSpan);var bytes=new byte[16384];
                        while(!stop.IsCancellationRequested) { var r=await ws.ReceiveAsync(bytes,stop.Token);if(r.MessageType==WebSocketMessageType.Close)break;await ws.SendAsync(bytes.AsMemory(0,r.Count),r.MessageType,r.EndOfMessage,stop.Token); }
                    } else await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 17\r\nSet-Cookie: PiDock.Cloud=bad; Domain=example.test\r\nSet-Cookie: PiDock.Appliance=ok; Domain=localhost\r\nConnection: close\r\n\r\nappliance reached"),stop.Token);
                }catch(Exception e)when(e is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException){ }
            }
        }
        public async Task StopPump(Task pump){stop.Cancel();control?.Abort();foreach(var b in bridges)try{await b;}catch(OperationCanceledException){}await pump;}
        public override async ValueTask DisposeAsync(){stop.Cancel();control?.Abort();control?.Dispose();Key.Dispose();await base.DisposeAsync();stop.Dispose();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}

[CollectionDefinition("Remote access loopback", DisableParallelization = true)]
public sealed class RemoteAccessLoopbackCollection;
