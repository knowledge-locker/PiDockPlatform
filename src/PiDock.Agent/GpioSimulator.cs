using System.Diagnostics;
namespace PiDock.Agent;

public sealed record PinState(int Physical, string Label, int? Gpio, bool? High, bool Forced, int RemainingSeconds);
public sealed class GpioSimulator(bool enabled)
{
    private readonly object gate = new();
    private readonly Dictionary<int, (bool High, long Deadline)> overrides = [];
    private static readonly string[] Labels = ["3.3V", "5V", "GPIO2", "5V", "GPIO3", "GND", "GPIO4", "GPIO14", "GND", "GPIO15", "GPIO17", "GPIO18", "GPIO27", "GND", "GPIO22", "GPIO23", "3.3V", "GPIO24", "GPIO10", "GND", "GPIO9", "GPIO25", "GPIO11", "GPIO8", "GND", "GPIO7", "ID_SD", "ID_SC", "GPIO5", "GND", "GPIO6", "GPIO12", "GPIO13", "GND", "GPIO19", "GPIO16", "GPIO26", "GPIO20", "GND", "GPIO21"];
    public bool Enabled => enabled;
    public static bool IsAssignable(int gpio) => Labels.Contains("GPIO" + gpio) && gpio is not (2 or 3 or 7 or 8 or 9 or 10 or 11 or 14 or 15);
    public bool CanForce(int? gpio) => enabled && gpio.HasValue && IsAssignable(gpio.Value);
    public IReadOnlyList<PinState> Read()
    {
        lock (gate)
        {
            long now = Stopwatch.GetTimestamp();
            foreach (var key in overrides.Where(p => p.Value.Deadline <= now).Select(p => p.Key).ToArray()) overrides.Remove(key);
            return Labels.Select((label, i) =>
            {
                int? gpio = label.StartsWith("GPIO", StringComparison.Ordinal) ? int.Parse(label[4..]) : null;
                bool forced = gpio.HasValue && overrides.TryGetValue(gpio.Value, out _);
                var current = forced ? overrides[gpio!.Value] : default;
                return new PinState(i + 1, label, gpio, enabled && gpio.HasValue ? forced && current.High : null, forced,
                    forced ? (int)Math.Ceiling((current.Deadline - now) / (double)Stopwatch.Frequency) : 0);
            }).ToArray();
        }
    }
    public void Force(int gpio, bool high)
    {
        if (!enabled) throw new InvalidOperationException("Physical GPIO control is not implemented. Explicit development simulation is required.");
        if (!IsAssignable(gpio)) throw new ArgumentException("Reserved bus pin cannot be forced.");
        lock (gate) overrides[gpio] = (high, Stopwatch.GetTimestamp() + 60 * Stopwatch.Frequency);
    }
    public void ReleaseAll() { lock (gate) overrides.Clear(); }
}
