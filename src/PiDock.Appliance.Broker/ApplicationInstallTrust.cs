using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

public interface IApplicationInstallTrustStore
{
    ApplicationInstallApproval[] Read();
    void Save(ApplicationInstallApproval approval);
}
public sealed class ApplicationInstallTrustStore(IDataProtectionProvider protection, string root) : IApplicationInstallTrustStore
{
    private readonly string directory = StateFiles.PrepareDirectory(root);
    private readonly IDataProtector protector = protection.CreateProtector("PiDock.ApplicationInstallTrust.v1");
    private readonly object gate = new();
    public ApplicationInstallApproval[] Read()
    {
        lock (gate)
        {
            var files = Directory.GetFiles(directory, "*.protected");
            if (files.Length > 64) throw new IOException("Image approval store limit exceeded.");
            try
            {
                return files.Select(path =>
                {
                    if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > 8192)
                        throw new IOException("Invalid image approval file.");
                    var approval = JsonSerializer.Deserialize<ApplicationInstallApproval>(protector.Unprotect(File.ReadAllText(path)), StateFiles.Json)
                        ?? throw new IOException("Invalid image approval.");
                    ApplicationInstallTrustRules.Validate(approval);
                    if (Path.GetFileName(path) != approval.ManifestSha256 + ".protected") throw new IOException("Invalid image approval identity.");
                    return approval;
                }).ToArray();
            }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException)
            { throw new IOException("Image approval store could not be authenticated."); }
        }
    }
    public void Save(ApplicationInstallApproval approval)
    {
        ApplicationInstallTrustRules.Validate(approval);
        lock (gate)
        {
            var current = Read();
            var path = Path.Combine(directory, approval.ManifestSha256 + ".protected");
            if (!File.Exists(path) && current.Length >= 64)
            {
                // Only expired records may be reclaimed, retaining active approvals and revocations.
                foreach (var expired in current.Where(a => a.ExpiresAt <= approval.ApprovedAt))
                    File.Delete(Path.Combine(directory, expired.ManifestSha256 + ".protected"));
                StateFiles.SynchronizeDirectory(directory);
                if (Read().Length >= 64) throw new InvalidOperationException("Image approval store is full.");
            }
            StateFiles.WriteAtomic(path, protector.Protect(JsonSerializer.Serialize(approval, StateFiles.Json)));
        }
    }
}

public sealed partial class DockerOperations
{
    public ApplicationInstallSupport InstallTrustSupport() => new(1, installTrustEnabled && installTrustStore is not null,
        "owner-approved-exact-runtime-manifest", ApplicationInstallTrustRules.MaximumApprovalHours,
        ["Explicit unsigned-image exception; publisher signatures are not verified.",
         "Approval covers app ID, pinned image and every runtime-manifest setting for at most 24 hours; no implicit capability or UI grants.",
         "Linux ARM64 image metadata is verified before container creation. Approval expiry/revocation prevents new installations and update candidates, not existing-container restarts.",
         "Package import/upload, publisher PKI, registry credential delivery and transactional UI registration remain separate work."]);
    public ApplicationInstallApproval[] InstallApprovals() => installTrustStore?.Read() ?? [];
    public async Task<ApplicationInstallApproval> ApproveInstall(ApplicationInstallApprovalRequest request, CancellationToken ct)
    {
        if (!InstallTrustSupport().Enabled) throw new InvalidOperationException("Exact-image approval policy is not enabled.");
        var hash = ApplicationInstallTrustRules.ManifestHash(request.Manifest);
        var approval = new ApplicationInstallApproval(hash, request.Manifest.Name, request.Manifest.Image, request.Owner, request.Reason, Now, request.ExpiresAt);
        ApplicationInstallTrustRules.Validate(approval);
        await deploymentLock.WaitAsync(ct);
        try { installTrustStore!.Save(approval); return approval; }
        finally { deploymentLock.Release(); }
    }
    public async Task RevokeInstallApproval(string hash, CancellationToken ct)
    {
        if (!InstallTrustSupport().Enabled) throw new InvalidOperationException("Exact-image approval policy is not enabled.");
        await deploymentLock.WaitAsync(ct);
        try
        {
            var approval = installTrustStore!.Read().SingleOrDefault(a => a.ManifestSha256 == hash) ?? throw new ArgumentException("Unknown image approval.");
            installTrustStore.Save(approval with { Revoked = true });
        }
        finally { deploymentLock.Release(); }
    }
    private void RequireInstallApproval(ApplicationManifest manifest)
    {
        if (!installTrustEnabled) return;
        var hash = ApplicationInstallTrustRules.ManifestHash(manifest);
        if (installTrustStore?.Read().SingleOrDefault(a => a.ManifestSha256 == hash) is not { Revoked: false } approval ||
            approval.ExpiresAt <= Now || approval.ApprovedAt > Now || approval.AppId != manifest.Name || approval.Image != manifest.Image)
            throw new InvalidOperationException("An unexpired Owner approval of this exact runtime manifest is required.");
    }
    private async Task ValidateInstallImage(ApplicationManifest manifest, CancellationToken ct)
    {
        RequireInstallApproval(manifest);
        if (!installTrustEnabled) return;
        using var response = await engine.GetAsync($"{Api}/images/{Uri.EscapeDataString(manifest.Image)}/json", ct);
        response.EnsureSuccessStatusCode();
        var image = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!image.TryGetProperty("Os", out var os) || os.GetString() != "linux" ||
            !image.TryGetProperty("Architecture", out var architecture) || architecture.GetString() != "arm64")
            throw new InvalidOperationException("The approved image must resolve to Linux ARM64.");
    }
}
