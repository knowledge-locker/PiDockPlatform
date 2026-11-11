using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiDock.Core;
namespace PiDock.Cloud.Web;

internal sealed record ArchivedFrame(long Sequence, string Hash, TelemetryEnvelope Envelope);
public sealed class TelemetryArchive
{
    private readonly object gate = new();
    private readonly string root;
    private readonly long maximumBytes;
    private readonly Dictionary<string, (string Path, ArchivedFrame Frame, long Bytes)> frames = [];
    private long sequence, bytes;
    public TelemetryArchive(string directory, long maximumBytes = 100L << 20)
    {
        if (maximumBytes < 65536) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.maximumBytes = maximumBytes; root = StateFiles.PrepareDirectory(Path.Combine(directory, "telemetry-archive"));
        foreach (var file in new DirectoryInfo(root).GetFiles("*.json").Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            if (file.Length > 131072) throw new IOException("Archive segment exceeds its bound.");
            var frame = JsonSerializer.Deserialize<ArchivedFrame>(File.ReadAllText(file.FullName), StateFiles.Json) ?? throw new IOException("Invalid telemetry archive.");
            frames.Add(Key(frame.Envelope), (file.FullName, frame, file.Length)); sequence = Math.Max(sequence, frame.Sequence); bytes += file.Length;
        }
    }
    private static string Key(TelemetryEnvelope envelope) => $"{envelope.TenantId}/{envelope.DeviceId}/{envelope.MessageId:N}";
    public bool Store(TelemetryEnvelope envelope)
    {
        string canonical = JsonSerializer.Serialize(envelope with { IsReplay = false }, StateFiles.Json);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        lock (gate)
        {
            if (frames.TryGetValue(Key(envelope), out var existing)) return existing.Frame.Hash == hash;
            var frame = new ArchivedFrame(checked(++sequence), hash, envelope);
            string json = JsonSerializer.Serialize(frame, StateFiles.Json); int length = Encoding.UTF8.GetByteCount(json);
            if (length > maximumBytes || length > 131072) return false;
            foreach (var old in frames.OrderBy(f => f.Value.Frame.Sequence).ToArray())
            {
                if (bytes + length <= maximumBytes && frames.Count < 10000) break;
                File.Delete(old.Value.Path); bytes -= old.Value.Bytes; frames.Remove(old.Key);
            }
            string path = Path.Combine(root, $"{sequence:D20}_{envelope.MessageId:N}.json");
            StateFiles.WriteAtomic(path, json); frames.Add(Key(envelope), (path, frame, length)); bytes += length; return true;
        }
    }
    public IReadOnlyList<TelemetryEnvelope> Range(string tenant, string device, DateTimeOffset from, DateTimeOffset to)
    {
        lock (gate) return frames.Values.Where(f => f.Frame.Envelope.TenantId == tenant && f.Frame.Envelope.DeviceId == device &&
            f.Frame.Envelope.Snapshot.ObservedAt >= from && f.Frame.Envelope.Snapshot.ObservedAt <= to)
            .OrderBy(f => f.Frame.Envelope.Snapshot.ObservedAt).Take(10000).Select(f => f.Frame.Envelope).ToArray();
    }
    public IReadOnlyList<TelemetryEnvelope> Read(string tenant, string device, int limit)
    {
        limit = Math.Clamp(limit, 1, 100);
        lock (gate) return frames.Values.Where(f => f.Frame.Envelope.TenantId == tenant && f.Frame.Envelope.DeviceId == device)
            .OrderByDescending(f => f.Frame.Sequence).Take(limit).Select(f => f.Frame.Envelope).ToArray();
    }
}
