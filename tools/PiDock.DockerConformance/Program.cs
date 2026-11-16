using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Agent;
using PiDock.Appliance.Broker;
using PiDock.Appliance.Web;
using PiDock.AppUi.Sample;
using PiDock.Core;
using PiDock.Web.Shared;

// Explicit disposable target test. Uses the production DockerOperations and shared HTTPS endpoints.
// Requires a Docker Desktop Linux ARM64 target with no existing PiDock managed containers.
bool healthRecovery = args.Contains("--health-recovery",StringComparer.Ordinal);
bool retryLimits = args.Contains("--retry-limits",StringComparer.Ordinal);
bool budgetReset = args.Contains("--budget-reset",StringComparer.Ordinal);
bool credentialRecovery = args.Contains("--credential-recovery",StringComparer.Ordinal);
bool applicationLibrary = args.Contains("--application-library",StringComparer.Ordinal);
bool workflow = args.Contains("--workflow",StringComparer.Ordinal);
bool targetValidation=args.Contains("--target-validation",StringComparer.Ordinal);
if(new[]{healthRecovery,retryLimits,budgetReset,credentialRecovery,workflow,targetValidation,applicationLibrary}.Count(enabled=>enabled)>1) throw new ArgumentException("Choose one conformance scenario per run.");
bool nativeHealth = healthRecovery || budgetReset || credentialRecovery || workflow || applicationLibrary;
bool supervised = nativeHealth || retryLimits;
var root = Path.GetFullPath(args.Where(a=>a is not ("--health-recovery" or "--retry-limits" or "--budget-reset" or "--credential-recovery" or "--workflow" or "--target-validation" or "--application-library")).SingleOrDefault() ?? ".");
if (!File.Exists(Path.Combine(root, "PiDock.sln"))) throw new ArgumentException("Pass the PiDock checkout path.");
foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => new[] { "PiDock__", "PiDock:", "Broker__", "Broker:", "Sample__", "Review__" }.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToArray()) Environment.SetEnvironmentVariable(key, null);
if(targetValidation){await TargetValidation.Run(root);return;}
string socket = Environment.GetEnvironmentVariable("DOCKER_HOST") is { } host && host.StartsWith("unix://") ? host[7..] :
    OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker/run/docker.sock") : "/var/run/docker.sock";
