using System.Text.Json;

namespace PiDock.Core;

public enum ApplicationSignalType { Boolean, Number, Text }
public enum ApplicationSignalAccess { Constant, ReadOnly, ReadWrite }
public sealed record ApplicationSignalDeclaration(string Id, ApplicationSignalType Type, ApplicationSignalAccess Access,
    JsonElement InitialValue, bool Forceable = false);
public sealed record ApplicationSignalRegistration(Guid InstanceId, string Revision, ApplicationSignalDeclaration[] Signals);
public sealed record ApplicationSignalValue(string SignalId, JsonElement Value);
public sealed record ApplicationSignalFrame(Guid InstanceId, string Revision, long Sequence,
    ApplicationSignalValue[] Values, Guid[] AppliedWrites);
public sealed record ApplicationSignalWriteRequest(Guid InstanceId, string Revision, string SignalId, JsonElement Value);
public sealed record ApplicationSignalForceRequest(Guid InstanceId, string Revision, string SignalId, JsonElement Value, int DurationSeconds);
public sealed record ApplicationSignalReleaseRequest(Guid InstanceId, string Revision, string? SignalId = null);
public sealed record ApplicationSignalPendingWrite(Guid Id, string SignalId, JsonElement Value, string Actor, DateTimeOffset ExpiresAt);
public sealed record ApplicationSignalForce(Guid Id, JsonElement Value, string Actor, DateTimeOffset ExpiresAt);
public sealed record ApplicationSignalState(ApplicationSignalDeclaration Declaration, JsonElement NormalValue, JsonElement EffectiveValue,
    ApplicationSignalForce? Force);
public sealed record ApplicationSignalSnapshot(string AppId, Guid InstanceId, string Revision, long Sequence, DateTimeOffset ObservedAt,
    string Quality, ApplicationSignalState[] Signals, ApplicationSignalPendingWrite[] PendingWrites);
public sealed record ApplicationEngineeringSupport(int SchemaVersion, bool Enabled, int MaximumSignalsPerApp, int MaximumForceSeconds,
    int FreshnessSeconds, long AutomaticAuditFailures, string[] Limitations, bool LifecycleBound = false);
public sealed record ApplicationEngineeringAudit(Guid EventId, string AppId, string Action, string Actor, string Scope,
    Guid InstanceId, string Revision, string? SignalId, DateTimeOffset ObservedAt, DateTimeOffset? ExpiresAt = null);

public static class ApplicationSignalRules
{
    public const int MaximumSignalsPerApp = 256;
    public const int MaximumForceSeconds = 300;
    public const int FreshnessSeconds = 5;
    public static bool Revision(string? value) => value is { Length: > 0 and <= 80 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');
    public static bool Value(ApplicationSignalType type, JsonElement value) => type switch
    {
        ApplicationSignalType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        ApplicationSignalType.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n),
        ApplicationSignalType.Text => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 256 },
        _ => false
    };
    public static void Validate(ApplicationSignalRegistration registration)
    {
        if (registration.InstanceId == Guid.Empty || !Revision(registration.Revision) || registration.Signals is not { Length: > 0 and <= MaximumSignalsPerApp } ||
            registration.Signals.Any(s => s is null || !IdentityRules.IsId(s.Id) || !Enum.IsDefined(s.Access) || !Value(s.Type, s.InitialValue) ||
                s.Access == ApplicationSignalAccess.Constant && s.Forceable) || registration.Signals.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != registration.Signals.Length)
            throw new ArgumentException("Declare bounded, uniquely identified typed variables; constants cannot be forced.");
    }
}
