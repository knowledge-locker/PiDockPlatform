using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PiDock.Agent;
using PiDock.Appliance.Web;
using PiDock.Core;

namespace PiDock.UiReview;

public sealed record ReviewRuntimeView(bool Paused, bool DesiredRunning, string State, int? ProcessId,
    int Leases, int Forces, int PendingWrites, bool OutputsAtFallback);

// Development-only process adapter for the SAME private lifecycle/access protocol used by the Docker broker.
// It has no Docker or hardware operations. The real broker independently implements RuntimeAccess from managed records.
public sealed class ReviewRuntimeController(IServiceProvider services, IConfiguration config) : IHostedService, IDisposable
{
    public const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly object gate = new();
    private readonly SemaphoreSlim actions = new(1, 1);
    private readonly Guid coordinator = Guid.NewGuid();
    private Process? process;
    private bool paused, desired = true;
    private string state = "created";
    private Guid delivery;
    private Action<Guid>? deliver;
    public void SetDelivery(Action<Guid> write) => deliver = write;
    public ApplicationRuntimeAccessSnapshot Access()
    {
        lock (gate)
        {
            bool running = process is { HasExited: false } && state == "running";
            return new(coordinator, paused, [new("ui-sample", delivery == Guid.Empty ? null : delivery,
                !paused && desired && running, paused ? "paused" : !desired ? "requested_stopped" : !running ? "not_running" : "allowed")]);
        }
    }
    public ApplicationOrchestrationStatus Orchestration() { lock (gate) return new(paused, "development_fixture", paused ? "paused" : "active", paused ? 1 : 0, 1); }
    public ReviewRuntimeView View()
    {
        var io = services.GetRequiredService<ApplicationIoService>().Activity("ui-sample");
        var engineering = services.GetRequiredService<ApplicationOverrideService>().Activity("ui-sample");
        lock (gate) return new(paused, desired, process is { HasExited: false } ? state : state == "running" ? "failed" : state,
            process is { HasExited: false } ? process.Id : null, io.Leases, engineering.Forces, engineering.Writes, io.OutputsAtFallback);
    }
    private void StartProcess()
    {
        lock (gate)
        {
            if (paused) throw new InvalidOperationException("Resume maintenance before starting the sample.");
            if (process is { HasExited: false }) return;
            var assembly = config["Review:SampleAssembly"] ?? throw new InvalidOperationException("Sample assembly required.");
            if (!Path.IsPathFullyQualified(assembly) || !File.Exists(assembly)) throw new InvalidOperationException("Build the sample before starting lifecycle review.");
            delivery = Guid.NewGuid(); deliver!(delivery);
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(assembly)!, UseShellExecute = false };
            start.ArgumentList.Add(assembly);
            foreach (var name in start.Environment.Keys.ToArray())
                if (name.StartsWith("PiDock", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Broker", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Review", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Sample", StringComparison.OrdinalIgnoreCase)) start.Environment.Remove(name);
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["Sample__Port"] = config["Review:SamplePort"] ?? "7365";
            start.Environment["Sample__ConnectionFile"] = config["Review:ConnectionFile"];
            start.Environment["Sample__BridgeToken"] = config["PiDock:ApplicationUi:Credentials:sample:Token"];
            start.Environment["Sample__BridgeExpiresAt"] = config["PiDock:ApplicationUi:Credentials:sample:ExpiresAt"];
            var previous = process; process = null; previous?.Dispose(); process = Process.Start(start) ?? throw new IOException("Sample could not start.");
            state = "running";
        }
    }
    private async Task Revoke(CancellationToken ct) => await services.GetRequiredService<ApplicationRuntimeAccessMonitor>().Refresh(ct, force: true);
    private async Task EndProcess(bool fail, CancellationToken ct)
    {
        Process? current; lock (gate) current = process;
        if (current is null || current.HasExited) return;
        if (fail) current.Kill(entireProcessTree: true);
        else Signal(current.Id, 15);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await current.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!current.HasExited) current.Kill(entireProcessTree: true); await current.WaitForExitAsync(CancellationToken.None); }
    }
    public async Task Control(string action, CancellationToken ct)
    {
        await actions.WaitAsync(ct);
        try
        {
            lock (gate) if (paused && action is "start" or "restart") throw new InvalidOperationException("Resume maintenance before starting or restarting the sample.");
            if (action is "pause" or "stop" or "restart")
            {
                lock (gate) { if (action == "pause") paused = true; if (action == "stop") desired = false; if (action == "restart") desired = true; state = "stopping"; }
                await Revoke(ct); await EndProcess(false, ct);
                lock (gate) state = "stopped";
                if (action == "restart") StartProcess();
            }
            else if (action is "start" or "resume")
            {
                lock (gate) { if (action == "resume") paused = false; else desired = true; }
                if (desired) StartProcess();
            }
            else if (action == "fail")
            {
                // Deliberately skip application cleanup AND pre-revocation to exercise independent host detection.
                await EndProcess(true, ct); lock (gate) state = "failed";
            }
            else throw new ArgumentException("Unsupported review lifecycle action.");
            await Revoke(ct);
        }
        finally { actions.Release(); }
    }
    public async Task StartAsync(CancellationToken ct) { StartProcess(); await Task.CompletedTask; }
    public async Task StopAsync(CancellationToken ct)
    {
        lock (gate) { desired = false; state = "stopping"; }
        services.GetRequiredService<ApplicationRuntimeAccessGate>().Unavailable();
        services.GetRequiredService<ApplicationIoService>().Sweep(); services.GetRequiredService<ApplicationOverrideService>().Tick();
        await EndProcess(false, ct);
    }
    public void Dispose() { lock (gate) { var current = process; process = null; if (current is { HasExited: false }) current.Kill(entireProcessTree: true); current?.Dispose(); } }
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Signal(int pid, int signal);
}
