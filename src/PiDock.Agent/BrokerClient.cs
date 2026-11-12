using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using PiDock.Core;
namespace PiDock.Agent;

public interface IApplicationRuntime
{
    bool Configured { get; }
    Task<ApplicationSummary[]> List(CancellationToken ct = default);
    Task ChangeState(string id, bool start, CancellationToken ct = default);
    Task Restart(string id, CancellationToken ct = default);
}
public sealed class BrokerClient(IConfiguration configuration) : IDisposable, IApplicationRuntime
{
    private readonly HttpClient? http = configuration["Broker:SocketPath"] is { Length: > 0 } path ? UnixHttp.Connect(path, TimeSpan.FromMinutes(3)) : null;
    public bool Configured => http is not null;
    public async Task<DigitalIoStatus> DigitalStatus(CancellationToken ct = default) => http is null
        ? new(false, "Host broker is not connected.", null, [], [])
        : await http.GetFromJsonAsync<DigitalIoStatus>("/gpio", StateFiles.Json, ct) ?? throw new HttpRequestException("GPIO response unavailable.");
    public async Task ClaimDigital(DigitalLeaseRequest request)
    {
        DigitalIoRules.Validate(request);
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.PostAsJsonAsync("/gpio/leases", request); response.EnsureSuccessStatusCode();
    }
    public async Task ReleaseDigital(Guid session)
    {
        if (http is null) return;
        using var response = await http.DeleteAsync($"/gpio/leases/{session:D}"); response.EnsureSuccessStatusCode();
    }
    public async Task<ApplicationSummary[]> List(CancellationToken ct = default) => http is null ? [] : await http.GetFromJsonAsync<ApplicationSummary[]>("/applications", StateFiles.Json, ct) ?? [];
    public async Task Deploy(ApplicationManifest manifest)
    {
        manifest.Validate(); if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.PostAsJsonAsync("/applications", manifest); response.EnsureSuccessStatusCode();
    }
    public async Task ChangeState(string id, bool start, CancellationToken ct = default)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.PostAsync($"/applications/{Uri.EscapeDataString(id)}/{(start ? "start" : "stop")}", null, ct); response.EnsureSuccessStatusCode();
    }
    public async Task Restart(string id, CancellationToken ct = default)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.PostAsync($"/applications/{Uri.EscapeDataString(id)}/restart", null, ct); response.EnsureSuccessStatusCode();
    }
    public void Dispose() => http?.Dispose();
}
