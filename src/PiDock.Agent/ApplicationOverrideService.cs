using System.Text.Json;
using PiDock.Core;

namespace PiDock.Agent;

// Authorities come only from the host's authenticated managed credential vault, never request bodies.
public sealed record ApplicationEngineeringAuthority(string AppId, string Actor, int CredentialVersion, DateTimeOffset ExpiresAt, string Scope, ApplicationRuntimeBinding? Runtime = null);

// A variable exchange/engineering contract, not a GPIO driver or an application scan scheduler.
public sealed class ApplicationOverrideService(TimeProvider clock, Func<ApplicationEngineeringAuthority, bool> currentAuthority,
    Action<ApplicationEngineeringAudit> audit)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Runtime> apps = new(StringComparer.Ordinal);
    private bool maintenance = true; // Host must prove an open orchestration state before use.
    private long auditFailures;
    private sealed class Runtime(ApplicationSignalRegistration registration, ApplicationEngineeringAuthority authority, DateTimeOffset now, long tick)
    {
        public readonly ApplicationSignalRegistration Registration = registration;
        public ApplicationEngineeringAuthority Authority = authority;
        public DateTimeOffset Updated = now;
        public long UpdatedTick = tick;
        public long Sequence;
        public bool Fenced;
        public readonly Dictionary<string, JsonElement> Normal = registration.Signals.ToDictionary(s => s.Id, s => s.InitialValue.Clone(), StringComparer.Ordinal);
        public readonly Dictionary<string, HeldForce> Forces = new(StringComparer.Ordinal);
        public readonly Dictionary<Guid, HeldWrite> Writes = [];
    }
    private sealed record HeldForce(ApplicationSignalForce Force, ApplicationEngineeringAuthority Authority, long StartedTick, TimeSpan Lifetime);
    private sealed record HeldWrite(ApplicationSignalPendingWrite Write, ApplicationEngineeringAuthority Authority, long StartedTick, TimeSpan Lifetime);
    private DateTimeOffset Now => clock.GetUtcNow();
    public ApplicationEngineeringSupport Support(bool enabled, bool lifecycleBound = false) { lock (gate) return new(1, enabled, ApplicationSignalRules.MaximumSignalsPerApp,
        ApplicationSignalRules.MaximumForceSeconds, ApplicationSignalRules.FreshnessSeconds, auditFailures,
        ["Managed app variable preview only; no physical GPIO forcing.", "Forces and queued writes are volatile and expire; registration resets them. Apps must generate a fresh instance GUID at every process start.",
         "Runtime must publish complete frames within five seconds and apply queued writes at its own scan boundary.", "Broker orchestration must be reachable and open; maintenance or unavailable status clears engineering state.", "Opt-in runtime access additionally binds startup identity and permanently fences invalidated registrations."], lifecycleBound); }
    public void SetMaintenance(bool paused)
    {
        lock (gate) { maintenance = paused; Sweep(); }
    }
    public void Tick() { lock (gate) Sweep(); }
    public (int Forces, int Writes, bool Fenced) Activity(string appId)
    { lock (gate) { Sweep(); return apps.TryGetValue(appId, out var runtime) ? (runtime.Forces.Count, runtime.Writes.Count, runtime.Fenced) : (0, 0, false); } }
    private bool Valid(ApplicationEngineeringAuthority authority) => authority.ExpiresAt > Now && currentAuthority(authority);
    private void Authorize(ApplicationEngineeringAuthority authority, string scope)
    {
        if (authority.Scope != scope || !IdentityRules.IsId(authority.AppId) || !IdentityRules.IsId(authority.Actor) || !Valid(authority))
            throw new UnauthorizedAccessException("Current app-scoped engineering authorization is required.");
    }
    private void Record(Runtime runtime, string app, string action, string actor, string scope, string? signal, DateTimeOffset? expires = null, bool safety = false)
    {
        var entry = new ApplicationEngineeringAudit(Guid.NewGuid(), app, action, actor, scope, runtime.Registration.InstanceId,
            runtime.Registration.Revision, signal, Now, expires);
        if (!safety) { audit(entry); return; }
        // An unavailable audit destination must never retain a force past its authorization/expiry.
        try { audit(entry); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformCapacityException) { auditFailures++; }
    }
    private void Sweep()
    {
        var now = Now;
        foreach (var (app, runtime) in apps)
        {
            var inactive = maintenance || Stale(runtime) || !Valid(runtime.Authority);
            if (inactive && runtime.Authority.Runtime is not null) runtime.Fenced = true;
            inactive |= runtime.Fenced;
            foreach (var (signal, held) in runtime.Forces.ToArray())
            {
                if (!inactive && held.Force.ExpiresAt > now && clock.GetElapsedTime(held.StartedTick) < held.Lifetime && Valid(held.Authority)) continue;
                runtime.Forces.Remove(signal);
                Record(runtime, app, "force-auto-release", held.Authority.Actor, held.Authority.Scope, signal, safety: true);
            }
            foreach (var (id, held) in runtime.Writes.ToArray())
            {
                if (!inactive && held.Write.ExpiresAt > now && clock.GetElapsedTime(held.StartedTick) < held.Lifetime && Valid(held.Authority)) continue;
                runtime.Writes.Remove(id);
                Record(runtime, app, "write-expired", held.Authority.Actor, held.Authority.Scope, held.Write.SignalId, safety: true);
            }
        }
    }
    private Runtime Find(string app, Guid? instance = null, string? revision = null)
    {
        Sweep();
        if (!apps.TryGetValue(app, out var runtime) || instance is { } id && runtime.Registration.InstanceId != id ||
            revision is not null && runtime.Registration.Revision != revision)
            throw new PlatformConflictException("Runtime instance or program revision is no longer current.");
        return runtime;
    }
    private void RequireOpen(Runtime runtime)
    {
        if (runtime.Fenced || maintenance || !Valid(runtime.Authority) || Stale(runtime))
            throw new PlatformConflictException("Engineering changes require an open, fresh, authorized runtime.");
    }
    public ApplicationSignalSnapshot Register(ApplicationEngineeringAuthority authority, ApplicationSignalRegistration registration)
    {
        ApplicationSignalRules.Validate(registration);
        lock (gate)
        {
            Authorize(authority, "signals.runtime"); Sweep();
            if (maintenance) throw new PlatformConflictException("Application engineering is paused.");
            Runtime? replaced = null;
            if (apps.TryGetValue(authority.AppId, out var existing))
            {
                if (existing.Registration.InstanceId == registration.InstanceId)
                {
                    if (existing.Fenced || existing.Authority.Runtime != authority.Runtime) throw new PlatformConflictException("Register a fresh runtime instance after lifecycle revocation.");
                    if (JsonSerializer.Serialize(existing.Registration, StateFiles.Json) != JsonSerializer.Serialize(registration, StateFiles.Json))
                        throw new PlatformConflictException("A runtime instance cannot change its declarations or revision.");
                    // Even a retry is an engineering reset boundary. A restarted app that mistakenly
                    // reuses an instance ID must not inherit temporary controls from its predecessor.
                    foreach (var (signal, held) in existing.Forces.ToArray())
                    {
                        existing.Forces.Remove(signal);
                        Record(existing, authority.AppId, "runtime-registration-release", held.Authority.Actor, held.Authority.Scope, signal, safety: true);
                    }
                    foreach (var (id, held) in existing.Writes.ToArray())
                    {
                        existing.Writes.Remove(id);
                        Record(existing, authority.AppId, "runtime-registration-write-cancelled", held.Authority.Actor, held.Authority.Scope, held.Write.SignalId, safety: true);
                    }
                    existing.Authority = authority; return Snapshot(authority.AppId, existing, true);
                }
                replaced = existing;
            }
            else if (apps.Count >= 16) throw new PlatformCapacityException("Runtime registry capacity reached.");
            // Keep no caller-owned arrays/elements, which could otherwise mutate authorized declarations.
            var copied = registration with { Signals = registration.Signals.Select(s => s with { InitialValue = s.InitialValue.Clone() }).ToArray() };
            var runtime = new Runtime(copied, authority, Now, clock.GetTimestamp());
            Record(runtime, authority.AppId, "runtime-registered", authority.Actor, authority.Scope, null);
            apps[authority.AppId] = runtime;
            if (replaced is not null)
            {
                foreach (var (signal, held) in replaced.Forces)
                    Record(replaced, authority.AppId, "runtime-replaced-release", held.Authority.Actor, held.Authority.Scope, signal, safety: true);
                foreach (var held in replaced.Writes.Values)
                    Record(replaced, authority.AppId, "runtime-replaced-write-cancelled", held.Authority.Actor, held.Authority.Scope, held.Write.SignalId, safety: true);
            }
            return Snapshot(authority.AppId, runtime, true);
        }
    }
    public ApplicationSignalSnapshot Publish(ApplicationEngineeringAuthority authority, ApplicationSignalFrame frame)
    {
        lock (gate)
        {
            Authorize(authority, "signals.runtime");
            var runtime = Find(authority.AppId, frame.InstanceId, frame.Revision);
            if (runtime.Fenced || runtime.Authority.Runtime != authority.Runtime || maintenance) throw new PlatformConflictException("Runtime authority has been revoked.");
            if (frame.Sequence <= runtime.Sequence || frame.Values is null || frame.AppliedWrites is null || frame.AppliedWrites.Length > 64 ||
                frame.AppliedWrites.Distinct().Count() != frame.AppliedWrites.Length || frame.AppliedWrites.Any(id => !runtime.Writes.ContainsKey(id)))
                throw new ArgumentException("Use increasing frames and acknowledge only current queued writes.");
            var declarations = runtime.Registration.Signals.Where(s => s.Access != ApplicationSignalAccess.Constant).ToArray();
            if (frame.Values.Length != declarations.Length || frame.Values.Any(v => v is null) || frame.Values.Select(v => v.SignalId).Distinct(StringComparer.Ordinal).Count() != frame.Values.Length ||
                declarations.Any(d => frame.Values.SingleOrDefault(v => v.SignalId == d.Id) is not { } v || !ApplicationSignalRules.Value(d.Type, v.Value)))
                throw new ArgumentException("Publish a complete typed normal-value frame; constants are fixed by declaration.");
            foreach (var id in frame.AppliedWrites)
                Record(runtime, authority.AppId, "write-applied", runtime.Writes[id].Authority.Actor, "signals.engineer", runtime.Writes[id].Write.SignalId);
            foreach (var value in frame.Values) runtime.Normal[value.SignalId] = value.Value.Clone();
            foreach (var id in frame.AppliedWrites) runtime.Writes.Remove(id);
            runtime.Sequence = frame.Sequence; runtime.Updated = Now; runtime.UpdatedTick = clock.GetTimestamp(); runtime.Authority = authority;
            return Snapshot(authority.AppId, runtime, true);
        }
    }
    public ApplicationSignalSnapshot Read(ApplicationEngineeringAuthority authority)
    {
        lock (gate) { Authorize(authority, authority.Scope is "signals.runtime" ? "signals.runtime" : "signals.engineer"); return Snapshot(authority.AppId, Find(authority.AppId), authority.Scope == "signals.runtime"); }
    }
    public ApplicationSignalPendingWrite SetOnce(ApplicationEngineeringAuthority authority, ApplicationSignalWriteRequest request)
    {
        lock (gate)
        {
            Authorize(authority, "signals.engineer"); var runtime = Find(authority.AppId, request.InstanceId, request.Revision); RequireOpen(runtime);
            var signal = Signal(runtime, request.SignalId, request.Value);
            if (signal.Access != ApplicationSignalAccess.ReadWrite) throw new ArgumentException("Set once requires a read/write variable.");
            if (runtime.Writes.Count >= 64 || runtime.Writes.Values.Any(w => w.Write.SignalId == signal.Id)) throw new PlatformConflictException("A variable write is already pending or the queue is full.");
            var value = new ApplicationSignalPendingWrite(Guid.NewGuid(), signal.Id, request.Value.Clone(), authority.Actor, Min(Now.AddSeconds(30), authority.ExpiresAt));
            Record(runtime, authority.AppId, "write-queued", authority.Actor, authority.Scope, signal.Id, value.ExpiresAt);
            runtime.Writes[value.Id] = new(value, authority, clock.GetTimestamp(), value.ExpiresAt - Now); return value;
        }
    }
    public ApplicationSignalForce Force(ApplicationEngineeringAuthority authority, ApplicationSignalForceRequest request)
    {
        lock (gate)
        {
            Authorize(authority, "signals.engineer"); var runtime = Find(authority.AppId, request.InstanceId, request.Revision); RequireOpen(runtime);
            var signal = Signal(runtime, request.SignalId, request.Value);
            if (!signal.Forceable || signal.Access == ApplicationSignalAccess.Constant || request.DurationSeconds is < 1 or > ApplicationSignalRules.MaximumForceSeconds)
                throw new ArgumentException("Use a forceable variable and a force lifetime between one and 300 seconds.");
            if (runtime.Forces.TryGetValue(signal.Id, out var existing) && (existing.Authority.Actor != authority.Actor || existing.Authority.CredentialVersion != authority.CredentialVersion))
                throw new PlatformConflictException("Release the existing engineering force before assigning another owner.");
            var value = new ApplicationSignalForce(Guid.NewGuid(), request.Value.Clone(), authority.Actor, Min(Now.AddSeconds(request.DurationSeconds), authority.ExpiresAt));
            Record(runtime, authority.AppId, "force-set", authority.Actor, authority.Scope, signal.Id, value.ExpiresAt);
            runtime.Forces[signal.Id] = new(value, authority, clock.GetTimestamp(), value.ExpiresAt - Now); return value;
        }
    }
    public ApplicationSignalSnapshot Release(ApplicationEngineeringAuthority authority, ApplicationSignalReleaseRequest request)
    {
        lock (gate)
        {
            Authorize(authority, "signals.engineer"); var runtime = Find(authority.AppId, request.InstanceId, request.Revision);
            if (request.SignalId is not null && !runtime.Normal.ContainsKey(request.SignalId)) throw new ArgumentException("Unknown variable.");
            foreach (var signal in runtime.Forces.Keys.Where(s => request.SignalId is null || s == request.SignalId).ToArray())
            {
                Record(runtime, authority.AppId, "force-released", authority.Actor, authority.Scope, signal); runtime.Forces.Remove(signal);
            }
            return Snapshot(authority.AppId, runtime, false);
        }
    }
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private bool Stale(Runtime runtime) => clock.GetElapsedTime(runtime.UpdatedTick) > TimeSpan.FromSeconds(ApplicationSignalRules.FreshnessSeconds);
    private static ApplicationSignalDeclaration Signal(Runtime runtime, string id, JsonElement value)
    {
        var signal = runtime.Registration.Signals.SingleOrDefault(s => s.Id == id);
        if (signal is null || !ApplicationSignalRules.Value(signal.Type, value)) throw new ArgumentException("Unknown variable or incompatible value type.");
        return signal;
    }
    private ApplicationSignalSnapshot Snapshot(string app, Runtime runtime, bool includeWrites)
    {
        var quality = runtime.Fenced ? "fenced" : maintenance ? "paused" : !Valid(runtime.Authority) ? "unauthorized" : Stale(runtime) ? "stale" : "current";
        return new(app, runtime.Registration.InstanceId, runtime.Registration.Revision, runtime.Sequence, runtime.Updated, quality,
            runtime.Registration.Signals.Select(d => new ApplicationSignalState(d with { InitialValue = d.InitialValue.Clone() }, runtime.Normal[d.Id].Clone(),
                runtime.Forces.TryGetValue(d.Id, out var held) ? held.Force.Value.Clone() : runtime.Normal[d.Id].Clone(), runtime.Forces.GetValueOrDefault(d.Id)?.Force)).ToArray(),
            includeWrites ? runtime.Writes.Values.Select(w => w.Write).ToArray() : []);
    }
}
