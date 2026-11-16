using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Appliance.Broker;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationRemovalTests
{
    [Fact]
    public async Task RemovalRetainsDataAndRequiresExternalCleanupBeforeExplicitResume()
    {
        using var f = new Fixture(); var request = new ApplicationRemovalRequest(Guid.NewGuid(), f.Manifest.Image);
        await f.Docker.RemoveApplication(f.Id, request, default);
        Assert.Empty(await f.Docker.List(default)); Assert.Null(f.Apps.Find(f.Id));
        var status = f.Removals.Read()!; Assert.Equal("awaiting_external_cleanup", status.Phase);
        Assert.Contains(status.RemainingActions, item => item.Contains("credentials", StringComparison.Ordinal));
        Assert.False(f.Docker.RemovalSupport().DataDeletion); Assert.True(f.Docker.OrchestrationStatus().Paused);
        Assert.Equal(["container:" + f.Id, "network:" + f.Network], f.Engine.Deleted);
        Assert.DoesNotContain(f.Engine.Requests, p => p.Contains("/volumes/", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Docker.ResumeApplications(default));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Docker.CompleteRemoval(new(request.OperationId, "owner", false), default));
        await f.Docker.CompleteRemoval(new(request.OperationId, "owner", true), default);
        Assert.Equal("completed", f.Removals.Read()!.Phase); Assert.Equal("owner", f.Removals.Read()!.CompletedBy);
        Assert.True(f.Docker.OrchestrationStatus().Paused); // Completion does not itself resume apps.
        await f.Docker.RemoveApplication(f.Id, request, default); // Matching operation is idempotent.
        await Assert.ThrowsAsync<ArgumentException>(() => f.Docker.RemoveApplication(f.Id, request with { ExpectedImage = "other" }, default));
        await f.Docker.ResumeApplications(default); Assert.False(f.Docker.OrchestrationStatus().Paused);
    }

    [Fact]
    public async Task InterruptedDeletionResumesFromProtectedJournalAndDisabledFlagCannotBypassBarrier()
    {
        using var f = new Fixture(); var request = new ApplicationRemovalRequest(Guid.NewGuid(), f.Manifest.Image);
        f.Engine.FailNetworkDelete = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Docker.RemoveApplication(f.Id, request, default));
        Assert.Equal("removing", f.Removals.Read()!.Phase); Assert.Equal("interrupted", f.Removals.Read()!.Outcome);
        Assert.Null(f.Engine.Containers.GetValueOrDefault(f.Id)); Assert.NotNull(f.Apps.Find(f.Id));
        var disabled = f.NewDocker(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.ResumeApplications(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.Deploy(new("other", f.Manifest.Image), default));
        f.Shutdown.Save(f.Shutdown.Read()! with { Reason = "host_shutdown" });
        await disabled.ReconcileOrchestration(default); Assert.True(disabled.OrchestrationStatus().Paused);
        f.Shutdown.Save(f.Shutdown.Read()! with { Reason = "maintenance" }); f.Engine.FailNetworkDelete = false;
        await f.NewDocker().RemoveApplication(f.Id, request, default);
        Assert.Equal("awaiting_external_cleanup", f.Removals.Read()!.Phase);
        Assert.Equal(1, f.Engine.Deleted.Count(p => p == "container:" + f.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Docker.RemoveApplication(f.Id, new(Guid.NewGuid(), f.Manifest.Image), default));
    }

    [Fact]
    public async Task ProviderRunningWrongImageAndUnownedNetworkAreRejectedBeforeDeletion()
    {
        using var f = new Fixture(); var request = new ApplicationRemovalRequest(Guid.NewGuid(), f.Manifest.Image);
        var dependentId = new string('c', 64);
        var dependent = new ApplicationManifest("child", f.Manifest.Image, Resources: new(ManagedLifecycle: true, Dependencies: [new("pump", "running")]));
        f.Add(dependentId, dependent, new string('d', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Docker.RemoveApplication(f.Id, request, default));
        Assert.Empty(f.Engine.Deleted); Assert.Null(f.Removals.Read());
        f.Engine.Containers.Remove(dependentId); f.Apps.Forget(dependentId);
        f.Engine.Containers[f.Id] = f.Engine.Containers[f.Id] with { State = "running" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Docker.RemoveApplication(f.Id, request, default));
        f.Engine.Containers[f.Id] = f.Engine.Containers[f.Id] with { State = "exited" };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Docker.RemoveApplication(f.Id, request with { ExpectedImage = "changed" }, default));
        f.Engine.UnownedNetwork = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Docker.RemoveApplication(f.Id, request, default));
        Assert.Empty(f.Engine.Deleted); Assert.Null(f.Removals.Read());
    }

    [Fact]
    public async Task RetainedVersionRemovalForgetsActivePointerAndRefusesUnsettledUpdates()
    {
        using var f = new Fixture(); var oldId = new string('c', 64); var oldNetwork = new string('d', 64);
        var oldManifest = f.Manifest with { Resources = new(PersistentData: true, Health: new(), Updates: new()) };
        var current = oldManifest with { Image = "registry/app@sha256:" + new string('f', 64) };
        f.Add(oldId, oldManifest, oldNetwork); f.Add(f.Id, current, f.Network);
        var operation = new ApplicationUpdateRecord(Guid.NewGuid(), "pump", "committed", f.Id, oldId, oldManifest, current, 1,
            DateTimeOffset.UtcNow, false, f.Id, f.Network, oldNetwork);
        f.Updates.Save(operation with { Phase = "rollback_cleanup" });
        var request = new ApplicationRemovalRequest(Guid.NewGuid(), current.Image);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Docker.RemoveApplication(f.Id, request, default)); Assert.Empty(f.Engine.Deleted);
        f.Updates.Save(operation);
        await f.Docker.RemoveApplication(f.Id, request, default);
        Assert.Empty(f.Updates.ReadAll()); Assert.Null(f.Apps.Find(oldId)); Assert.Null(f.Apps.Find(f.Id)); Assert.Empty(await f.Docker.List(default));
        Assert.Equal(2, f.Engine.Deleted.Count(p => p.StartsWith("container:", StringComparison.Ordinal)));
        Assert.Equal(2, f.Engine.Deleted.Count(p => p.StartsWith("network:", StringComparison.Ordinal)));
        // A newly appeared runtime cannot be hidden by checking the earlier deletion alone.
        f.Add(f.Id, current, f.Network);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Docker.CompleteRemoval(new(request.OperationId, "owner", true), default));
        Assert.Equal("awaiting_external_cleanup", f.Removals.Read()!.Phase);
    }

    [Fact]
    public async Task RemovalRequiresMaintenanceAndAuthenticatedJournalCannotBeTamperedWith()
    {
        using var f = new Fixture(); f.Shutdown.Save(f.Shutdown.Read()! with { Phase = "resumed" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Docker.RemoveApplication(f.Id, new(Guid.NewGuid(), f.Manifest.Image), default));
        f.Shutdown.Save(f.Shutdown.Read()! with { Phase = "paused" });
        await f.Docker.RemoveApplication(f.Id, new(Guid.NewGuid(), f.Manifest.Image), default);
        string journal = Directory.GetFiles(Path.Combine(f.Root, "removal")).Single();
        Assert.DoesNotContain("pump", File.ReadAllText(journal));
        File.WriteAllText(journal, "corrupt");
        Assert.Throws<IOException>(() => f.Docker.OrchestrationStatus());
        await Assert.ThrowsAsync<IOException>(() => f.NewDocker(false).Deploy(new("other", f.Manifest.Image), default));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-remove-" + Guid.NewGuid()));
        public string Id { get; } = new('a', 64); public string Network { get; } = new('e', 64);
        public ApplicationManifest Manifest { get; } = new("pump", "registry/app@sha256:" + new string('b', 64), Resources: new(PersistentData: true));
        public ManagedDeploymentStore Apps { get; } public ApplicationShutdownStore Shutdown { get; }
        public ApplicationUpdateStore Updates { get; } public ApplicationRemovalStore Removals { get; }
        public Engine Engine { get; } = new(); private readonly HttpClient http;
        public DockerOperations Docker { get; }
        public Fixture()
        {
            var protection = new EphemeralDataProtectionProvider();
            Apps = new(protection, Path.Combine(Root, "apps")); Shutdown = new(protection, Path.Combine(Root, "shutdown"));
            Updates = new(protection, Path.Combine(Root, "updates")); Removals = new(protection, Path.Combine(Root, "removal"));
            Shutdown.Save(new(Guid.NewGuid(), "maintenance", "paused", [Id], 1, DateTimeOffset.UtcNow));
            Add(Id, Manifest, Network); http = new(Engine) { BaseAddress = new("http://localhost") }; Docker = NewDocker();
        }
        public void Add(string id, ApplicationManifest manifest, string network)
        { Apps.Save(new(manifest, id, [], DateTimeOffset.UtcNow, Revision: 1)); Engine.Containers[id] = new(manifest, network); Engine.Networks[network] = manifest.Name; }
        public DockerOperations NewDocker(bool enabled = true) => new(http, Apps, new Delivery(), orchestrationEnabled: true,
            shutdownStore: Shutdown, updateStore: Updates, removalsEnabled: enabled, removalStore: Removals);
        public void Dispose() { http.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class Delivery : IApplicationBootstrapDelivery
    { public Task Deliver(string id, ApplicationBootstrapPacket packet, Func<Task> start, CancellationToken ct) => start(); }
    private sealed record Container(ApplicationManifest Manifest, string Network, string State = "exited");
    private sealed class Engine : HttpMessageHandler
    {
        private const string Label = "ai.knowledgelocker.pidock.managed";
        public Dictionary<string, Container> Containers = []; public Dictionary<string, string> Networks = [];
        public List<string> Deleted = []; public List<string> Requests = []; public bool FailNetworkDelete, UnownedNetwork;
        private static Dictionary<string, string> Labels(string app) => new() { [Label] = "true", [Label + ".resources"] = "v1", [Label + ".app"] = app };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath; Requests.Add(path);
            if (path.EndsWith("/containers/json", StringComparison.Ordinal))
                return Json(Containers.Select(c => new ApplicationSummary(c.Key, ["pidock-" + c.Value.Manifest.Name], c.Value.Manifest.Image, c.Value.State, c.Value.State, Labels(c.Value.Manifest.Name))).ToArray());
            string[] parts = path.Split('/'); string id = parts.Length > 3 ? parts[3] : "";
            if (parts.Length > 2 && parts[2] == "containers")
            {
                if (!Containers.TryGetValue(id, out var container)) return Response(HttpStatusCode.NotFound);
                if (request.Method == HttpMethod.Delete)
                {
                    Assert.Contains("force=0&v=0", request.RequestUri.Query); Containers.Remove(id); Deleted.Add("container:" + id); return Response(HttpStatusCode.NoContent);
                }
                return Json(new { Id = id, Config = new { Image = container.Manifest.Image, Labels = Labels(container.Manifest.Name) },
                    State = new { Running = container.State == "running", Status = container.State }, HostConfig = new { NetworkMode = container.Network } });
            }
            if (parts.Length > 2 && parts[2] == "networks")
            {
                if (!Networks.TryGetValue(id, out var app)) return Response(HttpStatusCode.NotFound);
                if (request.Method == HttpMethod.Delete)
                {
                    if (FailNetworkDelete) return Response(HttpStatusCode.InternalServerError);
                    Networks.Remove(id); Deleted.Add("network:" + id); return Response(HttpStatusCode.NoContent);
                }
                return Json(new { Id = id, Driver = "bridge", Labels = Labels(UnownedNetwork ? "other" : app),
                    Containers = Containers.Where(c => c.Value.Network == id).ToDictionary(c => c.Key, _ => new { }) });
            }
            throw new InvalidOperationException("Unexpected Docker request: " + path);
        }
        private static Task<HttpResponseMessage> Json(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: new JsonSerializerOptions()) });
        private static Task<HttpResponseMessage> Response(HttpStatusCode code) => Task.FromResult(new HttpResponseMessage(code));
    }
}
