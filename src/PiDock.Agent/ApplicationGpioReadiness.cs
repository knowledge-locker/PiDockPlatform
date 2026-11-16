using System.Runtime.InteropServices;
using System.Text;
using PiDock.Core;

namespace PiDock.Agent;

public interface IApplicationGpioReadinessProbe { ApplicationGpioHostEvidence Read(); }

// This runs in the platform process. No GPIO device is opened, allocated or written.
public sealed class ApplicationGpioReadinessProbe : IApplicationGpioReadinessProbe
{
    public ApplicationGpioHostEvidence Read()
    {
        string architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        if (!OperatingSystem.IsLinux()) return new(false, architecture, null, [], "observed");
        string? model = null;
        string[] nodes = [];
        bool complete = true;
        try
        {
            const string path = "/sys/firmware/devicetree/base/model";
            if (File.Exists(path))
            {
                // The fixed kernel-owned path is not configurable by an app. Bound before decoding.
                using var stream = File.OpenRead(path);
                var bytes = new byte[257]; int count = 0;
                while (count < bytes.Length)
                {
                    int read = stream.Read(bytes, count, bytes.Length - count);
                    if (read == 0) break;
                    count += read;
                }
                if (count > 256) complete = false;
                else
                {
                    var value = new UTF8Encoding(false, true).GetString(bytes, 0, count).TrimEnd('\0');
                    if (value.Length > 0 && !value.Any(char.IsControl)) model = value;
                    else complete = false;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException) { complete = false; }
        try
        {
            // Presence only: filenames are deliberately not reported as a detected ABI or line mapping.
            var candidates = Directory.EnumerateFileSystemEntries("/dev", "gpiochip*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).Where(IsChipName).Select(n => n!).Take(17).ToArray();
            if (candidates.Length > 16) complete = false;
            nodes = candidates.Take(16).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { complete = false; }
        return new(true, architecture, model, nodes, complete ? "observed" : "incomplete");
    }
    private static bool IsChipName(string? name) => name is { Length: >= 9 and <= 12 } && name.StartsWith("gpiochip", StringComparison.Ordinal) && name.AsSpan(8).IndexOfAnyExceptInRange('0', '9') < 0;
}

public sealed class ApplicationGpioReadinessService(IApplicationGpioReadinessProbe probe)
{
    public ApplicationGpioReadinessStatus Read() => ApplicationGpioReadinessRules.Evaluate(probe.Read());
}
