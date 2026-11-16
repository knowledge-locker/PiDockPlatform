using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PiDock.Agent;
using PiDock.Cloud.Web;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationLibraryTests : IDisposable
{
    private readonly string root = StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-library-" + Guid.NewGuid()));
    private static ClaimsPrincipal User(string tenant = "alpha", string role = "Owner") => new(new ClaimsIdentity([
        new("tenant", tenant), new(ClaimTypes.Role, role), new(ClaimTypes.Name, "test-owner")], "test"));
    public static ApplicationReleaseRequest Definition(string version = "1.0.0")
    {
        var image = "registry.example.test/logic-engine@sha256:" + new string('a', 64);
        var runtime = new ApplicationManifest("logic-engine", image, 256, 0.5, Resources: new(PersistentData: true,
            Credentials: ["platform"], AutomaticRecovery: true, Health: new(), ManagedLifecycle: true));
        return new(1, new(1, "logic-engine", version, "v1", image, ["logs.write"]), "Initial simulation runtime",
            new(1, runtime, RequiredCapabilities: ["logs.write"]));
    }
    [Fact]
    public async Task CloudAndLocalRoundTripPreservesAllRuntimeSettingsAndDigest()
    {
        var catalog = new ApplicationCatalog(new(root), TimeProvider.System);
        var library = new ApplicationLibrary(catalog, new(new(root)));
        var definition = Definition();
        var release = library.Publish(User(), definition);
        using var downloaded = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(library.Find(User(), "logic-engine", "1.0.0")!.Package, StateFiles.Json));
        var local = await ApplicationDefinition.Read(downloaded);
        Assert.Equal(release.Digest, ApplicationContractRules.Digest(local));
        Assert.Equal(JsonSerializer.Serialize(definition.Installation, StateFiles.Json), JsonSerializer.Serialize(local.Installation, StateFiles.Json));
        Assert.True(local.Installation!.Runtime.Resources!.PersistentData);
        Assert.Equal("platform", Assert.Single(local.Installation.Runtime.Resources.CredentialIds));
        var restarted = new ApplicationCatalog(new(root), TimeProvider.System);
        Assert.Equal(release.Digest, Assert.Single(restarted.List("alpha")).Digest);
    }
    [Fact]
    public void LibraryEnforcesWorkspaceRoleIsolationAndImmutableVersions()
    {
        var library = new ApplicationLibrary(new(new(root), TimeProvider.System), new(new(root)));
        Assert.Throws<UnauthorizedAccessException>(() => library.Publish(User(role: "Viewer"), Definition()));
        var release = library.Publish(User(), Definition());
        Assert.Empty(library.List(User("other")));
        Assert.Null(library.Find(User("other"), "logic-engine", "1.0.0"));
        Assert.Single(library.List(User(role: "Viewer")));
        Assert.Equal(release.Digest, library.Publish(User(), Definition()).Digest);
        Assert.Throws<PlatformConflictException>(() => library.Publish(User(), Definition() with { ReleaseNotes = "Changed" }));
        Assert.Throws<PlatformConflictException>(() => library.Publish(User(), Definition() with { Installation = Definition().Installation! with { Runtime = Definition().Installation!.Runtime with { MemoryMiB = 512 } } }));
    }
    [Theory]
    [InlineData("unknown")][InlineData("duplicate")][InlineData("nested-duplicate")][InlineData("oversize")][InlineData("null")]
    public void LocalDefinitionRejectsAmbiguousUnknownOrOversizedInput(string kind)
    {
        var json = JsonSerializer.Serialize(Definition(), StateFiles.Json);
        json = kind switch {
            "unknown" => json.Insert(1, "\"shellCommand\":\"run\","),
            "duplicate" => json.Insert(1, "\"SchemaVersion\":1,"),
            "nested-duplicate" => json.Replace("\"memoryMiB\":256", "\"memoryMiB\":256,\"MemoryMiB\":512"),
            "oversize" => new string(' ', ApplicationDefinition.MaximumBytes + 1),
            _ => "null"
        };
        Assert.True(Record.Exception(() => ApplicationDefinition.Parse(Encoding.UTF8.GetBytes(json))) is ArgumentException or JsonException);
    }
    [Fact]
    public void DefinitionRequiresMatchingIdentityImageCapabilitiesAndInstallableRuntime()
    {
        var d = Definition();
        foreach (var bad in new[] { d with { Installation = null }, d with { Manifest = d.Manifest with { Id = "other" } },
            d with { Manifest = d.Manifest with { Image = "registry.example.test/other@sha256:" + new string('b',64) } },
            d with { Manifest = d.Manifest with { RequestedCapabilities = [] } },
            d with { Installation = d.Installation! with { Runtime = d.Installation.Runtime with { CpuLimit = 99 } } } })
            Assert.Throws<ArgumentException>(() => ApplicationDefinition.Validate(bad));
    }
    [Fact]
    public void LegacyReleaseJsonAndDigestStayUnchanged()
    {
        var legacy = Definition() with { Installation = null };
        var json = JsonSerializer.Serialize(legacy, StateFiles.Json);
        Assert.DoesNotContain("installation", json);
        var oldShape = new { legacy.SchemaVersion, legacy.Manifest, legacy.ReleaseNotes };
        var expected = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(oldShape, StateFiles.Json))).ToLowerInvariant();
        Assert.Equal(expected, ApplicationContractRules.Digest(legacy));
        ApplicationContractRules.Validate(legacy);
    }
    [Fact]
    public void InstallReceiptFitsBoundedTransportBeyondLegacyFourKiBLimit()
    {
        var definition = Definition() with { ReleaseNotes = new string('x', 4096) };
        var now = DateTimeOffset.UtcNow;
        var command = new ApplicationCommand(Guid.NewGuid(), "alpha", "pi5", "install", "", now, now.AddMinutes(2), "owner", definition);
        var receipt = new TelemetryReceipt(1, "alpha", "pi5", Guid.NewGuid(), command);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, StateFiles.Json);
        Assert.InRange(bytes.Length, 4097, ApplicationCommands.MaximumReceiptBytes);
        var restored = JsonSerializer.Deserialize<TelemetryReceipt>(bytes, StateFiles.Json)!;
        Assert.True(ApplicationCommands.Valid(restored.Command!, "alpha", "pi5", now));
    }
    private TelemetryEnvelope Frame(bool installEnabled = true)
    {
        var now = DateTimeOffset.UtcNow;
        var sample = new HostSnapshot(now, "test-pi", "Linux ARM64", 25, 8L << 30, 6L << 30, 45, 90000,
            DataPolicyCalculator.Evaluate(new(), new(32L << 30, 16L << 30, 0)), [], "Test clock");
        return new(1, "alpha", "pi5", Guid.NewGuid(), Guid.NewGuid(), 1, sample,
            Applications: new(now, false, "Ready", [], [], installEnabled));
    }
    [Fact]
    public void RemoteInstallRequiresWorkspaceRoleLivePermissionAndExactPublishedDefinition()
    {
        var fleet = new FleetStore(new(root)); var catalog = new ApplicationCatalog(new(root), TimeProvider.System);
        var library = new ApplicationLibrary(catalog, fleet); var release = library.Publish(User(), Definition());
        var commands = new ApplicationCommandStore(new(root), fleet, TimeProvider.System);
        var frame = Frame(false); fleet.Accept(IdentityRules.Topic("alpha", "pi5"), frame);
        Assert.Throws<InvalidOperationException>(() => commands.QueueInstall(User(), "pi5", release, library));
        frame = Frame(); fleet.Accept(IdentityRules.Topic("alpha", "pi5"), frame);
        Assert.Throws<UnauthorizedAccessException>(() => commands.QueueInstall(User(role: "Viewer"), "pi5", release, library));
        Assert.Throws<ArgumentException>(() => commands.QueueInstall(User("other"), "pi5", release, library));
        var command = commands.QueueInstall(User(), "pi5", release, library);
        Assert.Equal(release.Digest, ApplicationContractRules.Digest(command.Definition!));
        Assert.True(ApplicationCommands.Valid(command, "alpha", "pi5", DateTimeOffset.UtcNow));
        Assert.False(ApplicationCommands.Valid(command with { Action = "restart" }, "alpha", "pi5", DateTimeOffset.UtcNow));
        Assert.Null(commands.ForReceipt(frame with { IsReplay = true }));
        Assert.Equal(command.Id, commands.ForReceipt(frame)!.Id);
        Assert.Throws<InvalidOperationException>(() => commands.QueueInstall(User(), "pi5", release, library));
    }
    [Fact]
    public async Task RemoteInstallUsesSeparateOptInAndDoesNotReplayAfterRestart()
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["PiDock:StateDirectory"] = root, ["Mqtt:Enabled"] = "true", ["Mqtt:Host"] = "broker", ["Mqtt:TenantId"] = "alpha", ["Mqtt:DeviceId"] = "pi5"
        }).Build();
        using var mqtt = new MqttConnection(settings); var runtime = new Runtime();
        using var agent = new RemoteApplicationAgent(settings, runtime, mqtt, TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var command = new ApplicationCommand(Guid.NewGuid(), "alpha", "pi5", "install", "", now, now.AddMinutes(2), "owner", Definition());
        agent.SetEnabled(true); await agent.ProcessAsync(command); Assert.Equal(0, runtime.Installs);
        agent.SetInstallEnabled(true); command = command with { Id = Guid.NewGuid() };
        await agent.ProcessAsync(command); await agent.ProcessAsync(command); Assert.Equal(1, runtime.Installs);
        using var restarted = new RemoteApplicationAgent(settings, runtime, mqtt, TimeProvider.System);
        Assert.True(restarted.Current.InstallEnabled); await restarted.ProcessAsync(command); Assert.Equal(1, runtime.Installs);
        restarted.SetEnabled(false); Assert.False(restarted.Current.InstallEnabled);
        await restarted.ProcessAsync(command with { Id = Guid.NewGuid() }); Assert.Equal(1, runtime.Installs);
    }
    private sealed class Runtime : IApplicationRuntime
    {
        public bool Configured => true; public int Installs;
        public Task<ApplicationSummary[]> List(CancellationToken ct = default) => Task.FromResult<ApplicationSummary[]>([]);
        public Task ChangeState(string id, bool start, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task Restart(string id, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task DeployDefinition(ApplicationReleaseRequest definition, CancellationToken ct = default) { ApplicationDefinition.Validate(definition); Installs++; return Task.CompletedTask; }
    }
    public void Dispose() => Directory.Delete(root, true);
}
