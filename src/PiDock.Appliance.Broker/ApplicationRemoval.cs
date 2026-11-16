using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

public sealed partial class DockerOperations
{
    private bool RemovalBlocksStartup => ApplicationRemovalRules.BlocksStartup(removalStore?.Read());
    public ApplicationRemovalSupport RemovalSupport() => new(1, removalsEnabled && removalStore is not null && OrchestrationSupport().Enabled, false,
        ["Stopped managed resource apps only; finish maintenance and remove dependent app declarations first.",
         "Runtime containers and their verified private networks are removed. Data volumes, snapshots and image cache are retained.",
         "A durable startup barrier remains until the Owner confirms external UI registrations, credentials and grants have been removed.",
         "Disabling removal does not bypass an incomplete operation. Retry its original operation ID after repair; maintenance remains paused."]);
    public ApplicationRemovalStatus? RemovalStatus() => removalStore?.Read();
    private void RequireRemoval()
    {
        if (!RemovalSupport().Enabled) throw new InvalidOperationException("Managed app removal is not enabled.");
        if (RestoreBlocksStartup) throw new InvalidOperationException("Complete the active app data restore before removal.");
        if (shutdownStore?.Read() is not { Reason: "maintenance", Phase: "paused" })
            throw new InvalidOperationException("Finish a maintenance pause before removing an app.");
    }
    public async Task RemoveApplication(string id, ApplicationRemovalRequest request, CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty) throw new ArgumentException("Removal operation ID required.");
        await deploymentLock.WaitAsync(ct);
        ApplicationRemovalStatus? operation = null;
        try
        {
            RequireRemoval();
            var prior = removalStore!.Read();
            if (prior?.OperationId == request.OperationId)
            {
                if (prior.ContainerId != id || prior.Image != request.ExpectedImage) throw new ArgumentException("Removal ID reused for a different app or image.");
                if (prior.Phase is "completed" or "awaiting_external_cleanup") return;
                operation = prior;
            }
            else
            {
                if (ApplicationRemovalRules.BlocksStartup(prior)) throw new InvalidOperationException("Complete the active removal first.");
                operation = await PrepareRemoval(id, request, ct);
                removalStore.Save(operation);
            }
            if (operation.Phase is "prepared" or "removing")
            {
                operation = operation with { Phase = "removing", Outcome = "in_progress" }; removalStore.Save(operation);
                await VerifyNoRemovalDependents(operation.AppId, ct);
                foreach (var target in operation.Targets)
                {
                    if (store?.Find(target.ContainerId) is { } record)
                        store.Save(record with { DesiredRunning = false, PendingStart = false, Revision = checked(record.Revision + 1), Outcome = "removing", StableSince = null });
                    if (await InspectRemovalContainer(target, operation.AppId, ct))
                    {
                        using var response = await engine.DeleteAsync($"{Api}/containers/{target.ContainerId}?force=0&v=0", ct);
                        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
                    }
                }
                foreach (var network in operation.Targets.Select(t => t.NetworkId).OfType<string>().Distinct(StringComparer.Ordinal))
                    await RemoveApplicationNetwork(network, operation.AppId, ct);
                operation = operation with { Phase = "metadata" }; removalStore.Save(operation);
            }
            if (operation.Phase == "metadata")
            {
                await VerifyRemovalEmpty(operation, ct);
                await VerifyNoRemovalDependents(operation.AppId, ct);
                if (updateStore?.ReadAll().Any(p => p.AppId == operation.AppId) == true) updateStore.Forget(operation.AppId);
                foreach (var target in operation.Targets) store!.Forget(target.ContainerId);
                operation = operation with { Phase = "awaiting_external_cleanup", Outcome = "external_cleanup_required" };
                removalStore.Save(operation);
            }
        }
        catch
        {
            if (operation is not null && operation.Phase is not ("completed" or "awaiting_external_cleanup"))
                removalStore!.Save(operation with { Outcome = "interrupted" });
            throw;
        }
        finally { deploymentLock.Release(); }
    }
    public async Task CompleteRemoval(ApplicationRemovalCompleteRequest request, CancellationToken ct)
    {
        await deploymentLock.WaitAsync(ct);
        try
        {
            RequireRemoval();
            if (!request.ExternalCleanupConfirmed || !ApplicationContractRules.Text(request.Owner, 128) || request.Owner.Any(char.IsControl))
                throw new ArgumentException("Fresh Owner confirmation of external cleanup is required.");
            var operation = removalStore!.Read() ?? throw new ArgumentException("No removal operation.");
            if (operation.OperationId != request.OperationId) throw new ArgumentException("Removal operation changed.");
            if (operation.Phase == "completed") return;
            if (operation.Phase != "awaiting_external_cleanup") throw new InvalidOperationException("Finish runtime removal first.");
            await VerifyRemovalEmpty(operation, ct);
            await VerifyNoRemovalDependents(operation.AppId, ct);
            removalStore.Save(operation with { Phase = "completed", Outcome = "completed", CompletedBy = request.Owner, RemainingActions = [] });
        }
        finally { deploymentLock.Release(); }
    }
    private async Task<ApplicationRemovalStatus> PrepareRemoval(string id, ApplicationRemovalRequest request, CancellationToken ct)
    {
        var logical = (await List(ct)).SingleOrDefault(a => a.Id == id) ?? throw new ArgumentException("Unknown active app.");
        var record = store?.Find(id) ?? throw new InvalidOperationException("Removal requires readable managed startup metadata.");
        if (logical.Labels?.GetValueOrDefault(Label + ".resources") != "v1" || logical.Labels.GetValueOrDefault(Label + ".app") != record.Manifest.Name || record.Manifest.Image != request.ExpectedImage)
            throw new ArgumentException("App identity or expected image changed.");
        await VerifyNoRemovalDependents(record.Manifest.Name, ct);
        var version = UpdateFor(record.Manifest.Name);
        if (version is not null && (ApplicationUpdateRules.BlocksLifecycle(version.Phase) || ApplicationUpdateRules.CanAdvance(version.Phase)))
            throw new InvalidOperationException("Finish version operations and cleanup before removal.");
        var ids = new[] { id, version?.OriginalId, version?.CandidateId }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var raw = await RawList(ct);
        if (raw.Any(a => a.Labels?.GetValueOrDefault(Label + ".app") == record.Manifest.Name && !ids.Contains(a.Id, StringComparer.Ordinal)))
            throw new InvalidOperationException("Unexpected containers claim this app identity.");
        var targets = new List<ApplicationRemovalTarget>();
        foreach (var containerId in ids)
        {
            var metadata = store!.Find(containerId);
            if (metadata is null && !raw.Any(a => a.Id == containerId)) continue;
            if (metadata is null || metadata.Manifest.Name != record.Manifest.Name) throw new InvalidOperationException("Retained app metadata unavailable.");
            string? network = raw.Any(a => a.Id == containerId) ? await ContainerNetwork(containerId, ct) : null;
            var target = new ApplicationRemovalTarget(containerId, metadata.Manifest.Image, network);
            await InspectRemovalContainer(target, record.Manifest.Name, ct);
            if (network is not null) await ValidateApplicationNetwork(network, record.Manifest.Name, ids, ct);
            targets.Add(target);
        }
        if (targets.Count is < 1 or > 2) throw new InvalidOperationException("Unexpected retained app count.");
        return new(request.OperationId, record.Manifest.Name, id, record.Manifest.Image, "prepared", targets.ToArray(), Now, "in_progress",
            record.Manifest.Resources!.CredentialIds.ToArray(),
            ["Disable or remove every app UI contribution/bridge registration.", "Revoke all managed and configured app credentials, including bridge credentials.", "Remove I/O, engineering, service and publishing grants; release remaining leases."]);
    }
    private async Task VerifyNoRemovalDependents(string appId, CancellationToken ct)
    {
        foreach (var app in await List(ct))
        {
            var record = store?.Find(app.Id); RequireResourceRecord(app, record);
            if (record is not null && record.Manifest.Name != appId && record.Manifest.Resources!.RequiredApps.Any(d => d.AppId == appId))
                throw new InvalidOperationException("Remove dependent app declarations before removing this provider.");
        }
    }
    private async Task VerifyRemovalEmpty(ApplicationRemovalStatus operation, CancellationToken ct)
    {
        if ((await RawList(ct)).Any(a => operation.Targets.Any(t => t.ContainerId == a.Id) || a.Labels?.GetValueOrDefault(Label + ".app") == operation.AppId))
            throw new InvalidOperationException("App runtime containers remain; repair and retry removal.");
    }
    private async Task<bool> InspectRemovalContainer(ApplicationRemovalTarget target, string appId, CancellationToken ct)
    {
        using var response = await engine.GetAsync($"{Api}/containers/{target.ContainerId}/json", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode(); var value = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var config = value.GetProperty("Config"); var labels = config.GetProperty("Labels"); var state = value.GetProperty("State");
        if (value.GetProperty("Id").GetString() != target.ContainerId || config.GetProperty("Image").GetString() != target.Image ||
            labels.GetProperty(Label).GetString() != "true" || labels.GetProperty(Label + ".app").GetString() != appId ||
            labels.GetProperty(Label + ".resources").GetString() != "v1" || state.GetProperty("Running").GetBoolean() ||
            state.GetProperty("Status").GetString() is "running" or "restarting" or "paused" ||
            value.GetProperty("HostConfig").GetProperty("NetworkMode").GetString() != target.NetworkId)
            throw new InvalidOperationException("Refusing a running or unowned removal target.");
        return true;
    }
    private async Task<bool> ValidateApplicationNetwork(string id, string appId, IReadOnlyCollection<string> permittedContainers, CancellationToken ct)
    {
        using var response = await engine.GetAsync($"{Api}/networks/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode(); var value = await response.Content.ReadFromJsonAsync<JsonElement>(ct); var labels = value.GetProperty("Labels");
        if (value.GetProperty("Id").GetString() != id || value.GetProperty("Driver").GetString() != "bridge" ||
            labels.GetProperty(Label).GetString() != "true" || labels.GetProperty(Label + ".app").GetString() != appId ||
            value.TryGetProperty("Containers", out var containers) && containers.ValueKind is not JsonValueKind.Null &&
                (containers.ValueKind != JsonValueKind.Object || containers.EnumerateObject().Any(c => !permittedContainers.Contains(c.Name))))
            throw new InvalidOperationException("Refusing a shared or unowned application network.");
        return true;
    }
    private async Task RemoveApplicationNetwork(string id, string appId, CancellationToken ct)
    {
        if (!await ValidateApplicationNetwork(id, appId, [], ct)) return;
        using var response = await engine.DeleteAsync($"{Api}/networks/{id}", ct);
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }
}
