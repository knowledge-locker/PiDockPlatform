using System.Net.Http.Json;

namespace PiDock.Core;

// Supply a caller-owned HTTPS HttpClient with the app's own credential. No owner/cloud authority is inferred.
public sealed class ApplicationServicesClient(HttpClient http)
{
    private const string Root = "/api/apps/v1";
    public Task<SharedServiceEvent> Log(ApplicationLogEvent value, CancellationToken ct = default) => Post<SharedServiceEvent>("/logs", value, ct);
    public Task<SharedServiceEvent> Measure(ApplicationMeasurement value, CancellationToken ct = default) => Post<SharedServiceEvent>("/measurements", value, ct);
    public Task<ApplicationAlarm> Raise(ApplicationAlarmRequest value, CancellationToken ct = default) => Post<ApplicationAlarm>("/alarms", value, ct);
    public Task<ApplicationAlarm> Clear(string condition, CancellationToken ct = default) => Post<ApplicationAlarm>("/alarms/" + Segment(condition) + "/clear", null, ct);
    public Task<ApplicationAlarm> Acknowledge(string app, string condition, CancellationToken ct = default) =>
        Post<ApplicationAlarm>("/alarms/" + Segment(app) + "/" + Segment(condition) + "/acknowledge", null, ct);
    public Task<SharedServiceEvent[]> Logs(CancellationToken ct = default) => Get<SharedServiceEvent[]>("/logs", ct);
    public Task<SharedServiceEvent[]> Measurements(CancellationToken ct = default) => Get<SharedServiceEvent[]>("/measurements", ct);
    public Task<ApplicationAlarm[]> Alarms(CancellationToken ct = default) => Get<ApplicationAlarm[]>("/alarms", ct);
    private static string Segment(string id) => IdentityRules.IsId(id) ? id : throw new ArgumentException("Invalid platform ID.");
    private async Task<T> Post<T>(string path, object? value, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Root + path);
        if (value is not null) request.Content = JsonContent.Create(value, options: StateFiles.Json);
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(StateFiles.Json, ct) ?? throw new HttpRequestException("Invalid platform response.");
    }
    private async Task<T> Get<T>(string path, CancellationToken ct) => await http.GetFromJsonAsync<T>(Root + path, StateFiles.Json, ct)
        ?? throw new HttpRequestException("Invalid platform response.");
}
