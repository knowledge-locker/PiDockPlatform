namespace PiDock.Core;

public sealed record DigitalLine(int Gpio, string Name, string Consumer, bool Used, string Direction, string Bias, bool ActiveLow, bool CanInput = false, bool CanOutput = false, bool? OutputFallback = null);
public sealed record DigitalIoStatus(bool Available, string Status, string? Chip, IReadOnlyList<DigitalLine> Lines, IReadOnlyList<DigitalLeaseView> Leases, bool Configurable = false);
public sealed record DigitalIoPolicy(int[] Inputs, Dictionary<int,bool> Outputs)
{
    public void Validate()
    {
        if(Inputs is null||Outputs is null||Inputs.Length>26||Outputs.Count>26||Inputs.Distinct().Count()!=Inputs.Length||Inputs.Any(pin=>!DigitalIoRules.Assignable(pin))||Outputs.Keys.Any(pin=>!DigitalIoRules.Assignable(pin)))
            throw new ArgumentException("GPIO policy may contain only non-reserved header pins and explicit output fallback levels.");
    }
}
public sealed record DigitalPinPermission(int Gpio,bool Input,bool Output,bool FallbackHigh);
public sealed record DigitalLeaseRequest(Guid SessionId, int Gpio, string Mode, bool High = false, int Seconds = 30);
public sealed record DigitalLeaseView(Guid SessionId, int Gpio, string Mode, bool? High, int RemainingSeconds);
public static class DigitalIoRules
{
    public static readonly int[] HeaderGpios = [2,3,4,14,15,17,18,27,22,23,24,10,9,25,11,8,7,5,6,12,13,19,16,26,20,21];
    public static bool Assignable(int gpio) => HeaderGpios.Contains(gpio) && gpio is not (2 or 3 or 7 or 8 or 9 or 10 or 11 or 14 or 15);
    public static void Validate(DigitalLeaseRequest request)
    {
        if (request.SessionId == Guid.Empty || !Assignable(request.Gpio) || request.Mode is not ("input" or "output") || request.Seconds is < 1 or > 60)
            throw new ArgumentException("Choose a non-reserved header GPIO, input/output mode and a 1–60 second session.");
    }
}
