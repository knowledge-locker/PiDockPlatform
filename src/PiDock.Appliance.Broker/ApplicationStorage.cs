using System.Text.Json;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

// The administrator must select the actual local filesystem backing Docker's managed volumes.
// A remote Docker daemon or an unverified host directory cannot provide an admission guarantee.
public sealed record ApplicationStorageConfiguration(string CapacityDirectory, bool BackingFilesystemConfirmed, ApplicationStoragePolicy? Policy = null);
public interface IApplicationStorageCapacityProbe { (long TotalBytes, long FreeBytes) Read(string directory); }
public sealed class ApplicationStorageCapacityProbe : IApplicationStorageCapacityProbe
{
    public (long TotalBytes, long FreeBytes) Read(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new IOException("Verified Docker backing filesystem is unavailable.");
        for (var entry = new DirectoryInfo(directory); entry is not null; entry = entry.Parent)
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Storage capacity paths cannot contain links.");
        // Pick the containing mount, rather than the process root filesystem (Docker commonly has its own mount).
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var drive = DriveInfo.GetDrives().Where(d => full == Path.TrimEndingDirectorySeparator(d.Name) ||
            full.StartsWith(Path.EndsInDirectorySeparator(d.Name) ? d.Name : d.Name + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderByDescending(d => d.Name.Length).FirstOrDefault() ?? throw new IOException("Storage filesystem could not be identified.");
        return (drive.TotalSize, drive.AvailableFreeSpace);
    }
}

public sealed partial class DockerOperations
{
    public ApplicationStorageConfiguration? StorageConfiguration { get; init; }
    public IApplicationStorageCapacityProbe StorageCapacityProbe { get; init; } = new ApplicationStorageCapacityProbe();
    public ApplicationStorageSupport StorageSupport() => new(1, StorageConfiguration is not null, false, "retain-until-explicit-removal",
        ["Optional admission reserve only; does not enforce ongoing per-app volume quotas or stop writes from running apps.",
         "Enable only with an administrator-confirmed local filesystem backing Docker managed data volumes; no remote-Docker capacity inference.",
         "Docker-reported volume usage is a point-in-time estimate, and can be unknown. No automatic data purge, volume migration or off-device backup.",
         "Backup retention is separately bounded by app-backups. Application data and retained data survive stop, restart and failed deployment."]);
    private ApplicationStorageBudget? ReadStorageCapacity()
    {
        if (StorageConfiguration is not { } config) return null;
        if (!config.BackingFilesystemConfirmed || !Path.IsPathFullyQualified(config.CapacityDirectory)) throw new InvalidOperationException("Confirm the local Docker backing filesystem before enabling app storage admission.");
        var capacity = StorageCapacityProbe.Read(config.CapacityDirectory);
        return (config.Policy ?? new ApplicationStoragePolicy()).Evaluate(capacity.TotalBytes, capacity.FreeBytes);
    }
    private void RequireStorageAdmission()
    {
        if (ReadStorageCapacity() is { CanAllocate: false }) throw new IOException("App data allocation would cross the configured storage reserve.");
    }
    public async Task<ApplicationStorageStatus> StorageStatus(CancellationToken ct)
    {
        var capacity = ReadStorageCapacity();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await engine.GetAsync($"{Api}/system/df", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 1048576) throw new IOException("Docker storage report exceeded its bounded metadata limit.");
        await using var source = await response.Content.ReadAsStreamAsync(deadline.Token); using var output = new MemoryStream();
        var buffer = new byte[16384]; int count;
        while ((count = await source.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (output.Length + count > 1048576) throw new IOException("Docker storage report exceeded its bounded metadata limit.");
            output.Write(buffer, 0, count);
        }
        using var json = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        var volumes = new List<ApplicationVolumeUsage>();
        if (json.RootElement.TryGetProperty("Volumes", out var entries) && entries.ValueKind != JsonValueKind.Null)
        {
            if (entries.ValueKind != JsonValueKind.Array) throw new IOException("Invalid Docker volume usage metadata.");
            foreach (var volume in entries.EnumerateArray())
            {
                if (!volume.TryGetProperty("Labels", out var labels) || labels.ValueKind != JsonValueKind.Object ||
                    !labels.TryGetProperty(Label, out var managed) || managed.GetString() != "true" ||
                    !labels.TryGetProperty(Label + ".app", out var appValue)) continue;
                string? app = appValue.GetString();
                if (app is null || !IdentityRules.IsId(app) || app.Length > 40 || volume.GetProperty("Name").GetString() != "pidock-app-" + app + "-data" ||
                    volume.GetProperty("Driver").GetString() != "local") continue;
                if (volume.TryGetProperty("Options", out var options) && options.ValueKind != JsonValueKind.Null &&
                    (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any())) continue;
                if (volumes.Count >= 64) throw new IOException("Managed app volume count exceeds the preview limit.");
                long? bytes = null, references = null;
                if (volume.TryGetProperty("UsageData", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("Size", out var size) && size.TryGetInt64(out var used) && used >= 0) bytes = used;
                    if (usage.TryGetProperty("RefCount", out var refs) && refs.TryGetInt64(out var refsValue) && refsValue >= 0) references = refsValue;
                }
                volumes.Add(new(app, bytes, references, bytes.HasValue ? "docker-estimate" : "unknown"));
            }
        }
        return new(Now, capacity, volumes.OrderBy(v => v.AppId, StringComparer.Ordinal).ToArray());
    }
}