using var engine = UnixHttp.Connect(socket, TimeSpan.FromMinutes(3));
var version = await engine.GetFromJsonAsync<JsonElement>("/version");
Check(version.GetProperty("Arch").GetString() == "arm64" && version.GetProperty("Os").GetString() == "linux", "This fixture requires Linux ARM64 Docker.");
string existing = await Run("docker", "ps", "-aq", "--filter", "label=ai.knowledgelocker.pidock.managed=true");
Check(string.IsNullOrWhiteSpace(existing), "Use a Docker target with no existing PiDock managed containers; no workloads were changed.");
string temp = StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pdc-" + Guid.NewGuid().ToString("N")));
string run = "pdc-" + Guid.NewGuid().ToString("N")[..10], registry = run + "-registry";
int servicePort = FreePort(), appPort = FreePort(), registryPort = FreePort();
string tag = $"localhost:{registryPort}/{run}:test";
string? container = null, network = null, imageId = null;
string? secondImage=null, secondImageId=null, helperImage=null;
var ownedContainers=new HashSet<string>(); var ownedNetworks=new HashSet<string>();
WorkflowReview? reviewShell=null;
IApplicationUpdateStore? workflowUpdates=null;
Guid? restoreOperation=null;
bool workflowVolumeOwned=false;
WebApplication? api = null, coordinator = null;
bool registryAttempted = false;
var evidence = new List<object>();
var healthEvidence = new ConcurrentQueue<HealthObservation>();
using var keypair = RSA.Create(2048);
var request = new CertificateRequest("CN=Disposable Docker conformance", keypair, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("host.docker.internal"); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
string fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
var expires = DateTimeOffset.UtcNow.AddMinutes(45);
ApplicationCredentialFile Credential(string name, string purpose, string[] scopes) => new(1, "ui-sample", name, 1, purpose, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), expires, scopes);
var credentials = new[] { Credential("bridge", "ui-bridge", []), Credential("sample-io", "platform", ["io.read", "io.write", "io.simulate"]), Credential("sample-runtime", "platform", ["signals.runtime"]), Credential("sample-engineer", "platform", ["signals.engineer"]) };
try
{
    if(workflow || applicationLibrary) {using var volumeCheck=await engine.GetAsync("/v1.47/volumes/pidock-app-ui-sample-data");Check(volumeCheck.StatusCode==HttpStatusCode.NotFound,"Workflow requires an absent sample data volume; retained data was not modified.");}
    Console.WriteLine("Preparing restricted Linux ARM64 sample image and disposable local registry…");
    await Run("dotnet", "publish", Path.Combine(root,"tools/PiDock.AppUi.Sample"), "-c", "Release", "-r", "linux-arm64", "--self-contained", "false", "-p:NuGetLockFilePath=obj/packages.linux-arm64.lock.json", "-o", Path.Combine(temp,"app"));
    if(nativeHealth) await File.WriteAllTextAsync(Path.Combine(temp,"pidock-health-probe.c"),ApplicationHealthAssets.ReaderSource);
    string probeStage=nativeHealth ? """
        FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS probe
        RUN apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev
        WORKDIR /build
        COPY pidock-health-probe.c ./
        RUN cc -std=c11 -static -Os -Wall -Wextra -Werror pidock-health-probe.c -o health-probe

        """ : "";
    string probeCopy=nativeHealth ? "COPY --from=probe --chmod=0555 /build/health-probe /usr/lib/pidock/health-probe" : "";
    await File.WriteAllTextAsync(Path.Combine(temp,"Dockerfile"), $"""
        {probeStage}
        FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
        WORKDIR /app
        COPY app/ ./
        {probeCopy}
        ARG VERSION=one
        ENV Sample__Version=$VERSION
        RUN mkdir -p /var/lib/pidock/app-data && chown 1654:1654 /var/lib/pidock/app-data && chmod 0700 /var/lib/pidock/app-data
        ENV ASPNETCORE_ENVIRONMENT=Development Sample__DockerFixture=true Sample__HealthFaults={budgetReset.ToString().ToLowerInvariant()} Sample__Port=7361 Sample__ServicesOrigin=https://host.docker.internal:{servicePort}/ Sample__CertificateSha256={fingerprint} Logging__LogLevel__Default=Warning
        USER 1654:1654
        ENTRYPOINT ["dotnet", "PiDock.AppUi.Sample.dll"]
        """);
    registryAttempted = true;
    await Run("docker", "run", "-d", "--name", registry, "-p", $"127.0.0.1:{registryPort}:5000", "registry:2");
    await Run("docker", "build", "--platform", "linux/arm64", "-t", tag, temp);
    imageId = (await Run("docker", "image", "inspect", "--format", "{{.Id}}", tag)).Trim();
    await Run("docker", "push", tag);
    string pinned = (await Run("docker", "image", "inspect", "--format", "{{index .RepoDigests 0}}", tag)).Trim();
    Check(pinned.StartsWith($"localhost:{registryPort}/{run}@sha256:"), "Expected exact local registry image digest.");
    if(workflow)
    {
        string secondTag=tag.Replace(":test",":two");
        await Run("docker","build","--platform","linux/arm64","--build-arg","VERSION=two","-t",secondTag,temp);
        secondImageId=(await Run("docker","image","inspect","--format","{{.Id}}",secondTag)).Trim(); await Run("docker","push",secondTag);
        secondImage=(await Run("docker","image","inspect","--format","{{index .RepoDigests 0}}",secondTag)).Trim();
        await Run("dotnet","publish",Path.Combine(root,"tools/PiDock.AppData.Helper"),"-c","Release","-r","linux-arm64","--self-contained","false","-p:NuGetLockFilePath=obj/packages.linux-arm64.lock.json","-o",Path.Combine(temp,"helper"));
        await File.WriteAllTextAsync(Path.Combine(temp,"Dockerfile.helper"),$"""
            FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
            LABEL fixture.run={run}
            COPY helper/ /app/
            USER 1654:1654
            ENTRYPOINT ["dotnet","/app/PiDock.AppData.Helper.dll"]
            """);
        await Run("docker","build","--platform","linux/arm64","-f",Path.Combine(temp,"Dockerfile.helper"),"-t",run+"-helper",temp);
        helperImage=(await Run("docker","image","inspect","--format","{{.Id}}",run+"-helper")).Trim();
        await Run("dotnet","build",Path.Combine(root,"tools/PiDock.UiReview"),"-c","Release");
    }

    // Both stores are private to this run; methods below are the actual broker implementation.
    var protection = DataProtectionProvider.Create(new DirectoryInfo(StateFiles.PrepareDirectory(Path.Combine(temp,"broker-keys"))));
    var store = new ObservedDeploymentStore(new ManagedDeploymentStore(protection, Path.Combine(temp,"managed")));
    var updates=new ApplicationUpdateStore(protection,Path.Combine(temp,"updates"));
    workflowUpdates=updates;
    var backups=new ApplicationBackupStore(protection,Path.Combine(temp,"backups"));
    var docker = new DockerOperations(engine, store, new ApplicationBootstrapDelivery(socket), TimeProvider.System,
        recoveryEnabled:supervised, orchestrationEnabled: true, shutdownStore: new ApplicationShutdownStore(protection, Path.Combine(temp,"shutdown")),
        updatesEnabled:workflow,updateStore:updates,backupsEnabled:workflow,backupStore:backups,appDataHelperImage:helperImage,
        installTrustEnabled:workflow || applicationLibrary,installTrustStore:new ApplicationInstallTrustStore(protection,Path.Combine(temp,"approvals")),
        removalsEnabled:workflow,removalStore:new ApplicationRemovalStore(protection,Path.Combine(temp,"removal")));
    string brokerSocket = Path.Combine(temp,"b.sock");
    var cb = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" }); cb.Logging.ClearProviders();
    cb.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(brokerSocket)); coordinator = cb.Build();
    coordinator.MapGet("/applications/runtime-access", (CancellationToken ct) => docker.RuntimeAccess(ct));
    coordinator.MapGet("/applications/orchestration/status", () => docker.OrchestrationStatus());
    coordinator.MapGet("/applications", (CancellationToken ct) => docker.List(ct));
    if(supervised)
    {
        coordinator.MapGet("/applications/health/support",()=>docker.HealthSupport());
        coordinator.MapPost("/applications/health/check",async(CancellationToken ct)=>
        {
            await docker.CheckHealth(ct);
            foreach(var status in await docker.LifecycleStatus(ct)) if(status.Health is { } h) healthEvidence.Enqueue(new(DateTimeOffset.UtcNow,h.State,h.Ready,h.Live,h.Failures,h.Reason,h.RecoveryRequired));
            return Results.Ok();
        });
        coordinator.MapGet("/applications/lifecycle/support",()=>docker.LifecycleSupport());
        coordinator.MapGet("/applications/lifecycle",(CancellationToken ct)=>docker.LifecycleStatus(ct));
        coordinator.MapGet("/applications/orchestration/support",()=>docker.OrchestrationSupport());
        coordinator.MapPost("/applications/orchestration/reconcile",async(CancellationToken ct)=>{await docker.ReconcileOrchestration(ct);return Results.Ok();});
        coordinator.MapPost("/applications/lifecycle/reconcile",async(CancellationToken ct)=>{await docker.ReconcileLifecycle(ct);return Results.Ok();});
        coordinator.MapPost("/applications/{id}/recover",async(string id,ApplicationRecoveryRequest request,CancellationToken ct)=>{await docker.Recover(id,request,ct);return Results.Ok();});
        coordinator.MapPost("/applications/{id}/converge",async(string id,ApplicationRecoveryRequest request,CancellationToken ct)=>{await docker.Recover(id,request,ct);return Results.Ok();});
        coordinator.MapPost("/applications/{id}/recovery-blocked",async(string id,ApplicationRecoveryBlocked request,CancellationToken ct)=>{await docker.RecoveryBlocked(id,request.Revision,ct);return Results.Ok();});
    }
    coordinator.MapPost("/applications/orchestration/pause", async (CancellationToken ct) => { await docker.PauseApplications(ct); return Results.Ok(); });
    coordinator.MapPost("/applications/orchestration/resume", async (CancellationToken ct) => { await docker.ResumeApplications(ct); return Results.Ok(); });
    coordinator.MapPost("/applications/{id}/stop", async (string id, CancellationToken ct) => { await docker.ChangeState(id,false,ct); return Results.Ok(); });
    coordinator.MapPost("/applications/{id}/start", async (string id, CancellationToken ct) => { await docker.ChangeState(id,true,ct); return Results.Ok(); });
    coordinator.MapPost("/applications/{id}/restart", async (string id, CancellationToken ct) => { await docker.Restart(id,ct); return Results.Ok(); });
    coordinator.MapGet("/applications/{id}/resources",(string id,CancellationToken ct)=>docker.Resources(id,ct));
    if(workflow || applicationLibrary)
    {
        coordinator.MapGet("/applications/resources",()=>new ApplicationResourceSupport(1,true,ApplicationResourceRules.DataDirectory,ApplicationResourceRules.CredentialDirectory,"pidock-stdin-v1",[]));
        coordinator.MapPost("/applications/prepared",(ApplicationDeploymentRequest prepared,CancellationToken ct)=>docker.DeployPrepared(prepared,ct));
        coordinator.MapGet("/applications/install-trust/support",()=>docker.InstallTrustSupport());
        coordinator.MapGet("/applications/install-trust/approvals",()=>docker.InstallApprovals());
        coordinator.MapGet("/applications/updates/support",()=>docker.UpdateSupport());
        coordinator.MapGet("/applications/updates",(CancellationToken ct)=>docker.UpdateStatus(ct));
        coordinator.MapPost("/applications/updates/{appId}/advance",async(string appId,ApplicationUpdateAdvance request,CancellationToken ct)=>{await docker.AdvanceUpdate(appId,request,ct);return Results.Ok();});
    }
    coordinator.MapPost("/applications/{id}/bootstrap/{action}",async(string id,string action,ApplicationCredentialFile[] material,CancellationToken ct)=>
    {
        if(action is not ("start" or "restart")) return Results.BadRequest();
        await docker.Lifecycle(id,true,action=="restart",material,ct); return Results.Ok();
    });
    await coordinator.StartAsync();

    var b = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" }); b.Logging.ClearProviders();
    var settings = new Dictionary<string,string?> { ["PiDock:StateDirectory"] = Path.Combine(temp,"host"), ["Broker:SocketPath"] = brokerSocket,
        ["PiDock:Apps:RuntimeAccess:Enabled"] = "true", ["PiDock:Apps:Io:Enabled"] = "true", ["PiDock:Engineering:Enabled"] = "true" };
    if(!workflow) for (int i=0; i<SampleConnectedSession.Aliases.Length; i++) { string p=$"PiDock:Apps:Io:Grants:{i}:"; settings[p+"AppId"]="ui-sample"; settings[p+"Alias"]=SampleConnectedSession.Aliases[i]; settings[p+"Direction"]=i<2?"Input":"Output"; }
    b.Configuration.AddInMemoryCollection(settings);
    if(workflow)
    {
        var grants=SampleConnectedSession.Aliases.Select((alias,i)=>new ApplicationIoGrant("ui-sample",alias,i<2?ApplicationIoDirection.Input:ApplicationIoDirection.Output)).ToArray();
        StateFiles.WriteAtomic(Path.Combine(temp,"io-grants.json"),JsonSerializer.Serialize(new{PiDock=new{Apps=new{Io=new{Grants=grants}}}},StateFiles.Json));
        b.Configuration.AddJsonFile(Path.Combine(temp,"io-grants.json"),optional:false,reloadOnChange:true);
    }
    b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback,servicePort, l => l.UseHttps(certificate)));
    b.AddPiDockAuthentication("DockerConformance"); b.AddServiceCredentials(cloud:false); b.AddApplicationIo(); b.AddApplicationEngineering(); b.AddApplicationRuntimeAccess();
    b.Services.AddSingleton<TimeProvider>(TimeProvider.System); b.Services.AddSingleton<BrokerClient>(); b.Services.AddSingleton<ApplicationCredentialVault>();
    b.Services.AddSingleton<IApplicationCredentialSource>(sp=>sp.GetRequiredService<ApplicationCredentialVault>());
    if(supervised) b.Services.AddHostedService<ApplicationRecoveryService>();
    if(workflow) b.Services.AddHostedService<ApplicationUpdateService>();
    b.Services.AddSingleton(sp => new HistoryStore(settings["PiDock:StateDirectory"]!,new DriveCapacityProbe()));
    api=b.Build();
    var vaultProtection=api.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("PiDock.ApplicationCredentials.v1");
    StateFiles.WriteAtomic(Path.Combine(settings["PiDock:StateDirectory"]!,"app-credentials.protected"),vaultProtection.Protect(JsonSerializer.Serialize(credentials,StateFiles.Json)));
    api.UseAuthentication(); api.UseAuthorization(); api.UseRateLimiter(); api.MapApplicationIo(); api.MapApplicationEngineering(); await api.StartAsync();
    var io=api.Services.GetRequiredService<ApplicationIoService>(); var engineering=api.Services.GetRequiredService<ApplicationOverrideService>();
    using var broker=UnixHttp.Connect(brokerSocket,TimeSpan.FromSeconds(45));
    using var commands=new HttpClient { BaseAddress=new($"http://127.0.0.1:{appPort}/"), Timeout=TimeSpan.FromSeconds(8) }; commands.DefaultRequestHeaders.Authorization=new("Bearer",credentials[0].Token);
    var manifest=new ApplicationManifest("ui-sample",pinned,256,1,7361,appPort,new(PersistentData:workflow || applicationLibrary,Credentials:credentials.Select(c=>c.Id).ToArray(),AutomaticRecovery:supervised,
        Health:nativeHealth?new(StartupSeconds:30,FreshnessSeconds:10,FailureThreshold:1,RestartOnFailure:true):null,ManagedLifecycle:true,ShutdownSeconds:2,Updates:workflow?new(ReadinessSeconds:60,StableSeconds:5):null));
    if(workflow)
    {
        bool denied=false;try{await docker.DeployPrepared(new(manifest,credentials),default);}catch(InvalidOperationException){denied=true;}Check(denied,"Unapproved installation must be denied.");
        await docker.ApproveInstall(new(manifest,"fixture-owner","Disposable integrated Docker workflow",DateTimeOffset.UtcNow.AddHours(1)),default);
    }
    workflowVolumeOwned=workflow || applicationLibrary;
    if(applicationLibrary)
    {
        var definition=await ApplicationLibraryConformance.PublishAndDownload(temp,manifest,certificate);
        var client=api.Services.GetRequiredService<BrokerClient>();
        bool denied=false;try{await client.DeployDefinition(definition);}catch(HttpRequestException){denied=true;}
        Check(denied && (await docker.List(default)).Length==0,"Definition installation must respect local exact-image approval.");
        await docker.ApproveInstall(new(manifest,"fixture-owner","Disposable application library acceptance",DateTimeOffset.UtcNow.AddHours(1)),default);
        async Task EvictImage()
        {
            await Run("docker","image","rm","--force",imageId!);
            using var absent=await engine.GetAsync($"/v1.47/images/{Uri.EscapeDataString(pinned)}/json");
            Check(absent.StatusCode==HttpStatusCode.NotFound,"Image must be absent before registry installation.");
        }
        await EvictImage();
        await client.DeployDefinition(definition);
        container=(await docker.List(default)).Single().Id;ownedContainers.Add(container);
        network=(await Inspect()).GetProperty("HostConfig").GetProperty("NetworkMode").GetString();if(network is not null)ownedNetworks.Add(network);
        await Fresh();
        var firstId=container;
        await docker.ChangeState(container,false,default);
        using(var remove=await engine.DeleteAsync($"/v1.47/containers/{container}?force=1"))remove.EnsureSuccessStatusCode();
        store.Forget(container);
        if(network is not null){using var remove=await engine.DeleteAsync($"/v1.47/networks/{network}");remove.EnsureSuccessStatusCode();}
        await EvictImage();
        await ApplicationLibraryConformance.InstallThroughCloud(temp,definition,client);
        container=(await docker.List(default)).Single().Id;ownedContainers.Add(container);
        Check(container!=firstId,"Cloud installation must create a fresh container through the normal broker.");
        var resolved=await engine.GetFromJsonAsync<JsonElement>($"/v1.47/images/{Uri.EscapeDataString(pinned)}/json");
        Check(resolved.GetProperty("Os").GetString()=="linux" && resolved.GetProperty("Architecture").GetString()=="arm64","Registry image must resolve to Linux ARM64.");
        evidence.Add(new{test="application-library-local-and-cloud-install",passed=true,browserDefinitionDownload=true,exactDefinitionDigest=ApplicationContractRules.Digest(definition),imageApprovalEnforced=true,localImageEvictedBeforeEachInstall=true,separateCloudPermissionEnforced=true,receiptLargerThanFourKiB=true,registryImageResolved=true,platform="linux/arm64"});
    }
    else {container=await docker.DeployPrepared(new(manifest,credentials),default); ownedContainers.Add(container);}
    var inspection=await Inspect(); network=inspection.GetProperty("HostConfig").GetProperty("NetworkMode").GetString();
    if(network is not null)ownedNetworks.Add(network);
    Check(inspection.GetProperty("Config").GetProperty("User").GetString()=="1654:1654" && inspection.GetProperty("HostConfig").GetProperty("ReadonlyRootfs").GetBoolean(),"Restricted runtime configuration required.");
    var first=await Fresh();
    await Command("demo.start",new{});
    await Wait(async()=> (await Watch()).Signals.Single(s=>s.Declaration.Id=="output-bank").NormalValue.GetBoolean() && !io.Activity("ui-sample").OutputsAtFallback,"latch and nonblocking delayed output");
    await Command("demo.stop",new{}); await Wait(()=>Task.FromResult(io.Activity("ui-sample").OutputsAtFallback),"normal Stop fallback");
    evidence.Add(new { test="latch-delay-stop",passed=true });
    foreach(string action in supervised ? Array.Empty<string>() : new[]{"pause","stop","restart","failure"})
    {
        var before=await Watch(); var oldDelivery=store.Find(container)!.LastDeliveryId!.Value;
        string oldStart=(await Inspect()).GetProperty("State").GetProperty("StartedAt").GetString()!;
        await Command("engineering.force",new{instanceId=before.InstanceId,revision=before.Revision,signalId="output-bank",value=true,seconds=300});
        await Wait(()=>Task.FromResult(io.Activity("ui-sample") is {Leases:1,OutputsAtFallback:false} && engineering.Activity("ui-sample").Forces==1),"active force and all outputs before "+action);
        var display=await Command("counter.increment",new{});
        var applied=display.Data!.Value.GetProperty("shared").Deserialize<SampleConnectedState>(StateFiles.Json)!;
        Check(applied.Outputs.Length==3 && applied.Outputs.All(o=>o.Value && o.Commanded==true),"All three outputs must be confirmed true before "+action);
        // Suspend PID 1 without Docker pause: Docker still reports running, so suspension itself cannot revoke access.
        using(var freeze=await engine.PostAsync($"/v1.47/containers/{container}/kill?signal=SIGSTOP",null)) freeze.EnsureSuccessStatusCode();
        using(var engineer=Client(3,oldDelivery))
        using(var queued=await engineer.PostAsJsonAsync("api/apps/v1/engineering/set-once",new ApplicationSignalWriteRequest(before.InstanceId,before.Revision,"bank-enabled",JsonSerializer.SerializeToElement(false)),StateFiles.Json)) queued.EnsureSuccessStatusCode();
        Check(engineering.Activity("ui-sample").Writes==1,"A queued write must exist before "+action);
        var revoked=Wait(()=>Task.FromResult(io.Activity("ui-sample") is {Leases:0,OutputsAtFallback:true} && engineering.Activity("ui-sample") is {Forces:0,Writes:0,Fenced:true}),action+" independent cleanup",12);
        var timer=Stopwatch.StartNew();
        Task operation=action switch { "pause"=>Post("/applications/orchestration/pause"), "stop"=>Post($"/applications/{container}/stop"), "restart"=>Post($"/applications/{container}/restart"), _=>Task.CompletedTask };
        if(action=="failure")
        {
            using(var kill=await engine.PostAsync($"/v1.47/containers/{container}/kill?signal=SIGKILL",null)) kill.EnsureSuccessStatusCode();
        }
        await revoked; double releaseMs=timer.Elapsed.TotalMilliseconds;
        Check(releaseMs<4000,"Cleanup must precede five-second engineering freshness and fifteen-second lease expiry.");
        if(action!="failure")
        {
            var state=(await Inspect()).GetProperty("State");
            if(state.GetProperty("Running").GetBoolean()) { using var thaw=await engine.PostAsync($"/v1.47/containers/{container}/kill?signal=SIGCONT",null); thaw.EnsureSuccessStatusCode(); }
        }
        await operation;
        var stoppedState=(await Inspect()).GetProperty("State");
        if(action!="restart") Check(!stoppedState.GetProperty("Running").GetBoolean(),"Container must be stopped after "+action);
        if(action=="failure") Check(stoppedState.GetProperty("ExitCode").GetInt32()==137,"Failure must be SIGKILL without graceful cleanup.");
        Check(io.Activity("ui-sample").OutputsAtFallback,"Fallback after "+action);
        if(action=="pause")
        {
            Check(docker.OrchestrationStatus().Paused,"Maintenance pause required.");
            await Post($"/applications/{container}/stop"); await Post("/applications/orchestration/resume"); await docker.ReconcileOrchestration(default);
            Check(!store.Find(container)!.DesiredRunning && !(await Inspect()).GetProperty("State").GetProperty("Running").GetBoolean(),"Stop intent survives maintenance Resume.");
            await Post($"/applications/{container}/start");
        }
        else if(action is "stop" or "failure") await Post($"/applications/{container}/start");
        var after=await Fresh();
        Check(after.InstanceId!=before.InstanceId && store.Find(container)!.LastDeliveryId!=oldDelivery,"Fresh process identity required.");
        Check((await Inspect()).GetProperty("State").GetProperty("StartedAt").GetString()!=oldStart,"Docker must confirm a new process start.");
        Check(after.Signals.All(s=>s.Force is null) && after.PendingWrites.Length==0 && !after.Signals.Single(s=>s.Declaration.Id=="run-request").NormalValue.GetBoolean(),"Fresh startup must clear transient state.");
        using(var stale=Client(1,oldDelivery))
        using(var denied=await stale.PostAsJsonAsync("api/apps/v1/io/leases",new ApplicationIoLeaseRequest(["sim:bcm17"]),StateFiles.Json)) Check(denied.StatusCode==HttpStatusCode.Forbidden,"Previous startup binding must be denied.");
        evidence.Add(new{test=action,passed=true,releaseMs=Math.Round(releaseMs),leases=0,forces=0,writes=0,outputsAtFallback=true,freshStartup=true,previousBindingDenied=true,queuedWriteRevoked=true,allThreeOutputsInitiallyTrue=true});
        Console.WriteLine($"PASS {action}: no leases/forces/writes, fallback confirmed; fresh startup ({releaseMs:F0} ms observed cleanup).");
    }
    if(healthRecovery) await ValidateHealthRecovery();
    if(retryLimits) await ValidateRetryLimits();
    if(budgetReset) await ValidateBudgetReset();
    if(credentialRecovery) await ValidateCredentialRecovery();
    if(workflow) await ValidateWorkflow();
    string report=Path.Combine(root,applicationLibrary?"artifacts/docker-application-library.json":workflow?"artifacts/docker-sample-workflow.json":credentialRecovery?"artifacts/docker-sample-credential-recovery.json":budgetReset?"artifacts/docker-sample-budget-reset.json":retryLimits?"artifacts/docker-sample-retry-limits.json":healthRecovery?"artifacts/docker-sample-health-recovery.json":"artifacts/docker-sample-lifecycle.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{schemaVersion=1,observedAt=DateTimeOffset.UtcNow,dockerVersion=version.GetProperty("Version").GetString(),platform="linux/arm64",image=pinned,physical=false,tests=evidence,health=healthEvidence.ToArray(),attempts=store.Attempts.ToArray(),budgetResets=store.BudgetResets.ToArray(),credentialWaits=store.CredentialWaits.ToArray()},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"PASS Docker sample {(applicationLibrary?"application library":workflow?"integrated workflow":credentialRecovery?"credential recovery":budgetReset?"budget reset":retryLimits?"retry limits":healthRecovery?"health/recovery":"lifecycle")} conformance. Evidence: "+report);

    async Task<JsonElement> Inspect()=>await engine.GetFromJsonAsync<JsonElement>($"/v1.47/containers/{container}/json");
    async Task Post(string path) { using var response=await broker.PostAsync(path,null); response.EnsureSuccessStatusCode(); }
    async Task<ApplicationSignalSnapshot> Watch()
    {
        var delivery=store.Find(container!)!.LastDeliveryId!.Value;
        using var client=Client(3,delivery);
        return await client.GetFromJsonAsync<ApplicationSignalSnapshot>("api/apps/v1/engineering/watch",StateFiles.Json)??throw new IOException("Missing watch.");
    }
    HttpClient Client(int credential,Guid delivery)
    {
        var handler=new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false,ServerCertificateCustomValidationCallback=(_,cert,_,errors)=>cert is not null && (errors & (System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch|System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable))==0 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData),SHA256.HashData(certificate.RawData)) };
        var client=new HttpClient(handler){BaseAddress=new($"https://127.0.0.1:{servicePort}/"),Timeout=TimeSpan.FromSeconds(3)}; client.DefaultRequestHeaders.Authorization=new("Bearer",credentials[credential].Token); ApplicationRuntimeAccess.Bind(client,delivery); return client;
    }
    async Task<ApplicationSignalSnapshot> Fresh()
    {
        ApplicationSignalSnapshot? frame=null;
        await Wait(async()=>{try{frame=await Watch();return frame.Quality=="current" && io.Activity("ui-sample").Leases==1;}catch(HttpRequestException){return false;}},"fresh shared runtime",30);
        return frame!;
    }
    async Task<ApplicationUiCommandResult> Command(string name,object arguments)
    {
        using var response=await commands.PostAsJsonAsync("commands",new ApplicationUiCommand(1,Guid.NewGuid(),name,JsonSerializer.SerializeToElement(arguments)),StateFiles.Json); response.EnsureSuccessStatusCode();
        var result=await response.Content.ReadFromJsonAsync<ApplicationUiCommandResult>(StateFiles.Json); Check(result?.Succeeded==true,"Sample command failed: "+name+" ("+result?.ErrorCode+")");
        return result!;
    }
    async Task ValidateHealthRecovery()
    {
        await Healthy();
        Check((await Inspect()).GetProperty("HostConfig").GetProperty("RestartPolicy").GetProperty("Name").GetString()=="no","PiDockOS must own recovery; Docker automatic restart is disabled.");
        evidence.Add(new{test="native-health-ready",passed=true,probeSha256=ApplicationHealthAssets.ReaderSha256});
        Console.WriteLine("PASS native health reader: managed startup reaches fresh Ready.");
        foreach(string fault in new[]{"crash","stale-heartbeat"})
        {
            await Healthy(); var before=await Fresh(); var previous=store.Find(container!)!;
            string previousStart=(await Inspect()).GetProperty("State").GetProperty("StartedAt").GetString()!;
            await Command("engineering.force",new{instanceId=before.InstanceId,revision=before.Revision,signalId="output-bank",value=true,seconds=300});
            await Wait(()=>Task.FromResult(engineering.Activity("ui-sample").Forces==1 && !io.Activity("ui-sample").OutputsAtFallback),"forced output before "+fault);
            var applied=(await Command("counter.increment",new{})).Data!.Value.GetProperty("shared").Deserialize<SampleConnectedState>(StateFiles.Json)!;
            Check(applied.Outputs.Length==3 && applied.Outputs.All(o=>o.Value && o.Commanded==true),"All outputs must be on before "+fault);
            using(var freeze=await engine.PostAsync($"/v1.47/containers/{container}/kill?signal=SIGSTOP",null)) freeze.EnsureSuccessStatusCode();
            using(var engineer=Client(3,previous.LastDeliveryId!.Value))
            using(var queued=await engineer.PostAsJsonAsync("api/apps/v1/engineering/set-once",new ApplicationSignalWriteRequest(before.InstanceId,before.Revision,"bank-enabled",JsonSerializer.SerializeToElement(false)),StateFiles.Json)) queued.EnsureSuccessStatusCode();
            Check(engineering.Activity("ui-sample").Writes==1,"Pending write before "+fault);
            var began=Stopwatch.StartNew();
            if(fault=="crash") {using var killed=await engine.PostAsync($"/v1.47/containers/{container}/kill?signal=SIGKILL",null);killed.EnsureSuccessStatusCode();}
            // No Start/Restart/Recover call from the test: the actual hosted service owns every recovery pass.
            await Wait(()=>Task.FromResult(io.Activity("ui-sample") is {Leases:0,OutputsAtFallback:true} && engineering.Activity("ui-sample") is {Forces:0,Writes:0,Fenced:true}),"independent cleanup after "+fault,25);
            double clearedMs=began.Elapsed.TotalMilliseconds;
            await Wait(()=>Task.FromResult(store.Find(container!)!.LastDeliveryId!=previous.LastDeliveryId),"automatic new startup after "+fault,60);
            await Healthy(); var after=await Fresh(); var recovered=store.Find(container!)!;
            Check(recovered.DesiredRunning && recovered.RecoveryEnabled && recovered.Attempts==previous.Attempts+1,"Exactly one bounded automatic recovery attempt expected.");
            Check(after.InstanceId!=before.InstanceId && (await Inspect()).GetProperty("State").GetProperty("StartedAt").GetString()!=previousStart,"Recovery must create a new process/runtime.");
            Check(after.Signals.All(s=>s.Force is null) && after.PendingWrites.Length==0 && !after.Signals.Single(s=>s.Declaration.Id=="run-request").NormalValue.GetBoolean() && io.Activity("ui-sample").OutputsAtFallback,"Recovery must start cleared at fallback.");
            using(var stale=Client(1,previous.LastDeliveryId.Value))
            using(var denied=await stale.PostAsJsonAsync("api/apps/v1/io/leases",new ApplicationIoLeaseRequest(["sim:bcm17"]),StateFiles.Json)) Check(denied.StatusCode==HttpStatusCode.Forbidden,"Old startup authority must stay denied after recovery.");
            if(fault=="stale-heartbeat") Check(healthEvidence.Any(h=>h.At>=DateTimeOffset.UtcNow-began.Elapsed && h.State=="unhealthy" && h.RecoveryRequired && h.Reason=="report_stale_or_invalid"),"Native stale heartbeat must trigger health-driven recovery.");
            evidence.Add(new{test="automatic-"+fault+"-recovery",passed=true,clearedMs=Math.Round(clearedMs),readyMs=Math.Round(began.Elapsed.TotalMilliseconds),attempts=recovered.Attempts,forcesRestored=false,queuedWritesRestored=false,outputsAtFallback=true,oldBindingDenied=true});
            Console.WriteLine($"PASS automatic {fault} recovery: fresh Ready, forces/writes cleared, fallback confirmed ({began.Elapsed.TotalSeconds:F1} s).");
        }
        // Exercise a stale recovery request as well as several independent background passes after operator Stop.
        var candidate=(await docker.LifecycleStatus(default)).Single();
        var current=await Fresh();
        await Command("engineering.force",new{instanceId=current.InstanceId,revision=current.Revision,signalId="output-bank",value=true,seconds=300});
        await Wait(()=>Task.FromResult(engineering.Activity("ui-sample").Forces==1 && !io.Activity("ui-sample").OutputsAtFallback),"force before intentional Stop");
        await Post($"/applications/{container}/stop");
        var stopped=store.Find(container!)!;
        Check(!stopped.DesiredRunning && stopped.RecoveryEnabled,"Stop must preserve recovery policy while withdrawing requested-running intent.");
        int stoppedPassCount=healthEvidence.Count;
        await api!.Services.GetRequiredService<BrokerClient>().Recover(candidate);
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(16))
        {
            var persisted=store.Find(container!)!; var state=(await Inspect()).GetProperty("State");
            Check(!state.GetProperty("Running").GetBoolean() && !persisted.DesiredRunning && persisted.LastDeliveryId==stopped.LastDeliveryId && persisted.Attempts==stopped.Attempts,"Automatic recovery must never override intentional Stop.");
            Check(io.Activity("ui-sample") is {Leases:0,OutputsAtFallback:true} && engineering.Activity("ui-sample") is {Forces:0,Writes:0},"Stopped controls must remain cleared.");
            await Task.Delay(250);
        }
        int observedPasses=healthEvidence.Count-stoppedPassCount;
        Check(observedPasses>=3,"At least three actual background health/recovery passes must occur after intentional Stop.");
        evidence.Add(new{test="intentional-stop-survives-recovery",passed=true,observationSeconds=16,backgroundPasses=observedPasses,recoveryEnabled=true,staleRecoveryDenied=true,leases=0,forces=0,writes=0,outputsAtFallback=true});
        Console.WriteLine($"PASS intentional Stop: remains stopped through stale recovery request and {observedPasses} background passes over 16 seconds.");
    }
    async Task Healthy()
    {
        await Wait(()=>Task.FromResult(store.Find(container!) is {Health.Ready:true,Health.Live:true} record && record.Health.CheckedAt>=DateTimeOffset.UtcNow.AddSeconds(-10) && record.Health.ReportedAt>=DateTimeOffset.UtcNow.AddSeconds(-10)),"fresh native Ready health",45);
    }
    async Task ValidateRetryLimits()
    {
        var support=await api!.Services.GetRequiredService<BrokerClient>().LifecycleSupport()??throw new InvalidOperationException("Lifecycle support is required.");
        Check(support is {Enabled:true,MaximumAttempts:5,StableSeconds:60},"Expected discovered five-attempt policy and sixty-second stability rule.");
        int[] expectedBackoffs=[5,10,20,40,80];
        Check((await Inspect()).GetProperty("HostConfig").GetProperty("RestartPolicy").GetProperty("Name").GetString()=="no","Docker automatic restart must remain disabled.");
        var initial=await Fresh();
        await Command("engineering.force",new{instanceId=initial.InstanceId,revision=initial.Revision,signalId="output-bank",value=true,seconds=300});
        await Wait(()=>Task.FromResult(engineering.Activity("ui-sample").Forces==1 && !io.Activity("ui-sample").OutputsAtFallback),"active force before retry series");
        var applied=(await Command("counter.increment",new{})).Data!.Value.GetProperty("shared").Deserialize<SampleConnectedState>(StateFiles.Json)!;
        Check(applied.Outputs.Length==3 && applied.Outputs.All(o=>o.Value && o.Commanded==true),"All three outputs must be on before retry series.");
        var observed=new List<object>();
        for(int attempt=1;attempt<=support.MaximumAttempts;attempt++)
        {
            var before=store.Find(container!)!;
            await Crash(); await Cleared();
            // The worker alone attempts recovery. Observe actual broker writes and Docker process starts.
            await Wait(()=>Task.FromResult(store.Find(container!)!.Attempts==attempt && store.Attempts.Count>=attempt),"automatic attempt "+attempt,110);
            var observation=store.Attempts.ToArray()[attempt-1];
            Check(observation.Attempt==attempt,"Exactly consecutive attempts are required.");
            Check(Math.Abs((observation.NextAttemptAt-observation.ObservedAt).TotalSeconds-expectedBackoffs[attempt-1])<1,"Persisted backoff must match the documented 5/10/20/40/80-second policy.");
            if(before.NextAttemptAt is { } due) Check(observation.ObservedAt>=due,"Recovery must not start before its persisted deadline.");
            if(attempt>1)
            {
                var previous=store.Attempts.ToArray()[attempt-2];
                Check(Stopwatch.GetElapsedTime(previous.MonotonicTicks,observation.MonotonicTicks)>=TimeSpan.FromSeconds(expectedBackoffs[attempt-2])-TimeSpan.FromMilliseconds(250),"Monotonic retry spacing must enforce the real backoff.");
            }
            var fresh=await Fresh(); var record=store.Find(container!)!;
            Check(record.LastDeliveryId!=before.LastDeliveryId && record.Attempts==attempt,"A fresh startup must consume one attempt without resetting budget.");
            Check(fresh.Signals.All(s=>s.Force is null) && fresh.PendingWrites.Length==0 && io.Activity("ui-sample").OutputsAtFallback,"Retries must not restore engineering controls or output state.");
            using(var stale=Client(1,before.LastDeliveryId!.Value))
            using(var denied=await stale.PostAsJsonAsync("api/apps/v1/io/leases",new ApplicationIoLeaseRequest(["sim:bcm17"]),StateFiles.Json)) Check(denied.StatusCode==HttpStatusCode.Forbidden,"Old startup authority must remain denied.");
            double? spacing=attempt>1?Math.Round(Stopwatch.GetElapsedTime(store.Attempts.ToArray()[attempt-2].MonotonicTicks,observation.MonotonicTicks).TotalSeconds,3):null;
            observed.Add(new{attempt,scheduledBackoffSeconds=expectedBackoffs[attempt-1],observedStartSpacingSeconds=spacing,notBeforeDeadline=true,clearedStartup=true});
            Console.WriteLine($"PASS retry {attempt}/{support.MaximumAttempts}: scheduled {expectedBackoffs[attempt-1]} s backoff, observed spacing {spacing?.ToString("F2")??"initial"}, fresh cleared startup.");
        }
        await Crash(); await Cleared();
        await Wait(()=>Task.FromResult(store.Find(container!)!.Outcome=="suspended"),"recovery suspension after five attempts",15);
        var suspended=store.Find(container!)!;
        Check(suspended.DesiredRunning && suspended.RecoveryEnabled && suspended.Attempts==5,"Suspension must preserve requested-running intent and policy.");
        var finalDeadline=suspended.NextAttemptAt??throw new InvalidOperationException("Final backoff deadline must persist.");
        Console.WriteLine("Observing suspension beyond the final 80-second deadline and three further background passes…");
        await HoldStoppedUntil(finalDeadline.AddSeconds(16),suspended,5,expectedIntent:true);
        int suspendedPasses=healthEvidence.Count(h=>h.At>=finalDeadline);
        Check(suspendedPasses>=3 && store.Attempts.Count==5,"No sixth automatic attempt, even after the final deadline.");
        evidence.Add(new{test="retry-backoff-and-exhaustion",passed=true,maximumAttempts=5,attempts=observed,finalBackoffSeconds=80,noSixthAttempt=true,postDeadlinePasses=suspendedPasses,forcesRestored=false,outputsAtFallback=true});
        Console.WriteLine("PASS exhausted recovery: stays suspended with five attempts past the last deadline.");

        // An explicit Start is the supported operator reset; never edit persisted attempts/deadlines.
        await Post($"/applications/{container}/start"); await Fresh();
        Check(store.Find(container!)!.Attempts==0,"Explicit Start resets the exhausted attempt budget.");
        int newSeries=store.Attempts.Count;
        for(int attempt=1;attempt<=2;attempt++)
        {
            await Crash(); await Cleared();
            await Wait(()=>Task.FromResult(store.Find(container!)!.Attempts==attempt),"retry before pending Stop",25);
            await Fresh();
        }
        var waiting=store.Find(container!)!;
        var pendingDeadline=waiting.NextAttemptAt??throw new InvalidOperationException("A pending retry deadline is required.");
        await Crash(); await Cleared();
        Check(pendingDeadline>DateTimeOffset.UtcNow.AddSeconds(2),"Stop must be issued during an actual pending backoff window.");
        var staleCandidate=(await docker.LifecycleStatus(default)).Single();
        await Post($"/applications/{container}/stop");
        var stopped=store.Find(container!)!;
        Check(!stopped.DesiredRunning && stopped.RecoveryEnabled && stopped.Attempts==0 && stopped.NextAttemptAt is null,"Stop must cancel pending recovery and retain policy.");
        Console.WriteLine("Observing intentional Stop beyond the cancelled retry deadline…");
        await HoldStoppedUntil(pendingDeadline.AddSeconds(16),stopped,0,expectedIntent:false);
        await api.Services.GetRequiredService<BrokerClient>().Recover(staleCandidate);
        Check(store.Attempts.Count==newSeries+2 && store.Find(container!)!.LastDeliveryId==stopped.LastDeliveryId && !(await Inspect()).GetProperty("State").GetProperty("Running").GetBoolean(),"A delayed stale recovery request must not undo Stop.");
        int stoppedPasses=healthEvidence.Count(h=>h.At>=pendingDeadline);
        Check(stoppedPasses>=3,"Recovery worker must remain active after Stop and the former deadline.");
        evidence.Add(new{test="intentional-stop-cancels-backoff",passed=true,cancelledBackoffSeconds=10,postDeadlinePasses=stoppedPasses,staleRecoveryDenied=true,recoveryEnabled=true,leases=0,forces=0,writes=0,outputsAtFallback=true});
        Console.WriteLine("PASS Stop precedence: cancelled pending retry, no later start, stale recovery ignored.");

        async Task Crash()
        { using var killed=await engine.PostAsync($"/v1.47/containers/{container}/kill?signal=SIGKILL",null); killed.EnsureSuccessStatusCode(); }
        async Task Cleared()
        { await Wait(()=>Task.FromResult(io.Activity("ui-sample") is {Leases:0,OutputsAtFallback:true} && engineering.Activity("ui-sample") is {Forces:0,Writes:0}),"independent resources cleared",8); }
        async Task HoldStoppedUntil(DateTimeOffset until,ManagedDeployment baseline,int attempts,bool expectedIntent)
        {
            while(DateTimeOffset.UtcNow<until)
            {
                var current=store.Find(container!)!;
                Check(current.Attempts==attempts && current.DesiredRunning==expectedIntent && current.LastDeliveryId==baseline.LastDeliveryId && current.RecoveryEnabled,"Stopped retry state must remain authoritative.");
                Check(!(await Inspect()).GetProperty("State").GetProperty("Running").GetBoolean(),"No automatic process start is permitted.");
                Check(io.Activity("ui-sample") is {Leases:0,OutputsAtFallback:true} && engineering.Activity("ui-sample") is {Forces:0,Writes:0},"Stopped resources must remain cleared.");
                await Task.Delay(500);
            }
        }
    }
    async Task ValidateBudgetReset()
    {
        Check((await api!.Services.GetRequiredService<BrokerClient>().LifecycleSupport()) is {Enabled:true,StableSeconds:60},"Discover the unchanged sixty-second stability rule.");
        await Healthy();
        using(var anonymous=new HttpClient{BaseAddress=commands.BaseAddress})
        using(var denied=await anonymous.PostAsJsonAsync("fixture/readiness",false)) Check(denied.StatusCode==HttpStatusCode.Unauthorized,"Readiness fixture requires private bridge authorization.");
        using(var invalid=await commands.PostAsJsonAsync("fixture/readiness",new{ready=false})) Check(invalid.StatusCode==HttpStatusCode.BadRequest,"Readiness fixture accepts only a Boolean.");
        await RecoverOnce(1);
        var first=store.Find(container!)!;
        var originalWindow=first.StableSince??throw new InvalidOperationException("Healthy stability start is required.");
        Console.WriteLine("Observing brief Ready operation with retry count retained…");
        await KeepHealthyUntil(originalWindow.AddSeconds(20),1,originalWindow);
        await Readiness(false);
        await Wait(()=>Task.FromResult(store.Find(container!) is {Health.State:"not_ready",Health.Live:true,StableSince:null}),"observed live but not Ready",15);
        Console.WriteLine("Observing withdrawn readiness past the original sixty-second window…");
        while(DateTimeOffset.UtcNow<originalWindow.AddSeconds(66))
        {
            var current=store.Find(container!)!;
            Check(current.Attempts==1 && current.LastDeliveryId==first.LastDeliveryId && current.StableSince is null && current.Health is {State:"not_ready",Live:true,RecoveryRequired:false},"Not-Ready uptime must not reset the budget or restart a live app.");
            Check((await Inspect()).GetProperty("State").GetProperty("Running").GetBoolean(),"Readiness withdrawal alone must leave the process running.");
            await Task.Delay(500);
        }
        Check(store.BudgetResets.All(r=>r.Outcome!="stable"),"Brief Ready followed by Not-Ready must not reset automatically.");
        evidence.Add(new{test="brief-and-interrupted-ready-retains-budget",passed=true,briefReadySeconds=20,observedPastOriginalWindowSeconds=66,attempts=1,noAutomaticReset=true,noReadinessOnlyRestart=true});
        Console.WriteLine("PASS interrupted stability: retry count retained beyond original window.");

        await Readiness(true); await Healthy();
        var restartedWindow=store.Find(container!)!.StableSince??throw new InvalidOperationException("A new stability window is required.");
        Check(restartedWindow>originalWindow.AddSeconds(60),"Restored Ready must begin a new window.");
        Console.WriteLine("Observing sixty seconds of continuous fresh Ready operation…");
        await KeepHealthyUntil(restartedWindow.AddSeconds(60),1,restartedWindow);
        await Wait(()=>Task.FromResult(store.Find(container!) is {Attempts:0,NextAttemptAt:null,Outcome:"stable"}),"automatic stability budget reset",15);
        var reset=store.BudgetResets.Single(r=>r.Outcome=="stable");
        Check(reset.PreviousAttempts==1 && reset.StableSince==restartedWindow && reset.ObservedAt>=restartedWindow.AddSeconds(60),"Only the completed healthy window may reset automatically.");
        Check(store.Find(container!)!.LastDeliveryId==first.LastDeliveryId && io.Activity("ui-sample").OutputsAtFallback,"Reset must not restart runtime or restore outputs.");
        evidence.Add(new{test="sustained-ready-resets-budget",passed=true,requiredSeconds=60,observedSeconds=Math.Round((reset.ObservedAt-restartedWindow).TotalSeconds,3),attempts=0,nextAttemptCleared=true,noProcessRestart=true});
        Console.WriteLine($"PASS sustained Ready: automatic reset after {(reset.ObservedAt-restartedWindow).TotalSeconds:F2} s.");

        // A post-reset failure spends attempt one again, then Stop interrupts its new stability period.
        await RecoverOnce(1);
        var next=store.Find(container!)!;
        Check(store.Attempts.Count==2 && store.Attempts.Last().Attempt==1,"After reset, the next failure must restart at attempt one.");
        var nextWindow=next.StableSince??throw new InvalidOperationException("Post-recovery stability window required.");
        await KeepHealthyUntil(nextWindow.AddSeconds(35),1,nextWindow);
        var candidate=(await docker.LifecycleStatus(default)).Single();
        await Post($"/applications/{container}/stop");
        var stopped=store.Find(container!)!;
        Check(!stopped.DesiredRunning && stopped.RecoveryEnabled && stopped.Attempts==0 && stopped.StableSince is null,"Operator Stop resets administrative state and withdraws intent.");
        Console.WriteLine("Observing intentional Stop across the former stability deadline…");
        while(DateTimeOffset.UtcNow<nextWindow.AddSeconds(76))
        {
            var current=store.Find(container!)!;
            Check(!current.DesiredRunning && current.Attempts==0 && current.StableSince is null && current.LastDeliveryId==stopped.LastDeliveryId && !(await Inspect()).GetProperty("State").GetProperty("Running").GetBoolean(),"A stability deadline must never undo intentional Stop.");
            Check(io.Activity("ui-sample") is {Leases:0,OutputsAtFallback:true} && engineering.Activity("ui-sample") is {Forces:0,Writes:0},"Stopped resources must remain cleared.");
            await Task.Delay(500);
        }
        await api.Services.GetRequiredService<BrokerClient>().Recover(candidate);
        int passes=healthEvidence.Count(h=>h.At>=nextWindow.AddSeconds(60));
        Check(passes>=3 && store.Attempts.Count==2 && store.BudgetResets.Count(r=>r.Outcome=="stable")==1 && !store.Find(container!)!.DesiredRunning && !(await Inspect()).GetProperty("State").GetProperty("Running").GetBoolean(),"Stop must survive worker passes, elapsed stability and stale recovery.");
        evidence.Add(new{test="stop-precedes-stability-reset",passed=true,postStabilityDeadlinePasses=passes,recoveryEnabled=true,staleRecoveryDenied=true,noAdditionalAutomaticReset=true,leases=0,forces=0,writes=0,outputsAtFallback=true});
        Console.WriteLine("PASS Stop precedence: remains stopped past former stability deadline and stale recovery.");

        async Task Readiness(bool ready)
        { using var response=await commands.PostAsJsonAsync("fixture/readiness",ready); response.EnsureSuccessStatusCode(); }
        async Task RecoverOnce(int expectedAttempts)
        {
            var before=store.Find(container!)!;
            using(var killed=await engine.PostAsync($"/v1.47/containers/{container}/kill?signal=SIGKILL",null)) killed.EnsureSuccessStatusCode();
            await Wait(()=>Task.FromResult(store.Find(container!)!.LastDeliveryId!=before.LastDeliveryId),"automatic startup for budget test",30);
            await Healthy(); var fresh=await Fresh();
            Check(store.Find(container!)!.Attempts==expectedAttempts && fresh.Signals.All(s=>s.Force is null) && fresh.PendingWrites.Length==0 && io.Activity("ui-sample").OutputsAtFallback,"Recovery must retain budget and start cleared.");
        }
        async Task KeepHealthyUntil(DateTimeOffset until,int attempts,DateTimeOffset stableSince)
        {
            while(DateTimeOffset.UtcNow<until)
            {
                var current=store.Find(container!)!;
                if(DateTimeOffset.UtcNow>=until) break;
                Check(current.Attempts==attempts && current.StableSince==stableSince && current.Health is {Ready:true,Live:true} h && h.CheckedAt>=DateTimeOffset.UtcNow.AddSeconds(-10) && h.ReportedAt>=DateTimeOffset.UtcNow.AddSeconds(-10),"Continuous fresh Ready is required; no early reset.");
                await Task.Delay(250);
            }
        }
    }
    async Task ValidateCredentialRecovery()
    {
        await Healthy();
        var vault=api!.Services.GetRequiredService<ApplicationCredentialVault>();
        var users=api.Services.GetRequiredService<UserStore>();
        string ownerPassword=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), ownerSecret=Totp.NewSecret();
        long lastCodeStep=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30-1;
        users.Add("fixture-owner",ownerPassword,ownerSecret,"fixture","Owner",true,-1);
        var login=users.Authenticate("fixture-owner",ownerPassword,Totp.Code(ownerSecret,lastCodeStep),DateTimeOffset.UtcNow);
        if(login is null)
        {
            lastCodeStep=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30;
            login=users.Authenticate("fixture-owner",ownerPassword,Totp.Code(ownerSecret,lastCodeStep),DateTimeOffset.UtcNow);
        }
        var owner=users.Principal(login??throw new InvalidOperationException("Disposable Owner authentication failed."));
        var host=api.Services.GetRequiredService<BrokerClient>();
        var initial=store.Find(container!)!;
        var oldIo=credentials[1];
        await ForcedOutputs();
        await vaultRevoke();
        Check(vault.Resolve("ui-sample","sample-io").Credential is null,"Revocation must remove current credential authority.");
        await Crash();
        Console.WriteLine("Observing revoked credentials through repeated real thirty-second rechecks…");
        var revokedWait=await BlockedWithoutAttempts(initial,0);
        Check(store.Find(container!)!.Credentials.Single(c=>c.Id=="sample-io").Version==oldIo.Version,"Broker retains old snapshot but must not use it for recovery.");
        // Repair with a credential that expires naturally after the deferred startup becomes eligible.
        var shortExpiry=revokedWait.NextCheckAt.AddSeconds(25);
        var repaired=await Issue(shortExpiry);
        Check(repaired.Version>oldIo.Version && repaired.Token!=oldIo.Token,"Repair must issue new credential material.");
        credentials[1]=repaired;
        await Recovered(initial,1,repaired);
        await RejectOldToken(oldIo);
        evidence.Add(new{test="revoked-credential-waits-without-retries",passed=true,blockedChecks=2,attemptsWhileBlocked=0,recoveredAttempt=1,currentCredentialVersion=repaired.Version,oldTokenDenied=true,noRetainedSnapshotFallback=true,outputsAtFallback=true});
        Console.WriteLine("PASS revoked credential: no retry spent while unavailable; automatic recovery used the newly issued version.");

        var beforeExpiry=store.Find(container!)!;
        Console.WriteLine("Waiting for repaired credential to expire naturally before another process failure…");
        await Wait(()=>Task.FromResult(DateTimeOffset.UtcNow>=shortExpiry),"natural credential expiry",35);
        Check(vault.Resolve("ui-sample","sample-io").Credential is null,"Expired credential must be unavailable without editing the clock or vault.");
        int expiryObservationStart=store.CredentialWaits.Count;
        await Crash();
        await BlockedWithoutAttempts(beforeExpiry,expiryObservationStart);
        var renewed=await Issue(DateTimeOffset.UtcNow.AddMinutes(30)); credentials[1]=renewed;
        await Recovered(beforeExpiry,2,renewed);
        await RejectOldToken(repaired);
        evidence.Add(new{test="expired-credential-repair-resumes-recovery",passed=true,blockedChecks=2,attemptsWhileBlocked=1,recoveredAttempt=2,currentCredentialVersion=renewed.Version,expiredTokenDenied=true,forcesRestored=false,outputsAtFallback=true});
        Console.WriteLine("PASS natural expiry: retry count retained across rechecks; recovery resumed with current credentials.");

        await ForcedOutputs();
        var beforeStop=store.Find(container!)!;
        await vaultRevoke(); await Crash();
        await Wait(()=>Task.FromResult(store.Find(container!)!.Outcome=="credential_unavailable"),"credential wait before Stop",15);
        var candidate=(await docker.LifecycleStatus(default)).Single();
        var formerDeadline=candidate.NextAttemptAt??throw new InvalidOperationException("Blocked credential deadline required.");
        Check(beforeStop.Attempts==store.Find(container!)!.Attempts,"Waiting must not spend the existing retry budget.");
        await host.ChangeState(container!,false);
        var stopped=store.Find(container!)!;
        Check(!stopped.DesiredRunning && stopped.RecoveryEnabled,"Stop must withdraw intent without disabling recovery policy.");
        var finalCredential=await Issue(DateTimeOffset.UtcNow.AddMinutes(30)); credentials[1]=finalCredential;
        var repairedAt=DateTimeOffset.UtcNow;
        var threshold=formerDeadline>repairedAt?formerDeadline:repairedAt;
        Console.WriteLine("Observing repaired credentials after intentional Stop, past the former retry deadline…");
        while(DateTimeOffset.UtcNow<threshold.AddSeconds(16))
        {
            var current=store.Find(container!)!;
            Check(!current.DesiredRunning && current.Attempts==0 && current.LastDeliveryId==stopped.LastDeliveryId && !(await Inspect()).GetProperty("State").GetProperty("Running").GetBoolean(),"Credential repair must never undo intentional Stop.");
            RequireCleared(); await Task.Delay(500);
        }
        await host.Recover(candidate);
        int stopPasses=healthEvidence.Count(h=>h.At>=threshold);
        Check(stopPasses>=3 && store.Attempts.Count==2 && !store.Find(container!)!.DesiredRunning && !(await Inspect()).GetProperty("State").GetProperty("Running").GetBoolean(),"Stop must survive current credentials, worker passes and stale recovery.");
        // Only explicit Start may resume; it must also resolve current vault credentials.
        await host.ChangeState(container!,true); await Healthy(); var started=await Fresh();
        VerifyDelivered(finalCredential);
        Check(store.Find(container!)!.Attempts==0 && started.Signals.All(s=>s.Force is null) && io.Activity("ui-sample").OutputsAtFallback,"Explicit Start after repair must begin cleared.");
        await host.ChangeState(container!,false);
        evidence.Add(new{test="repaired-credentials-preserve-stop",passed=true,postDeadlinePasses=stopPasses,currentCredentialVersion=finalCredential.Version,staleRecoveryDenied=true,explicitStartRequired=true,explicitStartUsedCurrentCredentials=true,leases=0,forces=0,writes=0,outputsAtFallback=true});
        Console.WriteLine("PASS Stop precedence: repair did not restart; explicit Start used current credentials with cleared controls.");

        async Task<string> FreshCode()
        {
            while(DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30<=lastCodeStep) await Task.Delay(250);
            lastCodeStep=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30;
            return Totp.Code(ownerSecret,lastCodeStep);
        }
        async Task vaultRevoke()=>vault.Revoke(owner,ownerPassword,await FreshCode(),"ui-sample","sample-io");
        async Task<ApplicationCredentialFile> Issue(DateTimeOffset expiry)
        {
            vault.Issue(owner,ownerPassword,await FreshCode(),"ui-sample","sample-io","platform",expiry,["io.read","io.write","io.simulate"]);
            return vault.Resolve("ui-sample","sample-io").Credential??throw new InvalidOperationException("Issued credential must resolve.");
        }
        async Task Crash()
        { using var killed=await engine.PostAsync($"/v1.47/containers/{container}/kill?signal=SIGKILL",null); killed.EnsureSuccessStatusCode(); }
        async Task<CredentialWaitObservation> BlockedWithoutAttempts(ManagedDeployment baseline,int observationStart)
        {
            await Wait(()=>Task.FromResult(store.CredentialWaits.Count>=observationStart+1),"credential wait state",15);
            int attempts=baseline.Attempts; int starts=store.Attempts.Count;
            var observationTimer=Stopwatch.StartNew();
            while(store.CredentialWaits.Count<observationStart+2)
            {
                Check(observationTimer.Elapsed<TimeSpan.FromSeconds(65),"Second credential recheck must arrive within the bounded observation window.");
                var current=store.Find(container!)!;
                Check(current.Attempts==attempts && current.LastDeliveryId==baseline.LastDeliveryId && store.Attempts.Count==starts && !(await Inspect()).GetProperty("State").GetProperty("Running").GetBoolean(),"Unavailable credentials must not consume retries or use retained startup material.");
                RequireCleared(); await Task.Delay(500);
            }
            var observations=store.CredentialWaits.ToArray(); var first=observations[observationStart]; var last=observations[observationStart+1];
            Check(first.Attempts==attempts && last.Attempts==attempts && last.ObservedAt>=first.NextCheckAt && Math.Abs((last.NextCheckAt-last.ObservedAt).TotalSeconds-30)<1,"Credential checks must retain count and real thirty-second cadence.");
            return last;
        }
        async Task Recovered(ManagedDeployment baseline,int attempts,ApplicationCredentialFile current)
        {
            await Wait(()=>Task.FromResult(store.Find(container!)!.LastDeliveryId!=baseline.LastDeliveryId),"automatic recovery after credential repair",75);
            await Healthy(); var state=await Fresh();
            VerifyDelivered(current);
            Check(store.Find(container!)!.Attempts==attempts && state.Signals.All(s=>s.Force is null) && state.PendingWrites.Length==0 && io.Activity("ui-sample").OutputsAtFallback,"Repaired recovery must spend exactly one attempt and start cleared.");
            using var stale=Client(3,baseline.LastDeliveryId!.Value);
            using var denied=await stale.GetAsync("api/apps/v1/engineering/watch");
            Check(denied.StatusCode==HttpStatusCode.Forbidden,"Old startup binding must be rejected even with unchanged valid engineering credentials.");
        }
        void VerifyDelivered(ApplicationCredentialFile current)
        {
            var delivered=store.Find(container!)!.Credentials.Single(c=>c.Id=="sample-io");
            Check(delivered.Version==current.Version && delivered.ExpiresAt==current.ExpiresAt && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(delivered.Token),Convert.FromHexString(current.Token)),"Actual broker delivery must use the current vault version/material.");
        }
        async Task RejectOldToken(ApplicationCredentialFile previous)
        {
            using var old=Client(1,store.Find(container!)!.LastDeliveryId!.Value); old.DefaultRequestHeaders.Authorization=new("Bearer",previous.Token);
            using var denied=await old.GetAsync("api/apps/v1/io/signals");
            Check(denied.StatusCode==HttpStatusCode.Unauthorized,"Previous credential token must fail independently of startup binding.");
        }
        void RequireCleared()=>Check(io.Activity("ui-sample") is {Leases:0,OutputsAtFallback:true} && engineering.Activity("ui-sample") is {Forces:0,Writes:0},"Unavailable/stopped resources must remain cleared.");
        async Task ForcedOutputs()
        {
            var state=await Fresh();
            await Command("engineering.force",new{instanceId=state.InstanceId,revision=state.Revision,signalId="output-bank",value=true,seconds=300});
            await Wait(()=>Task.FromResult(engineering.Activity("ui-sample").Forces==1 && !io.Activity("ui-sample").OutputsAtFallback),"active force before credential loss");
            var applied=(await Command("counter.increment",new{})).Data!.Value.GetProperty("shared").Deserialize<SampleConnectedState>(StateFiles.Json)!;
            Check(applied.Outputs.Length==3 && applied.Outputs.All(o=>o.Value && o.Commanded==true),"All three forced outputs must be confirmed before credential loss.");
        }
    }
    async Task ValidateWorkflow()
    {
        await Healthy(); await Fresh();
        Check(docker.InstallTrustSupport().Enabled && docker.UpdateSupport().Enabled && docker.BackupSupport().Enabled && docker.RemovalSupport().Enabled,"Discover all enabled workflow capabilities.");
        int shellPort=FreePort();
        reviewShell=new WorkflowReview(root,temp,shellPort,appPort,credentials[0],brokerSocket); reviewShell.Start();
        using var shell=new HttpClient{BaseAddress=new(reviewShell.Origin),Timeout=TimeSpan.FromSeconds(5)};
        await Wait(async()=>{try{using var response=await shell.GetAsync("/apps/ui-sample/main");return response.IsSuccessStatusCode;}catch(HttpRequestException){return false;}},"PiDockOS review shell",30);
        await reviewShell.Checkpoint("installed-and-registered");
        evidence.Add(new{test="approved-install-register-open",passed=true,unapprovedInstallDenied=true,registration="explicit-host-configuration",realPiDockShell=true,browserReviewed=true,version="one"});
        await Command("counter.reset",new{}); for(int i=0;i<3;i++)await Command("counter.increment",new{});
        var original=await State(); int originalCounter=original.GetProperty("counter").GetInt32();
        Check(original.GetProperty("version").GetString()=="one" && originalCounter==3,"Version one must persist the seeded counter.");
        var originalDelivery=store.Find(container!)!.LastDeliveryId;
        var replacement=manifest with{Image=secondImage!};
        await docker.ApproveInstall(new(replacement,"fixture-owner","Disposable compatible update",DateTimeOffset.UtcNow.AddHours(1)),default);
        Guid versionOperation=Guid.NewGuid();
        await docker.QueueUpdate(container!,new(versionOperation,pinned,secondImage!),default);
        await Wait(async()=>{var status=(await docker.UpdateStatus(default)).Single();await TrackVersions();return status.Phase=="committed";},"shared update worker promotion",90);
        await Active(); await Healthy(); var updated=await State();
        Check(updated.GetProperty("version").GetString()=="two" && updated.GetProperty("counter").GetInt32()==originalCounter && store.Find(container!)!.LastDeliveryId!=originalDelivery,"Update must change image/process, retain data and refresh authority.");
        Check((await docker.List(default)).Length==1 && (await Fresh()).Signals.All(s=>s.Force is null),"Only one logical active app and no restored forces.");
        await reviewShell.Checkpoint("updated-version-two");
        evidence.Add(new{test="update-retains-data-and-host-route",passed=true,image=secondImage,persistentCounter=originalCounter,newStartup=true,oneLogicalApp=true,browserReviewed=true});
        int beforeRollback=(await State()).GetProperty("counter").GetInt32();
        await docker.RequestRollback(container!,new(versionOperation),default);
        await Wait(async()=>{var status=(await docker.UpdateStatus(default)).Single();await TrackVersions();return status.Phase=="rolled_back";},"shared update worker rollback",90);
        await Active();await Healthy();var rolledBack=await State();
        Check(rolledBack.GetProperty("version").GetString()=="one" && rolledBack.GetProperty("counter").GetInt32()==beforeRollback,"Image rollback must preserve current data.");
        await reviewShell.Checkpoint("rolled-back-version-one");
        evidence.Add(new{test="image-rollback-retains-current-data",passed=true,image=pinned,persistentCounter=beforeRollback,browserReviewed=true});

        int snapshotCounter=(await State()).GetProperty("counter").GetInt32();
        await docker.PauseApplications(default); RequireCleared();
        var backup=await docker.CreateBackup(container!,new(Guid.NewGuid()),default);
        Check(backup.AppId=="ui-sample" && backup.Image==pinned && backup.Kind=="manual","Backup must bind the installed app/image.");
        await docker.ResumeApplications(default); await Healthy(); await Fresh();
        for(int i=0;i<7;i++)await Command("counter.increment",new{});
        int changedCounter=(await State()).GetProperty("counter").GetInt32(); Check(changedCounter==snapshotCounter+7,"Live data must differ from the snapshot.");
        await docker.PauseApplications(default); RequireCleared(); restoreOperation=Guid.NewGuid();
        await docker.RestoreBackup(container!,new(restoreOperation.Value,backup.Id),default);
        var restored=docker.DataRestoreStatus();
        Check(restored is {Phase:"completed",SafetySnapshotId:not null} && docker.OrchestrationStatus().Paused && !(await Inspect()).GetProperty("State").GetProperty("Running").GetBoolean(),"Restore must retain maintenance and safety snapshot.");
        var safety=backups.Read(restored!.SafetySnapshotId!.Value);
        Check(safety.Info.Kind=="pre_restore" && safety.Data.Entries.Any(e=>e.Path.EndsWith("counter.json",StringComparison.Ordinal)),"Safety snapshot must contain the previous live data.");
        await docker.ResumeApplications(default);await Healthy();var afterRestore=await State();
        Check(afterRestore.GetProperty("counter").GetInt32()==snapshotCounter && afterRestore.GetProperty("version").GetString()=="one" && (await Fresh()).Signals.All(s=>s.Force is null) && io.Activity("ui-sample").OutputsAtFallback,"Restore must restore data, not transient runtime authority.");
        await reviewShell.Checkpoint("restored-data");
        evidence.Add(new{test="backup-mutate-restore",passed=true,snapshotCounter,changedCounter,restoredCounter=snapshotCounter,safetySnapshot=true,maintenancePreserved=true,browserReviewed=true});

        await docker.PauseApplications(default); RequireCleared();
        Guid removal=Guid.NewGuid();await docker.RemoveApplication(container!,new(removal,pinned),default);
        Check(docker.RemovalStatus() is {Phase:"awaiting_external_cleanup"} && (await docker.List(default)).Length==0,"Runtime removal must await external registration/credential cleanup.");
        bool resumeDenied=false;try{await docker.ResumeApplications(default);}catch(InvalidOperationException){resumeDenied=true;}Check(resumeDenied,"Incomplete removal must block Resume.");
        reviewShell.Configure(false,includeCredential:false);
        await Wait(async()=>{using var response=await shell.GetAsync("/app-ui/ui-sample/main/index.html");return response.StatusCode==HttpStatusCode.NotFound;},"registration and bridge revocation",15);
        await reviewShell.Checkpoint("removed-and-registration-revoked");
        var users=api!.Services.GetRequiredService<UserStore>();var vault=api.Services.GetRequiredService<ApplicationCredentialVault>();
        string password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),secret=Totp.NewSecret();long step=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30-1;
        users.Add("workflow-owner",password,secret,"fixture","Owner",true,-1);
        var login=users.Authenticate("workflow-owner",password,Totp.Code(secret,step),DateTimeOffset.UtcNow);
        if(login is null){step=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30;login=users.Authenticate("workflow-owner",password,Totp.Code(secret,step),DateTimeOffset.UtcNow);}
        var owner=users.Principal(login??throw new InvalidOperationException("Disposable Owner authentication failed."));
        foreach(var credential in credentials)
        {
            while(DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30<=step)await Task.Delay(250);
            step=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30;vault.Revoke(owner,password,Totp.Code(secret,step),"ui-sample",credential.Id);
            Console.WriteLine("Revoked managed fixture credential "+credential.Id+" using fresh Owner confirmation.");
        }
        Check(vault.Read().Where(c=>c.AppId=="ui-sample").All(c=>c.Revoked),"All app credentials must be revoked.");
        StateFiles.WriteAtomic(Path.Combine(temp,"io-grants.json"),"{\"PiDock\":{\"Apps\":{\"Io\":{\"Grants\":[]}}}}");
        await Wait(()=>Task.FromResult((b.Configuration.GetSection("PiDock:Apps:Io:Grants").Get<ApplicationIoGrant[]>()??[]).Length==0),"I/O grant removal",15);
        RequireCleared();
        Check(users.IsCurrent(owner) && owner.IsInRole("Owner"),"Removal completion needs a current Owner identity.");
        await docker.CompleteRemoval(new(removal,owner.Identity!.Name!,true),default);
        Check(docker.RemovalStatus() is {Phase:"completed"} && docker.OrchestrationStatus().Paused && docker.Backups().Length==2,"Removal must complete without deleting snapshots or resuming apps.");
        using(var volume=await engine.GetAsync("/v1.47/volumes/pidock-app-ui-sample-data"))Check(volume.IsSuccessStatusCode,"Removal must retain the data volume.");
        evidence.Add(new{test="removal-and-external-cleanup",passed=true,registrationRevoked=true,bridgeCredentialRemoved=true,managedCredentialsRevoked=true,ioGrantsRemoved=true,runtimeAbsent=true,dataVolumeRetained=true,snapshotsRetained=true,maintenancePreserved=true,incompleteRemovalBlocksResume=true,browserReviewed=true});
        Console.WriteLine("PASS integrated app workflow: install/register/open/update/rollback/backup/restore/remove.");

        async Task Active(){container=(await docker.List(default)).Single().Id;ownedContainers.Add(container);network=(await Inspect()).GetProperty("HostConfig").GetProperty("NetworkMode").GetString();if(network is not null)ownedNetworks.Add(network);}
        Task TrackVersions(){foreach(var plan in updates.ReadAll()){ownedContainers.Add(plan.OriginalId);if(plan.CandidateId is not null)ownedContainers.Add(plan.CandidateId);if(plan.CandidateNetworkId is not null)ownedNetworks.Add(plan.CandidateNetworkId);if(plan.OriginalNetworkId is not null)ownedNetworks.Add(plan.OriginalNetworkId);}return Task.CompletedTask;}
        async Task<JsonElement> State()
        {
            using var response=await commands.SendAsync(new HttpRequestMessage(HttpMethod.Get,"events"),HttpCompletionOption.ResponseHeadersRead);response.EnsureSuccessStatusCode();
            using var reader=new StreamReader(await response.Content.ReadAsStreamAsync());var line=await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return JsonSerializer.Deserialize<ApplicationUiEvent>(line??"",StateFiles.Json)!.Data;
        }
        void RequireCleared()=>Check(io.Activity("ui-sample") is {Leases:0,OutputsAtFallback:true} && engineering.Activity("ui-sample") is {Forces:0,Writes:0},"Stopped app resources must be cleared.");
    }
}
finally
{
    // Only exact resources created by this run are removed. Never prune the user's Docker target.
    var errors=new List<Exception>();
    async Task Cleanup(Func<Task> work) { try { await work(); } catch(Exception e) { errors.Add(e); } }
    async Task Remove(string path) { using var response=await engine.DeleteAsync(path); Check(response.IsSuccessStatusCode || response.StatusCode==HttpStatusCode.NotFound,"Fixture cleanup failed: "+path); }
    if(reviewShell is not null)await Cleanup(async()=>await reviewShell.DisposeAsync());
    if(api is not null) await Cleanup(async()=>{try {await api.StopAsync();} finally {await api.DisposeAsync();}});
    if(coordinator is not null) await Cleanup(async()=>{try {await coordinator.StopAsync();} finally {await coordinator.DisposeAsync();}});
    if(workflowUpdates is not null)await Cleanup(()=>{foreach(var plan in workflowUpdates.ReadAll()){ownedContainers.Add(plan.OriginalId);if(plan.CandidateId is not null)ownedContainers.Add(plan.CandidateId);if(plan.OriginalNetworkId is not null)ownedNetworks.Add(plan.OriginalNetworkId);if(plan.CandidateNetworkId is not null)ownedNetworks.Add(plan.CandidateNetworkId);}return Task.CompletedTask;});
    if(container is not null)ownedContainers.Add(container);if(network is not null)ownedNetworks.Add(network);
    foreach(string id in ownedContainers)await Cleanup(()=>Remove($"/v1.47/containers/{id}?force=1"));
    foreach(string id in ownedNetworks)await Cleanup(()=>Remove($"/v1.47/networks/{id}"));
    if(restoreOperation is { } restore)foreach(string mode in new[]{"apply","finish"})await Cleanup(()=>Remove($"/v1.47/containers/pidock-data-{restore:N}-{mode}?force=1"));
    if(workflowVolumeOwned)await Cleanup(()=>Remove("/v1.47/volumes/pidock-app-ui-sample-data"));
    if(registryAttempted) await Cleanup(()=>Remove($"/v1.47/containers/{registry}?force=1&v=1"));
    if(imageId is not null) await Cleanup(async()=>{await Run("docker","image","rm","-f",imageId);});
    if(secondImageId is not null)await Cleanup(async()=>{await Run("docker","image","rm","-f",secondImageId);});
    if(helperImage is not null)await Cleanup(async()=>{await Run("docker","image","rm","-f",helperImage);});
    await Cleanup(()=>{Directory.Delete(temp,true);return Task.CompletedTask;});
    if(errors.Count>0) throw new AggregateException("Inspect this run's fixture resources; cleanup was incomplete.",errors);
}
static void Check(bool condition,string message) { if(!condition)throw new InvalidOperationException(message); }
static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
static async Task Wait(Func<Task<bool>> condition,string description,int seconds=15)
{ var timer=Stopwatch.StartNew();while(timer.Elapsed<TimeSpan.FromSeconds(seconds)){if(await condition())return;await Task.Delay(250);}throw new TimeoutException(description); }
static async Task<string> Run(string executable,params string[] arguments)
{
    var start=new ProcessStartInfo(executable){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false}; foreach(string argument in arguments)start.ArgumentList.Add(argument);
    using var process=Process.Start(start)??throw new IOException("Cannot start fixture tool."); var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(8));try{await process.WaitForExitAsync(timeout.Token);}catch{process.Kill(true);throw;}
    string output=await stdout,error=await stderr; if(process.ExitCode!=0)throw new IOException(executable+" failed: "+error+output);return output;
}

sealed record HealthObservation(DateTimeOffset At,string State,bool Ready,bool? Live,int Failures,string Reason,bool RecoveryRequired);
