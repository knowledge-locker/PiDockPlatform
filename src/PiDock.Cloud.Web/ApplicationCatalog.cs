using System.Security.Claims;
using System.Text.Json;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Cloud.Web;

public sealed class ApplicationCatalog
{
    public const string Root = "/api/applications/v1";
    private readonly object gate = new();
    private readonly string path;
    private readonly TimeProvider clock;
    private PublishedApplicationRelease[] releases;
    public ApplicationCatalog(StateDirectory state, TimeProvider clock)
    {
        this.clock = clock; path = Path.Combine(state.Path, "application-catalog.json");
        releases = File.Exists(path) ? JsonSerializer.Deserialize<PublishedApplicationRelease[]>(File.ReadAllText(path), StateFiles.Json)
            ?? throw new IOException("Invalid application catalog.") : [];
    }
    public PublishedApplicationRelease[] List(string tenant)
    {
        lock (gate) return releases.Where(r => r.TenantId == tenant).OrderByDescending(r => r.PublishedAt).Take(100).ToArray();
    }
    public PublishedApplicationRelease? Find(string tenant, string app, string version)
    {
        lock (gate) return releases.FirstOrDefault(r => r.TenantId == tenant && r.AppId == app && r.Version == version);
    }
    public (PublishedApplicationRelease Release, bool Created) Publish(string tenant, string publisher, ApplicationReleaseRequest request)
    {
        ApplicationContractRules.Validate(request);
        var digest = ApplicationContractRules.Digest(request);
        lock (gate)
        {
            var existing = Find(tenant, request.Manifest.Id, request.Manifest.Version);
            if (existing is not null)
            {
                if (existing.Digest != digest) throw new PlatformConflictException("Release versions are immutable.");
                return (existing, false);
            }
            if (releases.Length >= 10000 || releases.Count(r => r.TenantId == tenant) >= 1000)
                throw new PlatformCapacityException("Catalog quota reached.");
            var entry = new PublishedApplicationRelease(tenant, request.Manifest.Id, request.Manifest.Version, digest, publisher, clock.GetUtcNow(), request);
            var updated = releases.Append(entry).ToArray();
            var json = JsonSerializer.Serialize(updated, StateFiles.Json);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new PlatformCapacityException("Catalog byte quota reached.");
            var freeBytes = new PiDock.Agent.DriveCapacityProbe().Read(path, 0).FreeBytes;
            if (freeBytes < 64L * 1024 * 1024 + json.Length * 8L) throw new PlatformCapacityException("Cloud free-space reserve reached.");
            StateFiles.WriteAtomic(path, json); releases = updated;
            return (entry, true);
        }
    }
}

public static class ApplicationCatalogEndpoints
{
    public static void AddApplicationCatalog(this WebApplicationBuilder builder)
    {
        builder.AddServiceCredentials(cloud: true);
        builder.Services.AddSingleton<ApplicationCatalog>();
    }
    public static void MapApplicationCatalog(this WebApplication app)
    {
        var group = app.ServiceGroup(ApplicationCatalog.Root, cloud: true);
        group.MapGet("/discovery", (ClaimsPrincipal user) => new CloudApplicationDiscovery(1, user.FindFirstValue("tenant")!,
            ApplicationCatalog.Root + "/releases", ApplicationCatalog.Root + "/releases",
            user.FindAll("scope").Select(c => c.Value).Where(s => s is "packages.read" or "packages.publish").Distinct().ToArray(),
            "registry-reference-v1"));
        group.MapGet("/releases", (ClaimsPrincipal user, ApplicationCatalog catalog) => catalog.List(user.FindFirstValue("tenant")!)).Scope("packages.read");
        group.MapGet("/releases/{id}/{version}", (string id, string version, ClaimsPrincipal user, ApplicationCatalog catalog) =>
            catalog.Find(user.FindFirstValue("tenant")!, id, version) is { } release ? Results.Ok(release) : Results.NotFound()).Scope("packages.read");
        group.MapPost("/releases", (ApplicationReleaseRequest request, ClaimsPrincipal user, ApplicationCatalog catalog) =>
        {
            // A publisher identity is bound to its approved application, not a caller-supplied tenant.
            if (request.Manifest?.Id != user.FindFirstValue("app")) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (release, created) = catalog.Publish(user.FindFirstValue("tenant")!, user.Identity!.Name!, request);
            return created ? Results.Created($"{ApplicationCatalog.Root}/releases/{release.AppId}/{release.Version}", release) : Results.Ok(release);
        }).Scope("packages.publish");
    }
}
