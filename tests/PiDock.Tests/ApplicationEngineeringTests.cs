using System.Text.Json;
using PiDock.Agent;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationEngineeringTests
{
    private static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);
    [Fact]
    public void ForcesPreserveNormalValuesAndReleaseResumesLatestProducer()
    {
        var f = new Fixture(); var initial = f.Register();
        f.Service.Force(f.Engineer, new(initial.InstanceId, initial.Revision, "run", Value(true), 60));
        var forced = f.Publish(false);
        var row = forced.Signals.Single(s => s.Declaration.Id == "run");
        Assert.False(row.NormalValue.GetBoolean()); Assert.True(row.EffectiveValue.GetBoolean()); Assert.NotNull(row.Force);
        f.Publish(true);
        var released = f.Service.Release(f.Engineer, new(initial.InstanceId, initial.Revision));
        row = released.Signals.Single(s => s.Declaration.Id == "run");
        Assert.True(row.NormalValue.GetBoolean()); Assert.True(row.EffectiveValue.GetBoolean()); Assert.Null(row.Force);
        Assert.Contains(f.Audit, e => e.Action == "force-set" && e.Actor == "engineer" && e.Scope == "signals.engineer");
        Assert.Contains(f.Audit, e => e.Action == "force-released");
    }
    [Fact]
    public void SetOnceIsQueuedAndAcknowledgedAtRuntimeBoundaryAndConstantsAreProtected()
    {
        var f = new Fixture(); var initial = f.Register();
        var write = f.Service.SetOnce(f.Engineer, new(initial.InstanceId, initial.Revision, "enabled", Value(false)));
        Assert.True(f.Service.Read(f.Runtime).Signals.Single(s => s.Declaration.Id == "enabled").NormalValue.GetBoolean());
        Assert.Single(f.Service.Read(f.Runtime).PendingWrites);
        Assert.Empty(f.Service.Read(f.Engineer).PendingWrites);
        Assert.Throws<ArgumentException>(() => f.Service.SetOnce(f.Engineer, new(initial.InstanceId, initial.Revision, "run", Value(true))));
        Assert.Throws<ArgumentException>(() => f.Service.Force(f.Engineer, new(initial.InstanceId, initial.Revision, "delay", Value(4), 60)));
        Assert.Throws<ArgumentException>(() => f.Service.Force(f.Engineer, new(initial.InstanceId, initial.Revision, "run", Value(4), 60)));
        var next = f.Service.Publish(f.Runtime, new(initial.InstanceId, initial.Revision, 1,
            [new("run", Value(false)), new("enabled", Value(false))], [write.Id]));
        Assert.Empty(next.PendingWrites); Assert.False(next.Signals.Single(s => s.Declaration.Id == "enabled").NormalValue.GetBoolean());
        Assert.Throws<ArgumentException>(() => f.Service.Publish(f.Runtime, new(initial.InstanceId, initial.Revision, 2, [new("run", Value(true))], [])));
        Assert.Equal(1, f.Service.Read(f.Runtime).Sequence);
        Assert.Contains(f.Audit, e => e.Action == "write-applied");
    }
    [Fact]
    public void AuthorizationIsAppScopedAndEngineeringCannotBeInferredFromRuntimeAuthority()
    {
        var f = new Fixture(); var initial = f.Register();
        Assert.Throws<UnauthorizedAccessException>(() => f.Service.Force(f.Runtime, new(initial.InstanceId, initial.Revision, "run", Value(true), 60)));
        Assert.Throws<PlatformConflictException>(() => f.Service.Force(f.Engineer with { AppId = "another" }, new(initial.InstanceId, initial.Revision, "run", Value(true), 60)));
        Assert.Throws<PlatformConflictException>(() => f.Service.Force(f.Engineer, new(Guid.NewGuid(), initial.Revision, "run", Value(true), 60)));
        Assert.Throws<PlatformConflictException>(() => f.Service.Force(f.Engineer, new(initial.InstanceId, "old-revision", "run", Value(true), 60)));
        f.Service.Force(f.Engineer, new(initial.InstanceId, initial.Revision, "run", Value(true), 60));
        Assert.Throws<PlatformConflictException>(() => f.Service.Force(f.Engineer with { Actor = "another-engineer" }, new(initial.InstanceId, initial.Revision, "run", Value(false), 60)));
        f.Revoked.Add("engineer");
        var watched = f.Service.Read(f.Runtime);
        Assert.Null(watched.Signals.Single(s => s.Declaration.Id == "run").Force);
        Assert.Throws<UnauthorizedAccessException>(() => f.Service.Read(f.Engineer));
    }
    [Fact]
    public void ExpiryUsesMonotonicDurationAndMaintenanceOrStaleRuntimeClearsForces()
    {
        var f = new Fixture(); var initial = f.Register();
        f.Service.Force(f.Engineer, new(initial.InstanceId, initial.Revision, "run", Value(true), 2));
        f.Clock.Advance(TimeSpan.FromSeconds(3)); f.Clock.Utc = f.Clock.Utc.AddHours(-1);
        Assert.Null(f.Service.Read(f.Runtime).Signals.Single(s => s.Declaration.Id == "run").Force);
        f.Publish(false);
        f.Service.Force(f.Engineer, new(initial.InstanceId, initial.Revision, "run", Value(true), 60));
        f.Service.SetMaintenance(true);
        Assert.Equal("paused", f.Service.Read(f.Runtime).Quality);
        Assert.Null(f.Service.Read(f.Runtime).Signals.Single(s => s.Declaration.Id == "run").Force);
        Assert.Throws<PlatformConflictException>(() => f.Service.Force(f.Engineer, new(initial.InstanceId, initial.Revision, "run", Value(true), 60)));
        f.Service.SetMaintenance(false); f.Publish(false);
        f.Service.Force(f.Engineer, new(initial.InstanceId, initial.Revision, "run", Value(true), 60));
        f.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal("stale", f.Service.Read(f.Runtime).Quality);
        Assert.Null(f.Service.Read(f.Runtime).Signals.Single(s => s.Declaration.Id == "run").Force);
    }
    [Fact]
    public void RuntimeRestartAndRevisionReplacementDoNotRetainEngineeringState()
    {
        var f = new Fixture(); var initial = f.Register();
        f.Service.Force(f.Engineer, new(initial.InstanceId, initial.Revision, "run", Value(true), 60));
        f.Service.SetOnce(f.Engineer, new(initial.InstanceId, initial.Revision, "enabled", Value(false)));
        var replacement = f.Registration with { InstanceId = Guid.NewGuid(), Revision = "revision-two" };
        var next = f.Service.Register(f.Runtime, replacement);
        Assert.All(next.Signals, s => Assert.Null(s.Force)); Assert.Empty(next.PendingWrites);
        Assert.Throws<PlatformConflictException>(() => f.Publish(true));
        var restarted = f.NewService(); restarted.SetMaintenance(false);
        Assert.Throws<PlatformConflictException>(() => restarted.Read(f.Runtime));
    }
    [Fact]
    public void AuditFailureRejectsNewForceButCannotPreventAutomaticRelease()
    {
        var f = new Fixture(); var initial = f.Register(); f.AuditUnavailable = true;
        Assert.Throws<PlatformCapacityException>(() => f.Service.Force(f.Engineer, new(initial.InstanceId, initial.Revision, "run", Value(true), 60)));
        Assert.Null(f.Service.Read(f.Runtime).Signals.Single(s => s.Declaration.Id == "run").Force);
        f.AuditUnavailable = false;
        f.Service.Force(f.Engineer, new(initial.InstanceId, initial.Revision, "run", Value(true), 60));
        f.AuditUnavailable = true; f.Service.SetMaintenance(true);
        Assert.Null(f.Service.Read(f.Runtime).Signals.Single(s => s.Declaration.Id == "run").Force);
        Assert.Equal(1, f.Service.Support(true).AutomaticAuditFailures);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        private long ticks;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan time) { Utc += time; ticks += time.Ticks; }
    }
    private sealed class Fixture
    {
        public readonly Clock Clock = new();
        public readonly List<ApplicationEngineeringAudit> Audit = [];
        public readonly HashSet<string> Revoked = [];
        public bool AuditUnavailable;
        public readonly ApplicationOverrideService Service;
        public readonly ApplicationEngineeringAuthority Runtime;
        public readonly ApplicationEngineeringAuthority Engineer;
        public readonly ApplicationSignalRegistration Registration = new(Guid.NewGuid(), "revision-one",
            [new("run", ApplicationSignalType.Boolean, ApplicationSignalAccess.ReadOnly, Value(false), true),
             new("enabled", ApplicationSignalType.Boolean, ApplicationSignalAccess.ReadWrite, Value(true), true),
             new("delay", ApplicationSignalType.Number, ApplicationSignalAccess.Constant, Value(3))]);
        private long sequence;
        public Fixture()
        {
            Service = NewService(); Service.SetMaintenance(false);
            Runtime = new("app-one", "runtime", 1, Clock.Utc.AddDays(1), "signals.runtime");
            Engineer = new("app-one", "engineer", 1, Clock.Utc.AddDays(1), "signals.engineer");
        }
        public ApplicationOverrideService NewService() => new(Clock, a => !Revoked.Contains(a.Actor), entry =>
        { if (AuditUnavailable) throw new PlatformCapacityException("Test pressure."); Audit.Add(entry); });
        public ApplicationSignalSnapshot Register() => Service.Register(Runtime, Registration);
        public ApplicationSignalSnapshot Publish(bool run) => Service.Publish(Runtime, new(Registration.InstanceId, Registration.Revision, ++sequence,
            [new("run", Value(run)), new("enabled", Value(true))], []));
    }
}
