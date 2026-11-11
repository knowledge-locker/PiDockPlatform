using PiDock.Core;
using PiDock.Agent;
namespace PiDock.Tests;

public sealed class PolicyTests : IDisposable
{
    private const long GiB = 1L << 30;
    private readonly string root = Path.Combine((OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath()), "pidock-test-" + Guid.NewGuid());
    [Fact]
    public void BudgetCountsRetainedHistoryOnceAndContractsAsAppsGrow()
    {
        var full = DataPolicyCalculator.Evaluate(new(), new(24 * GiB, 8 * GiB, 2 * GiB));
        Assert.Equal((long)(24 * GiB * .1), full.BudgetBytes);
        var constrained = DataPolicyCalculator.Evaluate(new(), new(24 * GiB, 3 * GiB, GiB));
        Assert.Equal(4 * GiB - constrained.ReserveBytes, constrained.BudgetBytes);
    }
    [Fact]
    public void ExhaustionProducesZeroBudgetNotNegative()
    {
        var result = DataPolicyCalculator.Evaluate(new(), new(2 * GiB, 0, 0));
        Assert.Equal(0, result.BudgetBytes); Assert.True(result.UnderPressure);
    }
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(100)]
    public void UnsafePolicyIsRejected(double percent) => Assert.Throws<ArgumentException>(() => (new DataPolicy(HistoryPercent: percent)).Validate());
    [Fact] public void InvalidWatermarksAreRejected() => Assert.Throws<ArgumentException>(() => (new DataPolicy(CleanupAtPercent: 80, CleanupToPercent: 90)).Validate());
    [Fact]
    public void CleanupRotatesOldestSegmentsAndPreservesApplicationData()
    {
        Directory.CreateDirectory(root); var appFile = Path.Combine(root, "application.txt"); File.WriteAllText(appFile, "must survive");
        var store = new HistoryStore(root, new SmallProbe());
        store.Apply(new DataPolicy(MinimumReserveBytes: 512L << 20));
        for (int i = 0; i < 30; i++) Assert.True(store.Append(HistoryClass.Telemetry, new { index = i, data = new string('x', 700) }));
        var status = store.Status(); Assert.True(status.DeletedSegments > 0);
        Assert.True(status.ClassUsage[HistoryClass.Telemetry] <= status.Budget.ClassLimits[HistoryClass.Telemetry]);
        Assert.Equal("must survive", File.ReadAllText(appFile));
        var remaining = Directory.GetFiles(Path.Combine(root, "history", "Telemetry")).Order().ToArray();
        Assert.DoesNotContain("\"index\":0,", File.ReadAllText(remaining[0]));
    }
    [Fact]
    public void NeverWritesIntoLinkedHistoryDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root); var elsewhere = Path.Combine(root, "elsewhere"); Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(Path.Combine(root, "history"), elsewhere);
        Assert.Throws<IOException>(() => new HistoryStore(root, new SmallProbe()));
    }
    private sealed class SmallProbe : ICapacityProbe
    {
        public StorageCapacity Read(string path, long bytes) => new((512L << 20) + 10000, (512L << 20) + 10000 - bytes, bytes);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
