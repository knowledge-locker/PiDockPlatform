using System.Text;
using System.Text.Json;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationAuthoringTests
{
    private static ApplicationManifest Runtime => new("sample", "registry/sample@sha256:" + new string('a', 64), ContainerPort: 7361, HostPort: 7361,
        Resources: new(PersistentData: true, Credentials: ["bridge"], Health: new(), ManagedLifecycle: true));
    private static ApplicationAuthoringPlan Plan => new(1, Runtime,
        new(1, "sample", "main", "Sample", "index.html", "1.0", "1.0"), "http://127.0.0.1:7361/",
        ["assets/components.css", "assets/client.js"], new("bridge", "commands", ["demo.start", "demo.stop"], "events"), ["ui.hosting.interactive"]);
    private static ApplicationAuthoringReport Check(ApplicationAuthoringPlan plan) => ApplicationAuthoring.Check(JsonSerializer.SerializeToUtf8Bytes(plan, StateFiles.Json));

    [Fact]
    public void ValidOfflinePlanNeverPretendsTargetEnablementOrConformance()
    {
        var report = Check(Plan);
        Assert.True(report.Valid); Assert.False(report.TargetVerified); Assert.False(report.ReadyForDeployment);
        Assert.Equal(ApplicationInstallTrustRules.ManifestHash(Runtime), report.RuntimeManifestSha256);
        Assert.Contains("current_server_side_named_credentials_and_explicit_scopes", report.RequiredTargetChecks);
        Assert.Contains("target_health_enabled_and_fixed_reader_in_image", report.RequiredTargetChecks);
        Assert.Contains("target_conformance_gate", report.RequiredTargetChecks);
        Assert.True(Check(new(1, new("worker", Runtime.Image))).Valid);
    }
    [Fact]
    public void RejectsSecretsUnknownFieldsAmbiguousJsonAndBoundsWithoutEchoingValues()
    {
        const string sentinel = "PRIVATE-TOKEN-SENTINEL";
        foreach (var json in new[] {
            "{\"schemaVersion\":1,\"token\":\"" + sentinel + "\"}",
            "{\"schemaVersion\":1,\"SchemaVersion\":1,\"runtime\":{}}",
            "{\"schemaVersion\":1,\"runtime\":{\"name\":\"sample\",\"image\":\"" + sentinel + "\"}}",
            "{\"schemaVersion\":1,\"runtime\":null}", "{\"schemaVersion\":1,\"runtime\":{\"image\":null}}", "[]" })
        {
            var report = ApplicationAuthoring.Check(Encoding.UTF8.GetBytes(json));
            Assert.False(report.Valid); Assert.Null(report.RuntimeManifestSha256);
            Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(report, StateFiles.Json));
        }
        Assert.False(ApplicationAuthoring.Check(new byte[ApplicationAuthoring.MaximumInputBytes + 1]).Valid);
    }
    [Fact]
    public void ChecksHostIdentityRoutingPublicAssetsAndSeparateBridgeCredentialReference()
    {
        Assert.Contains(Check(Plan with { Ui = Plan.Ui! with { AppId = "other" } }).Issues, i => i.Code == "app_identity_mismatch");
        foreach (var origin in new[] { "http://localhost:7361/", "https://127.0.0.1:7361/", "http://127.0.0.1:7361/?token=secret", "http://user:secret@127.0.0.1:7361/" })
            Assert.Contains(Check(Plan with { ServiceOrigin = origin }).Issues, i => i.Code == "literal_loopback_origin_required");
        Assert.Contains(Check(Plan with { ServiceOrigin = "http://127.0.0.1:7362/" }).Issues, i => i.Code == "host_port_mismatch");
        Assert.Contains(Check(Plan with { Runtime = Runtime with { Resources = null } }).Issues, i => i.Code == "bridge_credential_not_requested");
        Assert.Contains(Check(Plan with { PublicAssets = ["index.html"] }).Issues, i => i.Code == "invalid_public_assets");
        Assert.Contains(Check(Plan with { Bridge = Plan.Bridge! with { CommandsPath = "assets/client.js" } }).Issues, i => i.Code == "invalid_public_assets");
        Assert.Contains(Check(Plan with { Ui = null }).Issues, i => i.Code == "ui_contribution_required");
        Assert.Contains("physical_gpio_unavailable_until_driver_and_board_gate_pass", Check(Plan with { RequiredCapabilities = ["gpio.control"] }).RequiredTargetChecks);
    }
}
