using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Agent;
using PiDock.Appliance.Broker;
using PiDock.AppUi.Sample;
using PiDock.Core;
using PiDock.Web.Shared;

internal static class TargetValidation
{
    private const string RuntimeImage="mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e";
    private const string SdkImage="mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0";
    public static async Task Run(string root)
    {
        string socket=Environment.GetEnvironmentVariable("DOCKER_HOST") is { } dockerHost && dockerHost.StartsWith("unix://",StringComparison.Ordinal)?dockerHost[7..]:OperatingSystem.IsMacOS()?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".docker/run/docker.sock"):"/var/run/docker.sock";
        using var engine=UnixHttp.Connect(socket,TimeSpan.FromMinutes(3));
        var target=await engine.GetFromJsonAsync<JsonElement>("/version");
        Check(target.GetProperty("Os").GetString()=="linux" && target.GetProperty("Arch").GetString()=="arm64","Use the Linux ARM64 Docker Desktop development target.");
        Check(string.IsNullOrWhiteSpace(await Exec("docker","ps","-aq","--filter","label=ai.knowledgelocker.pidock.managed=true")),"No existing PiDock managed containers may be present.");
        using(var volume=await engine.GetAsync("/v1.47/volumes/pidock-app-ui-sample-data"))Check(volume.StatusCode==HttpStatusCode.NotFound,"Existing sample data was not adopted or changed.");
        string temp=StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pdc-"+Guid.NewGuid().ToString("N")));
        string name="pdc-target-"+Guid.NewGuid().ToString("N")[..10],registry=name+"-registry",tag=$"localhost:{FreePort()}/{name}:test";
        int registryPort=int.Parse(new Uri("http://"+tag).Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),servicePort=FreePort(),appPort=FreePort();
        string state=StateFiles.PrepareDirectory(Path.Combine(temp,"host")),brokerState=StateFiles.PrepareDirectory(Path.Combine(temp,"broker-state")),brokerSocket=Path.Combine(StateFiles.PrepareDirectory(Path.Combine(temp,"broker-run")),"broker.sock");
        string? container=null,network=null,image=null;bool registryAttempted=false,volumeOwned=false;
        OwnedService? host=null,brokerProcess=null;
        var evidence=new List<object>();
        using var key=RSA.Create(2048);var request=new CertificateRequest("CN=Isolated target validation",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddIpAddress(IPAddress.Loopback);san.AddDnsName("host.docker.internal");san.AddDnsName("unapproved.invalid");request.CertificateExtensions.Add(san.Build());
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
        string certPath=Path.Combine(temp,"host.crt"),keyPath=Path.Combine(temp,"host.key");StateFiles.WriteAtomic(certPath,cert.ExportCertificatePem());StateFiles.WriteAtomic(keyPath,key.ExportPkcs8PrivateKeyPem());
        string origin=$"https://127.0.0.1:{servicePort}/",fingerprint=Convert.ToHexString(SHA256.HashData(cert.RawData));
        ApplicationCredentialFile Credential(string id,string purpose,string[] scopes)=>new(1,"ui-sample",id,1,purpose,Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),DateTimeOffset.UtcNow.AddMinutes(45),scopes);
        var credentials=new[]{Credential("bridge","ui-bridge",[]),Credential("sample-io","platform",["io.read","io.write","io.simulate"]),Credential("sample-runtime","platform",["signals.runtime"]),Credential("sample-engineer","platform",["signals.engineer"])};
        StateFiles.PrepareDirectory(Path.Combine(state,"keys"));
        var hostProtection=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(state,"keys")),b=>b.SetApplicationName("PiDock.Appliance"));
        string password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),secret=Totp.NewSecret(),viewerSecret=Totp.NewSecret();
        var users=new UserStore(hostProtection,state);users.Add("target-owner",password,secret,"local","Owner",true,-1);users.Add("target-viewer",password,viewerSecret,"local","Viewer",false,-1);
        StateFiles.WriteAtomic(Path.Combine(state,"app-credentials.protected"),hostProtection.CreateProtector("PiDock.ApplicationCredentials.v1").Protect(JsonSerializer.Serialize(credentials,StateFiles.Json)));
        var vault=new ApplicationCredentialVault(hostProtection,new(state),users,TimeProvider.System);
        var configuration=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Broker:SocketPath"]=brokerSocket}).Build();
        using var control=new BrokerClient(configuration,vault);
        var env=new Dictionary<string,string>{["ASPNETCORE_ENVIRONMENT"]="Production",["Logging__LogLevel__Default"]="Warning",["Broker__SocketPath"]=brokerSocket,["Broker__DockerSocket"]=socket,["Broker__StateDirectory"]=brokerState,["Broker__EnableAppResources"]="true",["Broker__EnableAppOrchestration"]="true",["Broker__EnableLifecycle"]="true",["Broker__EnableDeployments"]="true",["Broker__EnableAppRecovery"]="true"};
        var webEnv=new Dictionary<string,string>{["ASPNETCORE_ENVIRONMENT"]="Production",["ASPNETCORE_URLS"]=origin,["ASPNETCORE_Kestrel__Certificates__Default__Path"]=certPath,["ASPNETCORE_Kestrel__Certificates__Default__KeyPath"]=keyPath,["Logging__LogLevel__Default"]="Warning",["PiDock__StateDirectory"]=state,["Mqtt__Enabled"]="false",["Broker__SocketPath"]=brokerSocket,["PiDock__Apps__RuntimeAccess__Enabled"]="true",["PiDock__Apps__Io__Enabled"]="true",["PiDock__Engineering__Enabled"]="true"};
        webEnv["AllowedHosts"]="localhost;127.0.0.1;[::1];host.docker.internal";
        var aliases=SampleConnectedSession.Aliases.Append("sim:bcm23").ToArray();
        for(int i=0;i<aliases.Length;i++){string p=$"PiDock__Apps__Io__Grants__{i}__";webEnv[p+"AppId"]="ui-sample";webEnv[p+"Alias"]=aliases[i];webEnv[p+"Direction"]=i<2?"Input":"Output";}
        string ui="PiDock__ApplicationUi__Registrations__0__";
        foreach(var p in new Dictionary<string,string>{["Contribution__SchemaVersion"]="1",["Contribution__AppId"]="ui-sample",["Contribution__Id"]="main",["Contribution__Title"]="Target validation sample",["Contribution__EntryPath"]="index.html",["Contribution__RequiredUiVersion"]="1.0",["Contribution__RequiredStyleVersion"]="1.0",["ServiceOrigin"]=$"http://127.0.0.1:{appPort}/",["Enabled"]="true",["Bridge__Version"]="1.0",["Bridge__CredentialId"]="bridge",["Bridge__CommandsPath"]="commands",["Bridge__StreamPath"]="events",["Bridge__Commands__0"]="counter.increment",["PublicAssets__0"]="assets/components.css",["PublicAssets__1"]="assets/tokens.css",["PublicAssets__2"]="assets/client.js"})webEnv[ui+p.Key]=p.Value;
        try
        {
            Console.WriteLine("Building the isolated target; actual broker and appliance executables will be restarted.");
            await Exec("dotnet","publish",Path.Combine(root,"tools/PiDock.AppUi.Sample"),"-c","Release","-r","linux-arm64","--self-contained","false","-p:RestorePackagesWithLockFile=false","-o",Path.Combine(temp,"app"));
            await File.WriteAllTextAsync(Path.Combine(temp,"probe.c"),ApplicationHealthAssets.ReaderSource);
            await File.WriteAllTextAsync(Path.Combine(temp,"Dockerfile"),$"""
                FROM {SdkImage} AS probe
                RUN apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev
                COPY probe.c /build/probe.c
                RUN cc -std=c11 -static -Os -Wall -Wextra -Werror /build/probe.c -o /build/health-probe
                FROM {RuntimeImage}
                WORKDIR /app
                COPY app/ ./
                COPY --from=probe --chmod=0555 /build/health-probe /usr/lib/pidock/health-probe
                RUN mkdir -p /var/lib/pidock/app-data && chown 1654:1654 /var/lib/pidock/app-data && chmod 0700 /var/lib/pidock/app-data
                ENV ASPNETCORE_ENVIRONMENT=Development Sample__DockerFixture=true Sample__Version=target-review Sample__Port=7361 Sample__ServicesOrigin=https://host.docker.internal:{servicePort}/ Sample__CertificateSha256={fingerprint} Logging__LogLevel__Default=Warning
                USER 1654:1654
                ENTRYPOINT ["dotnet","PiDock.AppUi.Sample.dll"]
                """);
            registryAttempted=true;await Exec("docker","run","-d","--name",registry,"-p",$"127.0.0.1:{registryPort}:5000","registry:2");
            await Exec("docker","build","--platform","linux/arm64","-t",tag,temp);image=(await Exec("docker","image","inspect","--format","{{.Id}}",tag)).Trim();await Exec("docker","push",tag);
            string pinned=(await Exec("docker","image","inspect","--format","{{index .RepoDigests 0}}",tag)).Trim();
            brokerProcess=new(AssemblyPath("PiDock.Appliance.Broker"),Path.Combine(root,"src/PiDock.Appliance.Broker"),env);brokerProcess.Start();await BrokerReady();
            host=new(AssemblyPath("PiDock.Appliance.Web"),Path.Combine(root,"src/PiDock.Appliance.Web"),webEnv);host.Start();await HostReady();
            var ownerCookies=new CookieContainer();using var owner=Client(ownerCookies);using var anon=Client();
            using(var denied=await anon.GetAsync("api/platform/v1/discovery"))Check(denied.StatusCode==HttpStatusCode.Unauthorized,"Anonymous discovery must be denied.");
            using(var invalidHost=new HttpRequestMessage(HttpMethod.Get,"auth/login")){invalidHost.Headers.Host="unapproved.invalid";using var denied=await anon.SendAsync(invalidHost);Check(denied.StatusCode==HttpStatusCode.BadRequest,"Host filtering must reject an unapproved name.");}
            using(var csrf=await anon.PostAsync("auth/login",new FormUrlEncodedContent(new Dictionary<string,string>{{"username","target-owner"},{"password",password},{"code","000000"}})))Check(csrf.StatusCode==HttpStatusCode.BadRequest,"Login without anti-forgery must be rejected.");
            long loginStep=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30;
            string code=Totp.Code(secret,loginStep);
            using(var login=await Login(owner,"target-owner",code))
            {
                Check(login.StatusCode==HttpStatusCode.Redirect && login.Headers.TryGetValues("Set-Cookie",out var cookies) && cookies.Any(c=>c.StartsWith("PiDock.Appliance=",StringComparison.Ordinal)&&c.Contains("secure",StringComparison.OrdinalIgnoreCase)&&c.Contains("httponly",StringComparison.OrdinalIgnoreCase)&&c.Contains("samesite=strict",StringComparison.OrdinalIgnoreCase)),"Production Owner cookie must require Secure/HttpOnly/Strict.");
            }
            using(var discovery=await owner.GetAsync("api/platform/v1/discovery"))Check(discovery.IsSuccessStatusCode,"Authenticated Owner discovery must work.");
            using(var prodIo=await owner.GetAsync("api/platform/v1/app-io")){prodIo.EnsureSuccessStatusCode();var support=await prodIo.Content.ReadFromJsonAsync<ApplicationIoSupport>();Check(support is {Enabled:false,Physical:false},"Production host must not silently enable the Development simulation adapter.");}
            using var replay=Client();using(var rejected=await Login(replay,"target-owner",code))Check(!rejected.Headers.Contains("Set-Cookie") || !rejected.Headers.GetValues("Set-Cookie").Any(c=>c.StartsWith("PiDock.Appliance=",StringComparison.Ordinal)),"A replayed MFA code cannot create a session.");
            using(var denied=await replay.GetAsync("api/platform/v1/discovery"))Check(denied.StatusCode==HttpStatusCode.Unauthorized,"Replayed MFA must remain unauthorized.");
            using var viewer=Client();using(var logged=await Login(viewer,"target-viewer",Totp.Code(viewerSecret,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30)))Check(logged.StatusCode==HttpStatusCode.Redirect,"Viewer test login must work.");
            using(var denied=await viewer.GetAsync("api/platform/v1/discovery"))Check(denied.StatusCode==HttpStatusCode.Forbidden,"Non-Owner cannot obtain Owner discovery authority.");
            bool tlsDenied=false;try{using var untrusted=new HttpClient{Timeout=TimeSpan.FromSeconds(4)};using var ignored=await untrusted.GetAsync(origin);}catch(HttpRequestException){tlsDenied=true;}Check(tlsDenied,"Untrusted TLS must fail without explicit private certificate trust.");
            evidence.Add(new{test="actual-production-authentication",passed=true,anonymousDenied=true,csrfDenied=true,mfaReplayDenied=true,viewerDenied=true,secureOwnerCookie=true,untrustedTlsDenied=true,unapprovedHostDenied=true,productionSimulationDisabled=true});
            Console.WriteLine("PASS actual Production host: Owner authentication, CSRF/MFA/role isolation and TLS denial.");

            await host.Stop();webEnv["ASPNETCORE_ENVIRONMENT"]="Development";host=new(AssemblyPath("PiDock.Appliance.Web"),Path.Combine(root,"src/PiDock.Appliance.Web"),webEnv);host.Start();await HostReady();
            using(var persisted=await owner.GetAsync("api/platform/v1/discovery"))Check(persisted.IsSuccessStatusCode,"Owner session/key state must survive restart.");
            using(var replayAgain=Client()) {using var rejected=await Login(replayAgain,"target-owner",code);using var denied=await replayAgain.GetAsync("api/platform/v1/discovery");Check(denied.StatusCode==HttpStatusCode.Unauthorized,"MFA replay protection must survive host reload.");}
            using(var support=await owner.GetAsync("api/platform/v1/app-io")){support.EnsureSuccessStatusCode();Check((await support.Content.ReadFromJsonAsync<ApplicationIoSupport>()) is {Enabled:true,Physical:false,LifecycleBound:true},"Development simulation must be explicitly enabled and lifecycle bound.");}
            using(var support=await owner.GetAsync("api/platform/v1/app-runtime-access")){support.EnsureSuccessStatusCode();Check((await support.Content.ReadFromJsonAsync<ApplicationRuntimeAccessSupport>())!.Enabled,"Discover startup binding on this actual host.");}
            volumeOwned=true;
            await control.Deploy(new("ui-sample",pinned,256,1,7361,appPort,new(PersistentData:true,Credentials:credentials.Select(c=>c.Id).ToArray(),AutomaticRecovery:true,Health:new(30,10,1,true),ManagedLifecycle:true,ShutdownSeconds:2)));
            container=(await control.List()).Single().Id;var inspect=await Inspect();network=inspect.GetProperty("HostConfig").GetProperty("NetworkMode").GetString();
            await Ready();
            using var commands=new HttpClient{BaseAddress=new($"http://127.0.0.1:{appPort}/"),Timeout=TimeSpan.FromSeconds(6)};commands.DefaultRequestHeaders.Authorization=new("Bearer",credentials[0].Token);
            var brokerProtection=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(brokerState,"keys")),b=>b.SetApplicationName("PiDock.Broker"));
            var records=new ManagedDeploymentStore(brokerProtection,Path.Combine(brokerState,"apps"));
            var hc=inspect.GetProperty("HostConfig");var config=inspect.GetProperty("Config");
            Check(config.GetProperty("User").GetString()=="1654:1654" && hc.GetProperty("ReadonlyRootfs").GetBoolean() && !hc.GetProperty("Privileged").GetBoolean() && hc.GetProperty("CapDrop").EnumerateArray().Any(c=>c.GetString()=="ALL") && hc.GetProperty("SecurityOpt").EnumerateArray().Any(c=>c.GetString()=="no-new-privileges:true"),"Actual container security configuration required.");
            Check(hc.GetProperty("Memory").GetInt64()==256L*1024*1024 && hc.GetProperty("NanoCpus").GetInt64()==1_000_000_000 && hc.GetProperty("PidsLimit").GetInt64()==128 && hc.GetProperty("RestartPolicy").GetProperty("Name").GetString()=="no" && network!="host","Resource limits and platform-only restart must apply.");
            Check(inspect.GetProperty("Mounts").EnumerateArray().All(m=>m.GetProperty("Type").GetString()!="bind"),"No host bind mounts may enter the sample.");
            Check(config.GetProperty("Env").EnumerateArray().All(v=>!credentials.Any(c=>v.GetString()!.Contains(c.Token,StringComparison.Ordinal))) && !inspect.GetProperty("Args").GetRawText().Contains(credentials[0].Token,StringComparison.Ordinal),"Tokens must not enter environment/arguments.");
            string status=await Exec("docker","exec","--user","1654:1654",container,"cat","/proc/1/status");
            Check(Regex.IsMatch(status,@"(?m)^CapEff:\s+0+$") && Regex.IsMatch(status,@"(?m)^NoNewPrivs:\s+1$") && Regex.IsMatch(status,@"(?m)^Seccomp:\s+2$"),"Kernel must enforce zero effective capabilities, no-new-privileges and seccomp.");
            await Exec("docker","exec","--user","1654:1654",container,"sh","-c","test ! -e /var/run/docker.sock && test ! -e /dev/gpiomem && test ! -e /dev/mem && test ! -e /dev/gpiochip0 && if touch /app/forbidden-test 2>/dev/null; then exit 9; else exit 0; fi");
            foreach(var credential in credentials)Check((await Exec("docker","exec","--user","1654:1654",container,"stat","-c","%u %g %a",ApplicationResourceRules.CredentialDirectory+"/"+credential.Id+".json")).Trim()=="1654 1654 400","Delivered credential must be owned/readable only by the app user.");
            long vmSeconds=long.Parse((await Exec("docker","exec",container,"date","-u","+%s")).Trim(),System.Globalization.CultureInfo.InvariantCulture);long skew=Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-vmSeconds);Check(skew<=2,"Observed VM/host UTC skew must fit health validation.");
            using(var noToken=Client()){using var denied=await noToken.GetAsync("api/apps/v1/io/signals");Check(denied.StatusCode==HttpStatusCode.Unauthorized,"Anonymous app I/O denied.");}
            using(var wrongScope=AppClient(2)){using var denied=await wrongScope.GetAsync("api/apps/v1/io/signals");Check(denied.StatusCode==HttpStatusCode.Forbidden,"Runtime-only credential cannot obtain I/O authority.");}
            using(var missingBinding=AppClient(1)){missingBinding.DefaultRequestHeaders.Remove(ApplicationRuntimeAccess.Header);using var denied=await missingBinding.GetAsync("api/apps/v1/io/signals");Check(denied.StatusCode==HttpStatusCode.Forbidden,"Startup binding is required.");}
            using(var grant=AppClient(1)){using var denied=await grant.PostAsJsonAsync("api/apps/v1/io/leases",new ApplicationIoLeaseRequest(["sim:bcm18"]));Check(denied.StatusCode==HttpStatusCode.Forbidden,"Unconfigured alias must be denied.");}
            using(var html=await owner.GetAsync("app-ui/ui-sample/main/index.html")){html.EnsureSuccessStatusCode();string content=await html.Content.ReadAsStringAsync();Check(credentials.All(c=>!content.Contains(c.Token,StringComparison.Ordinal))&&!content.Contains(records.Find(container)!.LastDeliveryId!.Value.ToString(),StringComparison.OrdinalIgnoreCase),"Hosted app HTML must contain no private startup/token material.");}
            using(var html=await anon.GetAsync("app-ui/ui-sample/main/index.html"))Check(!html.IsSuccessStatusCode,"Anonymous app HTML must remain protected.");
            evidence.Add(new{test="real-container-and-wire-security",passed=true,uid=1654,readOnlyRoot=true,noEffectiveCapabilities=true,noNewPrivileges=true,seccomp=true,noHostBindsSocketOrGpio=true,privateCredentialModes=true,boundedResources=true,scopedIoDenials=true,privateHtml=true,vmClockSkewSeconds=skew});
            Console.WriteLine("PASS real kernel/container and HTTPS scope/binding security checks.");

            await Command("counter.reset",new{});for(int i=0;i<5;i++)await Command("counter.increment",new{});
            await Force();var before=records.Find(container)!;int oldBroker=brokerProcess.Id;
            await brokerProcess.Stop();await Until(async()=>!(await Inspect()).GetProperty("State").GetProperty("Running").GetBoolean(),"coordinated shutdown stopped the sample",10);
            brokerProcess.Start();await BrokerReady();await Ready();
            Check(brokerProcess.Id!=oldBroker && records.Find(container)!.LastDeliveryId!=before.LastDeliveryId,"Actual broker restart must bootstrap a new app identity from protected state.");
            await ClearedRuntime();Check((await State()).GetProperty("counter").GetInt32()==5,"Persistent data must survive actual broker shutdown/restart.");
            evidence.Add(new{test="actual-broker-graceful-restart",passed=true,newProcess=true,protectedStateRecovered=true,newStartup=true,currentCredentialsRedelivered=true,counter=5,forcesRestored=false});
            Console.WriteLine("PASS actual broker executable restart: coordinated stop/resume, retained data and cleared forces.");

            await Force();using var probeHttp=AppClient(1);var io=new ApplicationIoClient(probeHttp);var lease=await io.Acquire(new(["sim:bcm23"],15));await io.Write(lease.Id,new(lease.Revision,[new("sim:bcm23",true)]));
            int oldHost=host.Id;await host.Stop();host=new(AssemblyPath("PiDock.Appliance.Web"),Path.Combine(root,"src/PiDock.Appliance.Web"),webEnv);host.Start();await HostReady();
            using(var denied=await probeHttp.GetAsync("api/apps/v1/io/leases/"+lease.Id))Check(denied.StatusCode==HttpStatusCode.Forbidden,"Old lease must not revive across host restart.");
            using(var probe=AppClient(1)){var freshIo=new ApplicationIoClient(probe);var freshLease=await freshIo.Acquire(new(["sim:bcm23"],15));Check((await freshIo.Read(freshLease.Id)).Values.All(v=>!v.Value),"Simulator starts at configured fallback after host restart.");await freshIo.Release(freshLease.Id);}
            using(var watch=AppClient(3)){using var response=await watch.GetAsync("api/apps/v1/engineering/watch");Check(response.StatusCode==HttpStatusCode.Conflict,"Engineering controls must not reappear after host restart.");}
            await control.Restart(container);await Ready();await ClearedRuntime();
            Check(host.Id!=oldHost && (await State()).GetProperty("counter").GetInt32()==5,"Actual host restart and explicit repaired app restart must retain data.");
            using(var cookie=await owner.GetAsync("api/platform/v1/discovery"))Check(cookie.IsSuccessStatusCode,"Persisted secure session/key ring must remain usable after host restart.");
            evidence.Add(new{test="actual-appliance-host-restart",passed=true,newProcess=true,oldLeaseDenied=true,simulatorFallback=true,forcesRestored=false,cookieKeysRetained=true,counter=5});
            Console.WriteLine("PASS actual appliance executable restart: no restored leases/forces, retained data and keys.");

            await control.ChangeState(container,false);var stopped=records.Find(container)!;
            await host.Stop();await brokerProcess.Stop();brokerProcess.Start();await BrokerReady();
            host=new(AssemblyPath("PiDock.Appliance.Web"),Path.Combine(root,"src/PiDock.Appliance.Web"),webEnv);host.Start();await HostReady();
            var observed=Stopwatch.StartNew();int passes=0;
            while(observed.Elapsed<TimeSpan.FromSeconds(16))
            {
                var current=(await control.LifecycleStatus()).Single();Check(!current.DesiredRunning && current.RecoveryEnabled && current.RuntimeState=="exited" && records.Find(container)!.LastDeliveryId==stopped.LastDeliveryId,"Stop must survive real host and broker restarts.");
                var orchestration=await control.OrchestrationStatus();if(orchestration is {Paused:false})passes++;
                await Task.Delay(500);
            }
            Check(passes>=3,"Restarted services must be responsive and reconciled while Stop remains authoritative.");
            bool wrongKeysDenied=false;var wrong=DataProtectionProvider.Create(new DirectoryInfo(StateFiles.PrepareDirectory(Path.Combine(temp,"wrong-keys"))),b=>b.SetApplicationName("PiDock.Appliance"));
            try{_ = new UserStore(wrong,state);}catch(CryptographicException){wrongKeysDenied=true;}Check(wrongKeysDenied,"Protected account state must fail closed with an unrelated key ring.");
            string protectedAccounts=await File.ReadAllTextAsync(Path.Combine(state,"accounts.protected")),protectedCredentials=await File.ReadAllTextAsync(Path.Combine(state,"app-credentials.protected"));
            Check(!protectedAccounts.Contains(password,StringComparison.Ordinal)&&!protectedAccounts.Contains(secret,StringComparison.Ordinal)&&credentials.All(c=>!protectedCredentials.Contains(c.Token,StringComparison.Ordinal)),"Protected state must not retain plaintext credential/account values.");
            evidence.Add(new{test="intentional-stop-across-service-restarts",passed=true,observationSeconds=16,recoveryEnabled=true,requestedStopped=true,noNewStartup=true,wrongKeyRingDenied=true,protectedStateNoPlaintextSecrets=true});
            Console.WriteLine("PASS Stop survives both actual executable restarts; unrelated keys cannot read protected accounts.");
            var assemblies=new[]{"PiDock.Core","PiDock.Agent","PiDock.Web.Shared","PiDock.Appliance.Broker","PiDock.Appliance.Web"}.ToDictionary(n=>n,n=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(AssemblyPath(n)))));
            string report=Path.Combine(root,"artifacts/target-security-restart.json");Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{schemaVersion=1,observedAt=DateTimeOffset.UtcNow,scope="Docker Desktop Linux ARM64 simulation-first development",docker=target,hostOs=RuntimeInformation.OSDescription,hostRuntime=RuntimeInformation.FrameworkDescription,sourceRevision=(await Exec("git","-C",root,"rev-parse","HEAD")).Trim(),workingTreeDirty=true,assemblies,image=pinned,physical=false,systemdExercised=false,dockerDaemonRestarted=false,checks=evidence},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("PASS target security/restart validation. Evidence: "+report);

            string AssemblyPath(string n)
            {
                string project=n=="PiDock.Core"||n=="PiDock.Agent"||n=="PiDock.Web.Shared"||n=="PiDock.Appliance.Broker"||n=="PiDock.Appliance.Web"?"src":"tools";
                return Path.Combine(root,project,n,"bin/Release/net10.0",n+".dll");
            }
            async Task BrokerReady()=>await Until(async()=>{try{return (await control.LifecycleSupport())?.Enabled==true;}catch(HttpRequestException){return false;}},"actual broker startup",30);
            async Task HostReady()=>await Until(async()=>{try{using var client=Client();using var response=await client.GetAsync("auth/login");return response.IsSuccessStatusCode;}catch(HttpRequestException){return false;}},"actual host startup",30);
            async Task Ready()
            {
                ApplicationLifecycleStatus? latest=null;
                try{await Until(async()=>{latest=(await control.LifecycleStatus()).Single();return latest.RuntimeState=="running"&&latest.Health is {Ready:true,Live:true};},"fresh native Ready",60);}
                catch(TimeoutException){Console.WriteLine($"Ready timeout: runtime={latest?.RuntimeState}, health={latest?.Health?.State}, reason={latest?.Health?.Reason}, attempts={latest?.Attempts}.");throw;}
            }
            async Task<JsonElement> Inspect()=>await engine.GetFromJsonAsync<JsonElement>($"/v1.47/containers/{container}/json");
            HttpClient AppClient(int credential){var client=Client();client.DefaultRequestHeaders.Authorization=new("Bearer",credentials[credential].Token);ApplicationRuntimeAccess.Bind(client,records.Find(container!)!.LastDeliveryId!.Value);return client;}
            async Task<ApplicationUiCommandResult> Command(string name,object args)
            {using var response=await commands.PostAsJsonAsync("commands",new ApplicationUiCommand(1,Guid.NewGuid(),name,JsonSerializer.SerializeToElement(args)),StateFiles.Json);response.EnsureSuccessStatusCode();var result=await response.Content.ReadFromJsonAsync<ApplicationUiCommandResult>(StateFiles.Json);Check(result?.Succeeded==true,"Sample command was not accepted.");return result!;}
            async Task<JsonElement> State(){using var response=await commands.SendAsync(new HttpRequestMessage(HttpMethod.Get,"events"),HttpCompletionOption.ResponseHeadersRead);response.EnsureSuccessStatusCode();using var reader=new StreamReader(await response.Content.ReadAsStreamAsync());return JsonSerializer.Deserialize<ApplicationUiEvent>((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!,StateFiles.Json)!.Data;}
            async Task Force(){var snapshot=(await State()).GetProperty("shared").Deserialize<SampleConnectedState>(StateFiles.Json)!;await Command("engineering.force",new{instanceId=snapshot.InstanceId,revision=snapshot.Revision,signalId="output-bank",value=true,seconds=300});await Until(async()=>{var s=(await State()).GetProperty("shared").Deserialize<SampleConnectedState>(StateFiles.Json)!;return s.ForceCount==1&&s.Outputs.Length==3&&s.Outputs.All(o=>o.Value);},"active force",10);}
            async Task ClearedRuntime(){var snapshot=(await State()).GetProperty("shared").Deserialize<SampleConnectedState>(StateFiles.Json)!;Check(snapshot.Status=="connected"&&snapshot.ForceCount==0&&snapshot.Signals.All(s=>s.Force is null)&&snapshot.Outputs.All(o=>!o.Value),"Recovered runtime must be clear at fallback.");}
        }
        finally
        {
            var errors=new List<Exception>();async Task Cleanup(Func<Task> work){try{await work();}catch(Exception e){errors.Add(e);}}
            async Task Remove(string path){using var response=await engine.DeleteAsync(path);Check(response.IsSuccessStatusCode||response.StatusCode==HttpStatusCode.NotFound,"Owned Docker resource cleanup failed.");}
            if(host is not null)await Cleanup(()=>host.Stop());if(brokerProcess is not null)await Cleanup(()=>brokerProcess.Stop());
            if(container is not null)await Cleanup(()=>Remove($"/v1.47/containers/{container}?force=1"));if(network is not null)await Cleanup(()=>Remove($"/v1.47/networks/{network}"));
            if(volumeOwned)await Cleanup(()=>Remove("/v1.47/volumes/pidock-app-ui-sample-data"));if(registryAttempted)await Cleanup(()=>Remove($"/v1.47/containers/{registry}?force=1&v=1"));
            if(image is not null)await Cleanup(async()=>{await Exec("docker","image","rm","-f",image);});await Cleanup(()=>{Directory.Delete(temp,true);return Task.CompletedTask;});
            if(errors.Count>0)throw new AggregateException("Owned target fixture cleanup incomplete.",errors);
        }
        HttpClient Client(CookieContainer? cookies=null)
        {
            var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=true,CookieContainer=cookies??new CookieContainer(),ServerCertificateCustomValidationCallback=(_,certificate,_,errors)=>certificate is not null&&(errors&(SslPolicyErrors.RemoteCertificateNameMismatch|SslPolicyErrors.RemoteCertificateNotAvailable))==0&&CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData),SHA256.HashData(cert.RawData))};
            return new(handler){BaseAddress=new(origin),Timeout=TimeSpan.FromSeconds(5)};
        }
        async Task<HttpResponseMessage> Login(HttpClient client,string username,string code)
        {
            string html=await client.GetStringAsync("auth/login");var token=Regex.Match(html,"name=\"(__RequestVerificationToken)\" value=\"([^\"]+)\"");Check(token.Success,"Real anti-forgery field must be present.");
            return await client.PostAsync("auth/login",new FormUrlEncodedContent(new Dictionary<string,string>{{token.Groups[1].Value,WebUtility.HtmlDecode(token.Groups[2].Value)},{"username",username},{"password",password},{"code",code}}));
        }
    }
    private static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int p=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return p;}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static async Task Until(Func<Task<bool>> condition,string message,int seconds){var timer=Stopwatch.StartNew();while(timer.Elapsed<TimeSpan.FromSeconds(seconds)){if(await condition())return;await Task.Delay(300);}throw new TimeoutException(message);}
    private static async Task<string> Exec(string file,params string[] args)
    {
        var info=new ProcessStartInfo(file){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new IOException("Cannot start target fixture tool.");var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(8));}catch{process.Kill(true);throw;}
        string text=await output;await error;Check(process.ExitCode==0,"Target fixture tool failed; private output was not forwarded.");return text;
    }
}
