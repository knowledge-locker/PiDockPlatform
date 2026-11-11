using System.Security.Claims;
using System.Text.Json;
using PiDock.Core;
using PiDock.Web.Shared;
namespace PiDock.Cloud.Web;

public sealed record TelemetryAcceptance(bool Stored, bool UpdatedCurrent);
public sealed class FleetStore
{
    private readonly object gate = new();
    private readonly string path;
    private readonly List<StoredDevice> devices;
    private readonly TelemetryArchive archive;
    public FleetStore(StateDirectory state)
    {
        path = Path.Combine(state.Path, "fleet.json"); archive = new(state.Path);
        devices = File.Exists(path) ? JsonSerializer.Deserialize<List<StoredDevice>>(File.ReadAllText(path), StateFiles.Json)! : [];
    }
    public IReadOnlyList<StoredDevice> For(ClaimsPrincipal user)
    {
        var tenant = user.FindFirstValue("tenant");
        if (user.Identity?.IsAuthenticated != true || !IdentityRules.IsId(tenant)) return [];
        lock (gate) return devices.Where(d => d.TenantId == tenant).OrderBy(d => d.DeviceId).ToArray();
    }
    public IReadOnlyList<TelemetryEnvelope> HistoryFor(ClaimsPrincipal user, string device, int limit = 100)
    {
        var tenant = user.FindFirstValue("tenant");
        if (user.Identity?.IsAuthenticated != true || !IdentityRules.IsId(tenant) || !IdentityRules.IsId(device)) return [];
        return archive.Read(tenant!, device, limit);
    }
    public bool Ingest(string topic, TelemetryEnvelope envelope) => Accept(topic, envelope).UpdatedCurrent;
    public TelemetryAcceptance Accept(string topic, TelemetryEnvelope envelope)
    {
        if (!Valid(topic, envelope)) return new(false, false);
        lock (gate)
        {
            var index = devices.FindIndex(d => d.TenantId == envelope.TenantId && d.DeviceId == envelope.DeviceId);
            if (index < 0 && devices.Count >= 10000) return new(false, false);
            if (!archive.Store(envelope)) return new(false, false);
            bool live = !envelope.IsReplay && envelope.Snapshot.ObservedAt >= DateTimeOffset.UtcNow.AddMinutes(-2)
                && envelope.Snapshot.ObservedAt <= DateTimeOffset.UtcNow.AddSeconds(30);
            var retired = index >= 0 ? devices[index].RetiredBootIds ?? [] : Array.Empty<Guid>();
            if (index >= 0)
            {
                var previous = devices[index];
                if (!live && (previous.HasLiveContact || previous.Snapshot.ObservedAt >= envelope.Snapshot.ObservedAt)) return new(true, false);
                if (previous.BootId == envelope.BootId && envelope.Sequence <= previous.Sequence) return new(true, false);
                if (retired.Contains(envelope.BootId)) return new(true, false);
                if (previous.BootId != Guid.Empty && previous.BootId != envelope.BootId) retired = retired.Append(previous.BootId).TakeLast(16).ToArray();
            }
            var record = new StoredDevice(envelope.TenantId, envelope.DeviceId, envelope.MessageId, DateTimeOffset.UtcNow,
                envelope.Snapshot, envelope.BootId, envelope.Sequence, retired, live ? envelope.Alarms ?? [] : [], live);
            var updated = devices.ToList(); if (index >= 0) updated[index] = record; else updated.Add(record);
            StateFiles.WriteAtomic(path, JsonSerializer.Serialize(updated, StateFiles.Json));
            devices.Clear(); devices.AddRange(updated); return new(true, true);
        }
    }
    private static bool Valid(string topic, TelemetryEnvelope envelope)
    {
        var sample = envelope.Snapshot;
        return IdentityRules.MatchesTopic(topic, envelope) && envelope.MessageId != Guid.Empty && envelope.Sequence > 0 && envelope.BootId != Guid.Empty &&
            sample is not null && sample.Interfaces is { Count: <= 128 } && sample.Hostname is { Length: > 0 and <= 256 } &&
            sample.Platform is { Length: <= 256 } && sample.ClockState is { Length: <= 512 } && sample.Storage is not null &&
            sample.Storage.FreeBytes >= 0 && sample.Storage.BudgetBytes >= 0 && sample.Storage.ClassLimits is { Count: <= 4 } &&
            sample.Interfaces.All(i => i is not null && i.Name is { Length: <= 128 } && i.State is { Length: <= 64 } && i.Kind is { Length: <= 64 }) &&
            (envelope.Alarms is null || (envelope.Alarms.Count <= 32 && envelope.Alarms.All(a => a is not null && a.Id != Guid.Empty &&
                IdentityRules.IsId(a.RuleId) && a.Title is { Length: <= 120 } && a.Severity is "Warning" or "Critical" && double.IsFinite(a.Value))));
    }
}
