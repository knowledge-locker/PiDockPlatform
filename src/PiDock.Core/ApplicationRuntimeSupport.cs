using System.Net.Http.Json;

namespace PiDock.Core;

// Non-secret capability metadata for managed apps; grants no lifecycle mutation or agent authority.
public sealed record ApplicationRuntimeSupport(int SchemaVersion, ApplicationResourceSupport Resources,
    ApplicationHealthSupport Health, ApplicationLifecycleSupport Lifecycle, ApplicationOrchestrationSupport Orchestration,
    ApplicationIoSupport Io, ApplicationEngineeringSupport Engineering, ApplicationRuntimeAccessSupport RuntimeAccess);
public static class ApplicationRuntimeSupportClient
{
    public const string Path = "/api/apps/v1/runtime-support";
    public static async Task<ApplicationRuntimeSupport> Read(HttpClient client, CancellationToken ct = default) =>
        await client.GetFromJsonAsync<ApplicationRuntimeSupport>(Path, StateFiles.Json, ct)
        ?? throw new HttpRequestException("Runtime support unavailable.");
}
