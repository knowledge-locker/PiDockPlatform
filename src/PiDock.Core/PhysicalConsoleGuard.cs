using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PiDock.Core;

public static class PhysicalConsoleGuard
{
    [DllImport("libc", SetLastError=true)] private static extern int ttyname_r(int fd, StringBuilder buffer, nuint length);
    public static void RequireAttached()
    {
        if (!OperatingSystem.IsLinux() || Console.IsInputRedirected || Console.IsOutputRedirected || !File.Exists("/etc/pidock/image-profile.json"))
            throw new InvalidOperationException("This image operation requires its attached keyboard and display.");
        var terminal=new StringBuilder(256);
        if (ttyname_r(0,terminal,256) != 0 || terminal.ToString() != "/dev/tty1") throw new InvalidOperationException("Recovery is unavailable over SSH, serial or network terminals.");
    }
    public static void RequireRecovery()
    {
        if(!OperatingSystem.IsLinux())throw new InvalidOperationException("Physical image recovery requires Linux.");
        RequireAttached();
        using var profile=JsonDocument.Parse(File.ReadAllText("/etc/pidock/image-profile.json"));
        if(!profile.RootElement.TryGetProperty("profile",out var value)||value.GetString()!="recovery")throw new InvalidOperationException("Credential recovery requires the separate signed recovery environment.");
        const string marker="/run/pidock/physical-recovery";
        var file=new FileInfo(marker);
        if (!file.Exists || file.Length is <=0 or >64 || file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            (File.GetUnixFileMode(marker) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 || File.ReadAllText(marker).Trim() != "physical-console")
            throw new InvalidOperationException("Physical recovery confirmation is missing.");

    }
}
