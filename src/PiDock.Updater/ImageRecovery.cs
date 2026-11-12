using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiDock.Core;

namespace PiDock.Updater;

public static class ImageRecovery
{
    private static async Task<string> Command(string program,params string[] args)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var start=new ProcessStartInfo(program){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Recovery host command unavailable.");
        try
        {
            var output=process.StandardOutput.ReadToEndAsync(timeout.Token);var errors=process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);var text=await output;_=await errors;
            if(process.ExitCode!=0||text.Length>16384)throw new IOException("Recovery host operation failed.");
            return text;
        }
        catch {if(!process.HasExited)process.Kill(true);throw;}
    }
    public static async Task Restore(string package)
    {
        PhysicalConsoleGuard.RequireRecovery();
        string directory=StateFiles.PrepareDirectory("/run/pidock-recovery/"+Guid.NewGuid().ToString("N"));
        try { await RestorePrivate(package,directory); }
        finally {Directory.Delete(directory,true);}
    }
    private static async Task RestorePrivate(string package,string directory)
    {
        string staged=Path.Combine(directory,"image.zip");
        await using(var input=File.OpenRead(package))
        await using(var output=new FileStream(staged,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {await OsUpdatePackage.CopyBounded(input,output);output.Flush(true);}
        var release=OsUpdatePackage.Verify(staged,"/etc/pidock/update-trust","linux-arm64");
        if(release.Manifest.SchemaVersion!=2)throw new InvalidDataException("Choose a whole-system image package.");
        var profile=JsonSerializer.Deserialize<ImageProfile>(File.ReadAllText("/etc/pidock/recovery-profile.json"),StateFiles.Json)??throw new IOException("Recovery version policy unavailable.");
        if(release.Manifest.MinimumImageSequence<profile.MinimumSequence||release.Manifest.ImageSequence<profile.MinimumSequence)throw new InvalidDataException("Image is outside the signed recovery version policy.");
        var devices=(await Command("/bin/blkid","-t","PARTUUID=5049444b-05","-o","device")).Split('\n',StringSplitOptions.RemoveEmptyEntries);
        if(devices.Length!=1)throw new IOException("Exactly one PiDockOS system disk must be connected.");
        var match=Regex.Match(devices[0],@"\A/dev/(mmcblk[0-9]+p|nvme[0-9]+n[0-9]+p|sd[a-z]+)5\z",RegexOptions.CultureInvariant);
        if(!match.Success)throw new IOException("Unsupported recovery target.");
        string prefix=match.Groups[1].Value;
        Console.WriteLine($"Verified signed image {release.Manifest.Version}, SHA-256 {release.Digest}.");
        Console.WriteLine($"Both system slots on /dev/{prefix}[2,3,5,6] will be replaced. The managed data partition is preserved. This may restore an older signed OS within the recovery policy.");
        Console.Write("Type INSTALL IMAGE to continue: ");
        if(Console.ReadLine()!="INSTALL IMAGE"){Console.WriteLine("Image recovery cancelled.");return;}
        Directory.CreateDirectory("/state");
        var dataMount=File.ReadLines("/proc/mounts").Select(line=>line.Split(' ')).FirstOrDefault(parts=>parts.Length>=2&&parts[1]=="/state");
        if(dataMount is null)await Command("/bin/mount","-t","ext4","-o","nosuid,nodev,noexec","/dev/"+prefix+"7","/state");
        else if(dataMount[0]!="/dev/"+prefix+"7")throw new IOException("Recovery data mount is not on the selected system disk.");
        string state=StateFiles.PrepareDirectory("/state/lib/pidock/updater");
        var started=new OsUpdateOperation(Guid.NewGuid(),release.Digest,release.Manifest.Version,"RecoveryFailed","Physical image repair started; complete recovery before further updates",DateTimeOffset.UtcNow,"local","physical-console");
        StateFiles.WriteAtomic(Path.Combine(state,"image-state.json"),JsonSerializer.Serialize(new ImageUpdateState(false,null,null,[started]),StateFiles.Json));
        // Verify the complete, private RAM copy before any block write. Stream its already-verified
        // entries into the slots so recovery also fits on the 4 GiB reference appliance.
        using var archive=ZipFile.OpenRead(staged);
        {
            foreach(string slot in new[]{"A","B"})
                foreach(var (path,partition) in new[]{("system/rootfs.verity",slot=="A"?5:6),($"system/boot-{slot}.vfat",slot=="A"?2:3)})
                {
                    using var image=archive.GetEntry(path)!.Open();
                    await NativeImageHost.WritePartitionStream(prefix,image,release.Manifest.Files.Single(f=>f.Path==path),partition,CancellationToken.None);
                }
            Directory.CreateDirectory("/boot/control");
            await Command("/bin/mount","-t","vfat","-o","rw,nosuid,nodev,noexec","/dev/"+prefix+"1","/boot/control");
            try
            {
                const string next="/boot/control/autoboot.next";
                using(var output=new FileStream(next,FileMode.Create,FileAccess.Write,FileShare.None))
                {using var writer=new StreamWriter(output,leaveOpen:true);writer.Write("[all]\ntryboot_a_b=1\nboot_partition=2\n[tryboot]\nboot_partition=3\n");writer.Flush();output.Flush(true);}
                File.Move(next,"/boot/control/autoboot.txt",true);StateFiles.SynchronizeDirectory("/boot/control");
            }
            finally{await Command("/bin/umount","/boot/control");}
            var operation=new OsUpdateOperation(Guid.NewGuid(),release.Digest,release.Manifest.Version,"Recovered","Both system slots restored from signed local media; cloud update permission is off",DateTimeOffset.UtcNow,"local","physical-console");
            StateFiles.WriteAtomic(Path.Combine(state,"image-state.json"),JsonSerializer.Serialize(new ImageUpdateState(false,release,null,[operation]),StateFiles.Json));
            Console.WriteLine("System slots written and read back successfully. Restart normally; accounts and application data remain in place.");
        }
    }
}
