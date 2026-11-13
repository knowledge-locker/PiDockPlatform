using System.Text.Json;

namespace PiDock.Core;

public sealed record ClockTrustSample(string BootId, long Monotonic, DateTimeOffset Utc, bool Trusted);

// The image's root-owned time guard produces this volatile proof. The ordinary Debian profile is unchanged.
public sealed class ClockTrust(bool required, string path = "/run/pidock-time/status.json", TimeProvider? provider = null)
{
    private readonly TimeProvider time = provider ?? TimeProvider.System;
    public bool Required => required;
    public bool Allowed
    {
        get
        {
            if (!required) return true;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length is <= 0 or > 2048) return false;
                for (FileSystemInfo? entry = file; entry is not null; entry = entry is FileInfo f ? f.Directory : ((DirectoryInfo)entry).Parent)
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return false;
                var sample = JsonSerializer.Deserialize<ClockTrustSample>(File.ReadAllText(path), StateFiles.Json);
                var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
                return Accept(sample, boot, time);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
        }
    }
    public string Description => !required ? "Host clock trust not enforced in this installation" : Allowed ? "Authenticated network time verified" : "Waiting for authenticated time; protected access paused";
    public static bool Accept(ClockTrustSample? sample, string bootId, TimeProvider time)
    {
        if (sample is null || !sample.Trusted || sample.BootId != bootId || !Guid.TryParse(bootId, out _)) return false;
        double elapsed = time.GetElapsedTime(sample.Monotonic).TotalSeconds;
        return elapsed is >= 0 and <= 10 && Math.Abs((time.GetUtcNow() - sample.Utc).TotalSeconds - elapsed) <= 1;
    }
}
