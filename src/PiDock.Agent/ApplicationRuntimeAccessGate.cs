using PiDock.Core;

namespace PiDock.Agent;

// Fail closed on unknown, stale or changed broker authority; an invalidated admission never revives.
public sealed class ApplicationRuntimeAccessGate(bool enabled, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<string, ApplicationRuntimeBinding> admissions = new(StringComparer.Ordinal);
    private Guid coordinator;
    private long observed;
    private bool known;
    public bool Enabled => enabled;
    public ApplicationRuntimeAccessSupport Support() => new(1, enabled, ApplicationRuntimeAccess.Header, ApplicationRuntimeAccess.ObservationSeconds,
        ["Opt-in managed startup binding for simulated I/O and engineering; no physical GPIO authority.",
         "Private startup delivery IDs must stay server-side. Stop, pause, failure, replacement or stale/unavailable broker status revokes admissions.",
         "The host polls and rechecks before operations; scheduling/transport latency is bounded by policy, not a hard real-time guarantee.",
         "Invalidated leases/registrations cannot revive. New startup delivery and fresh registration are required after process restart."]);
    public bool IsFresh { get { lock (gate) return known && clock.GetElapsedTime(observed) < TimeSpan.FromSeconds(ApplicationRuntimeAccess.ObservationSeconds); } }
    public void Apply(ApplicationRuntimeAccessSnapshot snapshot)
    {
        if (snapshot.CoordinatorId == Guid.Empty || snapshot.Apps is not { Length: <= 16 } ||
            snapshot.Apps.Any(a => a is null || !IdentityRules.IsId(a.AppId) || a.AppId.Length > 40 || a.Allowed && (a.DeliveryId is null || a.DeliveryId == Guid.Empty)) ||
            snapshot.Apps.Select(a => a.AppId).Distinct(StringComparer.Ordinal).Count() != snapshot.Apps.Length)
            throw new ArgumentException("Invalid private runtime authority observation.");
        lock (gate)
        {
            if (!known || coordinator != snapshot.CoordinatorId || clock.GetElapsedTime(observed) >= TimeSpan.FromSeconds(ApplicationRuntimeAccess.ObservationSeconds)) admissions.Clear();
            coordinator = snapshot.CoordinatorId;
            var eligible = snapshot.Paused ? [] : snapshot.Apps.Where(a => a.Allowed).ToArray();
            foreach (var app in admissions.Keys.ToArray())
                if (!eligible.Any(a => a.AppId == app && a.DeliveryId == admissions[app].DeliveryId)) admissions.Remove(app);
            foreach (var app in eligible)
                if (!admissions.ContainsKey(app.AppId)) admissions.Add(app.AppId, new(app.DeliveryId!.Value, Guid.NewGuid()));
            observed = clock.GetTimestamp(); known = true;
        }
    }
    public void Unavailable() { lock (gate) { known = false; admissions.Clear(); } }
    public ApplicationRuntimeBinding? Require(string appId, Guid? delivery)
    {
        if (!enabled) return null;
        lock (gate)
        {
            if (!known || clock.GetElapsedTime(observed) >= TimeSpan.FromSeconds(ApplicationRuntimeAccess.ObservationSeconds)) { known = false; admissions.Clear(); }
            if (!known || !admissions.TryGetValue(appId, out var current) || delivery != current.DeliveryId)
                throw new UnauthorizedAccessException("Current managed runtime authority is unavailable.");
            return current;
        }
    }
    public bool IsCurrent(string appId, ApplicationRuntimeBinding? binding)
    {
        if (!enabled) return binding is null;
        try { return binding is not null && Require(appId, binding.DeliveryId) == binding; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
