using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiDock.Core;

public sealed record ApplicationReleaseRequest(int SchemaVersion, PlatformApplicationManifest Manifest, string ReleaseNotes);
public sealed record PublishedApplicationRelease(string TenantId, string AppId, string Version, string Digest,
    string PublishedBy, DateTimeOffset PublishedAt, ApplicationReleaseRequest Package);
public sealed record CloudApplicationDiscovery(int SchemaVersion, string TenantId, string CatalogEndpoint,
    string PublishingEndpoint, string[] PermittedActions, string PackageFormat);
public sealed record ApplicationMeasurement(int SchemaVersion, Guid EventId, string Name, double Value,
    string Unit, DateTimeOffset ObservedAt, string? CorrelationId = null);
public sealed record SharedServiceEvent(int SchemaVersion, Guid EventId, string AppId, string Kind,
    DateTimeOffset ObservedAt, DateTimeOffset ReceivedAt, Guid BootId, long MonotonicTicks, long TimestampFrequency,
    string ClockState, JsonElement Payload);
public sealed record ApplicationAlarmRequest(string Condition, OperationalPriority Priority, string Message);
public sealed record ApplicationAlarm(Guid Id, string AppId, string Condition, OperationalPriority Priority,
    string Message, DateTimeOffset RaisedAt, DateTimeOffset UpdatedAt, DateTimeOffset? AcknowledgedAt = null,
    DateTimeOffset? ResolvedAt = null);
public sealed class PlatformConflictException(string message) : Exception(message);
public sealed class PlatformCapacityException(string message) : Exception(message);

public static partial class ApplicationContractRules
{
    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})(-[a-zA-Z0-9]+(?:[.-][a-zA-Z0-9]+)*)?\z")]
    private static partial Regex ReleaseVersion();
    public static bool IsVersion(string? value) => value is { Length: <= 64 } && ReleaseVersion().IsMatch(value);
    public static bool Text(string? value, int max) => value is { Length: > 0 } && value.Length <= max && !value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));
    public static void Validate(ApplicationReleaseRequest request)
    {
        var m = request.Manifest;
        if (request.SchemaVersion != 1 || m is null || m.SchemaVersion != 1 || !IdentityRules.IsId(m.Id) || m.Id.Length > 40 ||
            !IsVersion(m.Version) || m.RequiredApiVersion != PlatformContracts.ApiVersion || request.ReleaseNotes is null || request.ReleaseNotes.Length > 4096 ||
            m.RequestedCapabilities is null || m.RequestedCapabilities.Length > 32 || m.RequestedCapabilities.Any(c => !Text(c, 80) || c.Any(char.IsWhiteSpace)) ||
            m.RequestedCapabilities.Distinct(StringComparer.Ordinal).Count() != m.RequestedCapabilities.Length)
            throw new ArgumentException("Invalid versioned package manifest, compatibility or release metadata.");
        new ApplicationManifest(m.Id, m.Image).Validate();
    }
    public static string Digest(ApplicationReleaseRequest package)
    {
        // Typed canonical form: JSON object/dictionary ordering from a caller cannot alter identity.
        var normalized = package with { Manifest = package.Manifest with {
            RequestedCapabilities = package.Manifest.RequestedCapabilities.Order(StringComparer.Ordinal).ToArray() } };
        return "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, StateFiles.Json))).ToLowerInvariant();
    }
    public static void Validate(ApplicationLogEvent e)
    {
        if (e.SchemaVersion != 1 || e.EventId == Guid.Empty || !IdentityRules.IsId(e.EventCode) || !Text(e.Message, 2048) ||
            e.DiagnosticLevel is not ("Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical") ||
            (e.Priority is { } priority && !Enum.IsDefined(priority)) || e.Fields is null || e.Fields.Count > 16 ||
            e.Fields.Any(f => !IdentityRules.IsId(f.Key)) || e.CorrelationId is { Length: > 128 } ||
            JsonSerializer.SerializeToUtf8Bytes(e, StateFiles.Json).Length > 8192)
            throw new ArgumentException("Invalid structured log event or payload limit exceeded.");
    }
    public static void Validate(ApplicationMeasurement m)
    {
        if (m.SchemaVersion != 1 || m.EventId == Guid.Empty || !IdentityRules.IsId(m.Name) || !double.IsFinite(m.Value) ||
            !Text(m.Unit, 32) || m.CorrelationId is { Length: > 128 }) throw new ArgumentException("Invalid typed measurement.");
    }
    public static void Validate(ApplicationAlarmRequest a)
    {
        if (!IdentityRules.IsId(a.Condition) || !Enum.IsDefined(a.Priority) || !Text(a.Message, 512))
            throw new ArgumentException("Invalid alarm condition, priority or message.");
    }
}
