namespace PiDock.Core;

public sealed record ApplicationCommand(Guid Id, string TenantId, string DeviceId, string Action, string ContainerId,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string RequestedBy);
public sealed record ApplicationCommandResult(Guid Id, string State, string Message, DateTimeOffset CompletedAt);
public sealed record RemoteApplicationSnapshot(DateTimeOffset ObservedAt, bool ControlEnabled, string Status,
    IReadOnlyList<ApplicationSummary> Applications, IReadOnlyList<ApplicationCommandResult> Results);

public static class ApplicationCommands
{
    public static bool Valid(ApplicationCommand command, string tenant, string device, DateTimeOffset now) =>
        command.Id != Guid.Empty && command.TenantId == tenant && command.DeviceId == device && IdentityRules.IsId(tenant) && IdentityRules.IsId(device) &&
        command.Action is "start" or "stop" or "restart" && command.ContainerId is { Length: 64 } && command.ContainerId.All(char.IsAsciiHexDigit) &&
        command.RequestedBy is { Length: > 0 and <= 64 } && command.IssuedAt <= now.AddSeconds(30) && command.ExpiresAt > now &&
        command.ExpiresAt > command.IssuedAt && command.ExpiresAt - command.IssuedAt <= TimeSpan.FromMinutes(2);
}
