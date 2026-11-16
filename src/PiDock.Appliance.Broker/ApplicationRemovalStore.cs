using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

public interface IApplicationRemovalStore
{
    ApplicationRemovalStatus? Read();
    void Save(ApplicationRemovalStatus status);
}

public sealed class ApplicationRemovalStore(IDataProtectionProvider protection, string root) : IApplicationRemovalStore
{
    private readonly string directory = StateFiles.PrepareDirectory(root);
    private readonly IDataProtector protector = protection.CreateProtector("PiDock.ApplicationRemoval.v1");
    public ApplicationRemovalStatus? Read()
    {
        string path = Path.Combine(directory, "operation.protected");
        if (!File.Exists(path)) return null;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > 32768)
            throw new IOException("Invalid app removal journal.");
        try
        {
            var value = JsonSerializer.Deserialize<ApplicationRemovalStatus>(protector.Unprotect(File.ReadAllText(path)), StateFiles.Json)
                ?? throw new IOException("App removal journal unavailable.");
            Validate(value); return value;
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException)
        { throw new IOException("App removal journal unavailable."); }
    }
    public void Save(ApplicationRemovalStatus status)
    {
        Validate(status);
        StateFiles.WriteAtomic(Path.Combine(directory, "operation.protected"), protector.Protect(JsonSerializer.Serialize(status, StateFiles.Json)));
    }
    private static void Validate(ApplicationRemovalStatus status)
    {
        static bool Id(string? id) => id is { Length: 64 } && id.All(char.IsAsciiHexDigit);
        if (status.OperationId == Guid.Empty || !IdentityRules.IsId(status.AppId) || status.AppId.Length > 40 || !Id(status.ContainerId) ||
            status.Targets is not { Length: > 0 and <= 2 } || status.Targets.Any(t => t is null || !Id(t.ContainerId) || t.NetworkId is not null && !Id(t.NetworkId)) ||
            status.Targets.Select(t => t.ContainerId).Distinct(StringComparer.Ordinal).Count() != status.Targets.Length ||
            !status.Targets.Any(t => t.ContainerId == status.ContainerId && t.Image == status.Image) ||
            status.Phase is not ("prepared" or "removing" or "metadata" or "awaiting_external_cleanup" or "completed") ||
            status.Outcome is not ("in_progress" or "interrupted" or "external_cleanup_required" or "completed") ||
            status.CredentialIds is not { Length: <= 8 } || status.CredentialIds.Any(id => !IdentityRules.IsId(id)) ||
            status.RemainingActions is not { Length: <= 8 } || status.RemainingActions.Any(a => !ApplicationContractRules.Text(a, 256)) ||
            status.CompletedBy is not null && (!ApplicationContractRules.Text(status.CompletedBy, 128) || status.CompletedBy.Any(char.IsControl)) ||
            status.Phase == "completed" && string.IsNullOrWhiteSpace(status.CompletedBy)) throw new ArgumentException("Invalid app removal journal.");
        foreach (var target in status.Targets) new ApplicationManifest(status.AppId, target.Image).Validate();
    }
}
