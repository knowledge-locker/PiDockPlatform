using System.Text.Json;
using PiDock.Core;
namespace PiDock.Agent;

public sealed class AlarmMonitor
{
    private readonly object gate = new();
    private readonly string path;
    private readonly HistoryStore history;
    private readonly ResourceAlarmEngine engine;
    private readonly List<AlarmTransition> pending = [];
    public AlarmMonitor(string directory, HistoryStore historyStore, TimeProvider clock, IReadOnlyList<AlarmRule>? rules = null)
    {
        path = Path.Combine(StateFiles.PrepareDirectory(directory), "alarm-state.json"); history = historyStore;
        engine = new(clock, rules);
        if (File.Exists(path)) engine.Restore(JsonSerializer.Deserialize<ResourceAlarm[]>(File.ReadAllText(path), StateFiles.Json) ?? []);
    }
    public IReadOnlyList<ResourceAlarm> Active { get { lock (gate) return engine.Active; } }
    public IReadOnlyList<AlarmTransition> Observe(HostSnapshot sample)
    {
        lock (gate)
        {
            pending.AddRange(engine.Observe(sample));
            if (pending.Count > 64) pending.RemoveRange(0, pending.Count - 64);
            if (pending.Count == 0) return [];
            // Keep the bounded transition buffer until the active-state snapshot is durable.
            StateFiles.WriteAtomic(path, JsonSerializer.Serialize(engine.Active, StateFiles.Json));
            var flushed = pending.ToArray();
            foreach (var transition in flushed) history.Append(HistoryClass.Audit, transition);
            pending.Clear();
            return flushed;
        }
    }
}
public sealed class AgentSession { public Guid BootId { get; } = Guid.NewGuid(); }
public sealed class DeliveryStatus
{
    private string text = "Not configured";
    public string Text { get => Volatile.Read(ref text); set => Volatile.Write(ref text, value); }
}
