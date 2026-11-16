using System.Text.Json;
using PiDock.Core;

namespace PiDock.AppUi.Sample;

public sealed record SampleStorage(string? Path, ApplicationHealthReporter? Reporter = null, string Version = "local");
public sealed class SampleRuntime : BackgroundService
{
    private readonly object gate = new();
    private readonly string epoch = Guid.NewGuid().ToString("N");
    private long sequence, ticks, disconnectGeneration;
    public long Generation { get { lock (gate) return disconnectGeneration; } }
    private int counter;
    private readonly string? dataPath;
    private readonly string version;
    private readonly ApplicationHealthReporter? reporter;
    private bool ready;
    private readonly TimeProvider clock;
    private readonly SampleSequence demonstration;
    private readonly SampleConnectedSession? connected;
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private bool CurrentReadiness => ready && (connected is null || connected.Read().Status == "connected");
    public void Readiness(bool value) { lock (gate) { ready = value; reporter?.Report(true,CurrentReadiness); } }
    public SampleRuntime(SampleStorage storage, TimeProvider? clock = null, SampleConnectedSession? connected = null)
    {
        this.connected = connected;
        this.clock = clock ?? TimeProvider.System;
        demonstration = new(this.clock);
        dataPath = storage.Path; reporter = storage.Reporter; version = storage.Version;
        reporter?.Report(true,false);
        if (dataPath is not null && File.Exists(dataPath)) counter = JsonSerializer.Deserialize<int>(File.ReadAllText(dataPath));
    }
    private readonly Queue<ApplicationUiEvent> journal = new();
    private readonly Dictionary<Guid, (string Fingerprint, ApplicationUiCommandResult Result)> commands = [];
    private JsonElement Data() => JsonSerializer.SerializeToElement(new { counter, ticks, version, demo = demonstration.Read(), shared = connected?.Read() }, StateFiles.Json);
    public async Task<ApplicationUiCommandResult> CommandAsync(ApplicationUiCommand command, CancellationToken ct)
    {
        if (connected is null || !(command.Name.StartsWith("demo.", StringComparison.Ordinal) || command.Name.StartsWith("engineering.", StringComparison.Ordinal))) return Command(command);
        await commandGate.WaitAsync(ct);
        try
        {
            var fingerprint = JsonSerializer.Serialize(command, StateFiles.Json);
            lock (gate)
            {
                if (commands.TryGetValue(command.CommandId, out var old)) return old.Fingerprint == fingerprint ? old.Result : new(1, command.CommandId, false, null, "command_id_conflict");
                if (command.SchemaVersion != 1 || command.CommandId == Guid.Empty || command.Arguments.ValueKind != JsonValueKind.Object || commands.Count >= 128) return new(1, command.CommandId, false, null, "rejected");
            }
            string? error = null;
            try { await connected.Command(command.Name, command.Arguments, ct); }
            catch (HttpRequestException e) { error = e.StatusCode is null ? "outcome_unknown" : "shared_service_rejected"; }
            catch (OperationCanceledException) { error = "outcome_unknown"; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException) { error = "invalid_or_unavailable"; }
            lock (gate)
            {
                Changed(); var result = new ApplicationUiCommandResult(1, command.CommandId, error is null, Data(), error);
                commands[command.CommandId] = (fingerprint, result); return result;
            }
        }
        finally { commandGate.Release(); }
    }
    private void Changed()
    {
        journal.Enqueue(new(1, epoch, ++sequence, "update", clock.GetUtcNow(), Data()));
        while (journal.Count > 64) journal.Dequeue();
    }
    public ApplicationUiCommandResult Command(ApplicationUiCommand command)
    {
        lock (gate)
        {
            var fingerprint = JsonSerializer.Serialize(command, StateFiles.Json);
            if (commands.TryGetValue(command.CommandId, out var old)) return old.Fingerprint == fingerprint ? old.Result : new(1, command.CommandId, false, null, "command_id_conflict");
            if (command.SchemaVersion != 1 || command.CommandId == Guid.Empty || command.Arguments.ValueKind != JsonValueKind.Object || commands.Count >= 128)
                return new(1, command.CommandId, false, null, "rejected");
            if (command.Name == "counter.increment") counter++;
            else if (command.Name == "counter.reset") counter = 0;
            else if (command.Name == "stream.disconnect") disconnectGeneration++;
            else if (command.Name == "demo.start") demonstration.Inputs(true, false);
            else if (command.Name == "demo.stop") demonstration.Inputs(false, true);
            else if (command.Name == "demo.bank-toggle") demonstration.ToggleBank();
            else if (command.Name == "demo.reset") demonstration.Reset();
            else if (command.Name == "demo.inputs")
            {
                if (!command.Arguments.TryGetProperty("start", out var start) || start.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !command.Arguments.TryGetProperty("stop", out var stop) || stop.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return new(1, command.CommandId, false, null, "invalid_inputs");
                demonstration.Inputs(start.GetBoolean(), stop.GetBoolean());
            }
            else return new(1, command.CommandId, false, null, "command_denied");
            if (dataPath is not null && command.Name is "counter.increment" or "counter.reset") StateFiles.WriteAtomic(dataPath,JsonSerializer.Serialize(counter));
            Changed(); var result = new ApplicationUiCommandResult(1, command.CommandId, true, Data(), null);
            commands[command.CommandId] = (fingerprint, result); return result;
        }
    }
    public ApplicationUiEvent[] Read(ApplicationUiCursor? cursor)
    {
        lock (gate)
        {
            if (cursor is null || cursor.Epoch != epoch || cursor.Sequence > sequence || (journal.TryPeek(out var first) && cursor.Sequence < first.Sequence - 1))
                return [new(1, epoch, sequence, "snapshot", clock.GetUtcNow(), Data())];
            var next = journal.Where(e => e.Sequence > cursor.Sequence).ToArray();
            return next.Length > 0 ? next : [new(1, epoch, sequence, "heartbeat", clock.GetUtcNow(), JsonSerializer.SerializeToElement(new { }))];
        }
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(ct)) lock (gate)
        {
            ticks++;
            // Execution and the display feed have separate rates. Leave headroom for commands/replay.
            if (ticks % 5 == 0) Changed();
            if (ticks % 10 == 0) reporter?.Report(true,CurrentReadiness);
        }
    }
}
