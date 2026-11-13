using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using PiDock.Core;
namespace PiDock.Agent;

public sealed class HostMonitor(HistoryStore history, ClockTrust? clock = null)
{
    private readonly object gate = new();
    private long previousIdle, previousTotal;
    private static long? MemoryValue(string text, string key)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal));
        return line is null ? null : long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
    }
    public HostSnapshot Read()
    {
        lock (gate)
        {
            double? cpu = null, temperature = null; long? totalMemory = null, availableMemory = null;
            double uptime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (OperatingSystem.IsLinux())
            {
                var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(s => long.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                long total = fields.Sum(), idle = fields[3] + fields[4];
                if (previousTotal > 0 && total > previousTotal) cpu = Math.Clamp(100d * (1 - (idle - previousIdle) / (double)(total - previousTotal)), 0, 100);
                previousTotal = total; previousIdle = idle;
                var memory = File.ReadAllText("/proc/meminfo"); totalMemory = MemoryValue(memory, "MemTotal"); availableMemory = MemoryValue(memory, "MemAvailable");
                uptime = double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture);
                const string thermal = "/sys/class/thermal/thermal_zone0/temp";
                if (File.Exists(thermal) && double.TryParse(File.ReadAllText(thermal), CultureInfo.InvariantCulture, out var milli)) temperature = milli / 1000;
            }
            var interfaces = new List<InterfaceSnapshot>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                try { var stats = nic.GetIPStatistics(); interfaces.Add(new(nic.Name, nic.NetworkInterfaceType.ToString(), nic.OperationalStatus.ToString(), stats.BytesReceived, stats.BytesSent, stats.IncomingPacketsWithErrors + stats.OutgoingPacketsWithErrors)); }
                catch (NetworkInformationException) { interfaces.Add(new(nic.Name, nic.NetworkInterfaceType.ToString(), "Unavailable", 0, 0, 0)); }
            }
            return new(DateTimeOffset.UtcNow, Environment.MachineName, RuntimeInformation.OSDescription, cpu, totalMemory, availableMemory, temperature, uptime, history.Status().Budget, interfaces, clock?.Description ?? "Host clock trust not enforced in this installation");
        }
    }
}
