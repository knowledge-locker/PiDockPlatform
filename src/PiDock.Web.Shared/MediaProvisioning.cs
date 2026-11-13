using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiDock.Core;
namespace PiDock.Web.Shared;

public sealed record MediaWifi(string Ssid,string Psk,string Country);
public sealed record MediaCloud(string Url, string? EnrollmentCode = null, bool RemoteControl = false, bool RemoteUi = false);
public sealed record MediaSetup(int Schema,string Username,string PasswordHash,string Secret,long LastStep,MediaWifi? Wifi,
    string Mode = "Main", MediaNetwork? Network = null, MediaCloud? Cloud = null);
public sealed record MediaCloudPlan(string Owner, string OwnerStamp, MediaCloud Cloud, bool Consumed = false);
public sealed class MediaCloudStatus { public string? PreferredUrl { get; set; } public string Status { get; set; } = ""; }
public static class MediaProvisioning
{
    public static bool OwnerCurrent(UserStore users, MediaCloudPlan plan) => users.IsLocalOwnerStampCurrent(plan.Owner,plan.OwnerStamp);
    public static void Validate(MediaSetup setup)
    {
        if (setup.Schema is not (1 or 2) || setup.Mode is not ("Main" or "Node")) throw new InvalidDataException("Unsupported image setup.");
        if (setup.Schema == 1 && (setup.Mode != "Main" || setup.Network is not null || setup.Cloud is not null)) throw new InvalidDataException("Expanded setup requires a compatible image.");
        if (setup.Schema == 2) {
            if (setup.Network is null) throw new InvalidDataException("Network configuration is required.");
            MediaNetworking.Validate(setup.Network, setup.Wifi, setup.Mode == "Node");
        }
        if (setup.Cloud is {} cloud) {
            if (!Uri.TryCreate(cloud.Url, UriKind.Absolute, out var origin) || origin.Scheme != "https" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/" || cloud.Url.Length > 512)
                throw new InvalidDataException("Enter an HTTPS cloud origin without a path, username or query.");
            if (cloud.EnrollmentCode is {} code && (code.Length != 32 || !code.All(char.IsAsciiHexDigit))) throw new InvalidDataException("Invalid cloud enrollment code.");
            if (cloud.EnrollmentCode is null && (cloud.RemoteControl || cloud.RemoteUi)) throw new InvalidDataException("Remote permissions require cloud registration.");
        }
        if (setup.Mode == "Node") {
            if (setup.Cloud is not null || setup.Username != "" || setup.PasswordHash != "" || setup.Secret != "" || setup.LastStep != 0 || setup.Wifi is not null)
                throw new InvalidDataException("Unpaired nodes do not accept owner or cloud configuration.");
            return;
        }
        if(!IdentityRules.IsId(setup.Username)||setup.Username.Length>64||
            !Regex.IsMatch(setup.Secret??"","^[A-Z2-7]{32}$",RegexOptions.CultureInvariant)||setup.LastStep<0||setup.LastStep>long.MaxValue/30)
            throw new InvalidDataException("Invalid initial owner setup.");
        byte[] hash;
        try { hash=Convert.FromBase64String(setup.PasswordHash??""); }catch(FormatException){throw new InvalidDataException("Invalid password hash.");}
        if(hash.Length!=61||hash[0]!=1||BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(1))!=2||
            BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(5))!=210000||BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(9))!=16)
            throw new InvalidDataException("Unsupported initial password hash.");
        if(setup.Wifi is {} w&&(!Regex.IsMatch(w.Ssid??"","^(?:[0-9a-f]{2}){1,32}$")||!Regex.IsMatch(w.Psk??"","^[0-9a-f]{64}$")||!Regex.IsMatch(w.Country??"","^[A-Z]{2}$")))throw new InvalidDataException("Invalid initial wireless setup.");
    }
    internal static void WriteSystemConfiguration(MediaSetup setup, string root = "/")
    {
        Validate(setup);
        string state=Path.Combine(root,"var/lib/pidock"), network=Path.Combine(state,"network");
        if(setup.Wifi is {} wifi)
            StateFiles.WriteAtomic(Path.Combine(network,"wpa_supplicant.conf"),$"country={wifi.Country}\nctrl_interface=/run/wpa_supplicant\nnetwork={{\n ssid={wifi.Ssid}\n psk={wifi.Psk}\n key_mgmt=WPA-PSK\n ieee80211w=1\n}}\n");
        if(setup.Schema != 2)return;
        StateFiles.WriteAtomic(Path.Combine(network,"interfaces.json"),JsonSerializer.Serialize(setup.Network,StateFiles.Json));
        try {
            StateFiles.WriteAtomic(Path.Combine(state,"device-mode.json"),JsonSerializer.Serialize(new { mode=setup.Mode },StateFiles.Json));
            if(setup.Mode=="Node")StateFiles.WriteAtomic(Path.Combine(state,"node-mode"),"unpaired\n");
        } finally {
            // Only individual state files are private. The appliance must traverse this root-owned parent.
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(state,(UnixFileMode)0x1ed);
        }
    }
    public static bool Import(WebApplication app,string[] args)
    {
        if(args.Contains("--apply-media-network")) {
            if(!OperatingSystem.IsLinux() || Environment.UserName!="root" || !File.Exists("/etc/pidock/image-profile.json")) throw new UnauthorizedAccessException("Image network setup requires the boot service.");
            const string networkFile="/var/lib/pidock/network/interfaces.json";
            if(File.Exists(networkFile)) {
                if(new FileInfo(networkFile).Length > 16384) throw new InvalidDataException("Invalid network configuration.");
                var network=JsonSerializer.Deserialize<MediaNetwork>(File.ReadAllText(networkFile),StateFiles.Json) ?? throw new InvalidDataException("Missing network configuration.");
                // Credentials were validated during import; use only their presence here, without reading them again.
                var wifi=File.Exists("/var/lib/pidock/network/wpa_supplicant.conf") ? new MediaWifi("00",new string('0',64),"US") : null;
                MediaNetworking.Validate(network,wifi,File.Exists("/var/lib/pidock/node-mode"));
                Directory.CreateDirectory("/run/systemd/network");
                StateFiles.WriteAtomic("/run/systemd/network/10-pidock-wired.network",MediaNetworking.Render(network.Ethernet,false));
                StateFiles.WriteAtomic("/run/systemd/network/10-pidock-wireless.network",MediaNetworking.Render(network.Wifi,true));
            }
            Directory.CreateDirectory("/run/systemd/network");
            StateFiles.WriteAtomic("/run/systemd/network/05-pidock-usb.network",MediaNetworking.UsbLink(File.Exists("/var/lib/pidock/node-mode")));
            StateFiles.WriteAtomic("/run/systemd/network/06-pidock-usb-deny.network",MediaNetworking.UsbFallback);
            File.SetUnixFileMode("/run/systemd/network",(UnixFileMode)0x1ed);
            foreach(var file in Directory.GetFiles("/run/systemd/network","*-pidock-*.network")) File.SetUnixFileMode(file,(UnixFileMode)0x1a4);
            return true;
        }
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
        WriteSystemConfiguration(setup);
        if (setup.Mode == "Main") {
            string stamp = users.ImportInitialMediaOwner(setup);
            if (setup.Cloud is {} cloud) {
                var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("PiDock.MediaCloud.v1");
                StateFiles.WriteAtomic(Path.Combine(directory,"media-cloud.protected"), protector.Protect(JsonSerializer.Serialize(new MediaCloudPlan(setup.Username,stamp,cloud),StateFiles.Json)));
            }
        }
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
