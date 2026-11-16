using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using PiDock.Agent;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Appliance.Web;

public static class ApplicationEngineeringEndpoints
{
    public static void AddApplicationEngineering(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(sp => new ApplicationOverrideService(sp.GetRequiredService<TimeProvider>(), authority =>
        {
            try
            {
                var current = sp.GetRequiredService<ApplicationCredentialVault>().Resolve(authority.AppId, authority.Actor).Credential;
                return sp.GetRequiredService<ApplicationRuntimeAccessGate>().IsCurrent(authority.AppId, authority.Runtime) && current is not null && current.Version == authority.CredentialVersion && current.Purpose == "platform" && current.Scopes.Contains(authority.Scope, StringComparer.Ordinal);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return false; }
        }, entry =>
        {
            if (!sp.GetRequiredService<HistoryStore>().AppendSource(HistoryClass.Audit, entry.AppId, entry))
                throw new PlatformCapacityException("Engineering audit storage is unavailable.");
        }));
        builder.Services.AddSingleton<ApplicationEngineeringMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ApplicationEngineeringMonitor>());
    }
    public static void MapApplicationEngineering(this WebApplication app)
    {
        var group = app.ServiceGroup("/api/apps/v1/engineering", cloud: false);
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!app.Configuration.GetValue<bool>("PiDock:Engineering:Enabled")) return Results.NotFound();
            try
            {
                await context.HttpContext.RequestServices.GetRequiredService<ApplicationRuntimeAccessMonitor>().Refresh(context.HttpContext.RequestAborted, force: context.HttpContext.Request.Method != "GET");
                await context.HttpContext.RequestServices.GetRequiredService<ApplicationEngineeringMonitor>().Refresh(context.HttpContext.RequestAborted);
                return await next(context);
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        });
        group.MapPost("/runtime", (ApplicationSignalRegistration registration, HttpContext context, ApplicationCredentialVault vault, ApplicationOverrideService service) =>
            service.Register(Authority(context, vault, "signals.runtime"), registration)).Scope("signals.runtime");
        group.MapPost("/frames", (ApplicationSignalFrame frame, HttpContext context, ApplicationCredentialVault vault, ApplicationOverrideService service) =>
            service.Publish(Authority(context, vault, "signals.runtime"), frame)).Scope("signals.runtime");
        group.MapGet("/runtime", (HttpContext context, ApplicationCredentialVault vault, ApplicationOverrideService service) =>
            service.Read(Authority(context, vault, "signals.runtime"))).Scope("signals.runtime");
        group.MapGet("/watch", (HttpContext context, ApplicationCredentialVault vault, ApplicationOverrideService service) =>
            service.Read(Authority(context, vault, "signals.engineer"))).Scope("signals.engineer");
        group.MapPost("/set-once", (ApplicationSignalWriteRequest request, HttpContext context, ApplicationCredentialVault vault, ApplicationOverrideService service) =>
            service.SetOnce(Authority(context, vault, "signals.engineer"), request)).Scope("signals.engineer");
        group.MapPost("/force", (ApplicationSignalForceRequest request, HttpContext context, ApplicationCredentialVault vault, ApplicationOverrideService service) =>
            service.Force(Authority(context, vault, "signals.engineer"), request)).Scope("signals.engineer");
        group.MapPost("/release", (ApplicationSignalReleaseRequest request, HttpContext context, ApplicationCredentialVault vault, ApplicationOverrideService service) =>
            service.Release(Authority(context, vault, "signals.engineer"), request)).Scope("signals.engineer");
    }
    private static ApplicationEngineeringAuthority Authority(HttpContext context, ApplicationCredentialVault vault, string scope)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var credential = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? vault.Authenticate(header[7..]) : null;
        if (credential is null || credential.AppId != context.User.FindFirstValue("app") || !credential.Scopes.Contains(scope, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Engineering requires an explicitly provisioned, current managed credential.");
        return new(credential.AppId, credential.Id, credential.Version, credential.ExpiresAt, scope, context.RuntimeBinding());
    }
}

// Polls while idle as well as before requests; loss of broker status clears transient engineering state.
public sealed class ApplicationEngineeringMonitor(BrokerClient broker, ApplicationOverrideService service, IConfiguration configuration,
    TimeProvider clock, ILogger<ApplicationEngineeringMonitor> logger) : BackgroundService
{
    private readonly SemaphoreSlim refresh = new(1, 1);
    public async Task Refresh(CancellationToken ct)
    {
        await refresh.WaitAsync(ct);
        try
        {
            bool open = false;
            if (configuration.GetValue<bool>("PiDock:Engineering:Enabled") && broker.Configured)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
                try { open = await broker.OrchestrationStatus(deadline.Token) is { Paused: false }; }
                catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException or OperationCanceledException or JsonException)
                { logger.LogDebug("App engineering paused: {ErrorType}", e.GetType().Name); }
            }
            service.SetMaintenance(!open);
        }
        finally { refresh.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Refresh(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { service.SetMaintenance(true); }
    }
}
