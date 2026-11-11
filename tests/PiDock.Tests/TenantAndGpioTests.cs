using System.Security.Claims;
using PiDock.Core;
using PiDock.Agent;
using PiDock.Cloud.Web;
using PiDock.Web.Shared;
namespace PiDock.Tests;

public sealed class TenantAndGpioTests : IDisposable
{
    private readonly string root = Path.Combine((OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath()), "pidock-fleet-test-" + Guid.NewGuid());
    [Fact]
    public void TopicAndTenantMustMatchAndReadersCannotCrossTenants()
    {
        Directory.CreateDirectory(root); var store = new FleetStore(new StateDirectory(root));
        var budget = DataPolicyCalculator.Evaluate(new(), new(10L << 30, 5L << 30, 0));
        var sample = new HostSnapshot(DateTimeOffset.UtcNow, "pi5", "Linux", 10, null, null, null, 100, budget, [], "unverified");
        var envelope = new TelemetryEnvelope(1, "alpha", "pi5", Guid.NewGuid(), Guid.NewGuid(), 1, sample);
        Assert.False(store.Ingest(IdentityRules.Topic("beta", "pi5"), envelope));
        Assert.True(store.Ingest(IdentityRules.Topic("alpha", "pi5"), envelope));
        Assert.False(store.Ingest(IdentityRules.Topic("alpha", "pi5"), envelope));
        Assert.False(store.Ingest(IdentityRules.Topic("alpha", "pi5"), envelope with { MessageId = Guid.NewGuid() }));
        Assert.True(store.Ingest(IdentityRules.Topic("alpha", "pi5"), envelope with { MessageId = Guid.NewGuid(), Sequence = 2 }));
        ClaimsPrincipal User(string tenant) => new(new ClaimsIdentity([new Claim("tenant", tenant)], "test"));
        Assert.Single(store.For(User("alpha"))); Assert.Empty(store.For(User("beta"))); Assert.Empty(store.For(new ClaimsPrincipal()));
    }
    [Theory]
    [InlineData("+")]
    [InlineData("x/y")]
    [InlineData("#")]
    [InlineData("")]
    public void WildcardAndPathIdentifiersAreRejected(string id) => Assert.Throws<ArgumentException>(() => IdentityRules.Topic(id, "pi5"));
    [Fact]
    public void SimulationIsExplicitAndBusPinsAreProtected()
    {
        var disabled = new GpioSimulator(false); Assert.Throws<InvalidOperationException>(() => disabled.Force(17, true));
        var simulator = new GpioSimulator(true);
        Assert.False(simulator.CanForce(2)); Assert.False(simulator.CanForce(null)); Assert.True(simulator.CanForce(17)); Assert.False(disabled.CanForce(17)); Assert.Throws<ArgumentException>(() => simulator.Force(2, true));
        simulator.Force(17, true); Assert.True(simulator.Read().Single(p => p.Gpio == 17).Forced);
        simulator.ReleaseAll(); Assert.False(simulator.Read().Single(p => p.Gpio == 17).Forced);
        Assert.Equal(40, simulator.Read().Count); Assert.Null(disabled.Read().Single(p => p.Gpio == 17).High);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
