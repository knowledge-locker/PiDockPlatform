using System.Security.Claims;
using System.Text.Json;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Cloud.Web;

public sealed record CloudApplicationCommand(ApplicationCommand Command, ApplicationCommandResult? Result = null);
public sealed class ApplicationCommandStore
{
    private readonly object gate = new(); private readonly string path; private readonly FleetStore fleet; private readonly TimeProvider clock;
    private List<CloudApplicationCommand> entries;
    public ApplicationCommandStore(StateDirectory state, FleetStore fleet, TimeProvider clock)
    {
        this.fleet = fleet; this.clock = clock; path = Path.Combine(state.Path, "application-commands.json");
        entries = File.Exists(path) ? JsonSerializer.Deserialize<List<CloudApplicationCommand>>(File.ReadAllText(path), StateFiles.Json)! : [];
    }
    public static bool CanControl(ClaimsPrincipal user) => EnrollmentStore.CanManage(user);
    public IReadOnlyList<CloudApplicationCommand> For(ClaimsPrincipal user, string device) { lock (gate) return user.Identity?.IsAuthenticated == true ? entries.Where(e => e.Command.TenantId == user.FindFirstValue("tenant") && e.Command.DeviceId == device).TakeLast(20).Reverse().ToArray() : []; }
    public ApplicationCommand Queue(ClaimsPrincipal user, string device, string action, string container)
    {
        if (!CanControl(user)) throw new UnauthorizedAccessException();
        var target = fleet.For(user).SingleOrDefault(d => d.DeviceId == device) ?? throw new UnauthorizedAccessException();
        var now = clock.GetUtcNow();
        if (!target.HasLiveContact || now - target.ReceivedAt > TimeSpan.FromSeconds(90) || target.Applications is not { ControlEnabled: true } inventory ||
            now - inventory.ObservedAt > TimeSpan.FromSeconds(90) || !inventory.Applications.Any(a => a.Id == container))
            throw new InvalidOperationException("The device must be online, allow cloud control locally, and report this managed container.");
        var command = new ApplicationCommand(Guid.NewGuid(), target.TenantId, device, action, container, now, now.AddMinutes(2), user.Identity!.Name ?? "cloud-owner");
        if (!ApplicationCommands.Valid(command, target.TenantId, device, now)) throw new ArgumentException("Invalid container operation.");
        lock (gate)
        {
            if (entries.Any(e => e.Command.TenantId == target.TenantId && e.Command.DeviceId == device && e.Result is null && e.Command.ExpiresAt > now)) throw new InvalidOperationException("Wait for the current device operation to finish or expire.");
            var next = entries.Where(e => e.Command.IssuedAt > now.AddDays(-1)).ToList();
            if (next.Count >= 512) throw new InvalidOperationException("Remote operation capacity reached. Try again later.");
            next.Add(new(command)); Save(next); return command;
        }
    }
    public ApplicationCommand? ForReceipt(TelemetryEnvelope frame)
    {
        var now = clock.GetUtcNow();
        if (frame.IsReplay || frame.Snapshot.ObservedAt < now.AddSeconds(-60) || frame.Snapshot.ObservedAt > now.AddSeconds(30)) return null;
        lock (gate) return entries.FirstOrDefault(e => e.Command.TenantId == frame.TenantId && e.Command.DeviceId == frame.DeviceId && e.Result is null && e.Command.ExpiresAt > now)?.Command;
    }
    public void AcceptResults(TelemetryEnvelope frame)
    {
        if (frame.Applications is null) return;
        lock (gate)
        {
            var next = entries.ToList(); bool changed = false;
            foreach (var result in frame.Applications.Results)
            {
                int index = next.FindIndex(e => e.Command.Id == result.Id && e.Command.TenantId == frame.TenantId && e.Command.DeviceId == frame.DeviceId && e.Result is null);
                if (index < 0 || result.CompletedAt < next[index].Command.IssuedAt.AddSeconds(-30) || result.CompletedAt > clock.GetUtcNow().AddSeconds(30)) continue;
                next[index] = next[index] with { Result = result }; changed = true;
            }
            if (changed) Save(next);
        }
    }
    private void Save(List<CloudApplicationCommand> next) { StateFiles.WriteAtomic(path, JsonSerializer.Serialize(next, StateFiles.Json)); entries = next; }
}
