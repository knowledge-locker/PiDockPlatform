using PiDock.Core;

namespace PiDock.Appliance.Broker;

public sealed partial class DockerOperations
{
    private readonly Guid runtimeCoordinatorId = Guid.NewGuid();
    public async Task<ApplicationRuntimeAccessSnapshot> RuntimeAccess(CancellationToken ct)
    {
        await deploymentLock.WaitAsync(ct);
        try
        {
            if (store is null || delivery is null || !orchestrationEnabled) throw new InvalidOperationException("Managed runtime authority requires resources and orchestration.");
            var catalog = await ReadCatalog(ct);
            var result = new List<ApplicationRuntimePermission>();
            foreach (var app in await List(ct))
            {
                if (app.Labels?.GetValueOrDefault(Label + ".resources") != "v1") continue;
                var record = store.Find(app.Id) ?? throw new InvalidOperationException("Managed runtime record unavailable.");
                var health = ProjectHealth(record, app.State);
                string reason = Paused ? "paused" : !record.DesiredRunning ? "requested_stopped" : app.State != "running" ? "not_running" :
                    record.LastDeliveryId is null || record.LastDeliveredAt == DateTimeOffset.MinValue || Incomplete(record) ? "bootstrap_incomplete" :
                    record.PendingStart || UpdateBlocksRuntime(record.Manifest.Name) || DependencyStatuses(record, catalog).Any(d => !d.Satisfied) ? "transition_or_dependency" :
                    record.Manifest.Resources?.Health is not null && (health.Live == false || health.State is not ("starting" or "ready" or "not_ready")) ? "health_unavailable" : "allowed";
                result.Add(new(record.Manifest.Name, record.LastDeliveryId, reason == "allowed", reason));
            }
            return new(runtimeCoordinatorId, Paused, result.ToArray());
        }
        finally { deploymentLock.Release(); }
    }
    // The selected process must execute while staging and while proving readiness. Lifecycle mutation
    // remains blocked by the stricter update policy; private startup identity still fences old processes.
    private bool UpdateBlocksRuntime(string app) => UpdateFor(app) is {Phase:"switching" or "rollback_pending" or "rollback_failed"};
}
