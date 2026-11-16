namespace PiDock.Core;

public sealed record ApplicationRemovalSupport(int SchemaVersion, bool Enabled, bool DataDeletion, string[] Limitations);
public sealed record ApplicationRemovalRequest(Guid OperationId, string ExpectedImage);
public sealed record ApplicationRemovalCompleteRequest(Guid OperationId, string Owner, bool ExternalCleanupConfirmed);
public sealed record ApplicationRemovalTarget(string ContainerId, string Image, string? NetworkId);
public sealed record ApplicationRemovalStatus(Guid OperationId, string AppId, string ContainerId, string Image,
    string Phase, ApplicationRemovalTarget[] Targets, DateTimeOffset RequestedAt, string Outcome,
    string[] CredentialIds, string[] RemainingActions, string? CompletedBy = null);
public static class ApplicationRemovalRules
{
    public static bool BlocksStartup(ApplicationRemovalStatus? status) => status is not null && status.Phase != "completed";
}
