using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiDock.Core;
using PiDock.Web.Shared;
namespace PiDock.Cloud.Web;

public sealed record DeviceInvitation(Guid Id, string TenantId, string DeviceId, string Name, string CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string CodeHash, string State = "Pending",
    string? KeyFingerprint = null, EnrollmentReply? Reply = null);
public sealed record EnrollmentCode(Guid Id, string DeviceId, string Code, DateTimeOffset ExpiresAt);
public sealed class EnrollmentStore
{
    private readonly object gate = new(); private readonly string path;
    private readonly TimeProvider clock; private readonly IDeviceCertificateIssuer issuer;
    private List<DeviceInvitation> entries;
    public EnrollmentStore(StateDirectory state, TimeProvider clock, IDeviceCertificateIssuer issuer)
    {
        this.clock = clock; this.issuer = issuer; path = Path.Combine(state.Path, "enrollments.json");
        entries = File.Exists(path) ? JsonSerializer.Deserialize<List<DeviceInvitation>>(File.ReadAllText(path), StateFiles.Json)! : [];
    }
    public bool Ready => issuer.Ready;
    public static bool CanManage(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && IdentityRules.IsId(user.FindFirstValue("tenant"))
        && (user.IsInRole("Owner") || user.IsInRole("Administrator"));
    public IReadOnlyList<DeviceInvitation> For(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true || !IdentityRules.IsId(user.FindFirstValue("tenant"))) return [];
        lock (gate) return entries.Where(e => e.TenantId == user.FindFirstValue("tenant"))
            .Select(e => e with { CodeHash = "", Reply = null, State = e.State == "Pending" && e.ExpiresAt <= clock.GetUtcNow() ? "Expired" : e.State }).ToArray();
    }
    public EnrollmentCode Create(ClaimsPrincipal user, string name)
    {
        if (!CanManage(user)) throw new UnauthorizedAccessException();
        if (!Ready) throw new InvalidOperationException("Enrollment signing is not configured.");
        name = name.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("Enter a device name of 1–80 characters.");
        lock (gate)
        {
            var tenant = user.FindFirstValue("tenant")!; var now = clock.GetUtcNow();
            var updated = entries.Where(e => e.State == "Registered" || e.ExpiresAt > now).ToList();
            if (updated.Count >= 10000 || updated.Count(e => e.TenantId == tenant && e.State == "Pending") >= 100)
                throw new InvalidOperationException("Enrollment capacity reached. Cancel unused invitations.");
            string raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            string code = string.Join('-', Enumerable.Range(0, 8).Select(i => raw.Substring(i * 4, 4)));
            var entry = new DeviceInvitation(Guid.NewGuid(), tenant, "pi-" + Guid.NewGuid().ToString("N"), name, user.Identity!.Name!, now, now.AddMinutes(10), Hash(raw));
            updated.Add(entry); Save(updated); return new(entry.Id, entry.DeviceId, code, entry.ExpiresAt);
        }
    }
    public bool Cancel(ClaimsPrincipal user, Guid id)
    {
        if (!CanManage(user)) throw new UnauthorizedAccessException();
        lock (gate)
        {
            int index = entries.FindIndex(e => e.Id == id && e.TenantId == user.FindFirstValue("tenant") && e.State == "Pending");
            if (index < 0) return false;
            var updated = entries.ToList(); updated[index] = updated[index] with { State = "Cancelled" }; Save(updated); return true;
        }
    }
    public EnrollmentReply? Redeem(EnrollmentRequest request)
    {
        if (request.Code is not { Length: <= 64 } || request.CsrPem is not { Length: > 0 and <= 8192 }) return null;
        string normalized = request.Code.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        if (normalized.Length != 32 || !normalized.All(Uri.IsHexDigit)) return null;
        string hash = Hash(normalized);
        lock (gate)
        {
            int index = entries.FindIndex(e => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(e.CodeHash), Convert.FromHexString(hash)));
            if (index < 0) return null; var entry = entries[index];
            if (entry.ExpiresAt <= clock.GetUtcNow() || entry.State is not ("Pending" or "Registered")) return null;
            string fingerprint = EnrollmentIdentity.Fingerprint(request.CsrPem);
            // A lost response can be retrieved with the same proven device key; never issue a second identity.
            if (entry.State == "Registered") return entry.KeyFingerprint == fingerprint ? entry.Reply : null;
            var reply = issuer.Issue(entry.TenantId, entry.DeviceId, request.CsrPem);
            var updated = entries.ToList(); updated[index] = entry with { State = "Registered", KeyFingerprint = fingerprint, Reply = reply };
            Save(updated); return reply;
        }
    }
    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    private void Save(List<DeviceInvitation> updated)
    {
        StateFiles.WriteAtomic(path, JsonSerializer.Serialize(updated, StateFiles.Json)); entries = updated;
    }
}
