namespace PiDock.AppUi.Sample;

// Disposable server-side behavior for host conformance, not a product scan engine or GPIO adapter.
public sealed record SampleSequenceState(bool RunRequest, bool BankEnabled, double ElapsedSeconds,
    bool Done, bool NormalOutput, bool EffectiveOutput, string Quality = "simulated");

public sealed class SampleSequence(TimeProvider clock)
{
    private bool request, enabled = true;
    private long started;
    public void Inputs(bool start, bool stop)
    {
        if (stop) { request = false; started = 0; }
        else if (start && !request) { request = true; started = clock.GetTimestamp(); }
    }
    public void ToggleBank() => enabled = !enabled;
    public void Reset() { Inputs(false, true); enabled = true; }
    public SampleSequenceState Read()
    {
        double elapsed = request ? Math.Clamp(clock.GetElapsedTime(started).TotalSeconds, 0, 3) : 0;
        bool done = request && elapsed >= 3, output = done && enabled;
        // No private browser-side force implementation. The unforced normal/effective values agree.
        return new(request, enabled, elapsed, done, output, output);
    }
}
