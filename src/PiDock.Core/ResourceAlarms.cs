using System.Text.Json.Serialization;
namespace PiDock.Core;

public enum AlarmMetric { CpuPercent, MemoryPercent, TemperatureC, DiskUsedPercent, ReservePressure }
public sealed record AlarmRule(string Id, string Title, AlarmMetric Metric, double Threshold,
    double ClearBelow, int HoldSeconds = 300, int RecoverySeconds = 30, string Severity = "Warning")
{
    public void Validate()
    {
        if (!IdentityRules.IsId(Id) || Title.Length is < 1 or > 120 || !Enum.IsDefined(Metric) ||
            !double.IsFinite(Threshold) || !double.IsFinite(ClearBelow) || ClearBelow < 0 || ClearBelow >= Threshold ||
            Threshold > (Metric == AlarmMetric.TemperatureC ? 120 : Metric == AlarmMetric.ReservePressure ? 1 : 100) ||
            HoldSeconds is < 0 or > 86400 || RecoverySeconds is < 0 or > 86400 || Severity is not ("Warning" or "Critical"))
            throw new ArgumentException("Invalid resource alarm rule.");
    }
    public static IReadOnlyList<AlarmRule> Defaults => [
        new("cpu-high", "Sustained CPU usage", AlarmMetric.CpuPercent, 90, 80),
        new("memory-high", "Memory pressure", AlarmMetric.MemoryPercent, 85, 75),
        new("temperature-high", "High board temperature", AlarmMetric.TemperatureC, 80, 75, 60),
        new("disk-warning", "Storage getting full", AlarmMetric.DiskUsedPercent, 80, 75),
        new("disk-critical", "Storage critically full", AlarmMetric.DiskUsedPercent, 90, 85, 60, 30, "Critical"),
        new("reserve-pressure", "Protected storage reserve reached", AlarmMetric.ReservePressure, 1, 0, 0, 30, "Critical")
    ];
}
public sealed record ResourceAlarm(Guid Id, string RuleId, string Title, string Severity,
    DateTimeOffset OpenedAt, double Value);
public sealed record AlarmTransition(Guid EventId, ResourceAlarm Alarm, string State, DateTimeOffset ObservedAt);
public sealed record TelemetryReceipt(int SchemaVersion, string TenantId, string DeviceId, Guid MessageId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApplicationCommand? Command = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OsUpdateCommand? OsUpdate = null);

public sealed class ResourceAlarmEngine
{
    private readonly TimeProvider clock;
    private readonly IReadOnlyList<AlarmRule> rules;
    private readonly Dictionary<string, ResourceAlarm> active = [];
    private readonly Dictionary<string, long> pending = [], recovering = [];
    public ResourceAlarmEngine(TimeProvider timeProvider, IReadOnlyList<AlarmRule>? configuredRules = null)
    {
        clock = timeProvider; rules = configuredRules ?? AlarmRule.Defaults;
        if (rules.Count > 32 || rules.Select(r => r.Id).Distinct().Count() != rules.Count) throw new ArgumentException("Alarm rules must be unique and bounded.");
        foreach (var rule in rules) rule.Validate();
    }
    public IReadOnlyList<ResourceAlarm> Active => active.Values.OrderBy(a => a.OpenedAt).ToArray();
    public void Restore(IEnumerable<ResourceAlarm> alarms)
    {
        foreach (var alarm in alarms)
            if (rules.Any(r => r.Id == alarm.RuleId) && alarm.Id != Guid.Empty) active[alarm.RuleId] = alarm;
    }
    public IReadOnlyList<AlarmTransition> Observe(HostSnapshot sample)
    {
        var events = new List<AlarmTransition>();
        foreach (var rule in rules)
        {
            double? value = rule.Metric switch
            {
                AlarmMetric.CpuPercent => sample.CpuPercent,
                AlarmMetric.MemoryPercent => sample.MemoryTotalBytes is > 0 && sample.MemoryAvailableBytes.HasValue
                    ? 100d * (sample.MemoryTotalBytes.Value - sample.MemoryAvailableBytes.Value) / sample.MemoryTotalBytes.Value : null,
                AlarmMetric.TemperatureC => sample.TemperatureC,
                AlarmMetric.DiskUsedPercent => sample.Storage.TotalBytes > 0 ? 100d * (sample.Storage.TotalBytes - sample.Storage.FreeBytes) / sample.Storage.TotalBytes : null,
                AlarmMetric.ReservePressure => sample.Storage.UnderPressure ? 1 : 0,
                _ => null
            };
            if (value is null || !double.IsFinite(value.Value)) { pending.Remove(rule.Id); recovering.Remove(rule.Id); continue; }
            if (!active.TryGetValue(rule.Id, out var alarm))
            {
                if (value < rule.Threshold) { pending.Remove(rule.Id); continue; }
                if (!pending.ContainsKey(rule.Id)) pending[rule.Id] = clock.GetTimestamp();
                if (clock.GetElapsedTime(pending[rule.Id]).TotalSeconds < rule.HoldSeconds) continue;
                alarm = new(Guid.NewGuid(), rule.Id, rule.Title, rule.Severity, clock.GetUtcNow(), value.Value);
                active[rule.Id] = alarm; pending.Remove(rule.Id);
                events.Add(new(Guid.NewGuid(), alarm, "Opened", clock.GetUtcNow()));
            }
            else
            {
                if (value > rule.ClearBelow) { recovering.Remove(rule.Id); continue; }
                if (!recovering.ContainsKey(rule.Id)) recovering[rule.Id] = clock.GetTimestamp();
                if (clock.GetElapsedTime(recovering[rule.Id]).TotalSeconds < rule.RecoverySeconds) continue;
                active.Remove(rule.Id); recovering.Remove(rule.Id);
                events.Add(new(Guid.NewGuid(), alarm, "Recovered", clock.GetUtcNow()));
            }
        }
        return events;
    }
}
