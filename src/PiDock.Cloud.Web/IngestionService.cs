using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Protocol;
using PiDock.Agent;
using PiDock.Core;
namespace PiDock.Cloud.Web;

public sealed class IngestionService(MqttConnection mqtt, FleetStore fleet, ILogger<IngestionService> logger, ApplicationCommandStore? commands = null) : BackgroundService
{
    public string Status { get; private set; } = "Not configured";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!mqtt.Enabled) return;
        using var client = new MqttClientFactory().CreateMqttClient();
        var receipts = Channel.CreateBounded<TelemetryReceipt>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        int reconnect = 0;
        client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                if (e.ApplicationMessage.Payload.Length is > 0 and <= 65536)
                {
                    var message = JsonSerializer.Deserialize<TelemetryEnvelope>(e.ApplicationMessage.Payload.ToArray(), StateFiles.Json);
                    if (message is not null && fleet.Accept(e.ApplicationMessage.Topic, message).Stored)
                    {
                        commands?.AcceptResults(message);
                        await receipts.Writer.WriteAsync(new(1, message.TenantId, message.DeviceId, message.MessageId, commands?.ForReceipt(message)), stoppingToken);
                    }
                    else logger.LogDebug("Rejected invalid telemetry message");
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException) { logger.LogWarning("Rejected malformed telemetry payload"); }
            catch (IOException ex) { e.ProcessingFailed = true; Interlocked.Exchange(ref reconnect, 1); logger.LogWarning(ex, "Telemetry persistence unavailable; no application receipt sent"); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { e.ProcessingFailed = true; }
        };
        int failures = 0; TelemetryReceipt? pendingReceipt = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Interlocked.Exchange(ref reconnect, 0) == 1 && client.IsConnected)
                {
                    using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); disconnect.CancelAfter(TimeSpan.FromSeconds(5));
                    await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), disconnect.Token);
                }
                if (!client.IsConnected)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await client.ConnectAsync(mqtt.Options("pidock-cloud-ingestion"), timeout.Token);
                    await client.SubscribeAsync(new MqttClientFactory().CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic("pidock/v1/tenants/+/devices/+/telemetry").WithAtLeastOnceQoS()).Build(), timeout.Token);
                }
                failures = 0; Status = "Connected · durable telemetry receipts";
                // Publish outside the receive callback to avoid QoS callback deadlocks.
                for (int count = 0; count < 64; count++)
                {
                    if (pendingReceipt is null && !receipts.Reader.TryRead(out pendingReceipt)) break;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
                        .WithTopic(IdentityRules.ReceiptTopic(pendingReceipt!.TenantId, pendingReceipt.DeviceId))
                        .WithPayload(JsonSerializer.Serialize(pendingReceipt, StateFiles.Json)).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), timeout.Token);
                    if (!result.IsSuccess) throw new InvalidOperationException("Broker denied telemetry receipt.");
                    pendingReceipt = null;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException or MQTTnet.Exceptions.MqttCommunicationException or OperationCanceledException)
            {
                Status = "Disconnected · retrying"; logger.LogWarning(ex, "Cloud MQTT ingestion unavailable");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(++failures, 6))) + Random.Shared.NextDouble()), stoppingToken);
            }
        }
    }
}
