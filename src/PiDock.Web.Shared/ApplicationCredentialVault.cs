using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using PiDock.Core;

namespace PiDock.Web.Shared;

// Only server-side consumers can resolve material. The management UI receives metadata exclusively.
public sealed class ApplicationCredentialVault : IApplicationCredentialSource
{
    private readonly object gate = new();
    private readonly string path;
    private readonly IDataProtector protector;
    private readonly UserStore users;
    private readonly TimeProvider clock;
    private ApplicationCredentialFile[] entries;
    private readonly ILogger<ApplicationCredentialVault>? logger;
    private static IReadOnlyList<string> AllowedScopes => ApplicationResourceRules.PlatformScopes;
    public ApplicationCredentialVault(IDataProtectionProvider protection, StateDirectory directory, UserStore users, TimeProvider clock, ILogger<ApplicationCredentialVault>? logger = null)
    {
        this.users = users; this.clock = clock; this.logger = logger;
        path = Path.Combine(StateFiles.PrepareDirectory(directory.Path), "app-credentials.protected");
        protector = protection.CreateProtector("PiDock.ApplicationCredentials.v1");
        if (File.Exists(path) && (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > 262144)) throw new IOException("Invalid credential store.");
        entries = File.Exists(path) ? JsonSerializer.Deserialize<ApplicationCredentialFile[]>(protector.Unprotect(File.ReadAllText(path)), StateFiles.Json) ?? throw new IOException("Invalid credential store.") : [];
        if (entries.Length > 128) throw new IOException("Credential store exceeds capacity.");
    }
    public ApplicationCredentialInfo[] Read() { lock (gate) return entries.Select(c => new ApplicationCredentialInfo(c.AppId,c.Id,c.Version,c.Purpose,c.ExpiresAt,c.Token.Length == 0,c.Scopes.ToArray())).ToArray(); }
    private void Confirm(ClaimsPrincipal owner, string password, string code)
    {
        if (!users.IsCurrent(owner) || !owner.IsInRole("Owner") || users.Authenticate(owner.Identity!.Name!, password, code, clock.GetUtcNow()) is not { Role: "Owner" })
            throw new UnauthorizedAccessException("Current owner credentials and a fresh authenticator code are required.");
    }
    public void Issue(ClaimsPrincipal owner, string password, string code, string app, string id, string purpose, DateTimeOffset expires, string[] scopes)
    {
        if (!IdentityRules.IsId(app) || app == "pidockos" || app.Length > 40 || !ApplicationResourceRules.CredentialId(id) || purpose is not ("ui-bridge" or "platform") ||
            expires <= clock.GetUtcNow() || expires > clock.GetUtcNow().AddDays(90) || scopes is not { Length: <= 8 } || scopes.Distinct().Count() != scopes.Length ||
            scopes.Any(s => !AllowedScopes.Contains(s, StringComparer.Ordinal)) || (purpose == "ui-bridge" && scopes.Length != 0) || (purpose == "platform" && scopes.Length == 0))
            throw new ArgumentException("Use a named app credential, a supported purpose and scopes, and an expiry within 90 days.");
        lock (gate)
        {
            Confirm(owner,password,code);
            var previous = entries.SingleOrDefault(c => c.AppId == app && c.Id == id);
            if (previous is null && entries.Length >= 128) throw new InvalidOperationException("Credential capacity reached.");
            var next = new ApplicationCredentialFile(1, app,id,checked((previous?.Version ?? 0)+1),purpose,Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),expires,scopes.ToArray());
            Save(entries.Where(c => c.AppId != app || c.Id != id).Append(next).ToArray());
            logger?.LogInformation("Owner {Owner} issued app credential {AppId}/{CredentialId} version {Version} purpose {Purpose}",owner.Identity?.Name,app,id,next.Version,purpose);
        }
    }
    public void Revoke(ClaimsPrincipal owner,string password,string code,string app,string id)
    {
        lock (gate)
        {
            Confirm(owner,password,code);
            var previous = entries.SingleOrDefault(c => c.AppId == app && c.Id == id) ?? throw new ArgumentException("Unknown app credential.");
            Save(entries.Select(c => c == previous ? c with { Token = "", Version = checked(c.Version + 1) } : c).ToArray());
            logger?.LogInformation("Owner {Owner} revoked app credential {AppId}/{CredentialId}",owner.Identity?.Name,app,id);
        }
    }
    private void Save(ApplicationCredentialFile[] next)
    { StateFiles.WriteAtomic(path,protector.Protect(JsonSerializer.Serialize(next,StateFiles.Json))); entries = next; }
    public ApplicationCredentialResolution Resolve(string appId,string id)
    {
        lock(gate)
        {
            var record = entries.SingleOrDefault(c => c.AppId == appId && c.Id == id);
            return new(record is not null,record is not null && ApplicationResourceRules.Credential(record,appId,clock.GetUtcNow()) ? record with { Scopes = record.Scopes.ToArray() } : null);
        }
    }
    public ApplicationCredentialFile? Authenticate(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        lock(gate)
        {
            var matches = entries.Where(c => c.Purpose == "platform" && ApplicationResourceRules.Credential(c,c.AppId,clock.GetUtcNow()) &&
                CryptographicOperations.FixedTimeEquals(hash,SHA256.HashData(Encoding.UTF8.GetBytes(c.Token)))).ToArray();
            return matches.Length == 1 ? matches[0] with { Scopes = matches[0].Scopes.ToArray() } : null;
        }
    }
}
