using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PiDock.Core;

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityState>))]
public enum CapabilityState { Available, Planned, Simulated }
[JsonConverter(typeof(JsonStringEnumConverter<CloudRegistrationState>))]
public enum CloudRegistrationState { Unregistered, Registered, Unavailable }
[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryStatus>))]
public enum DiscoveryStatus { NotConfigured, NotDiscovered }
public enum OperationalPriority { Critical = 1, Warning = 2, Alert = 3, Notice = 4 }

public sealed record PlatformCapability(string Id, CapabilityState State, string Description, string[] Operations);
public sealed record CloudTarget(CloudRegistrationState Registration, string? BaseUrl, string? TenantId,
    string? DeviceId, DiscoveryStatus Connectivity, DiscoveryStatus Publishing, string? PublishingEndpoint,
    string? CatalogEndpoint, bool CanPublish, string Reason);
public sealed record PlatformDiscoveryDocument(int SchemaVersion, string ContractVersion, string ApiVersion,
    string Product, string Stage, string Architecture, string? BoardModel, CloudTarget Cloud,
    PlatformCapability[] Capabilities, string[] ContractIds, string[] PermittedActions);
public sealed record ContractDescriptor(string Id, string Version, string Status, string SchemaPath);

// Registry-reference authoring and shared log ingestion contracts. Installation/grants remain separate.
public sealed record PlatformApplicationManifest(int SchemaVersion, string Id, string Version,
    string RequiredApiVersion, string Image, string[] RequestedCapabilities);
public sealed record ApplicationLogEvent(int SchemaVersion, Guid EventId, string EventCode,
    DateTimeOffset ObservedAt, string DiagnosticLevel, OperationalPriority? Priority,
    string Message, string? CorrelationId, Dictionary<string, JsonElement> Fields);

public static class PlatformContracts
{
    public const string Version = "1.0";
    public const string ApiVersion = "v1";
    public const string ApiRoot = "/api/platform/v1";
    private static readonly IReadOnlyDictionary<string, Type> Types = new Dictionary<string, Type>
    {
        ["discovery"] = typeof(PlatformDiscoveryDocument),
        ["application-manifest"] = typeof(PlatformApplicationManifest),
        ["application-log-event"] = typeof(ApplicationLogEvent),
        ["application-release"] = typeof(ApplicationReleaseRequest),
        ["measurement"] = typeof(ApplicationMeasurement),
        ["shared-event"] = typeof(SharedServiceEvent),
        ["cloud-applications"] = typeof(CloudApplicationDiscovery),
        ["application-alarm-request"] = typeof(ApplicationAlarmRequest),
        ["application-alarm"] = typeof(ApplicationAlarm)
    };
    private static readonly JsonSerializerOptions SchemaOptions = new(StateFiles.Json)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static ContractDescriptor[] List() => Types.Keys.Select(id =>
        new ContractDescriptor(id, Version, "preview",
            $"{ApiRoot}/contracts/{id}")).ToArray();

    public static JsonNode? Schema(string id)
    {
        if (!Types.TryGetValue(id, out var type)) return null;
        var schema = SchemaOptions.GetJsonSchemaAsNode(type);
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$id"] = $"urn:pidock:contracts:{Version}:{id}";
        if (schema["properties"]?["schemaVersion"] is { } schemaVersion) schemaVersion["const"] = 1;
        if (id is "application-alarm-request" or "application-alarm")
            schema["properties"]!["priority"] = JsonNode.Parse("""{"type":"integer","enum":[1,2,3,4]}""");
        if (id == "application-log-event")
        {
            schema["properties"]!["priority"] = JsonNode.Parse("""{"type":["integer","null"],"enum":[1,2,3,4,null]}""");
            schema["properties"]!["diagnosticLevel"]!["enum"] = JsonNode.Parse("""["Trace","Debug","Information","Warning","Error","Critical"]""");
        }
        if (id == "application-manifest")
        {
            schema["properties"]!["requiredApiVersion"]!["const"] = ApiVersion;
            schema["properties"]!["image"]!["pattern"] = "^[a-z0-9][a-z0-9./:_-]{0,200}@sha256:[a-f0-9]{64}$";
        }
        return schema;
    }

    public const string AuthoringGuide = """
        PiDockOS application platform 1.0 engineering preview

        Start with live target discovery. Available means the listed operations exist;
        Planned means no application operation is implemented. Simulated is never physical I/O.
        Read-only discovery, schemas and explicitly authorized cloud publishing are exposed to agents.
        Registry-reference release packages and shared log/measurement/alarm APIs are implemented.
        Package publication does not grant installation or hardware access.
        JSON schemas describe wire shape, not installation policy or hardware compatibility.

        Target Linux ARM64 images pinned by sha256 digest. Current managed Docker deployments
        run as UID/GID 1654, with a read-only root, dropped capabilities and resource limits.
        Do not request the Docker socket, privileged mode or unrestricted host mounts.
        Expiring app credentials are locally provisioned. Volumes, secrets, device leases and image archive upload are not ready.
        Do not build product apps until the platform conformance gate passes.

        Use the registered Cloud.BaseUrl only as target context. Registration is not publishing
        authorization or connectivity evidence. Never derive publishing endpoints from that URL.
        Appliance discovery cannot infer publishing endpoints or authority. Use pidock_cloud_discover
        with separate publisher credentials and a pinned cloud URL matching registration.
        No deployment, hardware-write or network-change MCP tools are exposed.

        Shared structured logs, typed measurements and durable app alarms are available. Priorities: Critical=1,
        Warning=2, Alert=3, Notice=4. DiagnosticLevel is separate from operational Priority.
        Use UTC observation times; the collector stamps identity, receipt time,
        clock trust, boot identity and ingestion order. Never include secrets in events.
        """;
}
