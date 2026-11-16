using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiDock.Core;

// An offline authoring input, not an accepted package manifest or installation grant.
public sealed record ApplicationAuthoringPlan(int SchemaVersion, ApplicationManifest Runtime,
    ApplicationUiContribution? Ui = null, string? ServiceOrigin = null,
    string[]? PublicAssets = null, ApplicationUiBridgeGrant? Bridge = null,
    string[]? RequiredCapabilities = null);
public sealed record ApplicationAuthoringIssue(string Code, string Field);
public sealed record ApplicationAuthoringReport(int SchemaVersion, bool Valid, bool TargetVerified,
    bool ReadyForDeployment, string? RuntimeManifestSha256, ApplicationAuthoringIssue[] Issues,
    string[] RequiredTargetChecks);

public static class ApplicationAuthoring
{
    public const int MaximumInputBytes = 65536;
    private static readonly JsonSerializerOptions Options = new(StateFiles.Json)
    { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static ApplicationAuthoringReport Check(ReadOnlyMemory<byte> input)
    {
        if (input.Length is 0 or > MaximumInputBytes) return Invalid("input_limit", "document");
        ApplicationAuthoringPlan plan;
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 16 });
            if (DuplicateProperties(document.RootElement)) return Invalid("duplicate_property", "document");
            plan = JsonSerializer.Deserialize<ApplicationAuthoringPlan>(input.Span, Options)
                ?? throw new JsonException();
        }
        catch (JsonException) { return Invalid("invalid_or_unknown_contract_field", "document"); }

        var issues = new List<ApplicationAuthoringIssue>();
        if (plan.SchemaVersion != 1) issues.Add(new("unsupported_schema", "schemaVersion"));
        bool runtimeValid = plan.Runtime is { Name: not null, Image: not null };
        if (runtimeValid)
        {
            try { plan.Runtime!.Validate(); }
            catch (Exception e) when (e is ArgumentException or NullReferenceException) { runtimeValid = false; }
        }
        if (!runtimeValid) issues.Add(new("invalid_runtime_manifest", "runtime"));
        if (plan.Ui is not null)
        {
            if (!ApplicationUiRules.Valid(plan.Ui)) issues.Add(new("invalid_ui_contribution", "ui"));
            if (runtimeValid && plan.Ui.AppId != plan.Runtime!.Name) issues.Add(new("app_identity_mismatch", "ui.appId"));
            if (!ValidOrigin(plan.ServiceOrigin, out var port)) issues.Add(new("literal_loopback_origin_required", "serviceOrigin"));
            else if (runtimeValid && plan.Runtime!.HostPort is { } expected && port != expected) issues.Add(new("host_port_mismatch", "serviceOrigin"));
        }
        else if (plan.ServiceOrigin is not null || plan.Bridge is not null || plan.PublicAssets is not null)
            issues.Add(new("ui_contribution_required", "ui"));

        if (!ApplicationUiBridgeRules.Grant(plan.Bridge)) issues.Add(new("invalid_bridge_grant", "bridge"));
        if (plan.Bridge is { } bridge && runtimeValid && !(plan.Runtime!.Resources?.CredentialIds ?? []).Contains(bridge.CredentialId, StringComparer.Ordinal))
            issues.Add(new("bridge_credential_not_requested", "bridge.credentialId"));
        var assets = plan.PublicAssets ?? [];
        if (assets.Length > 64 || assets.Distinct(StringComparer.Ordinal).Count() != assets.Length ||
            assets.Any(path => !ApplicationUiRules.SafePath(path) || !PublicExtension(path) ||
                path == plan.Bridge?.CommandsPath || path == plan.Bridge?.StreamPath))
            issues.Add(new("invalid_public_assets", "publicAssets"));
        var capabilities = plan.RequiredCapabilities ?? [];
        if (capabilities.Length > 32 || capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Length ||
            capabilities.Any(c => c is not { Length: > 0 and <= 80 } || !c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')))
            issues.Add(new("invalid_capability_requirements", "requiredCapabilities"));

        var checks = new List<string> { "live_target_discovery_and_contract_compatibility", "target_linux_arm64_image_and_resource_validation", "owner_install_policy_and_exact_manifest_review", "target_conformance_gate" };
        if (runtimeValid && plan.Runtime!.Resources?.Requested == true) checks.Add("target_managed_resources_enabled_and_image_bootstrap_compatible");
        if (runtimeValid && plan.Runtime!.Resources?.CredentialIds.Length > 0) checks.Add("current_server_side_named_credentials_and_explicit_scopes");
        if (plan.Ui is not null) checks.Add("host_owned_ui_registration_shared_styles_and_browser_conformance");
        if (plan.Bridge is not null) checks.Add("host_owned_named_bridge_grants_and_server_side_credential_binding");
        if (runtimeValid && plan.Runtime!.Resources?.Health is not null) checks.Add("target_health_enabled_and_fixed_reader_in_image");
        if (runtimeValid && plan.Runtime!.Resources is { } resources && (resources.RequiredApps.Length > 0 || resources.ManagedLifecycle)) checks.Add("target_orchestration_enabled_and_dependencies_satisfied");
        if (runtimeValid && plan.Runtime!.Resources?.AutomaticRecovery == true) checks.Add("target_recovery_enabled_and_safe_restart_policy");
        if (runtimeValid && plan.Runtime!.Resources?.Updates is not null) checks.Add("target_updates_enabled_and_backward_compatible_data_verified");
        if (capabilities.Length > 0) checks.Add("each_requested_capability_enabled_and_separately_authorized_on_target");
        if (capabilities.Contains("gpio.control", StringComparer.Ordinal)) checks.Add("physical_gpio_unavailable_until_driver_and_board_gate_pass");
        return new(1, issues.Count == 0, false, false, issues.Count == 0 ? ApplicationInstallTrustRules.ManifestHash(plan.Runtime!) : null, issues.ToArray(), checks.ToArray());
    }

    private static ApplicationAuthoringReport Invalid(string code, string field) => new(1, false, false, false, null, [new(code, field)], ["live_target_discovery_required"]);
    private static bool PublicExtension(string? path) => Path.GetExtension(path)?.ToLowerInvariant() is ".css" or ".js" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".ico" or ".woff" or ".woff2";
    private static bool ValidOrigin(string? text, out int port)
    {
        port = 0;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 ||
            uri.Port is < 1024 or > 65535 || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address)) return false;
        port = uri.Port; return true;
    }
    private static bool DuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name) || DuplicateProperties(property.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) if (DuplicateProperties(item)) return true;
        return false;
    }
}
