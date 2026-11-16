using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using PiDock.Core;

namespace PiDock.Agent;

public interface IOsUpdater
{
    bool Configured { get; }
    Task<OsUpdateSnapshot> Status(CancellationToken ct = default);
    Task SetCloudEnabled(bool enabled, CancellationToken ct = default);
    Task<OsReleaseInfo> Stage(Stream input, CancellationToken ct = default);
    Task<OsUpdateOperation> Install(OsUpdateRequest request, CancellationToken ct = default);
}
public sealed class OsUpdaterClient(IConfiguration settings) : IOsUpdater, IDisposable
{
    private readonly HttpClient? http = settings["Updates:SocketPath"] is { Length: > 0 } path ? UnixHttp.Connect(path, TimeSpan.FromMinutes(10)) : null;
    public bool Configured => http is not null;
    private HttpClient Client => http ?? throw new InvalidOperationException("The independent PiDockOS updater has not been installed.");
    public async Task<OsUpdateSnapshot> Status(CancellationToken ct = default) => await Client.GetFromJsonAsync<OsUpdateSnapshot>("/status", StateFiles.Json, ct) ?? throw new IOException("No updater status.");
    public async Task SetCloudEnabled(bool enabled, CancellationToken ct = default) { using var r = await Client.PostAsJsonAsync("/policy", new OsUpdatePolicy(enabled), ct); r.EnsureSuccessStatusCode(); }
    public async Task<OsReleaseInfo> Stage(Stream input, CancellationToken ct = default)
    {
        using var content = new StreamContent(input); content.Headers.ContentType = new("application/zip");
        using var response = await Client.PostAsync("/packages", content, ct); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OsReleaseInfo>(StateFiles.Json, ct) ?? throw new IOException("No verified release.");
    }
    public async Task<OsUpdateOperation> Install(OsUpdateRequest request, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync("/install", request, ct); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OsUpdateOperation>(StateFiles.Json, ct) ?? throw new IOException("No update operation.");
    }
    public void Dispose() => http?.Dispose();
}
public sealed record ReceivedOsUpdate(Guid Id, DateTimeOffset ExpiresAt);
public sealed record OsAgentJournal(IReadOnlyList<ReceivedOsUpdate> Received, OsUpdateOperation? Operation = null);
public sealed class OsUpdateAgent : BackgroundService
{
    private readonly IOsUpdater updater; private readonly CloudEnrollmentClient enrollment; private readonly MqttConnection mqtt;
    private readonly Func<CloudConnection, HttpClient> cloudClient;
    private readonly string path; private readonly object gate = new();
    private readonly Channel<OsUpdateCommand> queue = Channel.CreateBounded<OsUpdateCommand>(4);
    private OsAgentJournal journal;
    private OsUpdateSnapshot current = new(DateTimeOffset.MinValue, false, false, OsUpdateRules.Runtime, null, null, null, null, "Updater not connected");
    public OsUpdateAgent(IOsUpdater updater, CloudEnrollmentClient enrollment, MqttConnection mqtt, string directory, Func<CloudConnection, HttpClient>? cloudClient = null)
    {
        this.cloudClient = cloudClient ?? (connection => new HttpClient(CloudEnrollmentClient.CreateHandler(connection.ServerCaPem ?? connection.CaPem, allowSystemTrust: connection.ServerCaPem is null)) { Timeout = TimeSpan.FromMinutes(8) });
        this.updater = updater; this.enrollment = enrollment; this.mqtt = mqtt; path = Path.Combine(StateFiles.PrepareDirectory(directory), "os-update-agent.json");
        journal = File.Exists(path) ? JsonSerializer.Deserialize<OsAgentJournal>(File.ReadAllText(path), StateFiles.Json) ?? new([]) : new([]);
        if (journal.Operation?.State == "Downloading") Save(journal with { Operation = journal.Operation with { State = "Failed", Message = "Download interrupted; request the update again", ChangedAt = DateTimeOffset.UtcNow } });
    }
    public OsUpdateSnapshot Current { get { lock (gate) return current; } }
    private void Save(OsAgentJournal next) { StateFiles.WriteAtomic(path, JsonSerializer.Serialize(next, StateFiles.Json)); journal = next; }
    public bool Enqueue(OsUpdateCommand command) => OsUpdateRules.Valid(command, mqtt.Current.TenantId, mqtt.Current.DeviceId, DateTimeOffset.UtcNow) && queue.Writer.TryWrite(command);
    public async Task Refresh(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var snapshot = await updater.Status(timeout.Token);
            lock (gate) current = journal.Operation is { } op && snapshot.Operation?.Id != op.Id && (snapshot.Operation is null || snapshot.Operation.ChangedAt < op.ChangedAt)
                ? snapshot with { Operation = op } : snapshot;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or HttpRequestException or InvalidOperationException or OperationCanceledException)
        { lock (gate) current = current with { Available = false, CloudEnabled = false, Status = "Updater unavailable; cloud updates cannot be started" }; }
    }
    public async Task Process(OsUpdateCommand command, CancellationToken ct = default)
    {
        var profile = mqtt.Current; var now = DateTimeOffset.UtcNow;
        if (!OsUpdateRules.Valid(command, profile.TenantId, profile.DeviceId, now)) return;
        lock (gate)
        {
            if (journal.Received.Any(r => r.Id == command.Id)) return;
            var received = journal.Received.Where(r => r.ExpiresAt > now).ToList(); if (received.Count >= 128) return;
            received.Add(new(command.Id, command.ExpiresAt));
            Save(new(received, new(command.Id, command.Digest, "", "Downloading", "Fetching signed release from the registered cloud", now, "cloud")));
            current = current with { ObservedAt = now, Operation = journal.Operation };
        }
        try
        {
            var snapshot = await updater.Status(ct);
            if (!snapshot.CloudEnabled || !snapshot.Available || OsUpdateRules.Busy(snapshot.Operation?.State)) throw new InvalidOperationException("Cloud updates are off or the updater is busy.");
            var connection = enrollment.Connection ?? throw new InvalidOperationException("No cloud registration.");
            if (connection.TenantId != command.TenantId || connection.DeviceId != command.DeviceId || !Uri.TryCreate(connection.CloudUrl, UriKind.Absolute, out var origin) || origin.Scheme != "https" || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0) throw new InvalidOperationException("Invalid registered cloud origin.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(8));
            using var http = cloudClient(connection);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, $"api/os-updates/download/{command.Id:D}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", command.DownloadToken);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > OsUpdateRules.MaximumPackageBytes) throw new InvalidDataException("Package is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var release = await updater.Stage(stream, timeout.Token);
            if (release.Digest != command.Digest) throw new InvalidDataException("Downloaded release does not match the approved digest.");
            if (!OsUpdateRules.Valid(command, profile.TenantId, profile.DeviceId, DateTimeOffset.UtcNow)) throw new InvalidOperationException("The update approval expired during download.");
            var operation = await updater.Install(new(command.Id, command.Digest, "cloud", command.RequestedBy), timeout.Token);
            lock (gate) Save(journal with { Operation = operation });
        }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or OperationCanceledException or System.Security.Cryptography.CryptographicException or JsonException)
        {
            lock (gate) Save(journal with { Operation = new(command.Id, command.Digest, "", "Failed", "Cloud update unavailable or rejected; check local permission, signer trust, package and updater status", DateTimeOffset.UtcNow, "cloud") });
        }
        await Refresh(ct);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Refresh(stoppingToken);
            if (queue.Reader.TryRead(out var command)) await Process(command, stoppingToken);
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
