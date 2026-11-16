using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiDock.Core;

namespace PiDock.Agent;

// Internal authorization identity; fingerprints never appear in public I/O DTOs.
public sealed record ApplicationIoAuthority(string AppId, string CredentialId, string Fingerprint, string RequiredScope = "io.write", ApplicationRuntimeBinding? Runtime = null);
public interface IApplicationIoAuthorityValidator { bool IsCurrent(ApplicationIoAuthority authority); }
public sealed class ApplicationIoUnavailableException : Exception;
public sealed class ApplicationIoDeniedException : Exception;

// This is an app-facing simulation adapter, separate from the development dashboard's pin overrides.
// A physical adapter must separately establish electrical fallback and kernel ownership guarantees.
public sealed class ApplicationIoService(bool enabled, Func<ApplicationIoGrant[]> grants,
    TimeProvider clock, IApplicationIoAuthorityValidator authorities, ApplicationRuntimeAccessGate? runtimeAccess = null)
{
    private readonly object gate = new();
    private readonly Guid bootId = Guid.NewGuid();
    private readonly Dictionary<Guid, Held> leases = [];
    private readonly Dictionary<string, bool> values = new(StringComparer.Ordinal);
    private long sequence;
    private sealed record Held(ApplicationIoLease Lease, ApplicationIoAuthority Authority, long Started,
        int Seconds, ApplicationIoGrant[] Grants);
    private static readonly IReadOnlyDictionary<int, int> Pins = new Dictionary<int, int>
    { [4]=7, [5]=29, [6]=31, [12]=32, [13]=33, [16]=36, [17]=11, [18]=12, [19]=35, [20]=38, [21]=40, [22]=15, [23]=16, [24]=18, [25]=22, [26]=37, [27]=13 };
    public ApplicationIoSupport Support() => new(1, enabled, "simulation", false, 30, 16,
        ["Development-only, opt-in simulation; no physical GPIO operations or measured electrical state.",
         "Exclusive app leases are in memory and expire within 30 seconds without renewal.",
         "Expiry, revoked credentials and changed grants reset simulated outputs; the background sweep normally runs every 250 ms.",
         "No hard real-time guarantee, persisted forces, direct device access or cross-app sharing.",
         "Discover app-runtime-access before deployment; when enabled, private startup binding and lifecycle revocation are required."], runtimeAccess?.Enabled == true);
    private void RequireEnabled() { if (!enabled) throw new ApplicationIoUnavailableException(); }
    private ApplicationIoGrant[] CurrentGrants()
    {
        var current = grants();
        if (current is not { Length: <= 64 } || current.Any(g => g is null || !IdentityRules.IsId(g.AppId) || g.AppId == "pidockos" || !TryPin(g.Alias, out _) || !Enum.IsDefined(g.Direction) || g.Pull is not ("none" or "up" or "down")) ||
            current.Select(g => (g.AppId, g.Alias)).Distinct().Count() != current.Length)
            throw new ArgumentException("Invalid I/O grants.");
        return current;
    }
    private static bool TryPin(string? alias, out int bcm)
    {
        bcm = -1;
        return alias is not null && alias.StartsWith("sim:bcm", StringComparison.Ordinal) && int.TryParse(alias[7..], out bcm) &&
            Pins.ContainsKey(bcm) && alias == "sim:bcm" + bcm;
    }
    public ApplicationIoSignal[] Signals(ApplicationIoAuthority authority)
    {
        lock (gate)
        {
            RequireEnabled(); SweepLocked(); RequireAuthority(authority);
            return CurrentGrants().Where(g => g.AppId == authority.AppId).Select(g =>
            { TryPin(g.Alias, out var bcm); return new ApplicationIoSignal(g.Alias, bcm, Pins[bcm], g.Direction, g.ActiveLow, g.Pull, g.Fallback); }).ToArray();
        }
    }
    private void RequireAuthority(ApplicationIoAuthority authority)
    { if (runtimeAccess is not null && !runtimeAccess.IsCurrent(authority.AppId, authority.Runtime) || !authorities.IsCurrent(authority)) throw new ApplicationIoDeniedException(); }
    private static void Duration(int seconds)
    { if (seconds is < 1 or > 30) throw new ArgumentException("Lease duration must be 1–30 seconds."); }
    public ApplicationIoLease Acquire(ApplicationIoAuthority authority, ApplicationIoLeaseRequest request)
    {
        if (request.Aliases is not { Length: >= 1 and <= 16 } || request.Aliases.Any(a => !TryPin(a, out _)) || request.Aliases.Distinct(StringComparer.Ordinal).Count() != request.Aliases.Length)
            throw new ArgumentException("Select 1–16 distinct supported simulated aliases.");
        Duration(request.Seconds);
        lock (gate)
        {
            RequireEnabled(); SweepLocked(); RequireAuthority(authority);
            var current = CurrentGrants();
            var selected = request.Aliases.Select(alias => current.SingleOrDefault(g => g.AppId == authority.AppId && g.Alias == alias) ?? throw new ApplicationIoDeniedException()).ToArray();
            if (leases.Count >= 64 || leases.Values.Count(l => l.Authority.AppId == authority.AppId) >= 8) throw new PlatformCapacityException("I/O lease limit reached.");
            if (leases.Values.Any(l => l.Lease.Aliases.Intersect(request.Aliases, StringComparer.Ordinal).Any())) throw new PlatformConflictException("I/O alias is already leased.");
            var lease = new ApplicationIoLease(Guid.NewGuid(), authority.AppId, request.Aliases.ToArray(), 0, clock.GetUtcNow().AddSeconds(request.Seconds));
            leases.Add(lease.Id, new(lease, authority, clock.GetTimestamp(), request.Seconds, selected));
            foreach (var grant in selected) values[grant.Alias] = Initial(grant);
            sequence++; return Copy(lease);
        }
    }
    public ApplicationIoLease Renew(ApplicationIoAuthority authority, Guid id, ApplicationIoRenewRequest request)
    {
        Duration(request.Seconds);
        lock (gate)
        {
            var held = Find(authority, id); Revision(held, request.ExpectedRevision);
            var lease = held.Lease with { Revision = checked(held.Lease.Revision + 1), ExpiresAt = clock.GetUtcNow().AddSeconds(request.Seconds) };
            leases[id] = held with { Lease = lease, Started = clock.GetTimestamp(), Seconds = request.Seconds };
            return Copy(lease);
        }
    }
    public ApplicationIoFrame Read(ApplicationIoAuthority authority, Guid id)
    { lock (gate) return Frame(Find(authority, id)); }
    public ApplicationIoFrame Write(ApplicationIoAuthority authority, Guid id, ApplicationIoWriteRequest request, bool simulatedInputs = false)
    {
        if (request.Values is not { Length: >= 1 and <= 16 } || request.Values.Any(v => v is null || !TryPin(v.Alias, out _)) || request.Values.Select(v => v.Alias).Distinct(StringComparer.Ordinal).Count() != request.Values.Length)
            throw new ArgumentException("Supply 1–16 distinct I/O values.");
        lock (gate)
        {
            var held = Find(authority, id); Revision(held, request.ExpectedRevision);
            // Validate the complete batch before changing any signal.
            foreach (var value in request.Values)
                if (!held.Grants.Any(g => g.Alias == value.Alias && g.Direction == (simulatedInputs ? ApplicationIoDirection.Input : ApplicationIoDirection.Output)))
                    throw new ApplicationIoDeniedException();
            foreach (var value in request.Values) values[value.Alias] = value.Value;
            held = held with { Lease = held.Lease with { Revision = checked(held.Lease.Revision + 1) } };
            leases[id] = held; sequence++; return Frame(held);
        }
    }
    public void Release(ApplicationIoAuthority authority, Guid id)
    { lock (gate) { var held = Find(authority, id); Reset(held); leases.Remove(id); } }
    public (int Leases, bool OutputsAtFallback) Activity(string appId)
    { lock (gate) { SweepLocked(); return (leases.Values.Count(h => h.Lease.AppId == appId), CurrentGrants().Where(g => g.AppId == appId && g.Direction == ApplicationIoDirection.Output).All(g => !values.TryGetValue(g.Alias, out var current) || current == g.Fallback)); } }
    public void ReleaseAll()
    { lock (gate) { foreach (var held in leases.Values) Reset(held); leases.Clear(); } }
    private Held Find(ApplicationIoAuthority authority, Guid id)
    {
        RequireEnabled(); SweepLocked(); RequireAuthority(authority);
        if (!leases.TryGetValue(id, out var held) || held.Authority != authority) throw new ApplicationIoDeniedException();
        return held;
    }
    private static void Revision(Held held, long expected)
    { if (held.Lease.Revision != expected) throw new PlatformConflictException("Stale I/O lease revision."); }
    private static ApplicationIoLease Copy(ApplicationIoLease lease) => lease with { Aliases = lease.Aliases.ToArray() };
    private ApplicationIoFrame Frame(Held held) => new(bootId, sequence, clock.GetTimestamp(), clock.TimestampFrequency,
        clock.GetUtcNow(), "simulation", Copy(held.Lease), held.Grants.Select(g =>
            new ApplicationIoValue(g.Alias, values[g.Alias], g.Direction == ApplicationIoDirection.Output ? values[g.Alias] : null, "simulated")).ToArray());
    private void Reset(Held held)
    {
        foreach (var grant in held.Grants) values[grant.Alias] = Initial(grant);
        sequence++;
    }
    private static bool Initial(ApplicationIoGrant grant) => grant.Direction == ApplicationIoDirection.Output ? grant.Fallback : (grant.Pull == "up") ^ grant.ActiveLow;
    public void Sweep() { lock (gate) SweepLocked(); }
    private void SweepLocked()
    {
        ApplicationIoGrant[] current;
        try { current = CurrentGrants(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { foreach (var held in leases.Values) Reset(held); leases.Clear(); throw; }
        foreach (var held in leases.Values.ToArray())
        {
            bool valid;
            try { valid = (runtimeAccess is null || runtimeAccess.IsCurrent(held.Authority.AppId, held.Authority.Runtime)) && authorities.IsCurrent(held.Authority); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException) { valid = false; }
            if (!valid || clock.GetElapsedTime(held.Started) >= TimeSpan.FromSeconds(held.Seconds) || held.Grants.Any(g => !current.Contains(g)))
            { Reset(held); leases.Remove(held.Lease.Id); }
        }
    }
}

public sealed class ApplicationIoSweepService(ApplicationIoService io, ILogger<ApplicationIoSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { io.Sweep(); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
                { logger.LogWarning("App I/O lease validation unavailable; simulated leases were released."); io.ReleaseAll(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { io.ReleaseAll(); }
    }
}
