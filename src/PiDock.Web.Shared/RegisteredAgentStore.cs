using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Core;

namespace PiDock.Web.Shared;

public sealed record RegisteredAgentSummary(Guid Id, string Name, string DiscoveryUrl, string[] Scopes,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
public sealed record AgentConnectionDetails(RegisteredAgentSummary Application, string Token, string Prompt);

public sealed class RegisteredAgentStore
{
    private sealed record Registration(Guid Id, string Name, string Origin, string Token, string[] Scopes,
        string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt = null);
    private readonly object gate = new();
    private readonly string path;
    private readonly IDataProtector protector;
    private readonly UserStore users;
    private readonly TimeProvider clock;
    private Registration[] entries;
    public bool Enabled { get; }
    public RegisteredAgentStore(IDataProtectionProvider provider, StateDirectory state, UserStore users, TimeProvider clock, bool enabled = true)
    {
        this.users = users; this.clock = clock; Enabled = enabled;
        path = Path.Combine(StateFiles.PrepareDirectory(state.Path), "registered-agents.protected");
        protector = provider.CreateProtector("PiDock.RegisteredAgents.v1");
        entries = File.Exists(path) ? JsonSerializer.Deserialize<Registration[]>(protector.Unprotect(File.ReadAllText(path)), StateFiles.Json)
            ?? throw new CryptographicException("Agent registration state is invalid.") : [];
    }
    private void RequireOwner(ClaimsPrincipal user)
    {
        if (!users.IsCurrent(user) || !user.IsInRole("Owner")) throw new UnauthorizedAccessException("A current appliance owner session is required.");
    }
    private void Confirm(ClaimsPrincipal user, string password, string code)
    {
        RequireOwner(user);
        if (!Enabled || users.Authenticate(user.Identity!.Name!, password, code, clock.GetUtcNow()) is null)
            throw new UnauthorizedAccessException("Confirm with the owner password and a fresh authenticator code.");
    }
    public RegisteredAgentSummary[] List(ClaimsPrincipal user)
    {
        RequireOwner(user);
        lock (gate) return entries.OrderByDescending(e => e.CreatedAt).Select(Summary).ToArray();
    }
    public AgentConnectionDetails Create(ClaimsPrincipal owner, string password, string code, string name, string origin, int lifetimeDays, bool systemRead = false)
    {
        name = name.Trim();
        if (!ApplicationContractRules.Text(name, 80) || name.Any(char.IsControl) || lifetimeDays is not (1 or 7 or 30 or 90)) throw new ArgumentException("Enter a name of 1–80 characters and a supported lifetime.");
        origin = ValidateOrigin(origin);
        lock (gate)
        {
            Confirm(owner, password, code);
            if (entries.Count(e => e.RevokedAt is null && e.ExpiresAt > clock.GetUtcNow()) >= 96) throw new InvalidOperationException("Revoke an unused registration before adding another.");
            if (entries.Any(e => e.RevokedAt is null && e.ExpiresAt > clock.GetUtcNow() && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("An active application already uses that name.");
            var now = clock.GetUtcNow();
            var entry = new Registration(Guid.NewGuid(), name, origin, NewToken(), systemRead ? ["platform.discovery.read", "system.read"] : ["platform.discovery.read"],
                owner.Identity!.Name!, now, now.AddDays(lifetimeDays));
            var retained = entries.OrderByDescending(e => e.RevokedAt is null && e.ExpiresAt > now).ThenByDescending(e => e.CreatedAt).Take(127);
            Save(retained.Append(entry).ToArray()); return Details(entry);
        }
    }
    public AgentConnectionDetails Reveal(ClaimsPrincipal owner, string password, string code, Guid id)
    {
        lock (gate) { Confirm(owner, password, code); return Details(FindActive(id)); }
    }
    public AgentConnectionDetails Rotate(ClaimsPrincipal owner, string password, string code, Guid id, int lifetimeDays)
    {
        if (lifetimeDays is not (1 or 7 or 30 or 90)) throw new ArgumentException("Choose a supported lifetime.");
        lock (gate)
        {
            Confirm(owner, password, code); var entry = FindActive(id);
            var changed = entry with { Token = NewToken(), ExpiresAt = clock.GetUtcNow().AddDays(lifetimeDays) };
            Save(entries.Select(e => e.Id == id ? changed : e).ToArray()); return Details(changed);
        }
    }
    public void Revoke(ClaimsPrincipal owner, string password, string code, Guid id)
    {
        lock (gate)
        {
            Confirm(owner, password, code);
            var entry = entries.FirstOrDefault(e => e.Id == id) ?? throw new ArgumentException("Unknown registration.");
            if (entry.RevokedAt is not null) return;
            Save(entries.Select(e => e.Id == id ? e with { RevokedAt = clock.GetUtcNow(), Token = "" } : e).ToArray());
        }
    }
    public ServiceCredential? Authenticate(string token)
    {
        if (!Enabled || token is not { Length: 64 } || !token.All(char.IsAsciiHexDigit)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        lock (gate)
        {
            var match = entries.FirstOrDefault(e => e.RevokedAt is null && e.ExpiresAt > clock.GetUtcNow() && e.Token.Length == 64 &&
                CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(Encoding.UTF8.GetBytes(e.Token))));
            if (match is null) return null;
            var identity = "agent-" + match.Id.ToString("N");
            return new(identity, Convert.ToHexString(hash), match.ExpiresAt, identity, match.Scopes.ToArray());
        }
    }
    private Registration FindActive(Guid id) => entries.FirstOrDefault(e => e.Id == id && e.RevokedAt is null && e.ExpiresAt > clock.GetUtcNow())
        ?? throw new ArgumentException("Registration is expired, revoked or unavailable. Create a new registration if necessary.");
    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string ValidateOrigin(string origin)
    {
        if (origin.Length > 512 || !Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("Enter the appliance HTTPS address without a path, credentials, query or fragment.");
        return uri.AbsoluteUri;
    }
    private static RegisteredAgentSummary Summary(Registration e) => new(e.Id, e.Name, new Uri(new Uri(e.Origin), PlatformContracts.ApiRoot + "/discovery").AbsoluteUri,
        e.Scopes.ToArray(), e.CreatedAt, e.ExpiresAt, e.RevokedAt);
    private static AgentConnectionDetails Details(Registration entry)
    {
        var summary = Summary(entry);
        var prompt = $"""
            Connect to the PiDockOS application platform for {entry.Name}.
            Appliance URL: {entry.Origin}
            Discovery URL: {summary.DiscoveryUrl}
            Bearer token: {entry.Token}
            Expires (UTC): {entry.ExpiresAt:O}
            Granted scopes: {string.Join(", ", entry.Scopes)}

            Fetch the discovery URL over HTTPS using the Authorization: Bearer header.
            Follow its versioned contracts and capability states. Never put the token in a URL,
            logs, source control, package metadata, or another message. Use normal TLS validation.
            This token does not authorize owner management, deployment or cloud publishing.

            For the PiDock MCP stdio bridge, launch the installed PiDock.Agent.Mcp executable
            with PIDOCK_APPLIANCE_URL set to the appliance URL and PIDOCK_DISCOVERY_TOKEN
            set to this token through your private environment configuration. The bridge uses
            the authenticated API; this discovery URL is not an HTTP MCP transport endpoint.
            Cloud publishing requires separate cloud credentials and authorization.
            """;
        return new(summary, entry.Token, prompt);
    }
    private void Save(Registration[] updated)
    {
        StateFiles.WriteAtomic(path, protector.Protect(JsonSerializer.Serialize(updated, StateFiles.Json))); entries = updated;
    }
}
