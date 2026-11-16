using System.Globalization;
using System.Text;
using PiDock.Core;

namespace PiDock.Web.Shared;

public static class TelemetryHistory
{
    public static readonly string[] Metrics = ["cpu", "memory", "storage", "temperature", "uptime"];
    public static double? Value(HostSnapshot s, string metric) => metric switch {
        "cpu" => s.CpuPercent,
        "memory" => s.MemoryTotalBytes is > 0 && s.MemoryAvailableBytes is >= 0 ? 100.0 * (s.MemoryTotalBytes.Value - s.MemoryAvailableBytes.Value) / s.MemoryTotalBytes.Value : null,
        "storage" => s.Channels?.Storage != false && s.Storage.TotalBytes > 0 ? 100.0 * (s.Storage.TotalBytes - s.Storage.FreeBytes) / s.Storage.TotalBytes : null,
        "temperature" => s.TemperatureC, "uptime" => s.Channels?.Uptime == false ? null : s.UptimeSeconds / 3600, _ => null
    };
    public static string Label(string metric) => metric switch { "cpu" => "CPU utilization", "memory" => "Memory utilization", "storage" => "Storage utilization", "temperature" => "Temperature", "uptime" => "Uptime", _ => metric };
    public static string Unit(string metric) => metric == "temperature" ? "°C" : metric == "uptime" ? "hours" : "%";
    private static string Number(double? value) => value is { } v && double.IsFinite(v) ? v.ToString("0.####", CultureInfo.InvariantCulture) : "";
    private static string Cell(string value)
    {
        var content = value.TrimStart(' ', '\t', '\r', '\n', '\ufeff');
        if ((content.Length > 0 && "=+-@".Contains(content[0])) || (value.Length > 0 && "\t\r\n".Contains(value[0]))) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    public static string Csv(IReadOnlyList<TelemetryEnvelope> frames, IReadOnlyList<string> metrics)
    {
        var text = new StringBuilder("observed_at_utc,device_id,hostname,is_replay," + string.Join(',', metrics.Select(m => m + "_" + (m == "temperature" ? "c" : m == "uptime" ? "hours" : "percent"))) + "\r\n");
        foreach (var f in frames) text.Append(f.Snapshot.ObservedAt.ToUniversalTime().ToString("O")).Append(',').Append(Cell(f.DeviceId)).Append(',').Append(Cell(f.Snapshot.Hostname)).Append(',').Append(f.IsReplay ? "true" : "false").Append(',').AppendJoin(',', metrics.Select(m => Number(Value(f.Snapshot, m)))).Append("\r\n");
        return text.ToString();
    }
    public static IReadOnlyList<string> Paths(IReadOnlyList<TelemetryEnvelope> frames, string metric, DateTimeOffset from, DateTimeOffset to, double max)
    {
        var paths = new List<string>(); var current = new List<string>(); DateTimeOffset? previous = null;
        double width = Math.Max((to - from).TotalSeconds, 1); var values = frames.OrderBy(f => f.Snapshot.ObservedAt).ToArray();
        // Render retained points in observation order and preserve gaps between samples.
        foreach (var f in values)
        {
            var value = Value(f.Snapshot, metric);
            if (value is null || !double.IsFinite(value.Value) || (previous.HasValue && f.Snapshot.ObservedAt - previous.Value > TimeSpan.FromMinutes(2)))
            { if (current.Count > 0) { paths.Add(string.Join(' ', current)); current.Clear(); } }
            if (value is { } v && double.IsFinite(v)) {
                double x = 48 + 904 * Math.Clamp((f.Snapshot.ObservedAt - from).TotalSeconds / width, 0, 1);
                double y = 160 - 140 * Math.Clamp(v / Math.Max(max, 1), 0, 1);
                current.Add(x.ToString("0.##", CultureInfo.InvariantCulture) + "," + y.ToString("0.##", CultureInfo.InvariantCulture));
            }
            previous = f.Snapshot.ObservedAt;
        }
        if (current.Count > 0) paths.Add(string.Join(' ', current));
        return paths;
    }
}
