using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PiDock.Agent;

// Separate from health/recovery polling: pulling an immutable image must not stall those checks.
public sealed class ApplicationUpdateService(BrokerClient broker,TimeProvider clock,ILogger<ApplicationUpdateService> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try {await Reconcile(stoppingToken);}
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception e)when(e is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
            {logger.LogDebug("App update pass unavailable: {ErrorType}",e.GetType().Name);}
            try {await Task.Delay(TimeSpan.FromSeconds(2),clock,stoppingToken);}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
        }
    }
    public async Task Reconcile(CancellationToken ct)
    {
        if(!broker.Configured||(await broker.UpdateSupport(ct))?.Enabled!=true)return;
        foreach(var status in (await broker.UpdateStatus(ct)).Where(s=>s.CanAdvance&&s.NextCheckAt<=clock.GetUtcNow()||s.CanAdvance&&s.NextCheckAt is null).Take(16))
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(220));
            await broker.AdvanceUpdate(status,deadline.Token);
        }
    }
}
