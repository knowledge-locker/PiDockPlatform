using System.Text.Json;
using PiDock.Core;
namespace PiDock.Agent;

public sealed class TelemetryPolicy
{
    private readonly object gate = new(); private readonly string path; private TelemetryChannels current;
    public TelemetryPolicy(string directory)
    {
        path = Path.Combine(StateFiles.PrepareDirectory(directory), "telemetry-channels.json");
        current = File.Exists(path) ? JsonSerializer.Deserialize<TelemetryChannels>(File.ReadAllText(path), StateFiles.Json)
            ?? throw new InvalidDataException("Telemetry channel policy is invalid.") : new();
    }
    public TelemetryChannels Current { get { lock(gate) return current; } }
    public void Save(TelemetryChannels channels)
    {
        lock(gate) { StateFiles.WriteAtomic(path, JsonSerializer.Serialize(channels, StateFiles.Json)); current = channels; }
    }
}
