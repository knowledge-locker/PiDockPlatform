using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PiDock.Agent;
using PiDock.Cloud.Web;
using PiDock.Core;
using PiDock.Updater;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class OsUpdateTests : IDisposable
{
    private readonly string root = StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-updates-" + Guid.NewGuid()));
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string Trust => Path.Combine(root, "trust");
    private string Installed => Path.Combine(root, "installed");
    private string State => Path.Combine(root, "updater");
    private static ClaimsPrincipal User(string tenant = "alpha", string role = "Owner") => new(new ClaimsIdentity([new(ClaimTypes.Name, "owner"), new(ClaimTypes.Role, role), new("tenant", tenant)], "test"));
    private string Package(string version = "1.2.3", string runtime = "linux-arm64", string? name = null)
    {
        StateFiles.PrepareDirectory(Trust); StateFiles.WriteAtomic(Path.Combine(Trust, "release.pem"), key.ExportSubjectPublicKeyInfoPem());
        string files = StateFiles.PrepareDirectory(Path.Combine(root, "files"));
        foreach (string part in new[] { "appliance", "broker" })
        {
            string dir = StateFiles.PrepareDirectory(Path.Combine(files, part));
            File.WriteAllText(Path.Combine(dir, part == "appliance" ? "PiDock.Appliance.Web.dll" : "PiDock.Appliance.Broker.dll"), version + "-" + part);
        }
        string output = Path.Combine(root, name ?? Guid.NewGuid() + ".zip");
        OsUpdatePackage.Create(files, output, version, runtime, "release", key.ExportPkcs8PrivateKeyPem()); return output;
    }
    private UpdateEngine Engine(IUpdateHost host)
    {
        foreach (string part in new[] { "appliance", "broker" })
        {
            string dir = StateFiles.PrepareDirectory(Path.Combine(Installed, part));
            string file = Path.Combine(dir, "old.txt"); if (!File.Exists(file)) File.WriteAllText(file, "previous-" + part);
        }
        return new(State, Installed, Trust, "linux-arm64", host);
    }
    private async Task<OsRelease> Stage(UpdateEngine engine, string package) { await using var input = File.OpenRead(package); return await engine.Stage(input); }
    [Fact]
    public void PackageVerifiesEveryFileAndIndependentTrustAndTarget()
    {
        string package = Package(); var release = OsUpdatePackage.Verify(package, Trust, "linux-arm64");
        Assert.Equal("1.2.3", release.Manifest.Version); Assert.True(OsUpdateRules.Digest(release.Digest));
        Assert.Throws<InvalidDataException>(() => OsUpdatePackage.Verify(package, Trust, "linux-x64"));
        string emptyTrust = StateFiles.PrepareDirectory(Path.Combine(root, "empty-trust"));
        Assert.Throws<InvalidDataException>(() => OsUpdatePackage.Verify(package, emptyTrust));
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Update)) { zip.GetEntry("appliance/PiDock.Appliance.Web.dll")!.Delete(); using var output = zip.CreateEntry("appliance/PiDock.Appliance.Web.dll").Open(); output.Write(Encoding.UTF8.GetBytes("tampered")); }
        Assert.Throws<InvalidDataException>(() => OsUpdatePackage.Verify(package, Trust));
    }
    [Theory]
    [InlineData("extra-file")][InlineData("duplicate")][InlineData("signature")][InlineData("link")]
    public void InvalidPackageLayoutsAndSignaturesAreRejected(string kind)
    {
        string package = Package();
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            if (kind == "extra-file") zip.CreateEntry("../escape");
            if (kind == "duplicate") zip.CreateEntry("MANIFEST.JSON");
            if (kind == "signature") { zip.GetEntry("signature.bin")!.Delete(); using var output = zip.CreateEntry("signature.bin").Open(); output.Write(new byte[64]); }
            if (kind == "link") zip.GetEntry("appliance/PiDock.Appliance.Web.dll")!.ExternalAttributes = 0xa1ff << 16;
        }
        Assert.Throws<InvalidDataException>(() => OsUpdatePackage.Verify(package, Trust));
    }
    [Fact]
    public async Task LocalInstallNeedsNoCloudPermissionAndCommitsOnlyAfterHealth()
    {
        string package = Package(); var host = new Host(); var engine = Engine(host); var release = await Stage(engine, package);
        Assert.False(engine.Snapshot.CloudEnabled);
        var request = new OsUpdateRequest(Guid.NewGuid(), release.Digest, "local", "owner");
        await engine.Install(request); await engine.RunPending();
        Assert.Equal("Healthy", engine.Snapshot.Operation!.State); Assert.Equal("1.2.3", engine.Snapshot.InstalledVersion);
        Assert.Equal(release.Digest, Assert.Single(host.Checked)); Assert.Equal(1, host.Starts);
        Assert.Equal("1.2.3-appliance", File.ReadAllText(Path.Combine(Installed, "appliance", "PiDock.Appliance.Web.dll")));
        Assert.Equal("previous-appliance", File.ReadAllText(Path.Combine(Installed, ".pidock-update-" + request.Id.ToString("N"), "previous-appliance", "old.txt")));
        var restarted = new UpdateEngine(State, Installed, Trust, "linux-arm64", host);
        await restarted.Recover(); await restarted.Install(request); await restarted.RunPending();
        Assert.Equal(1, host.Starts); Assert.Equal("Healthy", restarted.Snapshot.Operation!.State);
    }
    [Fact]
    public async Task CloudPermissionIsSeparatePersistentAndRecheckedBeforeReplacement()
    {
        string package = Package(); var host = new Host(); var engine = Engine(host); var release = await Stage(engine, package);
        var request = new OsUpdateRequest(Guid.NewGuid(), release.Digest, "cloud", "cloud-owner");
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Install(request));
        await engine.SetCloudEnabled(true);
        Assert.True(new UpdateEngine(State, Installed, Trust, "linux-arm64", host).Snapshot.CloudEnabled);
        await engine.Install(request); await engine.SetCloudEnabled(false); await engine.RunPending();
        Assert.Equal("Rejected", engine.Snapshot.Operation!.State); Assert.Equal(0, host.Starts);
    }
    [Fact]
    public async Task FailedHealthRestoresBothServicesAndPreservesPersistentState()
    {
        string package = Package(); var host = new Host { FailCandidate = true }; var engine = Engine(host); var release = await Stage(engine, package);
        string account = Path.Combine(root, "accounts.json"); File.WriteAllText(account, "keep this state");
        await engine.Install(new(Guid.NewGuid(), release.Digest, "local", "owner")); await engine.RunPending();
        Assert.Equal("RolledBack", engine.Snapshot.Operation!.State); Assert.Null(engine.Snapshot.InstalledVersion);
        Assert.Equal(2, host.Starts); Assert.Equal(new string?[] { release.Digest, null }, host.Checked);
        foreach (string part in new[] { "appliance", "broker" }) Assert.Equal("previous-" + part, File.ReadAllText(Path.Combine(Installed, part, "old.txt")));
        Assert.Equal("keep this state", File.ReadAllText(account));
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    public async Task RestartRecoversEveryPartiallySwitchedDirectoryState(int moves)
    {
        string package = Package(); var host = new Host(); var engine = Engine(host); var release = await Stage(engine, package);
        var op = await engine.Install(new(Guid.NewGuid(), release.Digest, "local", "owner"));
        string transaction = StateFiles.PrepareDirectory(Path.Combine(Installed, ".pidock-update-" + op.Id.ToString("N")));
        string next = StateFiles.PrepareDirectory(Path.Combine(transaction, "next"));
        foreach (string part in new[] { "appliance", "broker" }) { string dir = StateFiles.PrepareDirectory(Path.Combine(next, part)); File.WriteAllText(Path.Combine(dir, "candidate.txt"), "candidate"); }
        if (moves >= 1) StateFiles.MoveDirectory(Path.Combine(Installed, "appliance"), Path.Combine(transaction, "previous-appliance"));
        if (moves >= 2) StateFiles.MoveDirectory(Path.Combine(next, "appliance"), Path.Combine(Installed, "appliance"));
        if (moves >= 3) StateFiles.MoveDirectory(Path.Combine(Installed, "broker"), Path.Combine(transaction, "previous-broker"));
        if (moves >= 4) StateFiles.MoveDirectory(Path.Combine(next, "broker"), Path.Combine(Installed, "broker"));
        StateFiles.WriteAtomic(Path.Combine(State, "state.json"), JsonSerializer.Serialize(new UpdateState(false, null, release, [op with { State = "Installing" }]), StateFiles.Json));
        var restarted = new UpdateEngine(State, Installed, Trust, "linux-arm64", host); await restarted.Recover();
        Assert.Equal("RolledBack", restarted.Snapshot.Operation!.State);
        foreach (string part in new[] { "appliance", "broker" }) Assert.True(File.Exists(Path.Combine(Installed, part, "old.txt")));
    }
    [Fact]
    public async Task CorruptedStagingAndConcurrentRequestsNeverReplaceInstalledPrograms()
    {
        string package = Package(); var host = new Host(); var engine = Engine(host); var release = await Stage(engine, package);
        var request = new OsUpdateRequest(Guid.NewGuid(), release.Digest, "local", "owner");
        await engine.Install(request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Install(request with { Id = Guid.NewGuid() }));
        await using (var input = File.OpenRead(package)) await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Stage(input));
        File.WriteAllText(Path.Combine(State, "package.zip"), "damaged");
        await engine.RunPending(); Assert.Equal("Failed", engine.Snapshot.Operation!.State); Assert.Equal(0, host.Starts);
        Assert.True(File.Exists(Path.Combine(Installed, "appliance", "old.txt")));
    }
    [Fact]
    public async Task RemovingSignerTrustAfterUploadBlocksInstallationBeforeServicesStop()
    {
        string package = Package(); var host = new Host(); var engine = Engine(host); var release = await Stage(engine, package);
        File.Delete(Path.Combine(Trust, "release.pem"));
        await engine.Install(new(Guid.NewGuid(), release.Digest, "local", "owner")); await engine.RunPending();
        Assert.Equal("Failed", engine.Snapshot.Operation!.State); Assert.Equal(0, host.Starts);
        Assert.True(File.Exists(Path.Combine(Installed, "appliance", "old.txt")));
    }
    [Fact]
    public async Task UnhealthyPreviousServicesRetainRecoveryJournalAndBlockFurtherInstalls()
    {
        string package = Package(); var host = new Host { FailAll = true }; var engine = Engine(host); var release = await Stage(engine, package);
        await engine.Install(new(Guid.NewGuid(), release.Digest, "local", "owner")); await engine.RunPending();
        Assert.Equal("RecoveryFailed", engine.Snapshot.Operation!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Install(new(Guid.NewGuid(), release.Digest, "local", "owner")));
        host.FailAll = false;
        var restarted = new UpdateEngine(State, Installed, Trust, "linux-arm64", host); await restarted.Recover();
        Assert.Equal("RolledBack", restarted.Snapshot.Operation!.State);
    }
    [Fact]
    public async Task CloudCatalogCommandsDownloadsAndResultsAreTenantScopedAndRestartSafe()
    {
        string package = Package(); var cloudRoot = StateFiles.PrepareDirectory(Path.Combine(root, "cloud"));
        var fleet = new FleetStore(new(cloudRoot)); var clock = TimeProvider.System;
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Updates:TrustDirectory"] = Trust }).Build();
        var store = new OsUpdateStore(new(cloudRoot), fleet, clock, settings);
        PublishedOsRelease published; await using (var input = File.OpenRead(package)) published = await store.Publish(User(), input);
        Assert.Empty(store.Releases(User("other")));
        var now = DateTimeOffset.UtcNow; var sample = new HostSnapshot(now, "pi", "Linux", 10, 100, 50, 40, 10, DataPolicyCalculator.Evaluate(new(), new(32L << 30, 16L << 30, 0)), [], "test");
        var status = new OsUpdateSnapshot(now, true, true, "linux-arm64", null, null, null, null, "Ready");
        var frame = new TelemetryEnvelope(1, "alpha", "pi", Guid.NewGuid(), Guid.NewGuid(), 1, sample, OsUpdates: status);
        Assert.True(fleet.Accept(IdentityRules.Topic("alpha", "pi"), frame).Stored);
        Assert.Throws<UnauthorizedAccessException>(() => store.Queue(User(role: "Viewer"), "pi", published.Release.Digest));
        Assert.Throws<UnauthorizedAccessException>(() => store.Queue(User("other"), "pi", published.Release.Digest));
        var command = store.Queue(User(), "pi", published.Release.Digest);
        Assert.Equal(command.Id, store.ForReceipt(frame)!.Id);
        Assert.Null(store.ForReceipt(frame with { IsReplay = true })); Assert.Null(store.ForReceipt(frame with { TenantId = "other" }));
        Assert.Null(store.Download(command.Id, new string('0', 64))); Assert.NotNull(store.Download(command.Id, command.DownloadToken));
        Assert.Throws<InvalidOperationException>(() => store.Queue(User(), "pi", published.Release.Digest));
        var result = new OsUpdateOperation(command.Id, command.Digest, "1.2.3", "Healthy", "Ready", now.AddSeconds(1));
        store.AcceptStatus(frame with { TenantId = "other", OsUpdates = status with { Operation = result } }); Assert.Null(Assert.Single(store.Operations(User(), "pi")).Operation);
        store.AcceptStatus(frame with { OsUpdates = status with { Operation = result } });
        Assert.Equal("Healthy", Assert.Single(new OsUpdateStore(new(cloudRoot), fleet, clock, settings).Operations(User(), "pi")).Operation!.State);
        Assert.Null(store.ForReceipt(frame));
        var offFrame = frame with { MessageId = Guid.NewGuid(), Sequence = 2, OsUpdates = status with { CloudEnabled = false } };
        fleet.Accept(IdentityRules.Topic("alpha", "pi"), offFrame);
        Assert.Throws<InvalidOperationException>(() => store.Queue(User(), "pi", published.Release.Digest));
    }
    [Fact]
    public async Task CloudReceiptDownloadInstallationAndStatusCompleteThroughTheAgent()
    {
        string package = Package(); var host = new Host(); var engine = Engine(host); await engine.SetCloudEnabled(true);
        string cloudRoot = StateFiles.PrepareDirectory(Path.Combine(root, "cloud-roundtrip")); var fleet = new FleetStore(new(cloudRoot));
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Updates:TrustDirectory"] = Trust, ["Mqtt:TenantId"] = "alpha", ["Mqtt:DeviceId"] = "pi" }).Build();
        var store = new OsUpdateStore(new(cloudRoot), fleet, TimeProvider.System, settings);
        PublishedOsRelease published; await using (var input = File.OpenRead(package)) published = await store.Publish(User(), input);
        string agentState = StateFiles.PrepareDirectory(Path.Combine(root, "agent"));
        StateFiles.WriteAtomic(Path.Combine(agentState, "cloud-connection.json"), JsonSerializer.Serialize(new CloudConnection("https://registered-cloud.example/", "alpha", "pi", "unused", "unused", "unused", "mqtt", 8883, DateTimeOffset.UtcNow.AddDays(1)), StateFiles.Json));
        using var mqtt = new MqttConnection(settings);
        var updater = new EngineClient(engine);
        using var agent = new OsUpdateAgent(updater, new(agentState, mqtt), mqtt, agentState, _ => new HttpClient(new DownloadHandler(store)));
        await agent.Refresh();
        var now = DateTimeOffset.UtcNow;
        var sample = new HostSnapshot(now, "pi", "Linux", 10, 100, 50, 40, 10, DataPolicyCalculator.Evaluate(new(), new(32L << 30, 16L << 30, 0)), [], "test");
        var frame = new TelemetryEnvelope(1, "alpha", "pi", Guid.NewGuid(), Guid.NewGuid(), 1, sample, OsUpdates: agent.Current);
        fleet.Accept(IdentityRules.Topic("alpha", "pi"), frame);
        var command = store.Queue(User(), "pi", published.Release.Digest);
        // Exercise the same receipt serialization used on the MQTT wire.
        var receipt = JsonSerializer.Deserialize<TelemetryReceipt>(JsonSerializer.Serialize(new TelemetryReceipt(1, "alpha", "pi", frame.MessageId, OsUpdate: store.ForReceipt(frame)), StateFiles.Json), StateFiles.Json)!;
        Assert.NotNull(receipt.OsUpdate);
        await agent.Process(receipt.OsUpdate!);
        Assert.Equal("Accepted", engine.Snapshot.Operation!.State);
        await engine.RunPending(); await agent.Refresh();
        store.AcceptStatus(frame with { OsUpdates = agent.Current });
        Assert.Equal("Healthy", Assert.Single(store.Operations(User(), "pi")).Operation!.State);
        // Repeated commands, including after an agent restart, never reinstall the accepted request.
        using var restarted = new OsUpdateAgent(updater, new(agentState, mqtt), mqtt, agentState, _ => throw new InvalidOperationException("Duplicate command must not download"));
        await restarted.Process(command); Assert.Equal(1, host.Starts);
        await restarted.Process(command with { Id = Guid.NewGuid(), TenantId = "other" }); Assert.Equal(1, host.Starts);
        await restarted.Process(command with { Id = Guid.NewGuid(), ExpiresAt = now.AddSeconds(-1) }); Assert.Equal(1, host.Starts);
    }
    private sealed class EngineClient(UpdateEngine engine) : IOsUpdater
    {
        public bool Configured => true;
        public Task<OsUpdateSnapshot> Status(CancellationToken ct = default) => Task.FromResult(engine.Snapshot);
        public Task SetCloudEnabled(bool enabled, CancellationToken ct = default) => engine.SetCloudEnabled(enabled, ct);
        public async Task<OsReleaseInfo> Stage(Stream input, CancellationToken ct = default) => (await engine.Stage(input, ct)).Info;
        public Task<OsUpdateOperation> Install(OsUpdateRequest request, CancellationToken ct = default) => engine.Install(request, ct);
    }
    private sealed class DownloadHandler(OsUpdateStore store) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https", request.RequestUri!.Scheme); Assert.Equal("registered-cloud.example", request.RequestUri.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            string? file = store.Download(Guid.Parse(request.RequestUri.Segments[^1]), request.Headers.Authorization.Parameter!);
            Assert.NotNull(file);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(file)) });
        }
    }
    [Fact]
    public async Task CloudDownloadEndpointRequiresItsScopedCapabilityAndHasNoBrowserSessionRequirement()
    {
        string package = Package(); string cloudRoot = StateFiles.PrepareDirectory(Path.Combine(root, "http-cloud"));
        using var factory = new CloudFactory(cloudRoot, Trust);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(System.Net.HttpStatusCode.Redirect, (await client.GetAsync("/os-updates")).StatusCode);
        var store = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<OsUpdateStore>(factory.Services);
        var fleet = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<FleetStore>(factory.Services);
        PublishedOsRelease published; await using (var input = File.OpenRead(package)) published = await store.Publish(User(), input);
        var now = DateTimeOffset.UtcNow;
        var sample = new HostSnapshot(now, "pi", "Linux", 10, 100, 50, 40, 10, DataPolicyCalculator.Evaluate(new(), new(32L << 30, 16L << 30, 0)), [], "test");
        var frame = new TelemetryEnvelope(1, "alpha", "pi", Guid.NewGuid(), Guid.NewGuid(), 1, sample,
            OsUpdates: new(now, true, true, "linux-arm64", null, null, null, null, "Ready"));
        fleet.Accept(IdentityRules.Topic("alpha", "pi"), frame);
        var command = store.Queue(User(), "pi", published.Release.Digest);
        string endpoint = "/api/os-updates/download/" + command.Id;
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync(endpoint)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", new string('0', 64));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync(endpoint)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", command.DownloadToken);
        var response = await client.GetAsync(endpoint);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(published.Release.Digest, Convert.ToHexStringLower(SHA256.HashData(await response.Content.ReadAsByteArrayAsync())));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/api/os-updates/download/" + Guid.NewGuid())).StatusCode);
        await using (var input = File.OpenRead(package)) Assert.Equal(published.Release.Digest, (await store.Publish(User(), input)).Release.Digest);
        string changed = Package(name: "changed.zip");
        await using (var input = File.OpenRead(changed)) await Assert.ThrowsAsync<InvalidOperationException>(() => store.Publish(User(), input));
    }
    private sealed class CloudFactory(string directory, string trust) : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<CloudMarker>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseEnvironment(builder, "Development");
            builder.UseSetting("PiDock:StateDirectory", directory).UseSetting("Updates:TrustDirectory", trust).UseSetting("Logging:LogLevel:Default", "Warning");
        }
    }
    [Fact]
    public void OptionalUpdateMetadataPreservesLegacyCanonicalTelemetry()
    {
        Assert.DoesNotContain("osUpdate", JsonSerializer.Serialize(new TelemetryReceipt(1, "alpha", "pi", Guid.NewGuid()), StateFiles.Json));
    }
    public void Dispose() { key.Dispose(); Directory.Delete(root, true); }
    private sealed class Host : IUpdateHost
    {
        public int Starts; public bool FailCandidate, FailAll; public List<string?> Checked = [];
        public Task Stop(CancellationToken ct) => Task.CompletedTask;
        public Task Start(CancellationToken ct) { Starts++; return Task.CompletedTask; }
        public Task<bool> Healthy(string? digest, CancellationToken ct) { Checked.Add(digest); return Task.FromResult(!FailAll && !(FailCandidate && digest is not null)); }
    }
}
