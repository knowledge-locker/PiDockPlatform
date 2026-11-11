using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using PiDock.Core;

namespace PiDock.Agent;

public sealed record LocalApplicationPolicy(bool Enabled, IReadOnlyList<ProcessedApplicationCommand> Processed);
public sealed record ProcessedApplicationCommand(ApplicationCommand Command, ApplicationCommandResult Result);

public sealed class RemoteApplicationAgent : BackgroundService
{
    private readonly IApplicationRuntime broker; private readonly MqttConnection mqtt; private readonly TimeProvider clock;
    private readonly string path; private readonly object gate = new();
    private readonly Channel<ApplicationCommand> commands = Channel.CreateBounded<ApplicationCommand>(16);
    private LocalApplicationPolicy policy; private RemoteApplicationSnapshot current;
    public RemoteApplicationAgent(IConfiguration configuration, IApplicationRuntime broker, MqttConnection mqtt, TimeProvider clock)
    {
        this.broker = broker; this.mqtt = mqtt; this.clock = clock;
        path = Path.Combine(StateFiles.PrepareDirectory(configuration["PiDock:StateDirectory"] ?? throw new InvalidOperationException("State directory required.")), "remote-applications.json");
        policy = File.Exists(path) ? JsonSerializer.Deserialize<LocalApplicationPolicy>(File.ReadAllText(path), StateFiles.Json)! : new(false, []);
        current = new(clock.GetUtcNow(), policy.Enabled, "Waiting for container inventory", [], policy.Processed.TakeLast(8).Select(p => p.Result).ToArray());
    }
    public RemoteApplicationSnapshot Current { get { lock (gate) return current; } }
    public void SetEnabled(bool enabled)
    {
        lock (gate) { Save(policy with { Enabled = enabled }); current = current with { ControlEnabled = enabled }; }
    }
    public bool Enqueue(ApplicationCommand command)
    {
        var profile = mqtt.Current;
        return ApplicationCommands.Valid(command, profile.TenantId, profile.DeviceId, clock.GetUtcNow()) && commands.Writer.TryWrite(command);
    }
    public async Task ProcessAsync(ApplicationCommand command, CancellationToken ct = default)
    {
        var profile = mqtt.Current; var now = clock.GetUtcNow();
        if (!ApplicationCommands.Valid(command, profile.TenantId, profile.DeviceId, now)) return;
        lock (gate)
        {
            if (policy.Processed.Any(p => p.Command.Id == command.Id)) return;
            var retained = policy.Processed.Where(p => p.Command.ExpiresAt >= now).ToList();
            if (retained.Count >= 128) return;
            // Persist acceptance before touching the runtime. A crash never re-executes an accepted command.
            retained.Add(new(command, new(command.Id, "Unknown", "Interrupted operation; inspect container state before retrying.", now)));
            Save(new(policy.Enabled, retained));
        }
        string state = "Rejected", message = "Remote container control is disabled by the local owner.";
        try
        {
            if (Current.ControlEnabled && broker.Configured)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                if (command.Action == "restart") await broker.Restart(command.ContainerId, timeout.Token);
                else await broker.ChangeState(command.ContainerId, command.Action == "start", timeout.Token);
                state = "Completed"; message = "Container operation completed.";
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or OperationCanceledException or ArgumentException)
        { state = "Failed"; message = "Container operation unavailable. Check the local runtime and broker permissions."; }
        lock (gate)
        {
            var result = new ApplicationCommandResult(command.Id, state, message, clock.GetUtcNow());
            Save(policy with { Processed = policy.Processed.Select(p => p.Command.Id == command.Id ? p with { Result = result } : p).ToArray() });
            current = current with { Results = policy.Processed.TakeLast(8).Select(p => p.Result).ToArray() };
        }
        await RefreshAsync(ct);
    }
    private void Save(LocalApplicationPolicy value) { StateFiles.WriteAtomic(path, JsonSerializer.Serialize(value, StateFiles.Json)); policy = value; }
    private async Task RefreshAsync(CancellationToken ct)
    {
        ApplicationSummary[] apps = []; string status = "Host broker is not configured";
        try
        {
            if (broker.Configured)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                apps = (await broker.List(timeout.Token)).Where(a => a.Id is { Length: 64 } && a.Id.All(char.IsAsciiHexDigit)).Take(16)
                    .Select(a => a with { Names = a.Names.Take(4).Select(n => n[..Math.Min(n.Length, 128)]).ToArray(), Image = a.Image[..Math.Min(a.Image.Length, 512)], Status = a.Status[..Math.Min(a.Status.Length, 256)], State = a.State[..Math.Min(a.State.Length, 32)] }).ToArray();
                status = "Managed containers reported";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { status = "Container runtime unavailable"; }
        lock (gate) current = new(clock.GetUtcNow(), policy.Enabled, status, apps, policy.Processed.TakeLast(8).Select(p => p.Result).ToArray());
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshAsync(stoppingToken);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); wait.CancelAfter(TimeSpan.FromSeconds(15));
                try { await commands.Reader.WaitToReadAsync(wait.Token); } catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
                while (commands.Reader.TryRead(out var command)) await ProcessAsync(command, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
