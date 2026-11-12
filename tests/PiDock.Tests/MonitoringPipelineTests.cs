using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using PiDock.Agent;
using PiDock.Cloud.Web;
using PiDock.Core;
using PiDock.Web.Shared;
namespace PiDock.Tests;

public sealed class MonitoringPipelineTests : IDisposable
{
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-pipeline-" + Guid.NewGuid());
    private readonly ManualClock clock = new();
    private string Local => Path.Combine(root, "local");
    private string Cloud => Path.Combine(root, "cloud");
    private static ClaimsPrincipal User(string tenant) => new(new ClaimsIdentity([new Claim("tenant", tenant)], "test"));
    private HistoryStore Store(bool small = false)
    {
        var store = new HistoryStore(Local, new Capacity(small));
        if (small) store.Apply(new DataPolicy(MinimumReserveBytes: 512L << 20));
        return store;
    }
    private TelemetryEnvelope Frame(Guid? boot = null, long sequence = 1, DateTimeOffset? observed = null, double? cpu = 20)
    {
        var budget = DataPolicyCalculator.Evaluate(new(), new(32L << 30, 18L << 30, 0));
        var sample = new HostSnapshot(observed ?? clock.GetUtcNow(), "test-pi", "Linux ARM64", cpu, 4L << 30, 3L << 30, 45, 100, budget, [], "Test clock");
        return new(1, "alpha", "pi4", Guid.NewGuid(), boot ?? Guid.NewGuid(), sequence, sample);
    }
    [Fact]
    public async Task ManualReconnectInterruptsBackoffWithoutDiscardingQueuedTelemetry()
    {
        var store = Store();
        using var mqtt = new MqttConnection(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Mqtt:Enabled"] = "true", ["Mqtt:Host"] = "localhost", ["Mqtt:TenantId"] = "alpha", ["Mqtt:DeviceId"] = "pi4"
        }).Build());
        var frame = Frame(); store.Enqueue(mqtt.RouteScope, frame);
        var frozen = new FrozenDeliveryClock();
        using var worker = new TelemetryDeliveryService(store, mqtt, new AgentSession(), new DeliveryStatus(), frozen,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TelemetryDeliveryService>.Instance);
        await worker.StartAsync(default);
        try {
            await frozen.Delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            worker.RetryConnection();
            // Backoff never advances on this clock. A second attempt requires the manual wake-up.
            await frozen.Delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(frame.MessageId, Assert.Single(store.PendingBatch(mqtt.RouteScope)).Envelope.MessageId);
            Assert.Equal("alpha", mqtt.Current.TenantId); Assert.Equal("pi4", mqtt.Current.DeviceId);
        }
        finally { await worker.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); }
    }
    private sealed class FrozenDeliveryClock : TimeProvider
    {
        public readonly global::System.Threading.Channels.Channel<bool> Delays = global::System.Threading.Channels.Channel.CreateUnbounded<bool>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) { Delays.Writer.TryWrite(true); return new FrozenTimer(); }
        private sealed class FrozenTimer : ITimer { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    [Fact]
    public async Task BrokerAcceptanceWithoutCloudReceiptDoesNotClearQueueAndSurvivesRestart()
    {
        var store = Store(); var frame = Frame(); Assert.True(store.Enqueue("server-a", frame));
        var publisher = new Publisher(_ => Task.CompletedTask);
        Assert.Equal(1, await new TelemetryDeliveryPump(store, clock).DeliverAsync("server-a", frame.BootId, publisher));
        Assert.Equal(1, new HistoryStore(Local, new Capacity()).Status().PendingSegments);
    }
    [Fact]
    public async Task OutageThenReconnectDeliversAndRetainsOneHistoricalRecord()
    {
        var store = Store(); var frame = Frame(); store.Enqueue("server-a", frame);
        var pump = new TelemetryDeliveryPump(store, clock);
        await Assert.ThrowsAsync<IOException>(() => pump.DeliverAsync("server-a", frame.BootId, new Publisher(_ => throw new IOException("offline"))));
        Assert.Equal(1, store.Status().PendingSegments);
        var cloud = new FleetStore(new StateDirectory(Cloud));
        await pump.DeliverAsync("server-a", frame.BootId, new Publisher(e =>
        {
            Assert.True(cloud.Accept(IdentityRules.Topic(e.TenantId, e.DeviceId), e).Stored);
            Assert.True(store.Acknowledge("server-a", e.MessageId)); return Task.CompletedTask;
        }));
        Assert.Equal(0, store.Status().PendingSegments);
        Assert.Single(cloud.HistoryFor(User("alpha"), "pi4"));
        Assert.Single(new FleetStore(new StateDirectory(Cloud)).HistoryFor(User("alpha"), "pi4"));
    }
    [Fact]
    public async Task LostReceiptRetriesAfterDelayWithoutDuplicatingCloudHistory()
    {
        var store = Store(); var frame = Frame(); store.Enqueue("server-a", frame);
        var cloud = new FleetStore(new StateDirectory(Cloud)); var pump = new TelemetryDeliveryPump(store, clock); int attempts = 0;
        var publisher = new Publisher(e =>
        {
            Assert.True(cloud.Accept(IdentityRules.Topic("alpha", "pi4"), e).Stored);
            if (++attempts > 1) store.Acknowledge("server-a", e.MessageId);
            return Task.CompletedTask;
        });
        await pump.DeliverAsync("server-a", frame.BootId, publisher);
        Assert.Equal(0, await pump.DeliverAsync("server-a", frame.BootId, publisher));
        clock.Advance(TimeSpan.FromSeconds(31)); await pump.DeliverAsync("server-a", frame.BootId, publisher);
        Assert.Equal(2, attempts); Assert.Equal(0, store.Status().PendingSegments);
        Assert.Single(cloud.HistoryFor(User("alpha"), "pi4"));
    }
    [Fact]
    public async Task LiveSampleIsPrioritizedAndHistoricalReplayCannotReplaceIt()
    {
        var store = Store(); var old = Frame(observed: clock.GetUtcNow().AddHours(-1)); var live = Frame();
        store.Enqueue("server-a", old); store.Enqueue("server-a", live);
        var cloud = new FleetStore(new StateDirectory(Cloud)); var sent = new List<TelemetryEnvelope>();
        await new TelemetryDeliveryPump(store, clock).DeliverAsync("server-a", live.BootId, new Publisher(e =>
        {
            sent.Add(e); Assert.True(cloud.Accept(IdentityRules.Topic("alpha", "pi4"), e).Stored);
            store.Acknowledge("server-a", e.MessageId); return Task.CompletedTask;
        }));
        Assert.Equal(live.MessageId, sent[0].MessageId); Assert.False(sent[0].IsReplay); Assert.True(sent[1].IsReplay);
        Assert.Equal(live.MessageId, cloud.For(User("alpha")).Single().LastMessageId);
        Assert.Equal(2, cloud.HistoryFor(User("alpha"), "pi4").Count);
    }
    [Fact]
    public void HistoricalAlarmCannotReopenAResolvedLiveAlarm()
    {
        var cloud = new FleetStore(new StateDirectory(Cloud)); var live = Frame(sequence: 2) with { Alarms = [] };
        var historical = Frame(live.BootId, 1, clock.GetUtcNow().AddHours(-1)) with
        {
            IsReplay = true,
            Alarms = [new ResourceAlarm(Guid.NewGuid(), "cpu", "High CPU", "Warning", clock.GetUtcNow().AddHours(-1), 95)]
        };
        Assert.True(cloud.Accept(IdentityRules.Topic("alpha", "pi4"), live).Stored);
        Assert.True(cloud.Accept(IdentityRules.Topic("alpha", "pi4"), historical).Stored);
        Assert.Empty(cloud.For(User("alpha")).Single().Alarms!);
        Assert.Single(cloud.HistoryFor(User("alpha"), "pi4").Single(e => e.IsReplay).Alarms!);
    }
    [Fact]
    public void DestinationSettingsAreFrozenAndChangedEndpointCannotReadOldQueue()
    {
        var settings = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Mqtt:Host"] = "server-a", ["Mqtt:TenantId"] = "alpha", ["Mqtt:DeviceId"] = "pi4" }).Build();
        using var first = new MqttConnection(settings); var scope = first.RouteScope;
        var store = Store(); store.Enqueue(scope, Frame()); settings["Mqtt:Host"] = "server-b";
        Assert.Equal(scope, first.RouteScope);
        using var changed = new MqttConnection(settings); Assert.NotEqual(scope, changed.RouteScope);
        Assert.Empty(store.PendingBatch(changed.RouteScope)); Assert.Single(store.PendingBatch(scope));
    }
    [Fact]
    public void HistoricalOnlyDeviceIsNotMarkedAsLive()
    {
        var cloud = new FleetStore(new StateDirectory(Cloud)); var old = Frame(observed: clock.GetUtcNow().AddDays(-1)) with { IsReplay = true };
        Assert.True(cloud.Accept(IdentityRules.Topic("alpha", "pi4"), old).Stored);
        Assert.False(cloud.For(User("alpha")).Single().HasLiveContact);
        Assert.Empty(cloud.HistoryFor(User("other"), "pi4"));
    }
    [Fact]
    public void ReceiptCannotClearAnotherDestinationAndPayloadReuseIsRejected()
    {
        var store = Store(); var frame = Frame(); store.Enqueue("server-a", frame);
        Assert.False(store.Acknowledge("server-b", frame.MessageId)); Assert.Empty(store.PendingBatch("server-b"));
        var cloud = new FleetStore(new StateDirectory(Cloud)); Assert.True(cloud.Accept(IdentityRules.Topic("alpha", "pi4"), frame).Stored);
        Assert.False(cloud.Accept(IdentityRules.Topic("alpha", "pi4"), frame with { Sequence = 999 }).Stored);
    }
    [Fact]
    public void PurgedUnsentDataIsCountedAcrossRestartAndCannotBeAcknowledgedLater()
    {
        var store = Store(small: true); var first = Frame(); Assert.True(store.Enqueue("server-a", first));
        for (int i = 0; i < 20; i++) Assert.True(store.Enqueue("server-a", Frame()));
        Assert.True(store.Status().UnsentDropped > 0);
        var reopened = new HistoryStore(Local, new Capacity(true));
        Assert.Equal(store.Status().UnsentDropped, reopened.Status().UnsentDropped);
        Assert.False(reopened.Acknowledge("server-a", first.MessageId));
    }
    [Fact]
    public void PersistenceFailureCanBeRetriedWithoutPrematureAcknowledgement()
    {
        Directory.CreateDirectory(Cloud); var cloud = new FleetStore(new StateDirectory(Cloud)); var frame = Frame();
        Directory.CreateDirectory(Path.Combine(Cloud, "fleet.json"));
        Assert.ThrowsAny<IOException>(() => cloud.Accept(IdentityRules.Topic("alpha", "pi4"), frame));
        Assert.Empty(cloud.For(User("alpha")));
        Directory.Delete(Path.Combine(Cloud, "fleet.json"));
        Assert.True(cloud.Accept(IdentityRules.Topic("alpha", "pi4"), frame).Stored);
        Assert.Single(cloud.HistoryFor(User("alpha"), "pi4"));
    }
    [Fact]
    public void AlarmUsesMonotonicDurationAndHysteresisInsteadOfWallClock()
    {
        var rule = new AlarmRule("cpu", "CPU", AlarmMetric.CpuPercent, 90, 80, 300, 30);
        var engine = new ResourceAlarmEngine(clock, [rule]); var hot = Frame(cpu: 95).Snapshot;
        Assert.Empty(engine.Observe(hot)); clock.ShiftWall(TimeSpan.FromDays(3)); Assert.Empty(engine.Observe(hot));
        clock.Advance(TimeSpan.FromSeconds(300)); Assert.Single(engine.Observe(hot)); Assert.Single(engine.Active);
        Assert.Empty(engine.Observe(hot));
        Assert.Empty(engine.Observe(hot with { CpuPercent = 85 })); Assert.Single(engine.Active);
        Assert.Empty(engine.Observe(hot with { CpuPercent = 70 })); clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("Recovered", engine.Observe(hot with { CpuPercent = 70 }).Single().State); Assert.Empty(engine.Active);
    }
    [Fact]
    public void MissingSensorDoesNotResolveActiveAlarmAndPendingHoldMustRestart()
    {
        var engine = new ResourceAlarmEngine(clock, [new("cpu", "CPU", AlarmMetric.CpuPercent, 90, 80, 10, 0)]);
        var sample = Frame(cpu: 95).Snapshot; engine.Observe(sample); clock.Advance(TimeSpan.FromSeconds(9));
        engine.Observe(sample with { CpuPercent = null }); clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Empty(engine.Observe(sample)); clock.Advance(TimeSpan.FromSeconds(10)); Assert.Single(engine.Observe(sample));
        Assert.Empty(engine.Observe(sample with { CpuPercent = null })); Assert.Single(engine.Active);
    }
    [Fact]
    public void ActiveAlarmSurvivesRestartAndRecoversWithSameIdentity()
    {
        var store = Store(); var rules = new[] { new AlarmRule("cpu", "CPU", AlarmMetric.CpuPercent, 90, 80, 0, 0) };
        var monitor = new AlarmMonitor(Local, store, clock, rules); var sample = Frame(cpu: 95).Snapshot;
        var opened = monitor.Observe(sample).Single();
        var restarted = new AlarmMonitor(Local, store, clock, rules); Assert.Equal(opened.Alarm.Id, restarted.Active.Single().Id);
        var recovered = restarted.Observe(sample with { CpuPercent = 60 }).Single();
        Assert.Equal(opened.Alarm.Id, recovered.Alarm.Id); Assert.Equal("Recovered", recovered.State);
    }
    [Fact]
    public void AlarmSnapshotWriteFailureIsRetriedWithoutLosingTheTransition()
    {
        var store = Store(); var monitor = new AlarmMonitor(Local, store, clock, [new("cpu", "CPU", AlarmMetric.CpuPercent, 90, 80, 0)]);
        Directory.CreateDirectory(Path.Combine(Local, "alarm-state.json")); var sample = Frame(cpu: 95).Snapshot;
        Assert.ThrowsAny<IOException>(() => monitor.Observe(sample)); Directory.Delete(Path.Combine(Local, "alarm-state.json"));
        Assert.Single(monitor.Observe(sample)); Assert.Single(new AlarmMonitor(Local, store, clock, [new("cpu", "CPU", AlarmMetric.CpuPercent, 90, 80, 0)]).Active);
    }
    [Fact]
    public void LocalHistoryCombinesStandalonePendingAndAcknowledgedSamplesAcrossRestart()
    {
        var store = Store(); var now = clock.GetUtcNow();
        var offline = Frame(observed: now.AddMinutes(-3));
        var acknowledged = Frame(observed: now.AddMinutes(-2));
        var pending = Frame(observed: now.AddMinutes(-1));
        var outside = Frame(observed: now.AddHours(-2));
        Assert.True(store.Append(HistoryClass.Telemetry, offline));
        Assert.True(store.Enqueue("old-server", acknowledged));
        Assert.True(store.Acknowledge("old-server", acknowledged.MessageId));
        Assert.True(store.Enqueue("new-server", pending));
        Assert.True(store.Append(HistoryClass.Telemetry, outside));
        // Legacy non-envelope records and malformed segments do not prevent browsing valid history.
        Assert.True(store.Append(HistoryClass.Telemetry, new { message = "legacy" }));
        StateFiles.WriteAtomic(Path.Combine(Local, "history", "Telemetry", "malformed.json"), "{");
        var reopened = new HistoryStore(Local, new Capacity());
        var range = reopened.Range(now.AddHours(-1), now);
        Assert.Equal(new[] { offline.MessageId, acknowledged.MessageId, pending.MessageId }, range.Select(f => f.MessageId));
        Assert.Single(reopened.PendingBatch("new-server"));
        Assert.Throws<ArgumentException>(() => reopened.Range(now, now));
        Assert.Throws<ArgumentException>(() => reopened.Range(now.AddDays(-32), now));
    }
    [Fact]
    public async Task FullLocalMeasurementsSurviveCloudFilteringDeliveryAndRestart()
    {
        var store = Store(); var original = Frame();
        var filtered = new TelemetryChannels(false, false, false, false, false, false, false).Filter(original);
        Assert.True(store.Enqueue("server", filtered, original.Snapshot));
        await new TelemetryDeliveryPump(store, clock).DeliverAsync("server", original.BootId, new Publisher(sent =>
        {
            Assert.Null(sent.Snapshot.CpuPercent);
            Assert.Null(sent.Snapshot.TemperatureC);
            Assert.Null(DeviceHistory.Value(sent.Snapshot, "storage"));
            Assert.True(store.Acknowledge("server", sent.MessageId));
            return Task.CompletedTask;
        }));
        var reopened = new HistoryStore(Local, new Capacity());
        var local = Assert.Single(reopened.Range(clock.GetUtcNow().AddHours(-1), clock.GetUtcNow()));
        Assert.Equal(original.Snapshot.CpuPercent, local.Snapshot.CpuPercent);
        Assert.Equal(original.Snapshot.TemperatureC, local.Snapshot.TemperatureC);
        Assert.Equal(original.Snapshot.Storage.TotalBytes, local.Snapshot.Storage.TotalBytes);
        Assert.Null(local.Snapshot.Channels);
        Assert.Equal(0, reopened.Status().PendingSegments);
        Assert.Single(Directory.GetFiles(Path.Combine(Local, "history", "Telemetry")));
    }
    [Fact]
    public void LocalBrowsingCannotRecoverPurgedSamples()
    {
        var store = Store(small: true); var first = Frame();
        Assert.True(store.Enqueue("server", first, first.Snapshot));
        for (int i = 0; i < 20; i++) { var frame = Frame(); Assert.True(store.Enqueue("server", frame, frame.Snapshot)); }
        var range = new HistoryStore(Local, new Capacity(true)).Range(clock.GetUtcNow().AddHours(-1), clock.GetUtcNow());
        Assert.NotEmpty(range);
        Assert.DoesNotContain(range, f => f.MessageId == first.MessageId);
        Assert.True(store.Status().DeletedSegments > 0);
    }
    private sealed class Publisher(Func<TelemetryEnvelope, Task> send) : ITelemetryPublisher
    { public Task PublishAsync(TelemetryEnvelope envelope, CancellationToken ct) => send(envelope); }
    private sealed class Capacity(bool small = false) : ICapacityProbe
    { public StorageCapacity Read(string path, long bytes) => small ? new((512L << 20) + 10000, (512L << 20) + 10000 - bytes, bytes) : new(32L << 30, (18L << 30) - bytes, bytes); }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset utc = DateTimeOffset.UtcNow; private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan duration) { timestamp += duration.Ticks; utc += duration; }
        public void ShiftWall(TimeSpan duration) => utc += duration;
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
