namespace PiDock.Core;

public sealed record ApplicationUpdatePolicy(string DataCompatibility = "backward-compatible", int ReadinessSeconds = 60, int StableSeconds = 10)
{
    public void Validate()
    {
        if(DataCompatibility!="backward-compatible" || ReadinessSeconds is <30 or >120 || StableSeconds is <5 or >30 || StableSeconds>=ReadinessSeconds)
            throw new ArgumentException("Updates require backward-compatible data, readiness 30–120 seconds and stability 5–30 seconds shorter than readiness.");
    }
}
public sealed record ApplicationUpdateSupport(int SchemaVersion,bool Enabled,string Strategy,int MaximumRetainedVersions,string[] Limitations);
public sealed record ApplicationUpdateRequest(Guid OperationId,string ExpectedImage,string Image);
public sealed record ApplicationUpdatePolicyRequest(ApplicationUpdatePolicy? Policy);
public sealed record ApplicationVersionRequest(Guid OperationId);
// Private host-to-broker material; excluded from public contract discovery.
public sealed record ApplicationUpdateAdvance(Guid OperationId,long Revision,ApplicationCredentialFile[]? Credentials);
public sealed record ApplicationUpdateStatus(string AppId,string ActiveContainerId,string CurrentImage,string? PreviousImage,string? TargetImage,
    Guid? OperationId,long Revision,string Phase,string Outcome,DateTimeOffset? NextCheckAt,bool CanUpdate,bool CanRollback,bool CanDiscard,bool CanAdvance,string[] CredentialIds,bool CanConfigure=false);
public static class ApplicationUpdateRules
{
    public static bool BlocksLifecycle(string phase)=>phase is "queued" or "staging" or "staged" or "switching" or "verifying" or "rollback_pending" or "rollback_verifying" or "rollback_failed";
    public static bool BlocksDependencies(string phase)=>BlocksLifecycle(phase)&&phase is not ("queued" or "staging" or "staged");
    public static bool CanAdvance(string phase)=>phase is "queued" or "staging" or "staged" or "switching" or "verifying" or "rollback_pending" or "rollback_verifying" or "rollback_cleanup" or "discarding" or "stage_cleanup";
}
