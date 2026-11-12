using System.Security.Claims;
using PiDock.Agent;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Appliance.Web;

public static class SharedApplicationEndpoints
{
    public const string Root = "/api/apps/v1";
    public static void AddSharedApplicationServices(this WebApplicationBuilder builder)
    {
        builder.AddServiceCredentials(cloud: false);
        builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider, PlatformLogProvider>();
        builder.Services.AddSingleton(sp => new SharedApplicationServices(sp.GetRequiredService<StateDirectory>().Path,
            sp.GetRequiredService<HistoryStore>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<AgentSession>()));
    }
    private static string App(ClaimsPrincipal user) => user.FindFirstValue("app")!;
    private static string[] Sources(ClaimsPrincipal user) => user.FindAll("read-source").Select(c => c.Value).Append(App(user)).Distinct().ToArray();
    public static void MapSharedApplicationServices(this WebApplication app)
    {
        var group = app.ServiceGroup(Root, cloud: false);
        group.MapPost("/logs", (ApplicationLogEvent value, ClaimsPrincipal user, SharedApplicationServices services) =>
            services.Log(App(user), value, value.Priority is { } p && user.HasClaim("alarm-priority", ((int)p).ToString(System.Globalization.CultureInfo.InvariantCulture)))).Scope("logs.write");
        group.MapGet("/logs", (ClaimsPrincipal user, SharedApplicationServices services) => services.Read("log", Sources(user))).Scope("logs.read");
        group.MapPost("/measurements", (ApplicationMeasurement value, ClaimsPrincipal user, SharedApplicationServices services) => services.Measure(App(user), value)).Scope("telemetry.write");
        group.MapGet("/measurements", (ClaimsPrincipal user, SharedApplicationServices services) => services.Read("measurement", Sources(user))).Scope("telemetry.read");
        group.MapGet("/alarms", (ClaimsPrincipal user, SharedApplicationServices services) => services.Alarms(Sources(user))).Scope("alarms.read");
        group.MapPost("/alarms", (ApplicationAlarmRequest value, ClaimsPrincipal user, SharedApplicationServices services) => services.Raise(App(user), value)).Scope("alarms.raise");
        group.MapPost("/alarms/{condition}/clear", (string condition, ClaimsPrincipal user, SharedApplicationServices services) =>
            services.Transition(App(user), condition, false) is { } alarm ? Results.Ok(alarm) : Results.NotFound()).Scope("alarms.raise");
        group.MapPost("/alarms/{source}/{condition}/acknowledge", (string source, string condition, ClaimsPrincipal user, SharedApplicationServices services) =>
        {
            if (!ServiceCredentials.CanRead(user, source)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return services.Transition(source, condition, true) is { } alarm ? Results.Ok(alarm) : Results.NotFound();
        }).Scope("alarms.acknowledge");
        group.MapGet("/alarm-events", (ClaimsPrincipal user, SharedApplicationServices services) => services.Read("alarm", Sources(user))).Scope("alarms.read");
        group.MapGet("/health", (SharedApplicationServices services, HistoryStore history) => new { pendingAlarmTransitions = services.PendingAlarmTransitions, hostLogDrops = PlatformLogProvider.Dropped, rejectedSourceWrites = history.Status().RejectedSourceWrites }).Scope("system.read");
        group.MapGet("/system", (MonitoringService monitor) => monitor.Current).Scope("system.read");
    }
}
