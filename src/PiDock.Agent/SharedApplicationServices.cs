using System.Text.Json;
using PiDock.Core;

namespace PiDock.Agent;

public sealed class SharedApplicationServices
{
    private readonly object gate = new();
    private readonly HistoryStore history;
    private readonly TimeProvider clock;
    private readonly AgentSession session;
    private readonly string path;
    private readonly Func<ApplicationNotificationPolicy[]> notificationPolicies;
    private sealed record AlarmJournal(Guid Id, string Action, ApplicationAlarm Alarm);
    private sealed record AlarmState(ApplicationAlarm[] Alarms, AlarmJournal[] Pending,
        ApplicationNotification[]? Notifications = null, long RejectedNotifications = 0);
    private AlarmState state;
    private ApplicationAlarm[] alarms => state.Alarms;
    private ApplicationNotification[] notifications => state.Notifications ?? [];
    public int PendingAlarmTransitions { get { lock (gate) return state.Pending.Length; } }
    public SharedApplicationServices(string directory, HistoryStore history, TimeProvider clock, AgentSession session,
        Func<ApplicationNotificationPolicy[]>? notificationPolicies = null)
    {
        this.history = history; this.clock = clock; this.session = session;
        this.notificationPolicies = notificationPolicies ?? (() => []);
        path = Path.Combine(StateFiles.PrepareDirectory(directory), "app-alarm-state.json");
        if (File.Exists(path) && (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > 1048576))
            throw new IOException("Invalid application alarm state file.");
        state = File.Exists(path) ? JsonSerializer.Deserialize<AlarmState>(File.ReadAllText(path), StateFiles.Json)
            ?? throw new IOException("Invalid application alarm state.") : new([], []);
        if (notifications.Length > ApplicationNotificationRules.MaximumUnreadTotal + ApplicationNotificationRules.MaximumRetainedRead || notifications.Any(n => n is null ||
                n.Id == Guid.Empty || n.AlarmId == Guid.Empty || !IdentityRules.IsId(n.AppId) || !IdentityRules.IsId(n.Condition) || !Enum.IsDefined(n.Priority) || n.Action is not ("raised" or "updated") ||
                (n.ReadAt is null) != (n.ReadBy is null) || n.ReadBy is not null && !IdentityRules.IsId(n.ReadBy)) ||
            notifications.Select(n => n.Id).Distinct().Count() != notifications.Length || state.RejectedNotifications < 0)
            throw new IOException("Invalid notification state.");
    }
    public IReadOnlyList<SharedServiceEvent> Read(string kind, IReadOnlyCollection<string> sources) => history.ReadSources<SharedServiceEvent>(Class(kind), sources);
    private static HistoryClass Class(string kind) => kind switch { "log" => HistoryClass.Logs, "measurement" => HistoryClass.Telemetry, _ => HistoryClass.Audit };
    private SharedServiceEvent Append(string app, string kind, Guid id, DateTimeOffset observed, object value)
    {
        var payload = JsonSerializer.SerializeToElement(value, StateFiles.Json);
        var previous = history.ReadSources<SharedServiceEvent>(Class(kind), [app], 256).FirstOrDefault(e => e.EventId == id);
        if (previous is not null)
        {
            if (previous.Kind != kind || !JsonElement.DeepEquals(previous.Payload, payload)) throw new PlatformConflictException("Event ID reused with changed content.");
            return previous;
        }
        var envelope = new SharedServiceEvent(1, id, app, kind, observed.ToUniversalTime(), clock.GetUtcNow(), session.BootId,
            clock.GetTimestamp(), clock.TimestampFrequency, "Unverified", payload);
        if (!history.AppendSource(Class(kind), app, envelope)) throw new PlatformCapacityException("History policy rejected the event.");
        return envelope;
    }
    public SharedServiceEvent Log(string app, ApplicationLogEvent value, bool alarmEligible = false)
    {
        ApplicationContractRules.Validate(value);
        lock (gate)
        {
            var seen = history.ReadSources<SharedServiceEvent>(HistoryClass.Logs, [app], 256).Any(e => e.EventId == value.EventId);
            var result = Append(app, "log", value.EventId, value.ObservedAt, value);
            // Only locally configured priority grants may turn an app event into an alarm.
            if (!seen && alarmEligible && value.Priority is { } priority)
                Raise(app, new(value.EventCode, priority, value.Message.Length > 512 ? value.Message[..512] : value.Message));
            return result;
        }
    }
    public SharedServiceEvent Measure(string app, ApplicationMeasurement value)
    {
        ApplicationContractRules.Validate(value);
        lock (gate)
        {
            var names = history.ReadSources<SharedServiceEvent>(HistoryClass.Telemetry, [app], 256)
                .Select(e => e.Payload.TryGetProperty("name", out var name) ? name.GetString() : null).Distinct().ToArray();
            if (names.Length >= 32 && !names.Contains(value.Name)) throw new PlatformCapacityException("Measurement cardinality quota reached.");
            return Append(app, "measurement", value.EventId, value.ObservedAt, value);
        }
    }
    public ApplicationAlarm[] Alarms(IReadOnlyCollection<string> sources)
    {
        lock (gate) { FlushTransitions(); return alarms.Where(a => sources.Contains(a.AppId)).OrderByDescending(a => a.UpdatedAt).Take(100).ToArray(); }
    }
    public ApplicationAlarm Raise(string app, ApplicationAlarmRequest request)
    {
        ApplicationContractRules.Validate(request);
        lock (gate)
        {
            var current = alarms.FirstOrDefault(a => a.AppId == app && a.Condition == request.Condition && a.ResolvedAt is null);
            if (current is not null)
            {
                if (current.Message == request.Message && current.Priority == request.Priority) { FlushTransitions(); return current; }
                var changed = current with { Message = request.Message, Priority = request.Priority, UpdatedAt = clock.GetUtcNow() };
                Commit(alarms.Select(a => a.Id == changed.Id ? changed : a).ToArray(), changed, "updated"); return changed;
            }
            if (alarms.Count(a => a.ResolvedAt is null) >= 256 || alarms.Count(a => a.AppId == app && a.ResolvedAt is null) >= 16)
                throw new PlatformCapacityException("Active alarm quota reached.");
            var now = clock.GetUtcNow();
            var alarm = new ApplicationAlarm(Guid.NewGuid(), app, request.Condition, request.Priority, request.Message, now, now);
            Commit(alarms.Where(a => a.ResolvedAt is null).Concat(alarms.Where(a => a.ResolvedAt is not null).OrderByDescending(a => a.UpdatedAt).Take(127)).Append(alarm).ToArray(), alarm, "raised");
            return alarm;
        }
    }
    public ApplicationAlarm? Transition(string app, string condition, bool acknowledge)
    {
        if (!IdentityRules.IsId(condition)) throw new ArgumentException("Invalid condition.");
        lock (gate)
        {
            var current = alarms.FirstOrDefault(a => a.AppId == app && a.Condition == condition && a.ResolvedAt is null);
            if (current is null) return null;
            if (acknowledge && current.AcknowledgedAt is not null) { FlushTransitions(); return current; }
            var now = clock.GetUtcNow();
            var changed = acknowledge ? current with { AcknowledgedAt = now, UpdatedAt = now } : current with { ResolvedAt = now, UpdatedAt = now };
            Commit(alarms.Select(a => a.Id == changed.Id ? changed : a).ToArray(), changed, acknowledge ? "acknowledged" : "resolved"); return changed;
        }
    }
    private void Commit(ApplicationAlarm[] updated, ApplicationAlarm alarm, string action)
    {
        FlushTransitions();
        if (state.Pending.Length >= 64) throw new PlatformCapacityException("Alarm transition outbox full.");
        var transition = new AlarmJournal(Guid.NewGuid(), action, alarm);
        var policies = action is "raised" or "updated" ? notificationPolicies() : []; ApplicationNotificationRules.Validate(policies);
        var retained = notifications.Where(n => n.ReadAt is null).Concat(notifications.Where(n => n.ReadAt is not null)
            .OrderByDescending(n => n.ReadAt).Take(ApplicationNotificationRules.MaximumRetainedRead)).ToArray();
        if (action is "raised" or "updated" && policies.Any(p => p.AppId == alarm.AppId && (int)alarm.Priority <= (int)p.MaximumPriority))
        {
            if (retained.Count(n => n.ReadAt is null) >= ApplicationNotificationRules.MaximumUnreadTotal ||
                retained.Count(n => n.AppId == alarm.AppId && n.ReadAt is null) >= ApplicationNotificationRules.MaximumUnreadPerApp)
            {
                Save(state with { RejectedNotifications = checked(state.RejectedNotifications + 1) });
                throw new PlatformCapacityException("Local notification inbox is full; acknowledge older entries before changing this alarm.");
            }
            retained = retained.Append(new ApplicationNotification(transition.Id, alarm.AppId, alarm.Id, alarm.Condition,
                alarm.Priority, action, clock.GetUtcNow())).ToArray();
        }
        Save(new(updated, state.Pending.Append(transition).ToArray(), retained, state.RejectedNotifications));
        FlushTransitions();
    }
    public ApplicationDiagnosticsSupport DiagnosticsSupport()
    {
        lock (gate)
        {
            var policies = notificationPolicies(); ApplicationNotificationRules.Validate(policies);
            return new(1, policies.Length > 0, ApplicationNotificationRules.MaximumUnreadPerApp, ApplicationNotificationRules.MaximumUnreadTotal,
                state.RejectedNotifications, ["Local durable alarm notification inbox only; no email, push, cloud or external delivery.",
                    "Opt-in priority policies apply to newly raised/updated alarms. Notification read receipts and alarm acknowledgement are separate.",
                    "A full unread inbox rejects the corresponding alarm change atomically; rejected count is retained.",
                    "Docker stdout/stderr capture, redaction and coordinated verbosity remain separate work."]);
        }
    }
    public ApplicationNotification[] Notifications(IReadOnlyCollection<string> sources)
    {
        lock (gate) return notifications.Where(n => sources.Contains(n.AppId)).OrderBy(n => n.ReadAt is not null)
            .ThenByDescending(n => n.CreatedAt).ToArray();
    }
    public ApplicationNotification? AcknowledgeNotification(IReadOnlyCollection<string> sources, Guid id, string actor)
    {
        if (id == Guid.Empty || !IdentityRules.IsId(actor)) throw new ArgumentException("A valid notification and authenticated actor are required.");
        lock (gate)
        {
            var entry = notifications.SingleOrDefault(n => n.Id == id && sources.Contains(n.AppId));
            if (entry is null || entry.ReadAt is not null) return entry;
            var changed = entry with { ReadAt = clock.GetUtcNow(), ReadBy = actor };
            Save(state with { Notifications = notifications.Select(n => n.Id == id ? changed : n).ToArray() });
            return changed;
        }
    }
    private void FlushTransitions()
    {
        foreach (var transition in state.Pending)
        {
            try { Append(transition.Alarm.AppId, "alarm", transition.Id, transition.Alarm.UpdatedAt, transition); }
            catch (PlatformCapacityException) { break; }
            Save(state with { Pending = state.Pending.Where(t => t.Id != transition.Id).ToArray() });
        }
    }
    private void Save(AlarmState updated)
    {
        // Bounded state/outbox survives rotation and restart. A full outbox rejects further changes.
        StateFiles.WriteAtomic(path, JsonSerializer.Serialize(updated, StateFiles.Json)); state = updated;
    }
}
