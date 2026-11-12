using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiDock.Core;

namespace PiDock.Updater;

public static class HostTimeGuard
{
    public static string? SelectedServer(string sources) => sources.Split('\n')
        .Select(line => Regex.Match(line, @"^\^\*\s+(\S+)\s", RegexOptions.CultureInvariant))
        .Where(match => match.Success && IPAddress.TryParse(match.Groups[1].Value, out _)).Select(match => match.Groups[1].Value).SingleOrDefault();
    public static bool ValidMeasurement(string tracking, string packet, DateTimeOffset now)
    {
        static string Field(string text, string name) => text.Split('\n').Select(line => line.Split(':',2)).Where(parts => parts.Length == 2 && parts[0].Trim() == name).Select(parts => parts[1].Trim()).SingleOrDefault() ?? "";
        static double Number(string text) => double.TryParse(text.Split(' ',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
        double delay = Number(Field(tracking,"Root delay")), dispersion = Number(Field(tracking,"Root dispersion")), offset = Number(Field(tracking,"Last offset"));
        return Field(packet,"Authenticated") == "Yes" && Field(packet,"Leap status") == "Normal" && Field(tracking,"Leap status") == "Normal" &&
            double.IsFinite(delay) && delay >= 0 && double.IsFinite(dispersion) && dispersion >= 0 && double.IsFinite(offset) && delay / 2 + dispersion + Math.Abs(offset) <= 1 &&
            DateTimeOffset.TryParseExact(Field(tracking,"Ref time (UTC)"), ["ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy"], CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var reference) &&
            now - reference >= TimeSpan.Zero && now - reference <= TimeSpan.FromMinutes(15);
    }
    private static async Task<string> Query(params string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var start = new ProcessStartInfo("/usr/bin/chronyc") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-n"); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start) ?? throw new IOException("Time monitor unavailable.");
        try
        {
            // chronyc's local-only reports are bounded; read both streams to avoid pipe stalls.
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout; _ = await stderr;
            if (process.ExitCode != 0 || output.Length > 16384) throw new IOException("Time monitor rejected query.");
            return output;
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree:true); throw; }
    }
    public static async Task Run(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Time guard requires Linux.");
        const string directory = "/run/pidock-time";
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, (UnixFileMode)0x1ed);
        var clock = TimeProvider.System; var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        bool previousGood = false; ClockTrustSample? previous = null;
        while (!ct.IsCancellationRequested)
        {
            bool good = false;
            try
            {
                var server = SelectedServer(await Query("sources"));
                if (server is not null) good = ValidMeasurement(await Query("tracking"), await Query("ntpdata",server), clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            var sample = new ClockTrustSample(boot,clock.GetTimestamp(),clock.GetUtcNow(),false);
            bool stable = previous is not null && Math.Abs((sample.Utc - previous.Utc).TotalSeconds - clock.GetElapsedTime(previous.Monotonic,sample.Monotonic).TotalSeconds) <= 1;
            var temporary = Path.Combine(directory,Guid.NewGuid()+".tmp");
            try
            {
                using (var output = new FileStream(temporary, new FileStreamOptions { Mode=FileMode.CreateNew, Access=FileAccess.Write, UnixCreateMode=(UnixFileMode)0x1a4 }))
                {
                    JsonSerializer.Serialize(output,sample with { Trusted = good && previousGood && stable },StateFiles.Json);
                    output.Flush(true);
                }
                File.Move(temporary,Path.Combine(directory,"status.json"),true);
                StateFiles.SynchronizeDirectory(directory);
            }
            finally { File.Delete(temporary); }
            previous = sample; previousGood = good;
            await Task.Delay(TimeSpan.FromSeconds(2),ct);
        }
    }
}
