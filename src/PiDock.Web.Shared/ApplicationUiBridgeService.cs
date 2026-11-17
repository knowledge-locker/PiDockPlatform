using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PiDock.Core;

namespace PiDock.Web.Shared;

// This service is invoked only by the authenticated host component, never directly by an app frame.
public sealed class ApplicationUiBridgeService(ApplicationUiRegistry registry, IConfiguration configuration,
    UserStore users, IHttpClientFactory clients, ILogger<ApplicationUiBridgeService> logger, IApplicationCredentialSource? managedCredentials = null)
{
    private static readonly JsonSerializerOptions Json = new(StateFiles.Json) { MaxDepth = 16 };
    private readonly SemaphoreSlim commands = new(4), streams = new(8);
    private readonly object gate = new();
    private readonly Dictionary<string, Cached> cache = [];
    private readonly Dictionary<string, Queue<DateTimeOffset>> rates = [];
    private readonly Dictionary<string, int> subscriptions = [];
    private sealed record Lease(ApplicationUiRegistration Registration, string Token, string Fingerprint);
    private sealed record Cached(string Fingerprint, DateTimeOffset At, Task<ApplicationUiCommandResult> Task);
    private static ApplicationUiCommandResult Error(Guid id, string code) => new(1, id, false, null, code);
    private static ApplicationUiStreamMessage Status(string code) => new("status", code, null);
    private static string Subject(ClaimsPrincipal user, string app, string id) => user.Identity!.Name + ":" + user.FindFirstValue("stamp") + ":" + app + ":" + id;

    private Lease? Authorize(ClaimsPrincipal user, string app, string id)
    {
        if (!user.IsInRole("Owner") || !users.IsCurrent(user)) return null;
        var registration = registry.Find(app, id);
        if (registration?.Bridge is not { } grant) return null;
        var credential = configuration.GetSection("PiDock:ApplicationUi:Credentials:" + grant.CredentialId);
        var managed = managedCredentials?.Resolve(app, grant.CredentialId);
        if (managed?.Managed == true && managed.Credential?.Purpose != "ui-bridge") return null;
        var token = managed?.Managed == true ? managed.Credential?.Token : credential["Token"];
        if (token is not { Length: 64 } || !token.All(char.IsAsciiHexDigit) ||
            !DateTimeOffset.TryParse(managed?.Managed == true ? managed.Credential?.ExpiresAt.ToString("O") : credential["ExpiresAt"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry) || expiry <= DateTimeOffset.UtcNow) return null;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(registration, Json) + token + expiry.ToString("O"))));
        return new(registration, token, fingerprint);
    }
    private bool Current(ClaimsPrincipal user, string app, string id, Lease lease) => Authorize(user, app, id)?.Fingerprint == lease.Fingerprint;
    public ApplicationUiBridgeCapabilities Capabilities(ClaimsPrincipal user, string app, string id)
    {
        var grant = Authorize(user, app, id)?.Registration.Bridge;
        return new(ApplicationUiBridgeRules.Version, grant?.Commands ?? [], grant?.StreamPath is not null);
    }

    public Task<ApplicationUiCommandResult> Command(ClaimsPrincipal user, string app, string id, ApplicationUiCommand command, CancellationToken ct)
    {
        var lease = Authorize(user, app, id);
        if (lease is null) return Task.FromResult(Error(command.CommandId, "not_authorized"));
        if (!users.HasRecentAuthorization(user, DateTimeOffset.UtcNow))
            return Task.FromResult(Error(command.CommandId, "authorization_required"));
        if (command.SchemaVersion != 1 || command.CommandId == Guid.Empty || !lease.Registration.Bridge!.Commands.Contains(command.Name, StringComparer.Ordinal))
            return Task.FromResult(Error(command.CommandId, "command_denied"));
        if (command.Arguments.ValueKind != JsonValueKind.Object) return Task.FromResult(Error(command.CommandId, "invalid_arguments"));
        byte[] body;
        try { body = JsonSerializer.SerializeToUtf8Bytes(command, Json); }
        catch (JsonException) { return Task.FromResult(Error(command.CommandId, "invalid_arguments")); }
        if (body.Length > ApplicationUiBridgeRules.MaximumCommandBytes) return Task.FromResult(Error(command.CommandId, "payload_too_large"));
        var fingerprint = Convert.ToHexString(SHA256.HashData(body)) + lease.Fingerprint;
        var subject = Subject(user, app, id); var key = subject + ":" + command.CommandId;
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var old in cache.Where(e => e.Value.Task.IsCompleted && now - e.Value.At > TimeSpan.FromMinutes(2)).Select(e => e.Key).ToArray()) cache.Remove(old);
            if (cache.TryGetValue(key, out var existing)) return existing.Fingerprint == fingerprint ? existing.Task : Task.FromResult(Error(command.CommandId, "command_id_conflict"));
            if (cache.Count >= 128) return Task.FromResult(Error(command.CommandId, "busy"));
            if (!rates.TryGetValue(subject, out var rate)) { if (rates.Count >= 128) rates.Remove(rates.Keys.First()); rates[subject] = rate = new(); }
            while (rate.TryPeek(out var at) && now - at >= TimeSpan.FromMinutes(1)) rate.Dequeue();
            if (rate.Count >= 60) return Task.FromResult(Error(command.CommandId, "rate_limited"));
            rate.Enqueue(now);
            var task = SendCommand(user, app, id, lease, command.CommandId, command.Name, body, ct);
            cache[key] = new(fingerprint, now, task); return task;
        }
    }
    private async Task<ApplicationUiCommandResult> SendCommand(ClaimsPrincipal user, string app, string id, Lease lease, Guid commandId, string name, byte[] body, CancellationToken ct)
    {
        if (!await commands.WaitAsync(0, ct)) return Error(commandId, "busy");
        ApplicationUiCommandResult result;
        try
        {
            if (ct.IsCancellationRequested || !Current(user, app, id, lease) || !users.HasRecentAuthorization(user, DateTimeOffset.UtcNow))
                return Error(commandId, "authorization_required");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = Request(lease, HttpMethod.Post, lease.Registration.Bridge!.CommandsPath!);
            request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json");
            using var response = await clients.CreateClient("PiDockAppUiBridge").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) result = Error(commandId, "outcome_unknown");
            else if (response.Content.Headers.ContentType?.MediaType != "application/json") result = Error(commandId, "outcome_unknown");
            else
            {
                var bytes = await BoundedBody(response.Content, ApplicationUiBridgeRules.MaximumEventBytes, timeout.Token);
                result = JsonSerializer.Deserialize<ApplicationUiCommandResult>(bytes, Json) ?? Error(commandId, "outcome_unknown");
                if (result.SchemaVersion != 1 || result.CommandId != commandId) result = Error(commandId, "outcome_unknown");
                if (!result.Succeeded) result = Error(commandId, result.ErrorCode == "outcome_unknown" ? "outcome_unknown" : "app_rejected");
            }
            if (!Current(user, app, id, lease)) result = Error(commandId, "outcome_unknown");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException) { result = Error(commandId, "outcome_unknown"); }
        finally { commands.Release(); }
        logger.LogInformation("Operator {Operator} app UI command {AppId}/{ContributionId} {Command} {CommandId}: {Outcome}", user.Identity?.Name, app, id, name, commandId, result.Succeeded ? "succeeded" : result.ErrorCode);
        return result;
    }
    private static HttpRequestMessage Request(Lease lease, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(lease.Registration.ServiceOrigin), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.Token); return request;
    }
    private static async Task<byte[]> BoundedBody(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum || content.Headers.ContentEncoding.Count != 0) throw new IOException("Invalid bridge response.");
        using var stream = await content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var bytes = new byte[4096];
        while (true) { int n = await stream.ReadAsync(bytes, ct); if (n == 0) return output.ToArray(); if (output.Length + n > maximum) throw new IOException("Bridge response limit."); await output.WriteAsync(bytes.AsMemory(0, n), ct); }
    }

    public async IAsyncEnumerable<ApplicationUiStreamMessage> Stream(ClaimsPrincipal user, string app, string id, ApplicationUiCursor? cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        var lease = Authorize(user, app, id);
        if (lease?.Registration.Bridge?.StreamPath is null || !ApplicationUiBridgeRules.Cursor(cursor)) { yield return Status("not_authorized"); yield break; }
        if (!await streams.WaitAsync(0, ct)) { yield return Status("busy"); yield break; }
        var subject = Subject(user, app, id); bool admitted;
        lock (gate) { subscriptions.TryGetValue(subject, out var count); admitted = count < 2; if (admitted) subscriptions[subject] = count + 1; }
        if (!admitted) { streams.Release(); yield return Status("busy"); yield break; }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        var monitor = Monitor();
        async Task Monitor()
        {
            try
            {
                var lifetime = System.Diagnostics.Stopwatch.StartNew();
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(500, stop.Token);
                    if (!Current(user, app, id, lease) || lifetime.Elapsed >= TimeSpan.FromMinutes(2)) { stop.Cancel(); return; }
                }
            }
            catch (OperationCanceledException) { }
        }
        try
        {
            using var request = Request(lease, HttpMethod.Get, lease.Registration.Bridge.StreamPath);
            request.Headers.Accept.Add(new("application/x-ndjson"));
            if (cursor is not null) request.Headers.TryAddWithoutValidation("Last-Event-ID", cursor.Epoch + ":" + cursor.Sequence.ToString(CultureInfo.InvariantCulture));
            HttpResponseMessage? response = null; string? error = null;
            try { response = await clients.CreateClient("PiDockAppUiBridge").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, readTimeout.Token); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { error = "disconnected"; }
            using (response)
            {
                if (response is null || !response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/x-ndjson" || response.Content.Headers.ContentEncoding.Count != 0)
                { if (!ct.IsCancellationRequested) yield return Status(Current(user, app, id, lease) ? error ?? "unavailable" : "revoked"); yield break; }
                Stream? sourceStream = null;
                try { sourceStream = await response.Content.ReadAsStreamAsync(readTimeout.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { error = "disconnected"; }
                if (sourceStream is null) { if (!ct.IsCancellationRequested) yield return Status(Current(user, app, id, lease) ? error! : "revoked"); yield break; }
                await using var source = sourceStream;
                await using var reader = Lines(source, readTimeout.Token).GetAsyncEnumerator(readTimeout.Token);
                var order = new ApplicationUiEventOrder(cursor); bool first = true;
                var rateWindow = System.Diagnostics.Stopwatch.StartNew(); int eventCount = 0;
                while (!ct.IsCancellationRequested)
                {
                    ApplicationUiEvent? item = null; bool hasNext = false; error = null;
                    try { hasNext = await reader.MoveNextAsync(); if (hasNext) item = JsonSerializer.Deserialize<ApplicationUiEvent>(reader.Current, Json); }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException) { error = ex is JsonException or IOException ? "protocol_error" : "stale"; }
                    if (!Current(user, app, id, lease)) { yield return Status("revoked"); yield break; }
                    if (error is not null || !hasNext || item is null) { if (!ct.IsCancellationRequested) yield return Status(error ?? "disconnected"); yield break; }
                    if (rateWindow.Elapsed >= TimeSpan.FromSeconds(1)) { rateWindow.Restart(); eventCount = 0; }
                    if (++eventCount > 20) { yield return Status("rate_limited"); yield break; }
                    if (!order.Accept(item)) { yield return Status("gap"); yield break; }
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    if (first) { yield return Status("connected"); first = false; }
                    yield return new("event", null, item);
                }
            }
        }
        finally
        {
            stop.Cancel(); await monitor;
            lock (gate) { if (--subscriptions[subject] == 0) subscriptions.Remove(subject); }
            streams.Release();
        }
    }
    private static async IAsyncEnumerable<byte[]> Lines(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var chunk = new byte[4096]; using var line = new MemoryStream();
        while (true)
        {
            int count = await stream.ReadAsync(chunk, ct);
            if (count == 0) { if (line.Length > 0) throw new IOException("Truncated event."); yield break; }
            for (int i = 0; i < count; i++)
            {
                if (chunk[i] == (byte)'\n') { if (line.Length > 0) { yield return line.ToArray(); line.SetLength(0); } }
                else { if (line.Length >= ApplicationUiBridgeRules.MaximumEventBytes) throw new IOException("Event limit."); line.WriteByte(chunk[i]); }
            }
        }
    }
}
