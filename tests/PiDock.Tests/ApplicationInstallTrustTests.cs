using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Appliance.Broker;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationInstallTrustTests
{
    private static readonly ApplicationManifest Manifest = new("approved-app", "registry.example/app@sha256:" + new string('a', 64));
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Store : IApplicationInstallTrustStore
    {
        public ApplicationInstallApproval[] Values = [];
        public ApplicationInstallApproval[] Read() => Values;
        public void Save(ApplicationInstallApproval approval) => Values = Values.Where(v => v.ManifestSha256 != approval.ManifestSha256).Append(approval).ToArray();
    }
    private static ApplicationInstallApproval Approval(Clock clock) => new(ApplicationInstallTrustRules.ManifestHash(Manifest), Manifest.Name,
        Manifest.Image, "owner", "Reviewed local fixture image and resource declarations", clock.Now, clock.Now.AddHours(1));

    [Fact]
    public async Task UnapprovedExpiredRevokedAndChangedSettingsCannotPullOrCreate()
    {
        var clock = new Clock(); var store = new Store();
        using var engine = new Engine(); using var client = new HttpClient(engine) { BaseAddress = new("http://localhost") };
        var docker = new DockerOperations(client, clock: clock, installTrustEnabled: true, installTrustStore: store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => docker.Deploy(Manifest, default));
        store.Save(Approval(clock) with { Revoked = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => docker.Deploy(Manifest, default));
        store.Save(Approval(clock) with { ApprovedAt = clock.Now.AddHours(-2), ExpiresAt = clock.Now.AddHours(-1) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => docker.Deploy(Manifest, default));
        store.Save(Approval(clock));
        await Assert.ThrowsAsync<InvalidOperationException>(() => docker.Deploy(Manifest with { MemoryMiB = 256 }, default));
        Assert.Equal(0, engine.Pulls); Assert.Equal(0, engine.Networks); Assert.Equal(0, engine.Containers);
    }

    [Fact]
    public async Task ApprovalIsRecheckedAfterPullAndArchitectureBeforeResourceCreation()
    {
        var clock = new Clock(); var store = new Store(); store.Save(Approval(clock));
        using var engine = new Engine { OnPull = () => store.Save(Approval(clock) with { Revoked = true }) };
        using var client = new HttpClient(engine) { BaseAddress = new("http://localhost") };
        var docker = new DockerOperations(client, clock: clock, installTrustEnabled: true, installTrustStore: store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => docker.Deploy(Manifest, default));
        Assert.Equal(1, engine.Pulls); Assert.Equal(0, engine.Networks);
        store.Save(Approval(clock)); engine.OnPull = null; engine.Architecture = "amd64";
        await Assert.ThrowsAsync<InvalidOperationException>(() => docker.Deploy(Manifest, default));
        Assert.Equal(0, engine.Networks); Assert.Equal(0, engine.Containers);
        engine.Architecture = "arm64";
        Assert.Equal(new string('b', 64), await docker.Deploy(Manifest, default));
        Assert.Equal(1, engine.Networks); Assert.Equal(1, engine.Containers);
    }

    [Fact]
    public async Task BrokerApprovalsRequireExplicitReviewAndBoundedLifetime()
    {
        var clock = new Clock(); var store = new Store();
        using var engine = new Engine(); using var client = new HttpClient(engine) { BaseAddress = new("http://localhost") };
        var docker = new DockerOperations(client, clock: clock, installTrustEnabled: true, installTrustStore: store);
        await Assert.ThrowsAsync<ArgumentException>(() => docker.ApproveInstall(new(Manifest, "owner", "", clock.Now.AddHours(1)), default));
        await Assert.ThrowsAsync<ArgumentException>(() => docker.ApproveInstall(new(Manifest, "owner", "Reviewed", clock.Now.AddHours(25)), default));
        var approved = await docker.ApproveInstall(new(Manifest, "owner", "Reviewed", clock.Now.AddHours(1)), default);
        Assert.Equal(ApplicationInstallTrustRules.ManifestHash(Manifest), approved.ManifestSha256);
        await docker.RevokeInstallApproval(approved.ManifestSha256, default);
        Assert.True(Assert.Single(docker.InstallApprovals()).Revoked);
        Assert.Contains(docker.InstallTrustSupport().Limitations, value => value.Contains("signatures are not verified", StringComparison.Ordinal));
    }

    [Fact]
    public void ApprovalStoreIsProtectedAndRejectsCopiedOrCorruptMetadata()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-install-trust-" + Guid.NewGuid());
        try
        {
            var protection = new EphemeralDataProtectionProvider(); var clock = new Clock();
            var store = new ApplicationInstallTrustStore(protection, root); var approval = Approval(clock);
            store.Save(approval); Assert.Equal(approval, Assert.Single(store.Read()));
            var file = Directory.GetFiles(root).Single(); Assert.DoesNotContain("Reviewed local fixture", File.ReadAllText(file));
            var copy = Path.Combine(root, new string('f', 64) + ".protected"); File.Copy(file, copy);
            Assert.Throws<IOException>(() => store.Read()); File.Delete(copy);
            File.WriteAllText(file, "corrupt"); Assert.Throws<IOException>(() => store.Read());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Engine : HttpMessageHandler
    {
        public int Pulls, Networks, Containers;
        public string Architecture = "arm64";
        public Action? OnPull;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = "{}";
            if (path.EndsWith("/containers/json", StringComparison.Ordinal)) body = "[]";
            else if (path.EndsWith("/images/create", StringComparison.Ordinal)) { Pulls++; OnPull?.Invoke(); }
            else if (path.Contains("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal)) body = "{\"Os\":\"linux\",\"Architecture\":\"" + Architecture + "\"}";
            else if (path.EndsWith("/networks/create", StringComparison.Ordinal)) { Networks++; body = "{\"Id\":\"network\"}"; }
            else if (path.EndsWith("/containers/create", StringComparison.Ordinal)) { Containers++; body = "{\"Id\":\"" + new string('b', 64) + "\"}"; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
