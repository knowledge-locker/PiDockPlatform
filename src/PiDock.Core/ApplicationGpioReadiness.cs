namespace PiDock.Core;

// Detection is diagnostic evidence, not permission to request a GPIO line.
public sealed record ApplicationGpioHostEvidence(bool Linux, string Architecture, string? BoardModel,
    string[] ChipNodes, string ProbeQuality);
public sealed record ApplicationGpioReadinessStatus(int SchemaVersion, bool PhysicalAvailable,
    ApplicationGpioHostEvidence Host, string[] BlockingRequirements, string[] Limitations);

public static class ApplicationGpioReadinessRules
{
    public static ApplicationGpioReadinessStatus Evaluate(ApplicationGpioHostEvidence host)
    {
        var blockers = new List<string>();
        if (!host.Linux) blockers.Add("linux_host_required");
        if (host.Architecture != "arm64") blockers.Add("supported_arm64_target_required");
        if (host.BoardModel is null) blockers.Add("board_identity_unverified");
        if (host.ChipNodes.Length == 0) blockers.Add("gpio_chip_nodes_not_observed");
        if (host.ProbeQuality != "observed") blockers.Add("host_probe_incomplete");
        // These cannot be supplied by app-controlled configuration or a matching model string.
        blockers.AddRange(["physical_driver_not_implemented", "board_line_mapping_not_validated",
            "broker_line_ownership_not_implemented", "lifecycle_maintenance_handoff_not_validated",
            "electrical_fallback_not_validated", "board_timing_and_fault_tests_not_completed"]);
        return new(1, false, host, blockers.ToArray(),
            ["Read-only host diagnostics. No line requests, pin writes or device grants are performed.",
             "ARM64 Docker, a board model string and gpiochip filenames do not establish physical GPIO support.",
             "Physical availability remains false until the supported driver and board-specific conformance gate exist.",
             "Simulation remains a separate Development-only capability and does not satisfy physical acceptance."]);
    }
}
