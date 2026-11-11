using PiDock.Core;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
namespace PiDock.Web.Shared;

public sealed record LoginUser(string Username, string PasswordHash, string Secret, string TenantId, string Role,
    string Stamp, long LastStep = -1, int Failures = 0, DateTimeOffset? LockedUntil = null);
public sealed class UserStore
{
    private readonly object gate = new();
    private readonly string path;
    private readonly IDataProtector protector;
    private readonly PasswordHasher<string> hasher = new();
    private readonly List<LoginUser> users;
    public UserStore(IDataProtectionProvider provider, string directory)
    {
        path = Path.Combine(StateFiles.PrepareDirectory(directory), "accounts.protected");
        protector = provider.CreateProtector("PiDock.Accounts.v1");
        users = File.Exists(path) ? JsonSerializer.Deserialize<List<LoginUser>>(protector.Unprotect(File.ReadAllText(path)), StateFiles.Json)! : [];
    }
    private void Save() => StateFiles.WriteAtomic(path, protector.Protect(JsonSerializer.Serialize(users, StateFiles.Json)));
    public bool HasUsers { get { lock (gate) return users.Count > 0; } }
    public void Add(string name, string password, string secret, string tenant, string role, bool singleOwner, long lastStep)
    {
        if (!PiDock.Core.IdentityRules.IsId(name) || !PiDock.Core.IdentityRules.IsId(tenant) || password.Length is < 14 or > 256 || !new[] { "Owner", "Administrator", "Operator", "Viewer", "Auditor" }.Contains(role))
            throw new ArgumentException("Use an ASCII identifier, a 14–256 character password, a valid tenant, and a supported role.");
        lock (gate)
        {
            if ((singleOwner && users.Count > 0) || users.Any(x => x.Username.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Account already exists.");
            users.Add(new(name, hasher.HashPassword(name, password), secret, tenant, role, Guid.NewGuid().ToString(), lastStep)); Save();
        }
    }
    public LoginUser? Authenticate(string username, string password, string code, DateTimeOffset now)
    {
        lock (gate)
        {
            int i = users.FindIndex(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (i < 0) { _ = hasher.HashPassword("dummy", password); return null; }
            var user = users[i];
            if (user.LockedUntil > now) return null;
            var step = password.Length <= 256 && hasher.VerifyHashedPassword(user.Username, user.PasswordHash, password) != PasswordVerificationResult.Failed
                ? Totp.Match(user.Secret, code, now, user.LastStep) : null;
            if (step is null)
            {
                int failures = user.Failures + 1;
                users[i] = user with { Failures = failures, LockedUntil = failures >= 5 ? now.AddSeconds(Math.Min(900, 30 * Math.Pow(2, Math.Min(5, failures - 5)))) : null };
                Save(); return null;
            }
            users[i] = user with { LastStep = step.Value, Failures = 0, LockedUntil = null }; Save(); return users[i];
        }
    }
    public bool IsCurrent(ClaimsPrincipal principal)
    {
        lock (gate) return long.TryParse(principal.FindFirstValue("expires"), out var expiry) && expiry > DateTimeOffset.UtcNow.ToUnixTimeSeconds() && users.Any(x => x.Username == principal.Identity?.Name && x.Stamp == principal.FindFirstValue("stamp") && x.TenantId == principal.FindFirstValue("tenant"));
    }
}
