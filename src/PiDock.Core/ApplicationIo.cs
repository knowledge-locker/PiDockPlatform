using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PiDock.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ApplicationIoDirection>))]
public enum ApplicationIoDirection { Input, Output }
public sealed record ApplicationIoSupport(int SchemaVersion, bool Enabled, string Adapter, bool Physical,
    int MaximumLeaseSeconds, int MaximumBatchSize, string[] Limitations, bool LifecycleBound = false);
public sealed record ApplicationIoGrant(string AppId, string Alias, ApplicationIoDirection Direction,
    bool Fallback = false, bool ActiveLow = false, string Pull = "none");
public sealed record ApplicationIoSignal(string Alias, int Bcm, int HeaderPin, ApplicationIoDirection Direction,
    bool ActiveLow, string Pull, bool Fallback);
public sealed record ApplicationIoLeaseRequest(string[] Aliases, int Seconds = 10);
public sealed record ApplicationIoLease(Guid Id, string AppId, string[] Aliases, long Revision, DateTimeOffset ExpiresAt);
public sealed record ApplicationIoRenewRequest(long ExpectedRevision, int Seconds = 10);
public sealed record ApplicationIoWrite(string Alias, bool Value);
public sealed record ApplicationIoWriteRequest(long ExpectedRevision, ApplicationIoWrite[] Values);
public sealed record ApplicationIoValue(string Alias, bool Value, bool? Commanded, string Quality);
public sealed record ApplicationIoFrame(Guid BootId, long Sequence, long MonotonicTicks, long TimestampFrequency,
    DateTimeOffset ObservedAt, string Adapter, ApplicationIoLease Lease, ApplicationIoValue[] Values);

// No Owner authority is inferred. Supply the app's caller-owned HTTPS client and platform credential.
public sealed class ApplicationIoClient(HttpClient http)
{
    private const string Root = "/api/apps/v1/io";
    public Task<ApplicationIoSupport> Support(CancellationToken ct = default) => Get<ApplicationIoSupport>("/support", ct);
    public Task<ApplicationIoSignal[]> Signals(CancellationToken ct = default) => Get<ApplicationIoSignal[]>("/signals", ct);
    public Task<ApplicationIoLease> Acquire(ApplicationIoLeaseRequest request, CancellationToken ct = default) => Post<ApplicationIoLease>("/leases", request, ct);
    public Task<ApplicationIoFrame> Read(Guid lease, CancellationToken ct = default) => Get<ApplicationIoFrame>("/leases/" + lease, ct);
    public Task<ApplicationIoLease> Renew(Guid lease, ApplicationIoRenewRequest request, CancellationToken ct = default) => Post<ApplicationIoLease>("/leases/" + lease + "/renew", request, ct);
    public Task<ApplicationIoFrame> Write(Guid lease, ApplicationIoWriteRequest request, CancellationToken ct = default) => Post<ApplicationIoFrame>("/leases/" + lease + "/outputs", request, ct);
    public Task<ApplicationIoFrame> SimulateInputs(Guid lease, ApplicationIoWriteRequest request, CancellationToken ct = default) => Post<ApplicationIoFrame>("/leases/" + lease + "/simulated-inputs", request, ct);
    public async Task Release(Guid lease, CancellationToken ct = default)
    { using var response = await http.DeleteAsync(Root + "/leases/" + lease, ct); response.EnsureSuccessStatusCode(); }
    private async Task<T> Get<T>(string path, CancellationToken ct) => await http.GetFromJsonAsync<T>(Root + path, StateFiles.Json, ct) ?? throw new HttpRequestException("Invalid I/O response.");
    private async Task<T> Post<T>(string path, object request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(Root + path, request, StateFiles.Json, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(StateFiles.Json, ct) ?? throw new HttpRequestException("Invalid I/O response.");
    }
}
