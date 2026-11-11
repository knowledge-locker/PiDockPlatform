namespace PiDock.Core;

// Null on legacy snapshots means all channels were enabled. Zero placeholders never mean measurements.
public sealed record TelemetryChannels(bool Cpu = true, bool Memory = true, bool Storage = true,
    bool Temperature = true, bool Uptime = true, bool Interfaces = true, bool Alarms = true)
{
    public TelemetryEnvelope Filter(TelemetryEnvelope frame) => frame with {
        Snapshot = frame.Snapshot with {
            CpuPercent = Cpu ? frame.Snapshot.CpuPercent : null,
            MemoryTotalBytes = Memory ? frame.Snapshot.MemoryTotalBytes : null,
            MemoryAvailableBytes = Memory ? frame.Snapshot.MemoryAvailableBytes : null,
            TemperatureC = Temperature ? frame.Snapshot.TemperatureC : null,
            UptimeSeconds = Uptime ? frame.Snapshot.UptimeSeconds : 0,
            Storage = Storage ? frame.Snapshot.Storage : new(0, 0, 0, 0, false, new Dictionary<HistoryClass,long>()),
            Interfaces = Interfaces ? frame.Snapshot.Interfaces : [], Channels = this
        }, Alarms = Alarms ? frame.Alarms : []
    };
}
