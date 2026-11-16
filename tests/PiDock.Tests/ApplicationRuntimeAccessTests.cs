using System.Text.Json;
using PiDock.Agent;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationRuntimeAccessTests
{
    [Fact]
    public void UnknownPausedExpiredAndChangedCoordinatorInvalidateAdmissionsPermanently()
    {
        var clock = new Clock(); var access = new ApplicationRuntimeAccessGate(true, clock); var delivery = Guid.NewGuid(); var coordinator = Guid.NewGuid();
        var active = new ApplicationRuntimeAccessSnapshot(coordinator, false, [new("app", delivery, true, "allowed")]);
        Assert.Throws<UnauthorizedAccessException>(() => access.Require("app", delivery)); access.Apply(active);
        var first = access.Require("app", delivery)!; Assert.True(access.IsCurrent("app", first));
        Assert.Throws<UnauthorizedAccessException>(() => access.Require("other", delivery)); Assert.Throws<UnauthorizedAccessException>(() => access.Require("app", Guid.NewGuid()));
        access.Apply(active with { Paused = true }); Assert.False(access.IsCurrent("app", first));
        access.Apply(active); var resumed = access.Require("app", delivery)!; Assert.NotEqual(first, resumed);
        clock.Advance(3); Assert.False(access.IsCurrent("app", resumed)); access.Apply(active); Assert.NotEqual(resumed, access.Require("app", delivery));
        var beforeRestart = access.Require("app", delivery)!;
        access.Apply(active with { CoordinatorId = Guid.NewGuid() }); Assert.False(access.IsCurrent("app", beforeRestart));
        access.Unavailable(); Assert.Throws<UnauthorizedAccessException>(() => access.Require("app", delivery));
    }
    [Theory]
    [InlineData("pause")]
    [InlineData("stop")]
    [InlineData("failure")]
    [InlineData("restart")]
    public void LifecycleRevocationClearsResourcesWithoutClientCleanupAndOldHandlesCannotRevive(string change)
    {
        var clock = new Clock(); var access = new ApplicationRuntimeAccessGate(true, clock); var delivery = Guid.NewGuid(); var coordinator = Guid.NewGuid();
        var snapshot = new ApplicationRuntimeAccessSnapshot(coordinator, false, [new("app", delivery, true, "allowed")]); access.Apply(snapshot);
        var binding = access.Require("app", delivery)!;
        var ioAuthority = new ApplicationIoAuthority("app", "io", new string('a', 64), Runtime: binding);
        var io = new ApplicationIoService(true, () => [new("app", "sim:bcm17", ApplicationIoDirection.Output)], clock, new Validator(access), access);
        var lease = io.Acquire(ioAuthority, new(["sim:bcm17"])); io.Write(ioAuthority, lease.Id, new(0, [new("sim:bcm17", true)]));
        var runtime = new ApplicationEngineeringAuthority("app", "runtime", 1, clock.GetUtcNow().AddHours(1), "signals.runtime", binding);
        var engineer = runtime with { Actor = "engineer", Scope = "signals.engineer" };
        var service = new ApplicationOverrideService(clock, a => access.IsCurrent(a.AppId, a.Runtime), _ => { }); service.SetMaintenance(false);
        var registration = new ApplicationSignalRegistration(Guid.NewGuid(), "one", [new("out", ApplicationSignalType.Boolean, ApplicationSignalAccess.ReadWrite, Value(false), true)]);
        service.Register(runtime, registration); service.Force(engineer, new(registration.InstanceId, "one", "out", Value(true), 60)); service.SetOnce(engineer, new(registration.InstanceId, "one", "out", Value(true)));
        var nextDelivery = change == "restart" ? Guid.NewGuid() : delivery;
        access.Apply(snapshot with { Paused = change == "pause", Apps = [new("app", nextDelivery, change == "restart", change)] });
        io.Sweep(); service.Tick(); // No request or cleanup from the application.
        Assert.Equal(0, io.Activity("app").Leases); Assert.True(io.Activity("app").OutputsAtFallback);
        Assert.Equal((0, 0, true), service.Activity("app"));
        Assert.Throws<ApplicationIoDeniedException>(() => io.Renew(ioAuthority, lease.Id, new(1)));
        access.Apply(new(coordinator, false, [new("app", nextDelivery, true, "allowed")]));
        var current = access.Require("app", nextDelivery)!;
        Assert.Throws<ApplicationIoDeniedException>(() => io.Read(ioAuthority with { Runtime = current }, lease.Id));
        Assert.Throws<PlatformConflictException>(() => service.Publish(runtime with { Runtime = current }, new(registration.InstanceId, "one", 1, [new("out", Value(false))], [])));
        Assert.Throws<PlatformConflictException>(() => service.Register(runtime with { Runtime = current }, registration));
        service.Register(runtime with { Runtime = current }, registration with { InstanceId = Guid.NewGuid() }); Assert.Equal((0, 0, false), service.Activity("app"));
    }
    private static JsonElement Value(bool value) => JsonSerializer.SerializeToElement(value);
    private sealed class Validator(ApplicationRuntimeAccessGate access) : IApplicationIoAuthorityValidator
    { public bool IsCurrent(ApplicationIoAuthority authority) => access.IsCurrent(authority.AppId, authority.Runtime); }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => 1000;
        public void Advance(int seconds) => ticks += seconds * 1000L;
    }
}
