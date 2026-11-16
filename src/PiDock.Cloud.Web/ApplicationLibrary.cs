using System.Security.Claims;
using System.Text.Json;
using PiDock.Core;

namespace PiDock.Cloud.Web;

// Browser access uses current workspace membership; publisher bearer credentials remain separate.
public sealed class ApplicationLibrary(ApplicationCatalog catalog, FleetStore fleet)
{
    public PublishedApplicationRelease[] List(ClaimsPrincipal user) =>
        fleet.HasAccess(user) && IdentityRules.IsId(user.FindFirstValue("tenant"))
            ? catalog.List(user.FindFirstValue("tenant")!, 1000) : [];

    public PublishedApplicationRelease? Find(ClaimsPrincipal user, string app, string version) =>
        fleet.HasAccess(user) && IdentityRules.IsId(user.FindFirstValue("tenant"))
            ? catalog.Find(user.FindFirstValue("tenant")!, app, version) : null;

    public PublishedApplicationRelease Publish(ClaimsPrincipal user, ApplicationReleaseRequest definition)
    {
        if (!fleet.HasAccess(user) || !EnrollmentStore.CanManage(user)) throw new UnauthorizedAccessException();
        ApplicationDefinition.Validate(definition);
        return catalog.Publish(user.FindFirstValue("tenant")!, user.Identity!.Name!, definition).Release;
    }
}

public static class ApplicationLibraryEndpoints
{
    public static void MapApplicationLibrary(this WebApplication app)
    {
        app.MapGet("/api/application-library/{id}/{version}/definition", (string id, string version,
            HttpContext context, ApplicationLibrary library) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            var release = library.Find(context.User, id, version);
            if (release is null || release.Package.Installation is null) return Results.NotFound();
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(release.Package, StateFiles.Json),
                "application/json", $"{release.AppId}-{release.Version}.pidock.json");
        }).RequireAuthorization();
    }
}
