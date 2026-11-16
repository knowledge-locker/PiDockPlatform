using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiDock.Core;

namespace PiDock.Agent;

// Runs in the appliance host, where the current credential vault is available. The broker owns all intent and retry state.
public sealed class ApplicationRecoveryService(BrokerClient broker, TimeProvider clock, ILogger<ApplicationRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Reconcile(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
            { logger.LogDebug("App recovery pass unavailable: {ErrorType}", e.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    public async Task Reconcile(CancellationToken ct)
    {
        if (!broker.Configured) return;
        try
        {
            using var healthPass = CancellationTokenSource.CreateLinkedTokenSource(ct);
            healthPass.CancelAfter(TimeSpan.FromSeconds(35));
            if ((await broker.HealthSupport(healthPass.Token))?.Enabled == true) await broker.CheckHealth(healthPass.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
        { logger.LogDebug("App health pass unavailable: {ErrorType}",e.GetType().Name); }
        using var pass = CancellationTokenSource.CreateLinkedTokenSource(ct); pass.CancelAfter(TimeSpan.FromSeconds(45));
        var orchestration=(await broker.OrchestrationSupport(pass.Token))?.Enabled==true;
        var recovery=(await broker.LifecycleSupport(pass.Token))?.Enabled==true;
        if(!orchestration && !recovery)return;
        if(orchestration)await broker.OrchestrationControl("reconcile",pass.Token);
        if(recovery)await broker.ReconcileLifecycle(pass.Token);
        var candidates = await broker.LifecycleStatus(pass.Token);
        foreach (var app in candidates.Take(16))
        {
            if (app.UpdateInProgress || !app.DesiredRunning || !(recovery && app.RecoveryEnabled || orchestration && app.PendingStart) || app.OrchestrationPaused || app.Dependencies?.Any(d=>!d.Satisfied)==true || app.RuntimeState is not ("exited" or "created" or "dead") ||
                app.Attempts >= ApplicationRecoveryRules.MaximumAttempts || app.NextAttemptAt > clock.GetUtcNow()) continue;
            try
            {
                await broker.Recover(app, pass.Token);
                logger.LogInformation("App recovery checked {AppId}/{ContainerId} at revision {Revision}",app.AppId,app.ContainerId,app.Revision);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
            { logger.LogWarning("App recovery unavailable for {AppId}/{ContainerId}: {ErrorType}",app.AppId,app.ContainerId,e.GetType().Name); }
            if (pass.IsCancellationRequested) break;
        }
    }
}
