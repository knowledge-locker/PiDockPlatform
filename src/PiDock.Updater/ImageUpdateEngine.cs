using System.Security.Cryptography;
using System.Text.Json;
using PiDock.Core;

namespace PiDock.Updater;

public sealed record ImageBoot(string Slot, bool Trial, long Sequence, long MinimumSequence, string Version,bool Development=true);
public interface IImageHost
{
    ImageBoot Boot { get; }
    Task WriteCandidate(string slot,string directory,OsRelease release,CancellationToken ct);
    Task Trial(string current,string candidate,CancellationToken ct);
    Task Promote(string slot,CancellationToken ct);
    Task RebootPrevious(CancellationToken ct);
    Task<bool> Healthy(CancellationToken ct);
}
public sealed record ImageUpdateState(bool CloudEnabled=false,OsRelease? Installed=null,OsRelease? Staged=null,
    IReadOnlyList<OsUpdateOperation>? Operations=null,string? PreviousSlot=null,string? CandidateSlot=null);

// The writable journal coordinates recovery; every executable byte still has to pass signed boot and verity.
public sealed class ImageUpdateEngine : IOsUpdateEngine
{
    private readonly string root,trust,path;
    private readonly IImageHost host;
    private readonly object stateGate=new();
    private readonly SemaphoreSlim gate=new(1);
    private ImageUpdateState state;
    public ImageUpdateEngine(string directory,string trust,IImageHost host)
    {
        root=StateFiles.PrepareDirectory(directory); this.trust=trust; this.host=host; path=Path.Combine(root,"image-state.json");
        state=File.Exists(path)?JsonSerializer.Deserialize<ImageUpdateState>(File.ReadAllText(path),StateFiles.Json)??throw new IOException("Invalid image journal."):new();
    }
    public OsUpdateSnapshot Snapshot
    {
        get
        {
            lock(stateGate)
            {
                var boot=host.Boot;
                return new(DateTimeOffset.UtcNow,true,state.CloudEnabled,"linux-arm64",boot.Version,
                    state.Installed?.Manifest.ImageSequence==boot.Sequence?state.Installed.Digest:null,state.Staged?.Info,state.Operations?.LastOrDefault(),
                    boot.Development?"Development image updater; hardware boot trust is not provisioned":"Verified-root A/B image updater ready","image");
            }
        }
    }
    private void Change(Func<ImageUpdateState,ImageUpdateState> update)
    {
        lock(stateGate)
        {
            var next=update(state); StateFiles.WriteAtomic(path,JsonSerializer.Serialize(next,StateFiles.Json)); state=next;
        }
    }
    private void Transition(OsUpdateOperation operation,string status,string message) => Change(s=>s with {
        Operations=(s.Operations??[]).Where(x=>x.Id!=operation.Id).Append(operation with {State=status,Message=message,ChangedAt=DateTimeOffset.UtcNow}).TakeLast(128).ToArray() });
    public Task SetCloudEnabled(bool enabled,CancellationToken ct=default) { Change(s=>s with {CloudEnabled=enabled}); return Task.CompletedTask; }
    private void CheckRelease(OsRelease release)
    {
        var boot=host.Boot;
        if(release.Manifest.SchemaVersion!=2 || release.Manifest.ImageSequence is not {} sequence || release.Manifest.MinimumImageSequence is not {} minimum ||
            sequence<=boot.Sequence || minimum<boot.MinimumSequence || minimum>boot.Sequence)
            throw new InvalidDataException("Image must advance this device's sequence and retain an allowed health-rollback slot.");
    }
    private void CheckIdle()
    {
        if(OsUpdateRules.Busy(Snapshot.Operation?.State)||Snapshot.Operation?.State=="RecoveryFailed"||host.Boot.Trial)
            throw new InvalidOperationException("An image trial or recovery is already active.");
    }
    private void Space(long bytes)
    {
        var mount=DriveInfo.GetDrives().Where(d=>d.IsReady&&(root==d.RootDirectory.FullName||root.StartsWith(d.RootDirectory.FullName.TrimEnd('/')+"/",StringComparison.Ordinal))).OrderByDescending(d=>d.RootDirectory.FullName.Length).First();
        if(mount.AvailableFreeSpace<bytes+(1L<<30))throw new IOException("Image staging requires additional free space and a 1 GiB protected reserve.");
    }
    public async Task<OsRelease> Stage(Stream input,CancellationToken ct=default)
    {
        if(!await gate.WaitAsync(0,ct))throw new InvalidOperationException("An image operation is already active.");
        string incoming=Path.Combine(root,"image-incoming.zip");
        try
        {
            CheckIdle(); Space(OsUpdateRules.MaximumPackageBytes);
            await using(var output=new FileStream(incoming,FileMode.Create,FileAccess.Write,FileShare.None)) { await OsUpdatePackage.CopyBounded(input,output,ct); output.Flush(true); }
            var release=OsUpdatePackage.Verify(incoming,trust,"linux-arm64"); CheckRelease(release);
            File.Move(incoming,Path.Combine(root,"image-package.zip"),true); StateFiles.SynchronizeDirectory(root);
            Change(s=>s with {Staged=release}); return release;
        }
        finally {File.Delete(incoming);gate.Release();}
    }
    public async Task<OsUpdateOperation> Install(OsUpdateRequest request,CancellationToken ct=default)
    {
        if(!await gate.WaitAsync(0,ct))throw new InvalidOperationException("An image operation is already active.");
        try
        {
            if(request.Id==Guid.Empty||!OsUpdateRules.Digest(request.Digest)||request.Source is not("local" or "cloud")||request.RequestedBy is not{Length:>0 and <=64})throw new ArgumentException("Invalid image request.");
            var previous=state.Operations?.FirstOrDefault(x=>x.Id==request.Id);
            if(previous is not null)return previous.Digest==request.Digest?previous:throw new InvalidOperationException("Request identity was reused.");
            CheckIdle();
            if(request.Source=="cloud"&&!Snapshot.CloudEnabled)throw new InvalidOperationException("Cloud-managed updates are disabled locally.");
            var release=state.Staged;
            if(release is null||release.Digest!=request.Digest)throw new InvalidOperationException("Verify this exact image package first.");
            CheckRelease(release);
            var operation=new OsUpdateOperation(request.Id,release.Digest,release.Manifest.Version,"Accepted","Signed image installation approved",DateTimeOffset.UtcNow,request.Source,request.RequestedBy);
            Transition(operation,"Accepted",operation.Message); return operation;
        }
        finally {gate.Release();}
    }
    public async Task RunPending(CancellationToken ct=default)
    {
        if(!await gate.WaitAsync(0,ct))return;
        try
        {
            var operation=Snapshot.Operation;
            if(operation?.State!="Accepted")return;
            if(operation.Source=="cloud"&&!Snapshot.CloudEnabled) {Transition(operation,"Rejected","Local update permission was revoked before installation");return;}
            string extracted=Path.Combine(root,"image-candidate-"+operation.Id.ToString("N"));
            try
            {
                Transition(operation,"Preparing","Re-verifying signed image components");
                var release=OsUpdatePackage.Verify(Path.Combine(root,"image-package.zip"),trust,"linux-arm64");
                CheckRelease(release);
                if(release.Digest!=operation.Digest)throw new InvalidDataException("Staged image package changed.");
                Space(release.Manifest.Files.Sum(f=>f.Length));
                StateFiles.PrepareDirectory(extracted);
                OsUpdatePackage.Verify(Path.Combine(root,"image-package.zip"),trust,"linux-arm64",extracted);
                var boot=host.Boot;
                string candidate=boot.Slot=="A"?"B":"A";
                lock(stateGate)
                {
                    if(operation.Source=="cloud"&&!state.CloudEnabled) {Transition(operation,"Rejected","Local update permission was revoked before slot writes");return;}
                    Change(s=>s with {PreviousSlot=boot.Slot,CandidateSlot=candidate});
                    Transition(operation,"Installing","Writing and reading back the inactive system and boot slots");
                }
                await host.WriteCandidate(candidate,extracted,release,ct);
                Transition(operation,"Checking","Restarting once into the candidate; promotion requires verified-root and service health checks");
                await host.Trial(boot.Slot,candidate,ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested&&Snapshot.Operation?.State=="Checking") { /* Expected shutdown during trial reboot; retain the durable recovery journal. */ }
            catch(Exception ex) when(ex is IOException or InvalidOperationException or CryptographicException or JsonException or OperationCanceledException or ArgumentException)
            {Transition(operation,"Failed","Image preparation or trial request failed; the previous boot slot remains selected");}
            finally {if(Directory.Exists(extracted))Directory.Delete(extracted,true);}
        }
        finally {gate.Release();}
    }
    public async Task Recover(CancellationToken ct=default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var operation=Snapshot.Operation;
            if(operation is null)return;
            if(operation.State is "Accepted" or "Preparing" or "Installing") {Transition(operation,"Failed","Installation was interrupted before a trial was committed; previous boot selection retained");return;}
            if(operation.State is not("Checking" or "RollingBack"))return;
            var boot=host.Boot;
            if(boot.Slot==state.PreviousSlot&&!boot.Trial)
            {
                Transition(operation,await host.Healthy(ct)?"RolledBack":"RecoveryFailed","Previous boot slot restored; inspect service health if recovery is incomplete");return;
            }
            if(boot.Slot!=state.CandidateSlot) {Transition(operation,"RecoveryFailed","Booted slot does not match the recorded image trial");return;}
            var release=state.Staged;
            bool valid=release is not null&&release.Digest==operation.Digest&&release.Manifest.ImageSequence==boot.Sequence&&release.Manifest.Version==boot.Version;
            if(operation.State=="Checking"&&valid&&await host.Healthy(ct))
            {
                await host.Promote(boot.Slot,ct);
                Change(s=>s with {Installed=release});
                Transition(operation,"Healthy","Candidate verified root and management services passed; boot slot promoted");
            }
            else
            {
                Transition(operation,"RollingBack","Candidate failed validation; restarting into the previous allowed slot");
                await host.RebootPrevious(ct);
            }
        }
        finally {gate.Release();}
    }
}
