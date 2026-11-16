using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Cloud.Web;

public static class DeviceHistory
{
    public static void MapExport(WebApplication app)
    {
        app.MapGet("/api/devices/{device}/export", (string device, DateTimeOffset from, DateTimeOffset to, string? format, string? signals, HttpContext context, System.Security.Claims.ClaimsPrincipal user, FleetStore fleet) =>
        {
            if (!fleet.For(user).Any(d => d.DeviceId == device)) return Results.NotFound();
            if (!TelemetryExport.TrySelection(from, to, format, signals, out var metrics)) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return TelemetryExport.Download(device, from, to, format!, metrics, fleet.RangeFor(user, device, from, to));
        }).RequireAuthorization();
    }

    public static readonly string[] Metrics = TelemetryHistory.Metrics;
    public static double? Value(HostSnapshot snapshot, string metric) => TelemetryHistory.Value(snapshot, metric);
    public static string Label(string metric) => TelemetryHistory.Label(metric);
    public static string Unit(string metric) => TelemetryHistory.Unit(metric);
    public static string Csv(IReadOnlyList<TelemetryEnvelope> frames, IReadOnlyList<string> metrics) => TelemetryHistory.Csv(frames, metrics);
    public static IReadOnlyList<string> Paths(IReadOnlyList<TelemetryEnvelope> frames, string metric, DateTimeOffset from, DateTimeOffset to, double max) => TelemetryHistory.Paths(frames, metric, from, to, max);
}
