namespace PiDock.Core;

public sealed record ApplicationLifecycleSupport(int SchemaVersion, bool Enabled, int MaximumAttempts,
    int StableSeconds, string Coordinator, string[] Limitations);
public sealed record ApplicationLifecycleStatus(string AppId, string ContainerId, string RuntimeState,
    bool DesiredRunning, bool RecoveryEnabled, long Revision, int Attempts, DateTimeOffset? NextAttemptAt,
    DateTimeOffset? StableSince, string Outcome, string[] CredentialIds, ApplicationHealthStatus? Health = null, ApplicationDependencyStatus[]? Dependencies = null, bool PendingStart = false, bool OrchestrationPaused = false, bool UpdateInProgress = false);
public sealed record ApplicationRecoveryRequest(long Revision, ApplicationCredentialFile[] Credentials);
public sealed record ApplicationRecoveryPolicy(bool Enabled);
public sealed record ApplicationRecoveryBlocked(long Revision);
public static class ApplicationRecoveryRules
{
    public const int MaximumAttempts = 5;
    public const int StableSeconds = 60;
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 6))));
}
