using PiDock.Core;
using PiDock.Appliance.Web;
using Microsoft.AspNetCore.Components.Authorization;
using PiDock.Agent;
using PiDock.Appliance.Web.Components;
using PiDock.Web.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.AddPiDockAuthentication("Appliance");
builder.AddPlatformDiscovery();
builder.AddSharedApplicationServices();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
builder.Services.AddSingleton<ICapacityProbe, DriveCapacityProbe>();
builder.Services.AddSingleton(sp => new HistoryStore(sp.GetRequiredService<StateDirectory>().Path, sp.GetRequiredService<ICapacityProbe>()));
builder.Services.AddSingleton<HostMonitor>();
builder.Services.AddSingleton<BrokerClient>();
builder.Services.AddSingleton<IOsUpdater, OsUpdaterClient>();
builder.Services.AddSingleton(sp => new OsUpdateAgent(sp.GetRequiredService<IOsUpdater>(), sp.GetRequiredService<CloudEnrollmentClient>(), sp.GetRequiredService<MqttConnection>(), sp.GetRequiredService<StateDirectory>().Path));
builder.Services.AddHostedService(sp => sp.GetRequiredService<OsUpdateAgent>());
builder.Services.AddSingleton<IApplicationRuntime>(sp => sp.GetRequiredService<BrokerClient>());
builder.Services.AddSingleton(sp => new RemoteApplicationAgent(builder.Configuration, sp.GetRequiredService<IApplicationRuntime>(), sp.GetRequiredService<MqttConnection>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<StateDirectory>().Path));
builder.Services.AddHostedService(sp => sp.GetRequiredService<RemoteApplicationAgent>());
builder.Services.AddSingleton(sp => new RemoteInterfaceAgent(sp.GetRequiredService<CloudEnrollmentClient>(), sp.GetRequiredService<StateDirectory>().Path, sp.GetRequiredService<ILogger<RemoteInterfaceAgent>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<RemoteInterfaceAgent>());
builder.Services.AddSingleton(new GpioSimulator(builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("PiDock:SimulateGpio")));
builder.Services.AddSingleton(sp => new MqttConnection(builder.Configuration, sp.GetRequiredService<StateDirectory>().Path));
builder.Services.AddSingleton(sp => new CloudEnrollmentClient(sp.GetRequiredService<StateDirectory>().Path, sp.GetRequiredService<MqttConnection>()));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<AgentSession>();
builder.Services.AddSingleton<DeliveryStatus>();
builder.Services.AddSingleton(sp => new AlarmMonitor(sp.GetRequiredService<StateDirectory>().Path, sp.GetRequiredService<HistoryStore>(), sp.GetRequiredService<TimeProvider>(), builder.Configuration.GetSection("Alarms:Rules").Get<AlarmRule[]>()));
builder.Services.AddSingleton<TelemetryDeliveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryDeliveryService>());
builder.Services.AddSingleton(sp => new TelemetryPolicy(sp.GetRequiredService<StateDirectory>().Path));
builder.Services.AddSingleton<MonitoringService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitoringService>());
var installedRelease = OsUpdateRules.Installed(AppContext.BaseDirectory);
var app = builder.Build();
if (MediaProvisioning.Import(app,args)) return;
if (app.Commission(args, singleOwner: true)) return;
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Error"); app.UseHsts(); app.UseHttpsRedirection(); }
app.Use(async (context,next) => {
    if(context.Request.Path.StartsWithSegments("/agent-access")) { context.Response.Headers.CacheControl="no-store";context.Response.Headers["Referrer-Policy"]="same-origin"; }
    await next(context);
});
app.UseAuthentication();
app.Use(async (context, next) => {
    var relay = context.RequestServices.GetRequiredService<RemoteInterfaceAgent>();
    var proof = context.Request.Headers["X-PiDock-Remote-Proof"].ToString();
    if (proof.Length > 0) {
        if (!relay.Allowed || proof != relay.LocalProof || !Guid.TryParse(context.Request.Headers["X-PiDock-Remote-Id"], out var session)) { context.Response.StatusCode = 403; return; }
        if (context.User.Identity?.IsAuthenticated == true) context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(context.User.Claims.Append(new("pidock_remote", session.ToString())), context.User.Identity.AuthenticationType));
    }
    await next(context);
});
app.UseAuthorization(); app.UseRateLimiter(); app.UseAntiforgery();
app.MapPiDockAuthentication("Appliance");
app.MapPlatformDiscovery();
app.MapSharedApplicationServices();
app.MapStaticAssets();
app.MapGet("/health/update", (MonitoringService monitor) => new OsUpdateHealth(installedRelease?.Digest,
    monitor.Current is { } current && DateTimeOffset.UtcNow - current.ObservedAt < TimeSpan.FromSeconds(15) && monitor.LastError is null));
app.MapGet("/api/telemetry/export", (DateTimeOffset from, DateTimeOffset to, string? format, string? signals, HttpContext context, HistoryStore history) =>
{
    if (!TelemetryExport.TrySelection(from, to, format, signals, out var metrics)) return Results.BadRequest();
    context.Response.Headers.CacheControl = "no-store";
    return TelemetryExport.Download("local", from, to, format!, metrics, history.Range(from, to));
}).RequireAuthorization();
app.MapGet("/api/status", (MonitoringService monitor) => monitor.Current).RequireAuthorization();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization();
app.Run();
public partial class Program;
