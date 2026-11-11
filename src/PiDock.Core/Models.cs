using System.Text.Json.Serialization;
namespace PiDock.Core;

public enum HistoryClass { Logs, Telemetry, Audit, Captures }
public record DataPolicy(double HistoryPercent = 10, double ReservePercent = 10,
    long MinimumReserveBytes = 1L << 30, double CleanupAtPercent = 90, double CleanupToPercent = 80)
{
    public void Validate()
    {
        if (!double.IsFinite(HistoryPercent) || HistoryPercent is < 1 or > 50 ||
            !double.IsFinite(ReservePercent) || ReservePercent is < 5 or > 80 ||
            HistoryPercent + ReservePercent > 90 || MinimumReserveBytes < (512L << 20) ||
            !double.IsFinite(CleanupAtPercent) || !double.IsFinite(CleanupToPercent) ||
            CleanupToPercent < 10 || CleanupAtPercent > 95 || CleanupToPercent >= CleanupAtPercent)
            throw new ArgumentException("Choose history 1–50%, reserve 5–80%, at least 512 MiB reserve, and 10 ≤ target < cleanup ≤ 95%.");
    }
}
public record StorageCapacity(long TotalBytes, long FreeBytes, long HistoryBytes);
public record PolicyBudget(long BudgetBytes, long ReserveBytes, long UsedBytes,
    long FreeBytes, bool UnderPressure, IReadOnlyDictionary<HistoryClass, long> ClassLimits, long TotalBytes = 0);
public static class DataPolicyCalculator
{
    public static PolicyBudget Evaluate(DataPolicy policy, StorageCapacity capacity)
    {
        policy.Validate();
        if (capacity.TotalBytes <= 0 || capacity.FreeBytes < 0 || capacity.FreeBytes > capacity.TotalBytes ||
            capacity.HistoryBytes < 0 || capacity.HistoryBytes > capacity.TotalBytes - capacity.FreeBytes)
            throw new ArgumentException("Invalid storage measurements.");
        var reserve = Math.Min(capacity.TotalBytes, Math.Max((long)(capacity.TotalBytes * policy.ReservePercent / 100), policy.MinimumReserveBytes));
        var budget = Math.Min((long)(capacity.TotalBytes * policy.HistoryPercent / 100),
            Math.Max(0, capacity.FreeBytes + capacity.HistoryBytes - reserve));
        var limits = new Dictionary<HistoryClass, long>
        {
            [HistoryClass.Logs] = budget / 2,
            [HistoryClass.Telemetry] = budget * 30 / 100,
            [HistoryClass.Audit] = budget * 15 / 100,
            [HistoryClass.Captures] = budget * 5 / 100
        };
        return new(budget, reserve, capacity.HistoryBytes, capacity.FreeBytes, capacity.FreeBytes < reserve, limits, capacity.TotalBytes);
    }
}
public record InterfaceSnapshot(string Name, string Kind, string State, long ReceivedBytes, long SentBytes, long Errors);
public record HostSnapshot(DateTimeOffset ObservedAt, string Hostname, string Platform,
    double? CpuPercent, long? MemoryTotalBytes, long? MemoryAvailableBytes, double? TemperatureC,
    double UptimeSeconds, PolicyBudget Storage, IReadOnlyList<InterfaceSnapshot> Interfaces, string ClockState);
public record TelemetryEnvelope(int SchemaVersion, string TenantId, string DeviceId, Guid MessageId,
    Guid BootId, long Sequence, HostSnapshot Snapshot, bool IsReplay = false, IReadOnlyList<ResourceAlarm>? Alarms = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemoteApplicationSnapshot? Applications = null);
public record StoredDevice(string TenantId, string DeviceId, Guid LastMessageId, DateTimeOffset ReceivedAt, HostSnapshot Snapshot, Guid BootId = default, long Sequence = 0, Guid[]? RetiredBootIds = null, IReadOnlyList<ResourceAlarm>? Alarms = null, bool HasLiveContact = true, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemoteApplicationSnapshot? Applications = null);
public static class IdentityRules
{
    public static bool IsId(string? value) => value is { Length: >= 1 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    public static string Topic(string tenant, string device)
    {
        if (!IsId(tenant) || !IsId(device)) throw new ArgumentException("IDs must contain only ASCII letters, digits, or hyphens.");
        return $"pidock/v1/tenants/{tenant}/devices/{device}/telemetry";
    }
    public static string ReceiptTopic(string tenant, string device) => Topic(tenant, device).Replace("/telemetry", "/receipts", StringComparison.Ordinal);
    public static bool MatchesTopic(string topic, TelemetryEnvelope envelope) =>
        IsId(envelope.TenantId) && IsId(envelope.DeviceId) && topic == Topic(envelope.TenantId, envelope.DeviceId) && envelope.SchemaVersion == 1;
}
