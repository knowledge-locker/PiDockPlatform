using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using PiDock.Core;

namespace PiDock.AppUi.Sample;

// Private fixture delivery, never an app manifest, frame message or public response.
public sealed record SampleConnection(string Origin, string CertificateSha256, string IoToken, string RuntimeToken, string EngineeringToken, Guid DeliveryId = default, bool DockerFixture = false);
public sealed record SampleConnectedState(string Status, Guid InstanceId, string Revision, long Sequence,
    ApplicationSignalState[] Signals, ApplicationIoValue[] Outputs, int ForceCount, DateTimeOffset? ObservedAt);

public sealed class SampleConnectedSession(HttpClient ioHttp, HttpClient runtimeHttp, HttpClient engineeringHttp,
    TimeProvider clock) : BackgroundService
{
    public const string Revision = "shared-demo-one";
    public static readonly string[] Aliases = ["sim:bcm5", "sim:bcm6", "sim:bcm17", "sim:bcm27", "sim:bcm22"];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateGate = new();
    private readonly ApplicationIoClient io = new(ioHttp);
    private ApplicationIoLease? lease;
    private ApplicationSignalSnapshot? frame;
    private Guid instance;
    private long sequence, lastCycle, timerStarted, lastRenew;
    private bool latched, bankEnabled = true, timing, clearInputs, faulted;
    private SampleConnectedState state = new("connecting", Guid.Empty, Revision, 0, [], [], 0, null);
    private static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);
    public SampleConnectedState Read()
    {
        lock (stateGate) return state with { Status = state.Status == "connected" && clock.GetElapsedTime(lastCycle) > TimeSpan.FromSeconds(5) ? "stale" : state.Status };
    }
    private void SetState(string status, ApplicationIoFrame? applied = null)
    {
        lock (stateGate) state = new(status, instance, Revision, frame?.Sequence ?? 0, frame?.Signals ?? [],
            applied?.Values.Where(v => v.Commanded.HasValue).ToArray() ?? state.Outputs,
            frame?.Signals.Count(s => s.Force is not null) ?? 0, frame?.ObservedAt);
    }
    public static SampleConnectedSession Create(SampleConnection settings)
    {
        if (!Uri.TryCreate(settings.Origin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            !(IPAddress.TryParse(origin.Host, out var ip) && IPAddress.IsLoopback(ip) || settings.DockerFixture && origin.Host == "host.docker.internal") || origin.AbsolutePath != "/" ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            settings.CertificateSha256 is not { Length: 64 } || !settings.CertificateSha256.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Use the private loopback HTTPS fixture connection.");
        HttpClient Client(string token)
        {
            if (token is not { Length: 64 } || !token.All(char.IsAsciiHexDigit)) throw new ArgumentException("Private sample credential unavailable.");
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
                ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate is not null &&
                    (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0 &&
                    CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), Convert.FromHexString(settings.CertificateSha256)) };
            var client = new HttpClient(handler) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(2) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (settings.DeliveryId != Guid.Empty) ApplicationRuntimeAccess.Bind(client, settings.DeliveryId); return client;
        }
        return new(Client(settings.IoToken), Client(settings.RuntimeToken), Client(settings.EngineeringToken), TimeProvider.System);
    }
    public async Task Connect(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var support = await io.Support(ct);
            if (!support.Enabled || support.Physical || support.Adapter != "simulation") throw new InvalidOperationException("Shared simulation is unavailable.");
            var signals = await io.Signals(ct);
            if (Aliases.Any(alias => !signals.Any(s => s.Alias == alias && s.Direction == (alias is "sim:bcm5" or "sim:bcm6" ? ApplicationIoDirection.Input : ApplicationIoDirection.Output))))
                throw new InvalidOperationException("Required simulation grants are unavailable.");
            lease = await io.Acquire(new(Aliases, 15), ct);
            instance = Guid.NewGuid(); sequence = 0; latched = false; bankEnabled = true; timing = false; clearInputs = false;
            frame = await Post<ApplicationSignalSnapshot>(runtimeHttp, "runtime", new ApplicationSignalRegistration(instance, Revision,
                [Bool("start", false), Bool("stop", false), Bool("run-request", false),
                 new("bank-enabled", ApplicationSignalType.Boolean, ApplicationSignalAccess.ReadWrite, Value(true), true),
                 new("delay-seconds", ApplicationSignalType.Number, ApplicationSignalAccess.Constant, Value(3)),
                 new("timer-elapsed", ApplicationSignalType.Number, ApplicationSignalAccess.ReadOnly, Value(0)),
                 Bool("timer-done", false, false), Bool("output-bank", false)]), ct);
            lastRenew = lastCycle = clock.GetTimestamp(); faulted = false; SetState("connected");
        }
        catch { await Fail(); throw; }
        finally { gate.Release(); }
    }
    private static ApplicationSignalDeclaration Bool(string name, bool initial, bool forceable = true) =>
        new(name, ApplicationSignalType.Boolean, ApplicationSignalAccess.ReadOnly, Value(initial), forceable);
    private bool Effective(string id, bool normal)
    {
        var signal = frame!.Signals.Single(s => s.Declaration.Id == id);
        return signal.Force is null ? normal : signal.EffectiveValue.GetBoolean();
    }
    public async Task Cycle(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (lease is null || faulted) return;
            frame = await Get<ApplicationSignalSnapshot>(runtimeHttp, "runtime", ct);
            if (frame.InstanceId != instance || frame.Revision != Revision || frame.Quality is "paused" or "unauthorized" or "fenced")
                throw new InvalidOperationException("Runtime authority changed.");
            if (clock.GetElapsedTime(lastRenew) >= TimeSpan.FromSeconds(5))
            { lease = await io.Renew(lease.Id, new(lease.Revision, 15), ct); lastRenew = clock.GetTimestamp(); }
            var input = await io.Read(lease.Id, ct);
            bool start = input.Values.Single(v => v.Alias == "sim:bcm5").Value, stop = input.Values.Single(v => v.Alias == "sim:bcm6").Value;
            if (Effective("stop", stop)) latched = false; else if (Effective("start", start)) latched = true;
            foreach (var write in frame.PendingWrites)
            { if (write.SignalId != "bank-enabled") throw new InvalidOperationException("Unsupported queued write."); bankEnabled = write.Value.GetBoolean(); }
            bool run = Effective("run-request", latched);
            if (!run) timing = false;
            else if (!timing) { timing = true; timerStarted = clock.GetTimestamp(); }
            double elapsed = timing ? Math.Clamp(clock.GetElapsedTime(timerStarted).TotalSeconds, 0, 3) : 0;
            bool done = timing && elapsed >= 3;
            bool normalOutput = done && Effective("bank-enabled", bankEnabled);
            frame = await Post<ApplicationSignalSnapshot>(runtimeHttp, "frames", new ApplicationSignalFrame(instance, Revision, ++sequence,
                [new("start", Value(start)), new("stop", Value(stop)), new("run-request", Value(latched)), new("bank-enabled", Value(bankEnabled)),
                 new("timer-elapsed", Value(elapsed)), new("timer-done", Value(done)), new("output-bank", Value(normalOutput))], frame.PendingWrites.Select(w => w.Id).ToArray()), ct);
            if (frame.Quality != "current") throw new InvalidOperationException("Runtime is not current.");
            bool output = frame.Signals.Single(s => s.Declaration.Id == "output-bank").EffectiveValue.GetBoolean();
            var applied = await io.Write(lease.Id, new(lease.Revision,
                [new("sim:bcm17", output), new("sim:bcm27", output), new("sim:bcm22", output)]), ct); lease = applied.Lease;
            if (clearInputs)
            { var cleared = await io.SimulateInputs(lease.Id, new(lease.Revision, [new("sim:bcm5", false), new("sim:bcm6", false)]), ct); lease = cleared.Lease; clearInputs = false; }
            lastCycle = clock.GetTimestamp(); SetState("connected", applied);
        }
        catch { await Fail(); throw; }
        finally { gate.Release(); }
    }
    public async Task Command(string name, JsonElement arguments, CancellationToken ct)
    {
        if (name == "demo.reset") { await Disconnect(ct); await Connect(ct); return; }
        await gate.WaitAsync(ct);
        try
        {
            bool releasing = name is "engineering.release" or "engineering.release-all";
            if (frame is null || (!releasing && (lease is null || faulted))) throw new InvalidOperationException("Shared runtime is unavailable; reset after repair.");
            if (name.StartsWith("engineering.", StringComparison.Ordinal) &&
                (!arguments.TryGetProperty("instanceId", out var expected) || !expected.TryGetGuid(out var expectedInstance) || expectedInstance != instance ||
                 !arguments.TryGetProperty("revision", out var expectedRevision) || expectedRevision.GetString() != Revision))
                throw new ArgumentException("The displayed runtime instance or revision is no longer current.");
            if (name is "demo.start" or "demo.stop")
            {
                var result = await io.SimulateInputs(lease!.Id, new(lease.Revision,
                    [new("sim:bcm5", name == "demo.start"), new("sim:bcm6", name == "demo.stop")]), ct);
                lease = result.Lease; clearInputs = true;
            }
            else if (name == "demo.bank-toggle")
                await Post<ApplicationSignalPendingWrite>(engineeringHttp, "set-once", new ApplicationSignalWriteRequest(instance, Revision, "bank-enabled", Value(!bankEnabled)), ct);
            else if (name == "engineering.force")
            {
                var id = arguments.GetProperty("signalId").GetString() ?? "";
                int seconds = arguments.GetProperty("seconds").GetInt32();
                await Post<ApplicationSignalForce>(engineeringHttp, "force", new ApplicationSignalForceRequest(instance, Revision, id, arguments.GetProperty("value"), seconds), ct);
            }
            else if (name is "engineering.release" or "engineering.release-all")
                await Post<ApplicationSignalSnapshot>(engineeringHttp, "release", new ApplicationSignalReleaseRequest(instance, Revision,
                    name == "engineering.release-all" ? null : arguments.GetProperty("signalId").GetString()), ct);
            else throw new ArgumentException("Unsupported shared command.");
            // Read authority-confirmed normal/effective state; applied GPIO stays separately visible until the next cycle.
            frame = await Get<ApplicationSignalSnapshot>(engineeringHttp, "watch", ct); SetState(faulted ? "unavailable" : "connected");
        }
        finally { gate.Release(); }
    }
    private async Task Fail()
    { faulted = true; latched = false; timing = false; SetState("unavailable"); await ReleaseLease(); }
    private async Task ReleaseLease()
    {
        var previous = lease; lease = null;
        if (previous is not null) try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await io.Release(previous.Id, timeout.Token); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { /* Server expiry remains the fallback. */ }
    }
    public async Task Disconnect(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (frame is not null) try { await Post<ApplicationSignalSnapshot>(engineeringHttp, "release", new ApplicationSignalReleaseRequest(instance, Revision), ct); }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
            await ReleaseLease(); faulted = true; SetState("stopped");
        }
        finally { gate.Release(); }
    }
    private static async Task<T> Get<T>(HttpClient client, string path, CancellationToken ct) =>
        await client.GetFromJsonAsync<T>("/api/apps/v1/engineering/" + path, StateFiles.Json, ct) ?? throw new HttpRequestException("Invalid engineering response.");
    private static async Task<T> Post<T>(HttpClient client, string path, object value, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/apps/v1/engineering/" + path, value, StateFiles.Json, ct); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(StateFiles.Json, ct) ?? throw new HttpRequestException("Invalid engineering response.");
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Startup can race the review server's listener. Retry only before the first successful attachment.
            while (!stoppingToken.IsCancellationRequested)
            { try { await Connect(stoppingToken); break; } catch (Exception e) when (e is HttpRequestException or InvalidOperationException or OperationCanceledException or JsonException or IOException) { await Task.Delay(1500, stoppingToken); } }
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1500));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                try { await Cycle(stoppingToken); } catch (Exception e) when (e is HttpRequestException or InvalidOperationException or OperationCanceledException or JsonException or IOException) { }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await Disconnect(timeout.Token); }
    }
    public override void Dispose() { base.Dispose(); ioHttp.Dispose(); runtimeHttp.Dispose(); engineeringHttp.Dispose(); }
}
