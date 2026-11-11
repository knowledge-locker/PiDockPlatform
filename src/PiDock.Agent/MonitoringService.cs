using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiDock.Core;
namespace PiDock.Agent;

public sealed class MonitoringService(HostMonitor monitor, HistoryStore history, MqttConnection mqtt,
    AlarmMonitor alarms, AgentSession session, DeliveryStatus delivery, ILogger<MonitoringService> logger) : BackgroundService
{
    private HostSnapshot? current;
    public HostSnapshot? Current => Volatile.Read(ref current);
    public string TransportStatus => delivery.Text;
    public string? LastError { get; private set; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        long sequence = 0; int tick = 0;
        do
        {
            try
            {
                var sample = monitor.Read(); Volatile.Write(ref current, sample); LastError = null;
                var transitions = alarms.Observe(sample);
                if (tick++ % 15 == 0 || transitions.Count > 0)
                {
                    var profile = mqtt.Current;
                    var envelope = new TelemetryEnvelope(1, profile.TenantId, profile.DeviceId, Guid.NewGuid(), session.BootId, ++sequence, sample, Alarms: alarms.Active);
                    bool stored = profile.Enabled ? history.Enqueue(profile.RouteScope, envelope) : history.Append(HistoryClass.Telemetry, envelope);
                    if (!stored) logger.LogWarning("Telemetry sample dropped by storage policy");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            { LastError = "Local monitoring persistence unavailable. Check service logs."; logger.LogWarning(ex, "Local monitoring operation failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
