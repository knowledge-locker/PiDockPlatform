using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiDock.Core;

namespace PiDock.Updater;

public sealed record ImageProfile(int Schema,string Version,long Sequence,long MinimumSequence,bool Development);
public sealed class NativeImageHost : IImageHost,IDisposable
{
    private readonly HttpClient appliance,broker;
    private readonly string devicePrefix;
    public ImageBoot Boot {get;}
    private static uint DeviceTree(string property)
    {
        var bytes=File.ReadAllBytes("/proc/device-tree/chosen/bootloader/"+property);
        if(bytes.Length!=4)throw new IOException("Invalid bootloader property.");
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
    public NativeImageHost()
    {
        if(!OperatingSystem.IsLinux())throw new InvalidOperationException("Verified image updates require Linux.");
        var profile=JsonSerializer.Deserialize<ImageProfile>(File.ReadAllText("/etc/pidock/image-profile.json"),StateFiles.Json)??throw new IOException("Image profile missing.");
        if(profile.Schema!=1||profile.Sequence<=0||profile.MinimumSequence<0||profile.MinimumSequence>profile.Sequence)throw new IOException("Invalid image version policy.");
        var mapping=Directory.EnumerateDirectories("/sys/class/block","dm-*").Single(path=>File.ReadAllText(Path.Combine(path,"dm/name")).Trim()=="pidock-root");
        var slave=Path.GetFileName(Directory.EnumerateDirectories(Path.Combine(mapping,"slaves")).Single());
        var match=Regex.Match(slave,@"\A(mmcblk[0-9]+p|nvme[0-9]+n[0-9]+p|sd[a-z]+)([56])\z",RegexOptions.CultureInvariant);
        if(!match.Success)throw new IOException("Verified root is not on the supported A/B disk layout.");
        devicePrefix=match.Groups[1].Value;
        string slot=match.Groups[2].Value=="5"?"A":"B";
        if(DeviceTree("partition")!=(slot=="A"?2:3))throw new IOException("Firmware and root partition disagree.");
        Boot=new(slot,DeviceTree("tryboot")==1,profile.Sequence,profile.MinimumSequence,profile.Version,profile.Development);
        var handler=new HttpClientHandler {AllowAutoRedirect=false,UseCookies=false};
        handler.ServerCertificateCustomValidationCallback=(_,certificate,_,errors)=>{
            if(certificate is null||(errors&(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch|System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable))!=0)return false;
            using var root=System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile("/etc/pidock/appliance.crt");
            using var chain=new System.Security.Cryptography.X509Certificates.X509Chain();
            chain.ChainPolicy.TrustMode=System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode=System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
            return chain.Build(certificate);
        };
        appliance=new(handler){BaseAddress=new("https://localhost:7241"),Timeout=TimeSpan.FromSeconds(3)};
        broker=UnixHttp.Connect("/run/pidock/broker.sock",TimeSpan.FromSeconds(3));
    }
    private static async Task<string> Command(string program,CancellationToken ct,params string[] args)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var start=new ProcessStartInfo(program){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        start.Environment["LC_ALL"]="C";
        using var process=Process.Start(start)??throw new IOException("Host command unavailable.");
        try
        {
            var output=process.StandardOutput.ReadToEndAsync(timeout.Token);var errors=process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);var result=await output;_=await errors;
            if(process.ExitCode!=0||result.Length>16384)throw new IOException("Host image operation failed.");
            return result;
        }
        catch {if(!process.HasExited)process.Kill(true);throw;}
    }
    private string Partition(int number)=>"/dev/"+devicePrefix+number;
    internal static async Task WritePartition(string prefix,string input,OsUpdateFile metadata,int partition,CancellationToken ct)
    {
        await using var source=new FileStream(input,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(source.Length!=metadata.Length)throw new IOException("Staged image changed.");
        await WritePartitionStream(prefix,source,metadata,partition,ct);
    }
    internal static async Task WritePartitionStream(string prefix,Stream source,OsUpdateFile metadata,int partition,CancellationToken ct)
    {
        if(!Regex.IsMatch(prefix,@"\A(mmcblk[0-9]+p|nvme[0-9]+n[0-9]+p|sd[a-z]+)\z",RegexOptions.CultureInvariant)||partition is not(2 or 3 or 5 or 6))throw new ArgumentException("Unsupported system partition.");
        string name=prefix+partition;
        if(!long.TryParse(File.ReadAllText("/sys/class/block/"+name+"/size"),out var sectors)||sectors<=0||metadata.Length>sectors*512L)
            throw new IOException("Signed image does not fit its inactive partition.");
        if(File.ReadAllText("/sys/class/block/"+name+"/partition").Trim()!=partition.ToString(System.Globalization.CultureInfo.InvariantCulture))throw new IOException("Target is not the expected block partition.");
        await using(var target=new FileStream("/dev/"+name,FileMode.Open,FileAccess.Write,FileShare.None))
        {
            var copy=new byte[1<<20];long left=metadata.Length;
            while(left>0)
            {
                int count=await source.ReadAsync(copy.AsMemory(0,(int)Math.Min(copy.Length,left)),ct);
                if(count==0)throw new IOException("Staged image was truncated.");
                await target.WriteAsync(copy.AsMemory(0,count),ct);left-=count;
            }
            if(await source.ReadAsync(copy.AsMemory(0,1),ct)!=0)throw new IOException("Staged image exceeds its signed size.");
            target.Flush(true);
        }
        await using var readback=new FileStream("/dev/"+name,FileMode.Open,FileAccess.Read,FileShare.Read);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer=new byte[1<<20];long remaining=metadata.Length;
        while(remaining>0)
        {
            int count=await readback.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,remaining)),ct);
            if(count==0)throw new IOException("Inactive partition readback was truncated.");
            hash.AppendData(buffer,0,count);remaining-=count;
        }
        if(Convert.ToHexStringLower(hash.GetHashAndReset())!=metadata.Sha256)throw new IOException("Inactive partition readback failed.");
    }
    public async Task WriteCandidate(string slot,string directory,OsRelease release,CancellationToken ct)
    {
        if(slot is not("A" or "B")||slot==Boot.Slot)throw new InvalidOperationException("Only the inactive slot may be written.");
        int root=slot=="A"?5:6,boot=slot=="A"?2:3;
        foreach(var (path,partition) in new[]{("system/rootfs.verity",root),($"system/boot-{slot}.vfat",boot)})
            await WritePartition(devicePrefix,Path.Combine(directory,path),release.Manifest.Files.Single(f=>f.Path==path),partition,ct);
    }
    private async Task Select(string active,string candidate,CancellationToken ct)
    {
        if(active is not("A" or "B")||candidate is not("A" or "B")||active==candidate)throw new ArgumentException("Invalid boot selection.");
        // This is the only mutable boot-selector file. Boot ramdisks, kernel and root hashes remain signed.
        await Command("/usr/bin/mount",ct,"-o","remount,rw","/boot/control");
        const string next="/boot/control/autoboot.next",path="/boot/control/autoboot.txt";
        try
        {
            var text=$"[all]\ntryboot_a_b=1\nboot_partition={(active=="A"?2:3)}\n[tryboot]\nboot_partition={(candidate=="A"?2:3)}\n";
            using(var file=new FileStream(next,FileMode.Create,FileAccess.Write,FileShare.None))
            {using var writer=new StreamWriter(file,leaveOpen:true);writer.Write(text);writer.Flush();file.Flush(true);}
            File.Move(next,path,true);StateFiles.SynchronizeDirectory("/boot/control");
        }
        finally {await Command("/usr/bin/mount",CancellationToken.None,"-o","remount,ro","/boot/control");}
    }
    public async Task Trial(string current,string candidate,CancellationToken ct)
    {
        if(current!=Boot.Slot||Boot.Trial)throw new InvalidOperationException("An image trial cannot be nested.");
        await Select(current,candidate,ct);
        await Command("/usr/bin/systemctl",ct,"--reboot-argument=0 tryboot","reboot");
    }
    public async Task Promote(string slot,CancellationToken ct)
    {
        if(slot!=Boot.Slot)throw new InvalidOperationException("Only the running verified slot may be promoted.");
        await Select(slot,slot=="A"?"B":"A",ct);
        Directory.CreateDirectory("/run/pidock-image");
        StateFiles.WriteAtomic("/run/pidock-image/healthy",slot);
    }
    public async Task RebootPrevious(CancellationToken ct)=>_=await Command("/usr/bin/systemctl",ct,"reboot");
    public async Task<bool> Healthy(CancellationToken ct)
    {
        for(int attempt=0;attempt<30;attempt++)
        {
            try
            {
                var status=await Command("/usr/sbin/dmsetup",ct,"status","pidock-root");
                bool rootValid=Regex.IsMatch(status,@"\A\s*[0-9]+\s+[0-9]+\s+verity\s+V\s*\z",RegexOptions.CultureInvariant)&&File.ReadLines("/proc/mounts").Any(line=>line.Split(' ') is {Length:>=4} parts&&parts[0]=="/dev/mapper/pidock-root"&&parts[1]=="/"&&parts[3].Split(',').Contains("ro"));
                var web=await appliance.GetFromJsonAsync<OsUpdateHealth>("/health/update",ct);
                var runtime=await broker.GetFromJsonAsync<OsUpdateHealth>("/health/update",ct);
                if(rootValid&&web is{Ready:true}&&runtime is{Ready:true})return true;
            }
            catch(Exception ex)when(ex is IOException or HttpRequestException or JsonException or OperationCanceledException) {if(ct.IsCancellationRequested)throw;}
            await Task.Delay(TimeSpan.FromSeconds(2),ct);
        }
        return false;
    }
    public void Dispose(){appliance.Dispose();broker.Dispose();}
}
