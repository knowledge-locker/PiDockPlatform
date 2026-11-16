using System.Text.Json;

namespace PiDock.Core;

public sealed record ApplicationHealthPolicy(int StartupSeconds = 60, int FreshnessSeconds = 15,
    int FailureThreshold = 3, bool RestartOnFailure = false)
{
    public void Validate()
    {
        if (StartupSeconds is < 15 or > 300 || FreshnessSeconds is < 10 or > 120 || FailureThreshold is < 1 or > 10)
            throw new ArgumentException("Health startup grace must be 15–300 seconds, freshness 10–120 seconds and failure threshold 1–10.");
    }
}
public sealed record ApplicationHealthReport(int SchemaVersion, Guid DeliveryId, long Sequence,
    DateTimeOffset ReportedAt, bool Live, bool Ready);
public sealed record ApplicationHealthStatus(string State, bool Ready, bool? Live, DateTimeOffset? CheckedAt,
    DateTimeOffset? ReportedAt, int Failures, string Reason, bool RecoveryRequired);
public sealed record ApplicationHealthSupport(int SchemaVersion, bool Enabled, string Protocol, string ReportPath, string ProbePath,
    int CheckSeconds, int MaximumReportBytes, string[] Limitations, string ReaderSourcePath, string ReaderSourceSha256);
public static class ApplicationHealthRules
{
    public const string Directory = "/run/pidock/health";
    public const string ReportPath = Directory + "/health.json";
    public const string ProbePath = "/usr/lib/pidock/health-probe";
    public const int MaximumReportBytes = 1024;
    public const int CheckSeconds = 5;
    public static bool Valid(ApplicationHealthReport report, Guid deliveryId, DateTimeOffset now, int freshness) =>
        report.SchemaVersion == 1 && deliveryId != Guid.Empty && report.DeliveryId == deliveryId &&
        report.Sequence > 0 && report.Sequence <= 9007199254740991 && (!report.Ready || report.Live) &&
        report.ReportedAt <= now.AddSeconds(2) && report.ReportedAt >= now.AddSeconds(-freshness);
}

// Call from a task that actually checks the app's critical work, at least every five seconds.
// Report booleans only: never write credentials, diagnostics or user content to this protocol.
public sealed class ApplicationHealthReporter
{
    private readonly Guid deliveryId;
    private readonly object gate = new();
    private long sequence;
    private ApplicationHealthReporter(Guid deliveryId) => this.deliveryId = deliveryId;
    public static ApplicationHealthReporter? Create(ApplicationBootstrapPacket? bootstrap)
    {
        if (bootstrap is null || Environment.GetEnvironmentVariable("PIDOCK_HEALTH_REPORTING") != "1") return null;
        if (!OperatingSystem.IsLinux() || bootstrap.DeliveryId == Guid.Empty || bootstrap.AppId != Environment.GetEnvironmentVariable("PIDOCK_APP_ID") ||
            !ApplicationBootstrap.IsMemoryDirectory(ApplicationHealthRules.Directory)) throw new IOException("Health reporting requires managed memory storage.");
        return new(bootstrap.DeliveryId);
    }
    public void Report(bool live, bool ready)
    {
        if (ready && !live) throw new ArgumentException("A ready app must be live.");
        lock (gate)
        {
            if (++sequence > 9007199254740991) throw new InvalidOperationException("Health sequence exhausted.");
            StateFiles.WriteAtomic(ApplicationHealthRules.ReportPath,JsonSerializer.Serialize(new ApplicationHealthReport(1,deliveryId,sequence,DateTimeOffset.UtcNow,live,ready),StateFiles.Json));
        }
    }
}

// Bundled for API/MCP authoring clients; no source checkout or runtime download is required by apps.
public static class ApplicationHealthAssets
{
    public const string SourcePath = "/api/platform/v1/app-health/reader.c";
    public static readonly string ReaderSource = Read();
    public static readonly string ReaderSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ReaderSource)));
    private static string Read()
    {
        using var stream = typeof(ApplicationHealthAssets).Assembly.GetManifestResourceStream("PiDock.HealthProbe.v1.c") ?? throw new InvalidOperationException("Health reader source unavailable.");
        using var reader = new StreamReader(stream);return reader.ReadToEnd();
    }
}
