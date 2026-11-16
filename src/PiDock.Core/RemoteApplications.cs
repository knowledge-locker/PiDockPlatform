using System.Text.Json.Serialization;

namespace PiDock.Core;

public sealed record ApplicationCommand(Guid Id, string TenantId, string DeviceId, string Action, string ContainerId,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string RequestedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApplicationReleaseRequest? Definition = null);
public sealed record ApplicationCommandResult(Guid Id, string State, string Message, DateTimeOffset CompletedAt);
public sealed record RemoteApplicationSnapshot(DateTimeOffset ObservedAt, bool ControlEnabled, string Status,
    IReadOnlyList<ApplicationSummary> Applications, IReadOnlyList<ApplicationCommandResult> Results,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool InstallEnabled = false);

public static class ApplicationCommands
{
    public const int MaximumReceiptBytes = 32768;
    public static bool Valid(ApplicationCommand command, string tenant, string device, DateTimeOffset now) =>
        command.Id != Guid.Empty && command.TenantId == tenant && command.DeviceId == device && IdentityRules.IsId(tenant) && IdentityRules.IsId(device) &&
        ValidAction(command) &&
        command.RequestedBy is { Length: > 0 and <= 64 } && command.IssuedAt <= now.AddSeconds(30) && command.ExpiresAt > now &&
        command.ExpiresAt > command.IssuedAt && command.ExpiresAt - command.IssuedAt <= TimeSpan.FromMinutes(2);
    private static bool ValidAction(ApplicationCommand command)
    {
        if (command.Action != "install") return command.Definition is null && command.Action is ("start" or "stop" or "restart") &&
            command.ContainerId is { Length: 64 } && command.ContainerId.All(char.IsAsciiHexDigit);
        if (command.ContainerId != "" || command.Definition is null) return false;
        try { ApplicationDefinition.Validate(command.Definition); return true; }
        catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException) { return false; }
    }
}
