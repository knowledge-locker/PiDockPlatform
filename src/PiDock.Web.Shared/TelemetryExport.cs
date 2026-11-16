using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PiDock.Core;

namespace PiDock.Web.Shared;

public static class TelemetryExport
{
    public static bool TrySelection(DateTimeOffset from, DateTimeOffset to, string? format, string? signals, out string[] metrics)
    {
        metrics = (signals ?? string.Join(',', TelemetryHistory.Metrics)).Split(',').Distinct().ToArray();
        return to > from && to - from <= TimeSpan.FromDays(31) && format is "csv" or "json"
            && metrics.Length is > 0 and <= 5 && metrics.All(TelemetryHistory.Metrics.Contains);
    }

    public static IResult Download(string device, DateTimeOffset from, DateTimeOffset to, string format,
        IReadOnlyList<string> metrics, IReadOnlyList<TelemetryEnvelope> frames)
    {
        string name = $"pidock-{device}-{from:yyyyMMddHHmm}-{to:yyyyMMddHHmm}.{format}";
        return format == "csv"
            ? Results.File(Encoding.UTF8.GetBytes(TelemetryHistory.Csv(frames, metrics)), "text/csv; charset=utf-8", name)
            : Results.File(JsonSerializer.SerializeToUtf8Bytes(new {
                DeviceId = device, From = from, To = to, Signals = metrics.ToDictionary(m => m, TelemetryHistory.Unit),
                Samples = frames.Select(f => new { f.Snapshot.ObservedAt, f.Snapshot.Hostname, f.IsReplay,
                    Values = metrics.ToDictionary(m => m, m => TelemetryHistory.Value(f.Snapshot, m)) })
            }, StateFiles.Json), "application/json", name);
    }
}
