using PiDock.Agent;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationGpioReadinessTests
{
    [Fact]
    public void Arm64ContainerAndChipFilenamesNeverImplyPhysicalSupport()
    {
        var status = ApplicationGpioReadinessRules.Evaluate(new(true, "arm64", "Raspberry Pi 5 Model B Rev 1.0", ["gpiochip0"], "observed"));
        Assert.False(status.PhysicalAvailable);
        Assert.DoesNotContain("linux_host_required", status.BlockingRequirements);
        Assert.DoesNotContain("board_identity_unverified", status.BlockingRequirements);
        Assert.Contains("physical_driver_not_implemented", status.BlockingRequirements);
        Assert.Contains("electrical_fallback_not_validated", status.BlockingRequirements);
        Assert.Contains("board_line_mapping_not_validated", status.BlockingRequirements);
    }

    [Fact]
    public void MissingHostEvidenceHasExplicitBlockersWithoutInventedBoard()
    {
        var service = new ApplicationGpioReadinessService(new Probe(new(false, "x64", null, [], "incomplete")));
        var status = service.Read();
        Assert.False(status.PhysicalAvailable); Assert.Null(status.Host.BoardModel);
        Assert.Contains("linux_host_required", status.BlockingRequirements);
        Assert.Contains("supported_arm64_target_required", status.BlockingRequirements);
        Assert.Contains("gpio_chip_nodes_not_observed", status.BlockingRequirements);
        Assert.Contains("host_probe_incomplete", status.BlockingRequirements);
    }

    [Fact]
    public void LocalReadOnlyProbeIsBoundedAndDoesNotEnablePhysicalOperations()
    {
        var status = new ApplicationGpioReadinessService(new ApplicationGpioReadinessProbe()).Read();
        Assert.False(status.PhysicalAvailable);
        Assert.InRange(status.Host.ChipNodes.Length, 0, 16);
        Assert.True(status.Host.BoardModel is null || status.Host.BoardModel.Length <= 256);
        Assert.All(status.Host.ChipNodes, name => Assert.StartsWith("gpiochip", name));
        Assert.Contains(status.Host.ProbeQuality, new[] { "observed", "incomplete" });
    }
    private sealed class Probe(ApplicationGpioHostEvidence value) : IApplicationGpioReadinessProbe
    { public ApplicationGpioHostEvidence Read() => value; }
}
