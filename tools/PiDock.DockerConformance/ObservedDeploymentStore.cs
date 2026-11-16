using System.Collections.Concurrent;
using System.Diagnostics;
using PiDock.Appliance.Broker;

// Observe real protected writes; do not change broker state, timing or credential material.
internal sealed class ObservedDeploymentStore(IManagedDeploymentStore inner) : IManagedDeploymentStore
{
    public ConcurrentQueue<RetryAttemptObservation> Attempts { get; } = new();
    public ConcurrentQueue<BudgetResetObservation> BudgetResets { get; } = new();
    public ConcurrentQueue<CredentialWaitObservation> CredentialWaits { get; } = new();
    public ManagedDeployment? Find(string id) => inner.Find(id);
    public void Forget(string id) => inner.Forget(id);
    public void Save(ManagedDeployment deployment)
    {
        var previous = inner.Find(deployment.ContainerId);
        inner.Save(deployment);
        if (deployment.Outcome == "recovering" && deployment.Attempts > (previous?.Attempts ?? 0) && deployment.NextAttemptAt is { } deadline)
            Attempts.Enqueue(new(deployment.Attempts, DateTimeOffset.UtcNow, deadline, Stopwatch.GetTimestamp()));
        if(previous is {Attempts:>0} && deployment.Attempts==0)
            BudgetResets.Enqueue(new(previous.Attempts,DateTimeOffset.UtcNow,deployment.StableSince,deployment.Outcome));
        if(deployment.Outcome=="credential_unavailable" && deployment.NextAttemptAt is { } recheck && deployment.Revision!=previous?.Revision)
            CredentialWaits.Enqueue(new(DateTimeOffset.UtcNow,deployment.Attempts,recheck));
    }
}

internal sealed record RetryAttemptObservation(int Attempt, DateTimeOffset ObservedAt, DateTimeOffset NextAttemptAt, long MonotonicTicks);
internal sealed record BudgetResetObservation(int PreviousAttempts,DateTimeOffset ObservedAt,DateTimeOffset? StableSince,string Outcome);
internal sealed record CredentialWaitObservation(DateTimeOffset ObservedAt,int Attempts,DateTimeOffset NextCheckAt);
