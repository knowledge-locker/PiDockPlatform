using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiDock.Core;

namespace PiDock.Updater;

public static class FirstBootDiagnostics
{
    private const string Complete = BootDiagnostics.DirectoryPath + "/initial-complete";
    public static async Task Run(bool once = false)
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root" || !File.Exists("/etc/pidock/image-profile.json"))
            throw new UnauthorizedAccessException("Installer diagnostics require the image's local boot service.");
        Directory.CreateDirectory(BootDiagnostics.DirectoryPath);
        var elapsed = Stopwatch.StartNew();
        string previous = "", log = ReadLog(), boot = ReadBoot(), version = ReadVersion();
        do
        {
            bool node = File.Exists("/var/lib/pidock/node-mode");
            bool applied = File.Exists("/var/lib/pidock/appliance/media-provisioned");
            bool wifi = !node && File.Exists("/var/lib/pidock/network/wpa_supplicant.conf");
            bool device = Directory.Exists("/sys/class/net/wlan0"), address = HasWifiAddress();
            string control = wifi && device ? await Query("/usr/sbin/wpa_cli", "-i", "wlan0", "status") : "";
            string events = wifi ? await Query("/usr/bin/journalctl", "-b", "-u", "pidock-wifi.service", "-n", "100", "-o", "cat", "--no-pager", "--quiet") : "";
            bool clock = new ClockTrust(true).Allowed;
            string provision = UnitState(await Query("/usr/bin/systemctl", "is-active", "pidock-provision.service"));
            string network = node ? "USB expansion; external networking disabled" : BootDiagnostics.WifiStatus(wifi, device, address, control, events);
            string snapshot = $"mode={(node ? "Node" : "Main")}; setup={(applied ? "applied" : "pending")}; provision={provision}; wifi={network}; clock={(clock ? "authenticated" : "waiting for authenticated time")}";
            if (snapshot != previous || once)
            {
                string entry = $"{DateTimeOffset.UtcNow:O} timeTrusted={clock.ToString().ToLowerInvariant()} boot={boot} image={version} {snapshot}\n";
                log = BootDiagnostics.AppendBounded(log, entry);
                Write(BootDiagnostics.LogPath, log, true);
                await ExportToCard(log);
                previous = snapshot;
            }
            if (applied && (node || clock))
            {
                Write(Complete, "1\n", true);
                break;
            }
            if (once) break;
            await Task.Delay(TimeSpan.FromSeconds(5));
        } while (elapsed.Elapsed < TimeSpan.FromMinutes(3));
        log = BootDiagnostics.AppendBounded(log, $"{DateTimeOffset.UtcNow:O} installer diagnostics captured; detailed journal remains private on the Linux data partition.\n");
        Write(BootDiagnostics.LogPath, log, true);
        await ExportToCard(log);
    }

    public static string ReadLog()
    {
        var file = new FileInfo(BootDiagnostics.LogPath);
        return file.Exists && file.Length <= BootDiagnostics.MaximumBytes && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            ? File.ReadAllText(file.FullName) : "";
    }

    private static string ReadBoot()
    {
        try { return Guid.TryParse(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(), out var id) ? id.ToString() : "unknown"; }
        catch (IOException) { return "unknown"; }
    }
    private static string ReadVersion()
    {
        using var profile = JsonDocument.Parse(File.ReadAllText("/etc/pidock/image-profile.json"));
        string version = profile.RootElement.GetProperty("version").GetString() ?? "unknown";
        return Regex.IsMatch(version, "^[A-Za-z0-9.+-]{1,64}$", RegexOptions.CultureInvariant) ? version : "unknown";
    }
    private static bool HasWifiAddress()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().Where(x => x.Name == "wlan0").Any(x => x.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))); }
        catch (NetworkInformationException) { return false; }
    }
    private static string UnitState(string value) => value.Trim() switch
    {
        "active" => "active", "failed" => "failed", "activating" => "starting", "inactive" => "inactive", _ => "unavailable"
    };
    private static async Task<string> Query(string program, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            async Task<string> Read(StreamReader reader)
            {
                var text = new StringBuilder(); var buffer = new char[2048]; int count;
                while ((count = await reader.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (text.Length + count > 65536) { process.Kill(true); break; }
                    text.Append(buffer, 0, count);
                }
                return text.ToString();
            }
            var output = Read(process.StandardOutput); var errors = Read(process.StandardError);
            try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, errors); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); return ""; }
            return await output;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return ""; }
    }
    private static void Write(string path, string text, bool permissions)
    {
        string temporary = path + ".next";
        if (File.Exists(temporary) && new FileInfo(temporary).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Diagnostic log link rejected.");
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(text); writer.Flush(); stream.Flush(true);
        }
        if (permissions && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        File.Move(temporary, path, true);
    }
    private static async Task ExportToCard(string log)
    {
        // This runs only before the updater starts, and never during a trial boot.
        const string trial = "/proc/device-tree/chosen/bootloader/tryboot";
        if (File.Exists(trial) && File.ReadAllBytes(trial).Any(x => x != 0)) return;
        bool writable = false;
        try
        {
            writable = await Mount("remount,rw");
            if (writable) Write(BootDiagnostics.CardLogPath, BootDiagnostics.AppendBounded("PiDockOS installer diagnostics — sanitized status only; not a security attestation.\n", log), false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.WriteLine("Installer report remains available on the Linux data partition; card export unavailable."); }
        finally { if (!await Mount("remount,ro")) throw new IOException("Cannot restore boot partition protection."); }
    }
    private static async Task<bool> Mount(string options)
    {
        var start = new ProcessStartInfo("/bin/mount") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-o"); start.ArgumentList.Add(options); start.ArgumentList.Add("/boot/control");
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token); return process.ExitCode == 0; }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); return false; }
    }
}
