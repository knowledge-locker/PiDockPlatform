using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Cloud.Web;

public sealed record PublishedOsRelease(string TenantId, OsReleaseInfo Release, string PublishedBy, DateTimeOffset PublishedAt);
public sealed record CloudOsUpdate(OsUpdateCommand Command, OsUpdateOperation? Operation = null);
public sealed record OsCloudState(IReadOnlyList<PublishedOsRelease> Releases, IReadOnlyList<CloudOsUpdate> Commands);
public sealed class OsUpdateStore
{
    private readonly object gate = new(); private readonly SemaphoreSlim uploads = new(1, 1);
    private readonly string root, path, trust; private readonly FleetStore fleet; private readonly TimeProvider clock;
    private OsCloudState state;
    public OsUpdateStore(StateDirectory directory, FleetStore fleet, TimeProvider clock, IConfiguration settings)
    {
        this.fleet = fleet; this.clock = clock; root = StateFiles.PrepareDirectory(Path.Combine(directory.Path, "os-updates")); path = Path.Combine(root, "catalog.json");
        trust = settings["Updates:TrustDirectory"] ?? "/etc/pidock/update-trust";
        state = File.Exists(path) ? JsonSerializer.Deserialize<OsCloudState>(File.ReadAllText(path), StateFiles.Json) ?? throw new IOException("Invalid update catalog.") : new([], []);
    }
    public PublishedOsRelease[] Releases(ClaimsPrincipal user) { lock (gate) return fleet.HasAccess(user) ? state.Releases.Where(r => r.TenantId == user.FindFirstValue("tenant")).OrderByDescending(r => r.PublishedAt).ToArray() : []; }
    public CloudOsUpdate[] Operations(ClaimsPrincipal user, string device) { lock (gate) return fleet.HasAccess(user) ? state.Commands.Where(c => c.Command.TenantId == user.FindFirstValue("tenant") && c.Command.DeviceId == device).TakeLast(20).Reverse().ToArray() : []; }
    private void Save(OsCloudState next) { StateFiles.WriteAtomic(path, JsonSerializer.Serialize(next, StateFiles.Json)); state = next; }
    public async Task<PublishedOsRelease> Publish(ClaimsPrincipal user, Stream input, CancellationToken ct = default)
    {
        if (!fleet.HasAccess(user) || !EnrollmentStore.CanManage(user) || !IdentityRules.IsId(user.FindFirstValue("tenant"))) throw new UnauthorizedAccessException();
        if (!await uploads.WaitAsync(0, ct)) throw new InvalidOperationException("Another release upload is in progress.");
        string incoming = Path.Combine(root, "incoming.zip");
        try
        {
            lock (gate) if (state.Releases.Count >= 100 || Releases(user).Length >= 20) throw new InvalidOperationException("Release catalog quota reached.");
            if (new PiDock.Agent.DriveCapacityProbe().Read(root, 0).FreeBytes < (1L << 30) + OsUpdateRules.MaximumPackageBytes || new DirectoryInfo(root).GetFiles("*.zip").Sum(f => f.Length) > (8L << 30) - OsUpdateRules.MaximumPackageBytes) throw new IOException("Update artifact storage reserve reached.");
            await using (var output = new FileStream(incoming, FileMode.Create, FileAccess.Write, FileShare.None)) { await OsUpdatePackage.CopyBounded(input, output, ct); output.Flush(true); }
            var release = OsUpdatePackage.Verify(incoming, trust).Info;
            lock (gate)
            {
                if (!fleet.HasAccess(user)) throw new UnauthorizedAccessException();
                var existing = Releases(user).FirstOrDefault(r => r.Release.Version == release.Version && r.Release.Runtime == release.Runtime);
                if (existing is not null) return existing.Release.Digest == release.Digest ? existing : throw new InvalidOperationException("Release versions are immutable for each runtime.");
                string destination = Path.Combine(root, release.Digest + ".zip");
                if (!File.Exists(destination)) File.Move(incoming, destination);
                var entry = new PublishedOsRelease(user.FindFirstValue("tenant")!, release, user.Identity!.Name!, clock.GetUtcNow());
                Save(state with { Releases = state.Releases.Append(entry).ToArray() }); return entry;
            }
        }
        finally { File.Delete(incoming); uploads.Release(); }
    }
    public OsUpdateCommand Queue(ClaimsPrincipal user, string device, string digest)
    {
        if (!fleet.HasAccess(user) || !EnrollmentStore.CanManage(user)) throw new UnauthorizedAccessException();
        var target = fleet.For(user).SingleOrDefault(d => d.DeviceId == device) ?? throw new UnauthorizedAccessException();
        var now = clock.GetUtcNow();
        if (!target.HasLiveContact || now - target.ReceivedAt > TimeSpan.FromSeconds(90) || target.OsUpdates is not { Available: true, CloudEnabled: true } updates ||
            now - updates.ObservedAt > TimeSpan.FromSeconds(90) || updates.ObservedAt > now.AddSeconds(30) || OsUpdateRules.Busy(updates.Operation?.State) || updates.Operation?.State == "RecoveryFailed")
            throw new InvalidOperationException("The device must be online with a ready updater and local permission for cloud-managed PiDockOS updates.");
        lock (gate)
        {
            var release = Releases(user).SingleOrDefault(r => r.Release.Digest == digest) ?? throw new ArgumentException("Choose a published release in your organization.");
            if (release.Release.Runtime != updates.Runtime) throw new ArgumentException("This release does not match the device runtime.");
            if (state.Commands.Any(c => c.Command.TenantId == target.TenantId && c.Command.DeviceId == device && c.Command.ExpiresAt > now && (c.Operation is null || OsUpdateRules.Busy(c.Operation.State) || c.Operation.State == "Downloading"))) throw new InvalidOperationException("Wait for the current update request to finish or expire.");
            var retained = state.Commands.Where(c => c.Command.IssuedAt > now.AddDays(-7)).ToList();
            if (retained.Count >= 512) throw new InvalidOperationException("Update operation quota reached.");
            var command = new OsUpdateCommand(Guid.NewGuid(), target.TenantId, device, digest, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), now, now.AddMinutes(10), user.Identity!.Name!);
            if (!OsUpdateRules.Valid(command, target.TenantId, device, now)) throw new ArgumentException("Invalid update request.");
            retained.Add(new(command)); Save(state with { Commands = retained }); return command;
        }
    }
    public OsUpdateCommand? ForReceipt(TelemetryEnvelope frame)
    {
        var now = clock.GetUtcNow();
        if (frame.IsReplay || frame.Snapshot.ObservedAt < now.AddSeconds(-60) || frame.Snapshot.ObservedAt > now.AddSeconds(30) || frame.OsUpdates is not { Available: true, CloudEnabled: true } status ||
            status.ObservedAt < now.AddSeconds(-90) || status.ObservedAt > now.AddSeconds(30) || OsUpdateRules.Busy(status.Operation?.State)) return null;
        lock (gate) return state.Commands.FirstOrDefault(c => c.Command.TenantId == frame.TenantId && c.Command.DeviceId == frame.DeviceId && c.Command.ExpiresAt > now && c.Operation is null)?.Command;
    }
    public void AcceptStatus(TelemetryEnvelope frame)
    {
        if (frame.IsReplay || frame.OsUpdates?.Operation is not { } op || frame.Snapshot.ObservedAt < clock.GetUtcNow().AddSeconds(-90) || frame.Snapshot.ObservedAt > clock.GetUtcNow().AddSeconds(30) ||
            op.State is not ("Downloading" or "Accepted" or "Preparing" or "Installing" or "Checking" or "Healthy" or "RollingBack" or "RolledBack" or "RecoveryFailed" or "Failed" or "Rejected") || op.Message.Length > 256) return;
        lock (gate)
        {
            var next = state.Commands.ToArray();
            int index = Array.FindIndex(next, c => c.Command.Id == op.Id && c.Command.Digest == op.Digest && c.Command.TenantId == frame.TenantId && c.Command.DeviceId == frame.DeviceId);
            if (index < 0 || op.ChangedAt < next[index].Command.IssuedAt.AddSeconds(-30) || op.ChangedAt > clock.GetUtcNow().AddSeconds(30) || next[index].Operation?.ChangedAt >= op.ChangedAt) return;
            next[index] = next[index] with { Operation = op }; Save(state with { Commands = next });
        }
    }
    public string? Download(Guid id, string token)
    {
        if (token.Length != 64) return null;
        lock (gate)
        {
            var entry = state.Commands.FirstOrDefault(c => c.Command.Id == id && c.Command.ExpiresAt > clock.GetUtcNow());
            if (entry is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(entry.Command.DownloadToken), Encoding.UTF8.GetBytes(token))) return null;
            string file = Path.Combine(root, entry.Command.Digest + ".zip");
            return File.Exists(file) && !File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) ? file : null;
        }
    }
}
public static class OsUpdateEndpoints
{
    public static void MapOsUpdates(this WebApplication app)
    {
        app.MapGet("/api/os-updates/download/{id:guid}", (Guid id, HttpContext context, OsUpdateStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var authorization = context.Request.Headers.Authorization.ToString();
            var file = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? store.Download(id, authorization[7..]) : null;
            return file is null ? Results.NotFound() : Results.File(file, "application/zip", "pidockos-update.zip");
        });
    }
}
