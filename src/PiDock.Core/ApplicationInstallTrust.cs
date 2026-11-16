using System.Security.Cryptography;
using System.Text.Json;

namespace PiDock.Core;

public sealed record ApplicationInstallSupport(int SchemaVersion, bool Enabled, string Policy, int MaximumApprovalHours, string[] Limitations);
public sealed record ApplicationInstallApprovalRequest(ApplicationManifest Manifest, string Owner, string Reason, DateTimeOffset ExpiresAt);
public sealed record ApplicationInstallApproval(string ManifestSha256, string AppId, string Image, string Owner, string Reason,
    DateTimeOffset ApprovedAt, DateTimeOffset ExpiresAt, bool Revoked = false);
public static class ApplicationInstallTrustRules
{
    public const int MaximumApprovalHours = 24;
    public static string ManifestHash(ApplicationManifest manifest)
    {
        manifest.Validate();
        // Array order remains part of the review: changing any declared setting requires a new approval.
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest, StateFiles.Json)));
    }
    public static void Validate(ApplicationInstallApproval approval)
    {
        if (approval.ManifestSha256 is not { Length: 64 } || !approval.ManifestSha256.All(char.IsAsciiHexDigit) ||
            !IdentityRules.IsId(approval.AppId) || approval.AppId.Length > 40 ||
            !ApplicationContractRules.Text(approval.Owner, 128) || approval.Owner.Any(char.IsControl) ||
            !ApplicationContractRules.Text(approval.Reason, 512) || approval.Reason.Any(char.IsControl) ||
            approval.ExpiresAt <= approval.ApprovedAt || approval.ExpiresAt - approval.ApprovedAt > TimeSpan.FromHours(MaximumApprovalHours))
            throw new ArgumentException("Invalid exact-manifest Owner approval.");
        new ApplicationManifest(approval.AppId, approval.Image).Validate();
    }
}
