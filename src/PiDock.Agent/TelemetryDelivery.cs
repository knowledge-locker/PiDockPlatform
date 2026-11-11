using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using PiDock.Core;
namespace PiDock.Agent;

public interface ITelemetryPublisher { Task PublishAsync(TelemetryEnvelope envelope, CancellationToken ct); }
public sealed class TelemetryDeliveryPump(HistoryStore history, TimeProvider clock)
{
    private readonly Dictionary<Guid, long> attempted = [];
    public async Task<int> DeliverAsync(string routeScope, Guid bootId, ITelemetryPublisher publisher, CancellationToken ct = default)
    {
        var records = history.PendingBatch(routeScope).ToList();
        var newest = history.NewestPending(routeScope, bootId);
        bool fresh = newest is not null && clock.GetUtcNow() - newest.Envelope.Snapshot.ObservedAt <= TimeSpan.FromSeconds(60)
            && newest.Envelope.Snapshot.ObservedAt <= clock.GetUtcNow().AddSeconds(30);
        if (fresh) { records.RemoveAll(r => r.Envelope.MessageId == newest!.Envelope.MessageId); records.Insert(0, newest!); }
        var pendingIds = records.Select(r => r.Envelope.MessageId).ToHashSet();
        foreach (var id in attempted.Keys.Where(id => !pendingIds.Contains(id)).ToArray()) attempted.Remove(id);
        int sent = 0;
        foreach (var record in records.Take(16))
        {
            var id = record.Envelope.MessageId;
            if (attempted.TryGetValue(id, out var previous) && clock.GetElapsedTime(previous) < TimeSpan.FromSeconds(30)) continue;
            var envelope = record.Envelope with { IsReplay = !fresh || id != newest!.Envelope.MessageId };
            await publisher.PublishAsync(envelope, ct);
            attempted[id] = clock.GetTimestamp(); sent++;
        }
        return sent;
    }
}

public sealed class MqttTelemetryPublisher : ITelemetryPublisher, IDisposable
{
    private readonly MqttConnection connection;
    private readonly HistoryStore history;
    private readonly string scope;
    private readonly IMqttClient client = new MqttClientFactory().CreateMqttClient();
    private readonly ILogger logger;
    private bool subscribed;
    public MqttTelemetryPublisher(MqttConnection connection, HistoryStore history, ILogger logger)
    {
        this.connection = connection; this.history = history; this.logger = logger; scope = connection.RouteScope;
        client.DisconnectedAsync += _ => { subscribed = false; return Task.CompletedTask; };
        client.ApplicationMessageReceivedAsync += e =>
        {
            try
            {
                if (e.ApplicationMessage.Topic == IdentityRules.ReceiptTopic(connection.TenantId, connection.DeviceId) && e.ApplicationMessage.Payload.Length <= 4096)
                {
                    var receipt = JsonSerializer.Deserialize<TelemetryReceipt>(e.ApplicationMessage.Payload.ToArray(), StateFiles.Json);
                    if (receipt is { SchemaVersion: 1 } && receipt.TenantId == connection.TenantId && receipt.DeviceId == connection.DeviceId)
                        history.Acknowledge(scope, receipt.MessageId);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException) { logger.LogWarning("Could not apply telemetry receipt; sample remains eligible for retry"); }
            return Task.CompletedTask;
        };
    }
    public async Task PublishAsync(TelemetryEnvelope envelope, CancellationToken ct)
    {
        if (!client.IsConnected) await client.ConnectAsync(connection.Options("pidock-device-" + scope[..16]), ct);
        if (!subscribed)
        {
            var result = await client.SubscribeAsync(new MqttClientFactory().CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(IdentityRules.ReceiptTopic(connection.TenantId, connection.DeviceId)).WithAtLeastOnceQoS()).Build(), ct);
            if (result.Items.Any(item => (int)item.ResultCode >= 128)) throw new InvalidOperationException("Broker denied receipt subscription.");
            subscribed = true;
        }
        var message = new MqttApplicationMessageBuilder().WithTopic(IdentityRules.Topic(connection.TenantId, connection.DeviceId))
            .WithPayload(JsonSerializer.Serialize(envelope, StateFiles.Json)).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
        var published = await client.PublishAsync(message, ct);
        if (!published.IsSuccess) throw new InvalidOperationException("Broker rejected telemetry publication.");
        // PUBACK is not delivery: only the cloud's persistence receipt clears the outbox.
    }
    public void Dispose() => client.Dispose();
}

public sealed class TelemetryDeliveryService(HistoryStore history, MqttConnection mqtt, AgentSession session,
    DeliveryStatus status, TimeProvider clock, ILogger<TelemetryDeliveryService> logger) : BackgroundService
{
    private readonly object retryGate = new();
    private CancellationTokenSource retry = new(); private bool stopped;
    public void RetryConnection()
    {
        lock (retryGate) { if (stopped) return; status.Text = "Retrying cloud connection…"; retry.Cancel(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MqttConnection? profile = null; MqttTelemetryPublisher? publisher = null;
        var pump = new TelemetryDeliveryPump(history, clock); int failures = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                CancellationToken retryToken; bool reset;
                lock (retryGate) {
                    reset = retry.IsCancellationRequested;
                    if (reset) { retry.Dispose(); retry = new(); }
                    retryToken = retry.Token;
                }
                if (reset) { publisher?.Dispose(); publisher = null; profile = null; pump = new(history, clock); failures = 0; }
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, retryToken);
                try
                {
                    var current = mqtt.Current;
                    if (!current.Enabled) { await Task.Delay(TimeSpan.FromSeconds(2), clock, cycle.Token); continue; }
                    double delay = 2;
                    try
                    {
                        if (!ReferenceEquals(profile, current))
                        {
                            publisher?.Dispose(); publisher = null;
                            publisher = new MqttTelemetryPublisher(current, history, logger); profile = current;
                            pump = new(history, clock); failures = 0;
                        }
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cycle.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        await pump.DeliverAsync(profile!.RouteScope, session.BootId, publisher!, timeout.Token);
                        failures = 0;
                        status.Text = history.PendingBatch(profile.RouteScope, 1).Count == 0 ? "Delivered · awaiting next sample" : "Connected · waiting for cloud receipts";
                    }
                    catch (OperationCanceledException) when (cycle.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or MQTTnet.Exceptions.MqttCommunicationException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
                    {
                        status.Text = "Disconnected · samples buffered";
                        delay = Math.Min(60, Math.Pow(2, Math.Min(++failures, 6))) + Random.Shared.NextDouble();
                        logger.LogWarning("Telemetry delivery unavailable; retrying with bounded backoff");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(delay), clock, cycle.Token);
                }
                catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
            }
        }
        finally { publisher?.Dispose(); lock (retryGate) { stopped = true; retry.Dispose(); } }
    }
}
