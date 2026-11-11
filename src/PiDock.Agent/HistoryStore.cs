using System.Text.Json;
using PiDock.Core;
namespace PiDock.Agent;

public interface ICapacityProbe { StorageCapacity Read(string path, long historyBytes); }
public sealed class DriveCapacityProbe : ICapacityProbe
{
    public StorageCapacity Read(string path, long historyBytes)
    {
        var full = Path.GetFullPath(path);
        var drive = DriveInfo.GetDrives().Where(d => d.IsReady && (full == d.RootDirectory.FullName || full.StartsWith(d.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            .OrderByDescending(d => d.RootDirectory.FullName.Length).First();
        return new(drive.TotalSize, drive.AvailableFreeSpace, historyBytes);
    }
}
public sealed record HistoryStatus(DataPolicy Policy, PolicyBudget Budget, long DeletedSegments, DateTimeOffset? LastPurge,
    IReadOnlyDictionary<HistoryClass, long> ClassUsage, int PendingSegments = 0, long UnsentDropped = 0, long RejectedQueued = 0);
public sealed record PendingTelemetry(string RouteScope, TelemetryEnvelope Envelope, bool Pending = true);
internal sealed record HistoryMetadata(long Sequence = 0, long Deleted = 0, long UnsentDropped = 0,
    long RejectedQueued = 0, DateTimeOffset? LastPurge = null);

public sealed class HistoryStore
{
    private readonly object gate = new();
    private readonly string root, policyPath, metadataPath;
    private readonly ICapacityProbe probe;
    private readonly Dictionary<Guid, (string Path, PendingTelemetry Record)> pending = [];
    private DataPolicy policy;
    private HistoryMetadata metadata;
    public HistoryStore(string directory, ICapacityProbe capacityProbe)
    {
        directory = StateFiles.PrepareDirectory(directory);
        root = StateFiles.PrepareDirectory(Path.Combine(directory, "history")); probe = capacityProbe;
        policyPath = Path.Combine(directory, "data-policy.json"); metadataPath = Path.Combine(directory, "history-state.json");
        policy = File.Exists(policyPath) ? JsonSerializer.Deserialize<DataPolicy>(File.ReadAllText(policyPath), StateFiles.Json)! : new(); policy.Validate();
        metadata = File.Exists(metadataPath) ? JsonSerializer.Deserialize<HistoryMetadata>(File.ReadAllText(metadataPath), StateFiles.Json)! : new();
        foreach (var kind in Enum.GetValues<HistoryClass>()) StateFiles.PrepareDirectory(Path.Combine(root, kind.ToString()));
        var files = Segments();
        metadata = metadata with { Sequence = Math.Max(metadata.Sequence, files.Select(f => long.TryParse(Path.GetFileNameWithoutExtension(f.Name), out var n) ? n : 0).DefaultIfEmpty().Max()) };
        foreach (var file in files.Where(f => f.Directory!.Name == HistoryClass.Telemetry.ToString() && f.Length <= 65536))
        {
            try
            {
                var record = JsonSerializer.Deserialize<PendingTelemetry>(File.ReadAllText(file.FullName), StateFiles.Json);
                if (record is { Pending: true, Envelope: not null } && !string.IsNullOrEmpty(record.RouteScope))
                    pending[record.Envelope.MessageId] = (file.FullName, record);
            }
            catch (JsonException) { /* Non-queue legacy history remains bounded by normal retention. */ }
        }
    }
    private void SaveMetadata() => StateFiles.WriteAtomic(metadataPath, JsonSerializer.Serialize(metadata, StateFiles.Json));
    private List<FileInfo> Segments()
    {
        var files = new List<FileInfo>();
        foreach (var kind in Enum.GetValues<HistoryClass>())
        {
            var path = StateFiles.PrepareDirectory(Path.Combine(root, kind.ToString()));
            files.AddRange(new DirectoryInfo(path).GetFiles("*.json").Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint)));
        }
        return files;
    }
    public HistoryStatus Status()
    {
        lock (gate)
        {
            var files = Segments(); var bytes = files.Sum(f => f.Length);
            var budget = DataPolicyCalculator.Evaluate(policy, probe.Read(root, bytes));
            return new(policy, budget, metadata.Deleted, metadata.LastPurge,
                Enum.GetValues<HistoryClass>().ToDictionary(k => k, k => files.Where(f => f.Directory!.Name == k.ToString()).Sum(f => f.Length)),
                pending.Count, metadata.UnsentDropped, metadata.RejectedQueued);
        }
    }
    public PolicyBudget Preview(DataPolicy candidate)
    {
        lock (gate) return DataPolicyCalculator.Evaluate(candidate, probe.Read(root, Segments().Sum(f => f.Length)));
    }
    public void Apply(DataPolicy candidate)
    {
        candidate.Validate(); lock (gate) { StateFiles.WriteAtomic(policyPath, JsonSerializer.Serialize(candidate, StateFiles.Json)); policy = candidate; Purge(); }
    }
    public bool Append<T>(HistoryClass kind, T value)
    {
        lock (gate) return AppendJson(kind, JsonSerializer.Serialize(value, StateFiles.Json)) is not null;
    }
    public bool Enqueue(string routeScope, TelemetryEnvelope envelope)
    {
        if (string.IsNullOrEmpty(routeScope) || routeScope.Length > 256) throw new ArgumentException("Invalid delivery scope.");
        lock (gate)
        {
            var record = new PendingTelemetry(routeScope, envelope);
            var path = AppendJson(HistoryClass.Telemetry, JsonSerializer.Serialize(record, StateFiles.Json));
            if (path is null) { metadata = metadata with { RejectedQueued = metadata.RejectedQueued + 1 }; SaveMetadata(); return false; }
            pending[envelope.MessageId] = (path, record); return true;
        }
    }
    private string? AppendJson(HistoryClass kind, string json)
    {
        Purge(); var status = Status(); var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        if (bytes <= 65536 && bytes <= status.Budget.ClassLimits[kind] && bytes > status.Budget.ClassLimits[kind] - status.ClassUsage[kind])
        { Purge(kind, bytes); status = Status(); }
        if (bytes > 65536 || status.Budget.UnderPressure || status.Budget.BudgetBytes == 0 ||
            bytes > status.Budget.ClassLimits[kind] - status.ClassUsage[kind] || bytes > status.Budget.FreeBytes - status.Budget.ReserveBytes) return null;
        metadata = metadata with { Sequence = checked(metadata.Sequence + 1) }; SaveMetadata();
        var path = Path.Combine(StateFiles.PrepareDirectory(Path.Combine(root, kind.ToString())), $"{metadata.Sequence:D20}.json");
        StateFiles.WriteAtomic(path, json); return path;
    }
    public IReadOnlyList<PendingTelemetry> PendingBatch(string routeScope, int limit = 16)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (gate) return pending.Values.Where(p => p.Record.RouteScope == routeScope).OrderBy(p => p.Path, StringComparer.Ordinal).Take(limit).Select(p => p.Record).ToArray();
    }
    public PendingTelemetry? NewestPending(string routeScope, Guid bootId)
    {
        lock (gate) return pending.Values.Where(p => p.Record.RouteScope == routeScope && p.Record.Envelope.BootId == bootId)
            .OrderByDescending(p => p.Path, StringComparer.Ordinal).Select(p => p.Record).FirstOrDefault();
    }
    public bool Acknowledge(string routeScope, Guid messageId)
    {
        lock (gate)
        {
            if (!pending.TryGetValue(messageId, out var entry) || entry.Record.RouteScope != routeScope) return false;
            StateFiles.WriteAtomic(entry.Path, JsonSerializer.Serialize(entry.Record with { Pending = false }, StateFiles.Json));
            pending.Remove(messageId); return true;
        }
    }
    public int Purge(HistoryClass? forceKind = null, long incomingBytes = 0)
    {
        lock (gate)
        {
            var files = Segments(); var status = Status(); int count = 0;
            long retained = files.Sum(f => f.Length);
            var pendingPaths = pending.ToDictionary(p => p.Value.Path, p => p.Key, StringComparer.Ordinal);
            foreach (var kind in new[] { HistoryClass.Captures, HistoryClass.Logs, HistoryClass.Telemetry, HistoryClass.Audit })
            {
                long used = status.ClassUsage[kind], limit = status.Budget.ClassLimits[kind];
                bool pressure = probe.Read(root, retained).FreeBytes < status.Budget.ReserveBytes;
                if (used <= limit * policy.CleanupAtPercent / 100 && !pressure && kind != forceKind) continue;
                long target = (long)(limit * policy.CleanupToPercent / 100);
                if (kind == forceKind) target = Math.Min(target, Math.Max(0, limit - incomingBytes));
                foreach (var file in files.Where(f => f.Directory!.Name == kind.ToString()).OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    pressure = probe.Read(root, retained).FreeBytes < status.Budget.ReserveBytes;
                    if (used <= target && !pressure) break;
                    long length = file.Length; file.Delete(); used -= length; retained -= length; count++;
                    long lost = 0;
                    if (pendingPaths.TryGetValue(file.FullName, out var id)) { pending.Remove(id); lost = 1; }
                    metadata = metadata with { Deleted = metadata.Deleted + 1, UnsentDropped = metadata.UnsentDropped + lost };
                }
            }
            if (count > 0) { metadata = metadata with { LastPurge = DateTimeOffset.UtcNow }; SaveMetadata(); }
            return count;
        }
    }
}
