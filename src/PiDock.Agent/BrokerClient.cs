using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using PiDock.Core;
namespace PiDock.Agent;

public sealed class BrokerClient(IConfiguration configuration) : IDisposable
{
    private readonly HttpClient? http = configuration["Broker:SocketPath"] is { Length: > 0 } path ? UnixHttp.Connect(path, TimeSpan.FromMinutes(3)) : null;
    public bool Configured => http is not null;
    public async Task<ApplicationSummary[]> List() => http is null ? [] : await http.GetFromJsonAsync<ApplicationSummary[]>("/applications", StateFiles.Json) ?? [];
    public async Task Deploy(ApplicationManifest manifest)
    {
        manifest.Validate(); if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.PostAsJsonAsync("/applications", manifest); response.EnsureSuccessStatusCode();
    }
    public async Task ChangeState(string id, bool start)
    {
        if (http is null) throw new InvalidOperationException("Host broker is not configured.");
        using var response = await http.PostAsync($"/applications/{Uri.EscapeDataString(id)}/{(start ? "start" : "stop")}", null); response.EnsureSuccessStatusCode();
    }
    public void Dispose() => http?.Dispose();
}
