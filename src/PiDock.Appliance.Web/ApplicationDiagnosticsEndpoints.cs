using System.Security.Claims;
using PiDock.Agent;
using PiDock.Web.Shared;

namespace PiDock.Appliance.Web;

public static class ApplicationDiagnosticsEndpoints
{
    public static void MapApplicationDiagnostics(this WebApplication app)
    {
        var group = app.ServiceGroup("/api/apps/v1/notifications", cloud: false);
        group.MapGet("", (ClaimsPrincipal user, SharedApplicationServices services) => services.Notifications(Sources(user))).Scope("notifications.read");
        group.MapPost("/{id:guid}/acknowledge", (Guid id, ClaimsPrincipal user, SharedApplicationServices services) =>
            services.AcknowledgeNotification(Sources(user), id, user.Identity!.Name!) is { } entry ? Results.Ok(entry) : Results.NotFound()).Scope("notifications.acknowledge");
    }
    private static string[] Sources(ClaimsPrincipal user) => user.FindAll("read-source").Select(c => c.Value)
        .Append(user.FindFirstValue("app")!).Distinct(StringComparer.Ordinal).ToArray();
}
