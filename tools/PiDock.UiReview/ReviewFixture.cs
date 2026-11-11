using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PiDock.Agent;
using PiDock.Cloud.Web;
using PiDock.Core;
namespace PiDock.UiReview;

public sealed record ReviewSurface(string Name);
public sealed class ReviewIdentity { public ClaimsPrincipal User { get; set; } = new(); }
public sealed class ReviewAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ReviewIdentity identity)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(identity.User, Scheme.Name)));
}
public sealed class ReviewCapacity : ICapacityProbe
{
    public StorageCapacity Read(string path, long bytes) => new(32L << 30, (18L << 30) - bytes, bytes);
}
public static class ReviewSamples
{
    public static void Seed(string directory)
    {
        var now = DateTimeOffset.UtcNow; var archive = new TelemetryArchive(directory); var records = new List<StoredDevice>();
        foreach (var item in new[] { ("sample-pi4-workbench", now, 18.5, 20L << 30), ("sample-pi3-greenhouse", now.AddSeconds(-110), 72.4, 3L << 30), ("sample-pi4-lab", now.AddHours(-2), 4.2, 14L << 30) })
        {
            var budget = DataPolicyCalculator.Evaluate(new(), new(32L << 30, item.Item4, 0));
            var snapshot = new HostSnapshot(item.Item2, item.Item1, "Linux ARM64 · sample", item.Item3, 4L << 30, 2L << 30, 48.2, 86400, budget, [], "Synchronized · sample");
            var envelope = new TelemetryEnvelope(1, "review", item.Item1, Guid.NewGuid(), Guid.NewGuid(), 1, snapshot);
            var apps = new RemoteApplicationSnapshot(now, false, "Managed containers reported · sample", [new(new string('a',64),["/sensor-api"],"trusted/sensor-api@sha256:"+new string('1',64),"running","Up 6 hours"),new(new string('b',64),["/local-logger"],"trusted/logger@sha256:"+new string('2',64),"exited","Exited (0)")],[]);
            for(int index=120;index>=0;index--) {
                var time=now.AddSeconds(-index*30);var historical=snapshot with { ObservedAt=time, CpuPercent=18+12*Math.Sin(index*.13)+3*Math.Cos(index*.6), TemperatureC=44+5*Math.Sin(index*.06), MemoryAvailableBytes=(long)((2+.3*Math.Sin(index*.05))*(1L<<30)), UptimeSeconds=86400-index*30 };
                archive.Store(new(1,"review",item.Item1,Guid.NewGuid(),envelope.BootId,121-index,historical,Applications:apps));
            }
            records.Add(new StoredDevice("review", item.Item1, envelope.MessageId, item.Item2, snapshot, envelope.BootId, 1, Applications:apps));
        }
        var invitations = records.Select(d => new DeviceInvitation(Guid.NewGuid(), "review", d.DeviceId, d.DeviceId, "review-owner", now.AddMinutes(-1), now.AddMinutes(9), new string('0', 64), "Registered")).ToList();
        invitations.Add(new DeviceInvitation(Guid.NewGuid(), "review", "sample-pending", "Pending connection sample", "review-owner", now, now.AddMinutes(10), new string('0', 64)));
        StateFiles.WriteAtomic(Path.Combine(directory, "enrollments.json"), System.Text.Json.JsonSerializer.Serialize(invitations, StateFiles.Json));
        StateFiles.WriteAtomic(Path.Combine(directory, "fleet.json"), System.Text.Json.JsonSerializer.Serialize(records, StateFiles.Json));
    }
}

public sealed class ReviewFleetUpdates(FleetStore fleet, ReviewIdentity identity) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var device = fleet.For(identity.User).First(d => d.DeviceId == "sample-pi4-workbench");
            var sample = device.Snapshot with { ObservedAt = DateTimeOffset.UtcNow, CpuPercent = 18 + 4 * Math.Sin(device.Sequence * .3) };
            fleet.Ingest(IdentityRules.Topic("review", device.DeviceId), new TelemetryEnvelope(1, "review", device.DeviceId, Guid.NewGuid(), device.BootId, device.Sequence + 1, sample, Applications:device.Applications));
        }
    }
}
