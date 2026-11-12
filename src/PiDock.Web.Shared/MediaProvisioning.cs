using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiDock.Core;
namespace PiDock.Web.Shared;

public sealed record MediaWifi(string Ssid,string Psk,string Country);
public sealed record MediaSetup(int Schema,string Username,string PasswordHash,string Secret,long LastStep,MediaWifi? Wifi);
public static class MediaProvisioning
{
    public static void Validate(MediaSetup setup)
    {
        if(setup.Schema!=1||!IdentityRules.IsId(setup.Username)||setup.Username.Length>64||
            !Regex.IsMatch(setup.Secret??"","^[A-Z2-7]{32}$",RegexOptions.CultureInvariant)||setup.LastStep<0||setup.LastStep>long.MaxValue/30)
            throw new InvalidDataException("Invalid initial owner setup.");
        byte[] hash;
        try { hash=Convert.FromBase64String(setup.PasswordHash??""); }catch(FormatException){throw new InvalidDataException("Invalid password hash.");}
        if(hash.Length!=61||hash[0]!=1||BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(1))!=2||
            BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(5))!=210000||BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(9))!=16)
            throw new InvalidDataException("Unsupported initial password hash.");
        if(setup.Wifi is {} w&&(!Regex.IsMatch(w.Ssid??"","^(?:[0-9a-f]{2}){1,32}$")||!Regex.IsMatch(w.Psk??"","^[0-9a-f]{64}$")||!Regex.IsMatch(w.Country??"","^[A-Z]{2}$")))throw new InvalidDataException("Invalid initial wireless setup.");
    }
    public static bool Import(WebApplication app,string[] args)
    {
        if(!args.Contains("--import-media"))return false;
        if(!OperatingSystem.IsLinux()||Environment.UserName!="root"||!File.Exists("/etc/pidock/image-profile.json"))throw new UnauthorizedAccessException("Initial media import requires the image boot service.");
        const string input="/boot/control/pidock-provisioning.json";
        if(!File.Exists(input))return true;
        var info=new FileInfo(input);
        if(info.Attributes.HasFlag(FileAttributes.ReparsePoint)||info.Length>1<<20)throw new InvalidDataException("Invalid provisioning file.");
        var json=File.ReadAllText(input).Trim();if(json=="{}")return true;
        var users=app.Services.GetRequiredService<UserStore>();
        var directory=app.Services.GetRequiredService<StateDirectory>().Path;
        string marker=Path.Combine(directory,"media-provisioned");
        // A consumed setup cannot reset ownership, even when replayed from the boot partition.
        if(!File.Exists(marker)&&!users.HasUsers) {
        var setup=JsonSerializer.Deserialize<MediaSetup>(json,StateFiles.Json)??throw new InvalidDataException("Missing provisioning setup.");Validate(setup);
        if(setup.Wifi is {} wifi) {
            var network=StateFiles.PrepareDirectory("/var/lib/pidock/network");
            StateFiles.WriteAtomic(Path.Combine(network,"wpa_supplicant.conf"),$"country={wifi.Country}\nctrl_interface=/run/wpa_supplicant\nnetwork={{\n ssid={wifi.Ssid}\n psk={wifi.Psk}\n key_mgmt=WPA-PSK\n ieee80211w=1\n}}\n");
        }
        users.ImportInitialMediaOwner(setup);
        StateFiles.WriteAtomic(marker,"1\n");
        }
        // The boot partition is mounted read-only during normal operation.
        using(var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/mount","-o remount,rw /boot/control"){UseShellExecute=false})!) {
            process.WaitForExit();if(process.ExitCode!=0)throw new IOException("Cannot clear consumed provisioning file.");
        }
        try { File.Delete(input); }finally {
            using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/mount","-o remount,ro /boot/control"){UseShellExecute=false})!;process.WaitForExit();if(process.ExitCode!=0)throw new IOException("Cannot restore boot partition protection.");
        }
        Console.WriteLine("Initial media setup applied. Owner, network and keys remain local.");return true;
    }
}
