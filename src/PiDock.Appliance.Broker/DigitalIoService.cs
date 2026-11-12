using PiDock.Core;
using System.Text.Json;

namespace PiDock.Appliance.Broker;

public sealed class DigitalIoService(IDigitalIoDriver driver, IConfiguration configuration, TimeProvider time, ILogger<DigitalIoService> logger) : BackgroundService
{
    private sealed record Lease(DigitalLeaseRequest Request, IDigitalLine Line, long Started, bool Fallback);
    private readonly object gate = new();
    private readonly Dictionary<int, Lease> leases = [];
    private readonly string? policyPath=configuration["Gpio:PolicyPath"];
    private DigitalIoPolicy? policy;
    private DigitalIoPolicy Policy
    {
        get
        {
            if(policy is not null)return policy;
            DigitalIoPolicy loaded;
            if(policyPath is not null&&File.Exists(policyPath))
            {
                if(new FileInfo(policyPath).Length>4096)throw new IOException("GPIO policy is oversized.");
                loaded=JsonSerializer.Deserialize<DigitalIoPolicy>(File.ReadAllText(policyPath),StateFiles.Json)??throw new IOException("GPIO policy is invalid.");
            }
            else loaded=new(configuration.GetSection("Gpio:Inputs").Get<int[]>()??[],configuration.GetSection("Gpio:Outputs").GetChildren()
                .ToDictionary(x=>int.Parse(x.Key,System.Globalization.CultureInfo.InvariantCulture),x=>bool.Parse(x.Value!)));
            loaded.Validate();policy=loaded;return loaded;
        }
    }
    public void Configure(DigitalIoPolicy next)
    {
        next.Validate();
        lock(gate)
        {
            if(policyPath is null)throw new InvalidOperationException("Local GPIO policy storage is not configured on this host.");
            Expire();if(leases.Count!=0)throw new InvalidOperationException("Release maintenance sessions before changing the pin policy.");
            var saved=new DigitalIoPolicy(next.Inputs.ToArray(),new(next.Outputs));
            StateFiles.WriteAtomic(policyPath,JsonSerializer.Serialize(saved,StateFiles.Json));policy=saved;
            logger.LogInformation("Local GPIO policy changed: {Inputs} inputs, {Outputs} outputs approved",saved.Inputs.Length,saved.Outputs.Count);
        }
    }
    public void ConfigurePin(DigitalPinPermission permission)
    {
        if(!DigitalIoRules.Assignable(permission.Gpio))throw new ArgumentException("Reserved or invalid GPIO.");
        lock(gate)
        {
            var current=Policy;
            var inputs=current.Inputs.Where(x=>x!=permission.Gpio).ToList();
            if(permission.Input)inputs.Add(permission.Gpio);
            var outputs=new Dictionary<int,bool>(current.Outputs);outputs.Remove(permission.Gpio);
            if(permission.Output)outputs[permission.Gpio]=permission.FallbackHigh;
            Configure(new(inputs.ToArray(),outputs));
        }
    }

    public DigitalIoStatus Status()
    {
        lock (gate)
        {
            Expire();
            try
            {
                var (chip, lines) = driver.Describe();
                var approved=Policy;
                return new(true, approved.Inputs.Length + approved.Outputs.Count == 0 ? "Header detected. No pins are approved for maintenance control." : "Header detected. Only locally configured pins can be claimed.", chip,
                    lines.Select(x => x with { CanInput = DigitalIoRules.Assignable(x.Gpio) && approved.Inputs.Contains(x.Gpio), CanOutput = DigitalIoRules.Assignable(x.Gpio) && approved.Outputs.ContainsKey(x.Gpio), OutputFallback=approved.Outputs.TryGetValue(x.Gpio,out var fallback)?fallback:null }).ToArray(),
                    leases.Values.Select(x => new DigitalLeaseView(x.Request.SessionId, x.Request.Gpio, x.Request.Mode, x.Line.Read(),
                        Math.Max(0, (int)Math.Ceiling(x.Request.Seconds - time.GetElapsedTime(x.Started).TotalSeconds)))).ToArray(),policyPath is not null);
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
            var approved=Policy;
            if (output ? !approved.Outputs.ContainsKey(request.Gpio) : !approved.Inputs.Contains(request.Gpio))
                throw new ArgumentException("This pin and direction have not been approved in the local host policy.");
            if (leases.ContainsKey(request.Gpio)) throw new ArgumentException("This pin already has an active maintenance session. Release it before claiming it again.");
            var line = driver.Open(request.Gpio, output, request.High);
            leases.Add(request.Gpio, new(request, line, time.GetTimestamp(), output && approved.Outputs[request.Gpio]));
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
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // In .NET 10 a stop can cancel the scheduled background delegate before ExecuteAsync starts.
        // Claims accepted in that interval must still close; its finally block may never run.
        try { await base.StopAsync(cancellationToken); }
        finally { lock(gate) foreach(var gpio in leases.Keys.ToArray()) ReleasePin(gpio); }
    }
    public override void Dispose()
    {
        base.Dispose();
        lock(gate) foreach(var gpio in leases.Keys.ToArray()) ReleasePin(gpio);
    }
}
