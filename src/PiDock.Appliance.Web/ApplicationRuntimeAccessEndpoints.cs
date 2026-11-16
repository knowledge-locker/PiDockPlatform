using PiDock.Agent;
using PiDock.Core;

namespace PiDock.Appliance.Web;

public static class ApplicationRuntimeAccessEndpoints
{
    public static void AddApplicationRuntimeAccess(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(sp => new ApplicationRuntimeAccessGate(builder.Configuration.GetValue<bool>("PiDock:Apps:RuntimeAccess:Enabled"), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<ApplicationRuntimeAccessMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ApplicationRuntimeAccessMonitor>());
    }
    public static ApplicationRuntimeBinding? RuntimeBinding(this HttpContext context)
    {
        var access = context.RequestServices.GetRequiredService<ApplicationRuntimeAccessGate>();
        Guid? delivery = null;
        if (access.Enabled)
        {
            var header = context.Request.Headers[ApplicationRuntimeAccess.Header];
            if (header.Count != 1 || !Guid.TryParse(header.ToString(), out var value) || value == Guid.Empty)
                throw new UnauthorizedAccessException("Private startup authority is required.");
            delivery = value;
        }
        return access.Require(context.User.FindFirst("app")!.Value, delivery);
    }
}

public sealed class ApplicationRuntimeAccessMonitor(BrokerClient broker, ApplicationRuntimeAccessGate access,
    ApplicationIoService io, ApplicationOverrideService engineering, TimeProvider clock) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long lastProbe;
    private bool probed;
    public async Task Refresh(CancellationToken ct, bool force = false)
    {
        if (!access.Enabled) return;
        await gate.WaitAsync(ct);
        try
        {
            if (!force && probed && access.IsFresh && clock.GetElapsedTime(lastProbe) < TimeSpan.FromMilliseconds(250)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                var snapshot = await broker.RuntimeAccess(timeout.Token) ?? throw new IOException("Runtime authority is unavailable.");
                access.Apply(snapshot);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
            { access.Unavailable(); }
            lastProbe = clock.GetTimestamp(); probed = true;
            // The actor services invalidate independently of application cooperation.
            try { io.Sweep(); engineering.Tick(); }
            catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException)
            { access.Unavailable(); io.ReleaseAll(); engineering.Tick(); }
        }
        finally { gate.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!access.Enabled) return;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(stoppingToken)) await Refresh(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { access.Unavailable(); io.ReleaseAll(); engineering.Tick(); }
    }
}
