using System.Text.Json;
using PiDock.Core;

namespace PiDock.Updater;

public interface IUpdateHost
{
    Task Stop(CancellationToken ct);
    Task Start(CancellationToken ct);
    Task<bool> Healthy(string? digest, CancellationToken ct);
}
public sealed record UpdateState(bool CloudEnabled = false, OsRelease? Installed = null, OsRelease? Staged = null,
    IReadOnlyList<OsUpdateOperation>? Operations = null);

public sealed class UpdateEngine
{
    private readonly string root, installRoot, trust, runtime, statePath;
    private readonly IUpdateHost host;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateGate = new();
    private UpdateState state;
    public UpdateEngine(string root, string installRoot, string trust, string runtime, IUpdateHost host)
    {
        this.root = StateFiles.PrepareDirectory(root); this.installRoot = Path.GetFullPath(installRoot); CheckDirectory(this.installRoot);
        this.trust = trust; this.runtime = runtime; this.host = host;
        statePath = Path.Combine(root, "state.json");
        state = File.Exists(statePath) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(statePath), StateFiles.Json) ?? throw new IOException("Invalid updater journal.") : new();
    }
    public OsUpdateSnapshot Snapshot
    {
        get { lock (stateGate) return new(DateTimeOffset.UtcNow, true, state.CloudEnabled, runtime, state.Installed?.Manifest.Version,
            state.Installed?.Digest, state.Staged?.Info, state.Operations?.LastOrDefault(), "Independent updater ready"); }
    }
    private void Save(UpdateState next) { lock (stateGate) { StateFiles.WriteAtomic(statePath, JsonSerializer.Serialize(next, StateFiles.Json)); state = next; } }
    private void Change(Func<UpdateState, UpdateState> update) { lock (stateGate) Save(update(state)); }
    private void Operation(OsUpdateOperation operation) => Change(s => s with { Operations = (s.Operations ?? []).Where(o => o.Id != operation.Id).Append(operation).TakeLast(128).ToArray() });
    private void Transition(OsUpdateOperation op, string status, string message) => Operation(op with { State = status, Message = message, ChangedAt = DateTimeOffset.UtcNow });
    public async Task SetCloudEnabled(bool enabled, CancellationToken ct = default)
    {
        // Revocation remains available while installation is running; accepted installation may already be irreversible without rollback.
        await Task.CompletedTask;
        Change(s => s with { CloudEnabled = enabled });
    }
    public async Task<OsRelease> Stage(Stream input, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("An update operation is already running.");
        string incoming = Path.Combine(root, "incoming.zip");
        try
        {
            if (OsUpdateRules.Busy(Snapshot.Operation?.State) || Snapshot.Operation?.State == "RecoveryFailed") throw new InvalidOperationException("Finish the current update or repair recovery first.");
            RequireSpace(OsUpdateRules.MaximumPackageBytes);
            await using (var output = new FileStream(incoming, FileMode.Create, FileAccess.Write, FileShare.None)) { await OsUpdatePackage.CopyBounded(input, output, ct); output.Flush(true); }
            var release = OsUpdatePackage.Verify(incoming, trust, runtime);
            File.Move(incoming, Path.Combine(root, "package.zip"), true);
            Change(s => s with { Staged = release }); return release;
        }
        finally { File.Delete(incoming); gate.Release(); }
    }
    public async Task<OsUpdateOperation> Install(OsUpdateRequest request, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("An update operation is already running.");
        try
        {
            if (request.Id == Guid.Empty || !OsUpdateRules.Digest(request.Digest) || request.Source is not ("local" or "cloud") || request.RequestedBy is not { Length: > 0 and <= 64 }) throw new ArgumentException("Invalid update request.");
            var previous = state.Operations?.FirstOrDefault(o => o.Id == request.Id);
            if (previous is not null) return previous.Digest == request.Digest ? previous : throw new InvalidOperationException("Update request identity was reused.");
            if (request.Source == "cloud" && !Snapshot.CloudEnabled) throw new InvalidOperationException("Cloud-managed PiDockOS updates are disabled locally.");
            if (OsUpdateRules.Busy(Snapshot.Operation?.State) || Snapshot.Operation?.State == "RecoveryFailed") throw new InvalidOperationException("An update or recovery is already in progress.");
            var release = state.Staged;
            if (release?.Digest != request.Digest) throw new InvalidOperationException("Upload and verify this exact package first.");
            var operation = new OsUpdateOperation(request.Id, request.Digest, release.Manifest.Version, "Accepted", request.Source == "cloud" ? "Cloud update approved" : "Local update approved", DateTimeOffset.UtcNow, request.Source, request.RequestedBy);
            Operation(operation); return operation;
        }
        finally { gate.Release(); }
    }
    public async Task Recover(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var op = Snapshot.Operation;
            if (op is null) return;
            if (op.State is "Accepted" or "Preparing") { Transition(op, "Failed", "Update interrupted before service replacement; submit a new request."); Cleanup(op.Id); return; }
            if (OsUpdateRules.Busy(op.State) || op.State == "RecoveryFailed") await Rollback(op, ct);
        }
        finally { gate.Release(); }
    }
    public async Task RunPending(CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            var op = Snapshot.Operation;
            if (op?.State != "Accepted") return;
            if (op.Source == "cloud" && !Snapshot.CloudEnabled) { Transition(op, "Rejected", "Local cloud-update permission was revoked before installation"); return; }
            string transaction = Transaction(op.Id), next = Path.Combine(transaction, "next");
            try
            {
                Transition(op, "Preparing", "Verifying and staging signed program files");
                var release = OsUpdatePackage.Verify(Path.Combine(root, "package.zip"), trust, runtime);
                if (release.Digest != op.Digest) throw new InvalidDataException("The staged package changed.");
                RequireSpace(release.Manifest.Files.Sum(f => f.Length) * 2);
                foreach (string part in Parts) CheckDirectory(Path.Combine(installRoot, part));
                StateFiles.PrepareDirectory(transaction); StateFiles.PrepareDirectory(next);
                OsUpdatePackage.Verify(Path.Combine(root, "package.zip"), trust, runtime, next);
                foreach (string part in Parts)
                {
                    string directory = Path.Combine(next, part);
                    StateFiles.WriteAtomic(Path.Combine(directory, "pidock-release.json"), JsonSerializer.Serialize(release, StateFiles.Json));
                    if (!OperatingSystem.IsWindows())
                    {
                        foreach (var path in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories).Prepend(directory)) File.SetUnixFileMode(path, (UnixFileMode)0x1ed);
                        foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                        {
                            File.SetUnixFileMode(path, Path.GetFileName(path) is "PiDock.Appliance.Web" or "PiDock.Appliance.Broker" ? (UnixFileMode)0x1ed : (UnixFileMode)0x1a4);
                            using var persisted = File.Open(path, FileMode.Open, FileAccess.Write); persisted.Flush(true);
                        }
                        foreach (var path in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length).Append(directory)) StateFiles.SynchronizeDirectory(path);
                    }
                }
                // Journal is durable before either service is stopped or any installed directory is moved.
                lock (stateGate)
                {
                    if (op.Source == "cloud" && !state.CloudEnabled) { Transition(op, "Rejected", "Local cloud-update permission was revoked before installation"); Cleanup(op.Id); return; }
                    Transition(op, "Installing", "Replacing PiDockOS services");
                }
                await host.Stop(ct);
                foreach (string part in Parts)
                {
                    StateFiles.MoveDirectory(Path.Combine(installRoot, part), Path.Combine(transaction, "previous-" + part));
                    StateFiles.MoveDirectory(Path.Combine(next, part), Path.Combine(installRoot, part));
                }
                await host.Start(ct);
                Transition(op, "Checking", "Waiting for both services and fresh monitoring to become healthy");
                if (!await host.Healthy(release.Digest, ct)) throw new IOException("Candidate health checks failed.");
                Change(s => s with { Installed = release, Operations = (s.Operations ?? []).Where(o => o.Id != op.Id).Append(op with {
                    State = "Healthy", Message = "Update installed; both services passed health checks", ChangedAt = DateTimeOffset.UtcNow }).TakeLast(128).ToArray() });
                // Keep one previous program set. Persistent appliance state and volumes are never moved.
                try { foreach (var old in Directory.GetDirectories(installRoot, ".pidock-update-*")) if (old != transaction) Directory.Delete(old, true); }
                catch (IOException) { /* Cleanup can be retried after a later healthy update. */ }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.Security.Cryptography.CryptographicException or JsonException or OperationCanceledException)
            {
                if (Snapshot.Operation?.State is "Installing" or "Checking" or "RollingBack") await Rollback(op, CancellationToken.None);
                else { Transition(op, "Failed", "Package preparation failed; installed programs were not replaced"); Cleanup(op.Id); }
            }
        }
        finally { gate.Release(); }
    }
    private async Task Rollback(OsUpdateOperation op, CancellationToken ct)
    {
        Transition(op, "RollingBack", "Restoring the previous PiDockOS program files");
        try
        {
            await host.Stop(ct);
            string transaction = Transaction(op.Id);
            foreach (string part in Parts)
            {
                string previous = Path.Combine(transaction, "previous-" + part), installed = Path.Combine(installRoot, part);
                if (!Directory.Exists(previous)) continue; // This component had not yet been moved.
                string failed = Path.Combine(transaction, "failed-" + part);
                if (Directory.Exists(installed)) { if (Directory.Exists(failed)) Directory.Delete(failed, true); StateFiles.MoveDirectory(installed, failed); }
                StateFiles.MoveDirectory(previous, installed);
            }
            await host.Start(ct);
            if (!await host.Healthy(state.Installed?.Digest, ct)) throw new IOException("Previous release did not recover.");
            Transition(op, "RolledBack", "Previous program files restored and healthy; persistent data preserved");
            try { if (Directory.Exists(transaction)) Directory.Delete(transaction, true); } catch (IOException) { }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        { Transition(op, "RecoveryFailed", "Automatic recovery needs local administrator attention; backups and journal retained"); }
    }
    private void Cleanup(Guid id) { try { string path = Transaction(id); if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } }
    private static void CheckDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new IOException("Installed program directories are required.");
        for (var entry = new DirectoryInfo(path); entry is not null; entry = entry.Parent)
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Update paths cannot contain links.");
    }
    private string Transaction(Guid id) => Path.Combine(installRoot, ".pidock-update-" + id.ToString("N"));
    private static readonly string[] Parts = ["appliance", "broker"];
    private void RequireSpace(long bytes)
    {
        foreach (string path in new[] { root, installRoot })
            if (DriveInfo.GetDrives().Where(d => d.IsReady && (path == d.RootDirectory.FullName || path.StartsWith(d.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))).OrderByDescending(d => d.RootDirectory.FullName.Length).First().AvailableFreeSpace < (1L << 30) + bytes) throw new IOException("Insufficient free space for staging and rollback reserve.");
    }
}
