using PiDock.Core;

namespace PiDock.Appliance.Broker;

public sealed class DigitalIoService(IDigitalIoDriver driver, IConfiguration configuration, TimeProvider time, ILogger<DigitalIoService> logger) : BackgroundService
{
    private sealed record Lease(DigitalLeaseRequest Request, IDigitalLine Line, long Started, bool Fallback);
    private readonly object gate = new();
    private readonly Dictionary<int, Lease> leases = [];
    private readonly HashSet<int> inputs = configuration.GetSection("Gpio:Inputs").Get<int[]>()?.ToHashSet() ?? [];
    private readonly Dictionary<int, bool> outputs = configuration.GetSection("Gpio:Outputs").GetChildren()
        .ToDictionary(x => int.Parse(x.Key, System.Globalization.CultureInfo.InvariantCulture), x => bool.Parse(x.Value!));

    public DigitalIoStatus Status()
    {
        lock (gate)
        {
            Expire();
            try
            {
                var (chip, lines) = driver.Describe();
                return new(true, inputs.Count + outputs.Count == 0 ? "Header detected. No pins are approved for maintenance control." : "Header detected. Only locally configured pins can be claimed.", chip,
                    lines.Select(x => x with { CanInput = DigitalIoRules.Assignable(x.Gpio) && inputs.Contains(x.Gpio), CanOutput = DigitalIoRules.Assignable(x.Gpio) && outputs.ContainsKey(x.Gpio) }).ToArray(),
                    leases.Values.Select(x => new DigitalLeaseView(x.Request.SessionId, x.Request.Gpio, x.Request.Mode, x.Line.Read(),
                        Math.Max(0, (int)Math.Ceiling(x.Request.Seconds - time.GetElapsedTime(x.Started).TotalSeconds)))).ToArray());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(false, "Physical GPIO is unavailable. Check the host broker and Pi 5 GPIO device permissions.", null, [], []); }
        }
    }

    public void Claim(DigitalLeaseRequest request)
    {
        DigitalIoRules.Validate(request);
        lock (gate)
        {
            Expire();
            bool output = request.Mode == "output";
            if (output ? !outputs.ContainsKey(request.Gpio) : !inputs.Contains(request.Gpio))
                throw new ArgumentException("This pin and direction have not been approved in the local host policy.");
            if (leases.ContainsKey(request.Gpio)) throw new ArgumentException("This pin already has an active maintenance session. Release it before claiming it again.");
            var line = driver.Open(request.Gpio, output, request.High);
            leases.Add(request.Gpio, new(request, line, time.GetTimestamp(), output && outputs[request.Gpio]));
            logger.LogInformation("GPIO maintenance claimed: GPIO {Gpio}, mode {Mode}, duration {Seconds}s, session {Session}", request.Gpio, request.Mode, request.Seconds, request.SessionId);
        }
    }

    public void Release(Guid session)
    {
        lock (gate) foreach (var gpio in leases.Where(x => x.Value.Request.SessionId == session).Select(x => x.Key).ToArray()) ReleasePin(gpio);
    }
    private void Expire()
    {
        foreach (var gpio in leases.Where(x => time.GetElapsedTime(x.Value.Started) >= TimeSpan.FromSeconds(x.Value.Request.Seconds)).Select(x => x.Key).ToArray()) ReleasePin(gpio);
    }
    private void ReleasePin(int gpio)
    {
        var lease = leases[gpio];
        try { if (lease.Request.Mode == "output") lease.Line.Write(lease.Fallback); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogError(ex, "GPIO {Gpio} fallback write failed", gpio); }
        finally { lease.Line.Dispose(); leases.Remove(gpio); }
        logger.LogInformation("GPIO maintenance released: GPIO {Gpio}, session {Session}", gpio, lease.Request.SessionId);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), time);
            while (await timer.WaitForNextTickAsync(stoppingToken)) lock (gate) Expire();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { lock (gate) foreach (var gpio in leases.Keys.ToArray()) ReleasePin(gpio); }
    }
}
