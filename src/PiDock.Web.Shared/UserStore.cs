using PiDock.Core;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
namespace PiDock.Web.Shared;

public sealed record LoginUser(string Username, string PasswordHash, string Secret, string TenantId, string Role,
    string Stamp, long LastStep = -1, int Failures = 0, DateTimeOffset? LockedUntil = null);
public sealed record Workspace(string Id, string Name);
public sealed record WorkspaceMembership(string Username, string WorkspaceId, string Role);
public sealed record WorkspaceAccess(string Id, string Name, string Role);
public sealed record TeamInvitation(string Id, string WorkspaceId, string Username, string Role, string TokenHash,
    DateTimeOffset ExpiresAt, string CreatedBy);
public sealed record WorkspaceAudit(DateTimeOffset At, string Actor, string WorkspaceId, string Action, string Target);
public sealed record AccountState(LoginUser[] Users, Workspace[] Workspaces, WorkspaceMembership[] Memberships,
    TeamInvitation[] Invitations, WorkspaceAudit[] Audit, bool? MultiTenantSignupEnabled = null);

public sealed class UserStore
{
    public static readonly IReadOnlyList<string> WorkspaceRoles = Array.AsReadOnly(new[] { "Administrator", "Operator", "Viewer", "Auditor" });
    private readonly object gate = new();
    private readonly string path;
    private readonly IDataProtector protector;
    private readonly PasswordHasher<string> hasher = new();
    private readonly bool cloud;
    private AccountState state = new([], [], [], [], []);
    public UserStore(IDataProtectionProvider provider, string directory, bool cloud = false)
    {
        this.cloud = cloud;
        path = Path.Combine(StateFiles.PrepareDirectory(directory), "accounts.protected");
        protector = provider.CreateProtector("PiDock.Accounts.v1");
        if (!File.Exists(path)) return;
        var json = protector.Unprotect(File.ReadAllText(path));
        if (json.TrimStart().StartsWith('['))
        {
            var users = JsonSerializer.Deserialize<LoginUser[]>(json, StateFiles.Json)!;
            state = new(users, users.Select(u => u.TenantId).Distinct().Select(id => new Workspace(id, id)).ToArray(),
                users.Where(u => u.Role != "Owner").Select(u => new WorkspaceMembership(u.Username, u.TenantId, u.Role)).ToArray(), [], []);
        }
        else state = JsonSerializer.Deserialize<AccountState>(json, StateFiles.Json)!;
        if (cloud && state.Users.Count(u => u.Role == "Owner") > 1)
            throw new InvalidOperationException("Multiple legacy cloud Owners exist. Resolve the accounts locally before starting: exactly one platform Owner is supported.");
    }
    private void Save(AccountState next)
    {
        StateFiles.WriteAtomic(path, protector.Protect(cloud ? JsonSerializer.Serialize(next, StateFiles.Json) : JsonSerializer.Serialize(next.Users, StateFiles.Json)));
        state = next;
    }
    public bool CloudAccounts => cloud;
    public bool HasUsers { get { lock (gate) return state.Users.Length > 0; } }
    public bool HasOwner { get { lock (gate) return state.Users.Any(u => u.Role == "Owner"); } }
    public bool MultiTenantSignupEnabled { get { lock (gate) return state.MultiTenantSignupEnabled != false; } }
    public void SetMultiTenantSignup(ClaimsPrincipal principal, bool enabled)
    {
        lock (gate)
        {
            var owner = RequireCurrent(principal);
            if (owner.Role != "Owner") throw new UnauthorizedAccessException("Only the platform Owner can change workspace signup settings.");
            Save(WithAudit(state with { MultiTenantSignupEnabled = enabled }, owner.Username, "", "Multi-tenant signup " + (enabled ? "enabled" : "disabled"), "Platform settings"));
        }
    }
    // A valid invitation authorizes account creation, never an additional workspace.
    public TeamInvitation? SignupInvitation(string token)
    {
        lock (gate) return cloud && HasOwner ? FindInvitation(token) is { } invite ? invite with { TokenHash = "" } : null : null;
    }
    private TeamInvitation? FindInvitation(string token) => token.Length == 64 && token.All(char.IsAsciiHexDigit)
        ? state.Invitations.SingleOrDefault(i => i.TokenHash == Hash(token) && i.ExpiresAt > DateTimeOffset.UtcNow) : null;
    public LoginUser RegisterInvitedAccount(string username, string password, string secret, string code, string token)
    {
        ValidateAccount(username, password);
        lock (gate)
        {
            if (!cloud || !HasOwner) throw new UnauthorizedAccessException("Account signup is unavailable until the platform Owner is established.");
            var invite = FindInvitation(token);
            if (invite is null || !Same(invite.Username, username)) throw new UnauthorizedAccessException("A valid invitation for this username is required.");
            if (state.Users.Any(u => Same(u.Username, username))) throw new InvalidOperationException("Account already exists. Sign in to accept your invitation.");
            var step = Totp.Match(secret, code, DateTimeOffset.UtcNow) ?? throw new ArgumentException("Enter the current code from your authenticator.");
            var user = new LoginUser(username, hasher.HashPassword(username, password), secret, invite.WorkspaceId, invite.Role, Guid.NewGuid().ToString(), step);
            Save(WithAudit(state with { Users = [.. state.Users, user], Memberships = [.. state.Memberships, new(username, invite.WorkspaceId, invite.Role)],
                Invitations = state.Invitations.Where(i => i.Id != invite.Id).ToArray() }, username, invite.WorkspaceId, "Invited account created", username));
            return user;
        }
    }
    // Trusted console provisioning only. Public signup always uses RegisterWorkspace.
    public void Add(string name, string password, string secret, string tenant, string role, bool singleOwner, long lastStep)
    {
        ValidateAccount(name, password);
        if (!IdentityRules.IsId(tenant) || (role != "Owner" && !WorkspaceRoles.Contains(role))) throw new ArgumentException("Invalid tenant or role.");
        lock (gate)
        {
            if ((singleOwner && HasUsers) || state.Users.Any(x => Same(x.Username, name)) || (cloud && role == "Owner" && HasOwner))
                throw new InvalidOperationException("Account or platform Owner already exists.");
            var user = new LoginUser(name, hasher.HashPassword(name, password), secret, tenant, role, Guid.NewGuid().ToString(), lastStep);
            var workspaces = (cloud && role == "Owner") || state.Workspaces.Any(w => w.Id == tenant) ? state.Workspaces : [.. state.Workspaces, new Workspace(tenant, tenant)];
            Save(state with { Users = [.. state.Users, user], Workspaces = workspaces,
                Memberships = role == "Owner" ? state.Memberships : [.. state.Memberships, new(name, tenant, role)] });
        }
    }
    private static void ValidateAccount(string name, string password)
    {
        if (!IdentityRules.IsId(name) || name.Length > 64 || password.Length is < 14 or > 256)
            throw new ArgumentException("Use a username with ASCII letters, digits or hyphens (up to 64 characters), and a 14–256 character password.");
    }
    private static string WorkspaceName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("Workspace names must have 1–80 characters.");
        return name;
    }
    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    public LoginUser RegisterWorkspace(string username, string password, string secret, string code, string workspaceName)
    {
        ValidateAccount(username, password); workspaceName = WorkspaceName(workspaceName);
        lock (gate)
        {
            if (!cloud || !HasOwner) throw new InvalidOperationException("Workspace signup is unavailable until the platform Owner is established.");
            if (!MultiTenantSignupEnabled) throw new UnauthorizedAccessException("Public workspace signup is disabled. Use a team invitation to create an account.");
            if (state.Users.Any(u => Same(u.Username, username))) throw new InvalidOperationException("Account could not be created. Choose another username or sign in.");
            var step = Totp.Match(secret, code, DateTimeOffset.UtcNow) ?? throw new ArgumentException("Enter the current code from your authenticator.");
            var workspace = new Workspace("ws-" + Guid.NewGuid().ToString("N"), workspaceName);
            var user = new LoginUser(username, hasher.HashPassword(username, password), secret, workspace.Id, "Administrator", Guid.NewGuid().ToString(), step);
            Save(WithAudit(state with { Users = [.. state.Users, user], Workspaces = [.. state.Workspaces, workspace],
                Memberships = [.. state.Memberships, new(username, workspace.Id, "Administrator")] }, username, workspace.Id, "Workspace created", workspace.Id));
            return user;
        }
    }
    public LoginUser? Authenticate(string username, string password, string code, DateTimeOffset now)
    {
        lock (gate)
        {
            int i = Array.FindIndex(state.Users, x => Same(x.Username, username));
            if (i < 0) { _ = hasher.HashPassword("dummy", password); return null; }
            var user = state.Users[i];
            if (user.LockedUntil > now) return null;
            var step = password.Length <= 256 && hasher.VerifyHashedPassword(user.Username, user.PasswordHash, password) != PasswordVerificationResult.Failed
                ? Totp.Match(user.Secret, code, now, user.LastStep) : null;
            var users = state.Users.ToArray();
            if (step is null)
            {
                int failures = user.Failures + 1;
                users[i] = user with { Failures = failures, LockedUntil = failures >= 5 ? now.AddSeconds(Math.Min(900, 30 * Math.Pow(2, Math.Min(5, failures - 5)))) : null };
                Save(state with { Users = users }); return null;
            }
            users[i] = user with { LastStep = step.Value, Failures = 0, LockedUntil = null };
            Save(state with { Users = users }); return users[i];
        }
    }
    public ClaimsPrincipal Principal(LoginUser user, string? workspaceId = null)
    {
        lock (gate)
        {
            // Never trust a caller's cached account record after membership or session changes.
            user = state.Users.Single(u => u.Username == user.Username && u.Stamp == user.Stamp);
            workspaceId ??= cloud && user.Role == "Owner" ? "" : user.TenantId;
            string role = user.Role;
            if (cloud && role != "Owner") role = workspaceId == "" && !state.Memberships.Any(m => m.Username == user.Username) ? "Viewer" : state.Memberships.Single(m => m.Username == user.Username && m.WorkspaceId == workspaceId).Role;
            if (cloud && workspaceId.Length > 0 && !state.Workspaces.Any(w => w.Id == workspaceId)) throw new UnauthorizedAccessException();
            return new(new ClaimsIdentity([new(ClaimTypes.Name, user.Username), new(ClaimTypes.Role, role), new("tenant", workspaceId),
                new("stamp", user.Stamp), new("expires", DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))], "Cookies"));
        }
    }
    public bool IsCurrent(ClaimsPrincipal principal)
    {
        lock (gate)
        {
            if (principal.Identity?.IsAuthenticated != true || !long.TryParse(principal.FindFirstValue("expires"), out var expiry) || expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
            var user = state.Users.SingleOrDefault(u => u.Username == principal.Identity.Name && u.Stamp == principal.FindFirstValue("stamp"));
            if (user is null) return false;
            var tenant = principal.FindFirstValue("tenant"); var role = principal.FindFirstValue(ClaimTypes.Role);
            if (!cloud) return user.TenantId == tenant && user.Role == role;
            if (user.Role == "Owner") return role == "Owner" && (tenant == "" || state.Workspaces.Any(w => w.Id == tenant));
            return (tenant == "" && role == "Viewer" && !state.Memberships.Any(m => m.Username == user.Username)) || state.Memberships.Any(m => m.Username == user.Username && m.WorkspaceId == tenant && m.Role == role);
        }
    }
    private LoginUser RequireCurrent(ClaimsPrincipal principal)
    {
        if (!cloud || !IsCurrent(principal)) throw new UnauthorizedAccessException("Sign in again to continue.");
        return state.Users.Single(u => u.Username == principal.Identity!.Name);
    }
    private LoginUser RequireAdmin(ClaimsPrincipal principal)
    {
        var user = RequireCurrent(principal);
        if (!IdentityRules.IsId(principal.FindFirstValue("tenant")) || !(principal.IsInRole("Owner") || principal.IsInRole("Administrator")))
            throw new UnauthorizedAccessException("Workspace Administrator access is required.");
        return user;
    }
    public WorkspaceAccess[] WorkspacesFor(ClaimsPrincipal principal)
    {
        lock (gate)
        {
            var user = RequireCurrent(principal);
            return state.Workspaces.Where(w => user.Role == "Owner" || state.Memberships.Any(m => m.Username == user.Username && m.WorkspaceId == w.Id))
                .Select(w => new WorkspaceAccess(w.Id, w.Name, user.Role == "Owner" ? "Owner support" : state.Memberships.Single(m => m.Username == user.Username && m.WorkspaceId == w.Id).Role)).ToArray();
        }
    }
    public ClaimsPrincipal Switch(ClaimsPrincipal principal, string workspaceId)
    {
        lock (gate)
        {
            var user = RequireCurrent(principal);
            if (!(workspaceId == "" && user.Role == "Owner") && !WorkspacesFor(principal).Any(w => w.Id == workspaceId)) throw new UnauthorizedAccessException("Workspace unavailable.");
            // Invalidate existing cookies and Blazor circuits before issuing the new workspace context.
            var updated = user with { Stamp = Guid.NewGuid().ToString(), TenantId = workspaceId.Length > 0 ? workspaceId : user.TenantId };
            Save(WithAudit(state with { Users = state.Users.Select(u => u.Username == user.Username ? updated : u).ToArray() }, user.Username, workspaceId,
                user.Role == "Owner" ? "Owner support context changed" : "Workspace switched", workspaceId));
            return Principal(updated, workspaceId);
        }
    }
    public ClaimsPrincipal CreateWorkspace(ClaimsPrincipal principal, string name)
    {
        name = WorkspaceName(name);
        lock (gate)
        {
            var user = RequireCurrent(principal);
            if (!MultiTenantSignupEnabled && user.Role != "Owner") throw new UnauthorizedAccessException("Only the platform Owner can create workspaces while multi-tenant signup is disabled.");
            var workspace = new Workspace("ws-" + Guid.NewGuid().ToString("N"), name);
            var updated = user with { Stamp = Guid.NewGuid().ToString(), TenantId = workspace.Id };
            Save(WithAudit(state with { Users = state.Users.Select(u => u.Username == user.Username ? updated : u).ToArray(),
                Workspaces = [.. state.Workspaces, workspace], Memberships = [.. state.Memberships, new(user.Username, workspace.Id, "Administrator")] }, user.Username, workspace.Id, "Workspace created", workspace.Id));
            return Principal(updated, workspace.Id);
        }
    }
    public WorkspaceMembership[] Members(ClaimsPrincipal principal)
    {
        lock (gate) { RequireAdmin(principal); return state.Memberships.Where(m => m.WorkspaceId == principal.FindFirstValue("tenant")).ToArray(); }
    }
    public TeamInvitation[] Invitations(ClaimsPrincipal principal)
    {
        lock (gate) { RequireAdmin(principal); return state.Invitations.Where(i => i.WorkspaceId == principal.FindFirstValue("tenant") && i.ExpiresAt > DateTimeOffset.UtcNow).Select(i => i with { TokenHash = "" }).ToArray(); }
    }
    public (string Token, TeamInvitation Invitation) Invite(ClaimsPrincipal principal, string username, string role)
    {
        if (!IdentityRules.IsId(username) || username.Length > 64 || !WorkspaceRoles.Contains(role)) throw new ArgumentException("Enter a valid username and workspace role.");
        lock (gate)
        {
            var user = RequireAdmin(principal); var tenant = principal.FindFirstValue("tenant")!;
            if (state.Memberships.Any(m => m.WorkspaceId == tenant && Same(m.Username, username))) throw new InvalidOperationException("This user is already a member.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var invitation = new TeamInvitation(Guid.NewGuid().ToString("N"), tenant, username, role, Hash(token), DateTimeOffset.UtcNow.AddDays(7), user.Username);
            var invitations = state.Invitations.Where(i => i.ExpiresAt > DateTimeOffset.UtcNow && !(i.WorkspaceId == tenant && Same(i.Username, username))).ToArray();
            if (invitations.Count(i => i.WorkspaceId == tenant) >= 100) throw new InvalidOperationException("Cancel unused invitations before creating more.");
            Save(WithAudit(state with { Invitations = [.. invitations, invitation] }, user.Username, tenant, "Member invited", username));
            return (token, invitation with { TokenHash = "" });
        }
    }
    public ClaimsPrincipal Accept(ClaimsPrincipal principal, string token)
    {
        lock (gate)
        {
            var user = RequireCurrent(principal); string hash = Hash(token);
            var invite = state.Invitations.SingleOrDefault(i => i.TokenHash == hash && i.ExpiresAt > DateTimeOffset.UtcNow && Same(i.Username, user.Username))
                ?? throw new ArgumentException("Invitation is expired, cancelled, already used, or belongs to a different username.");
            if (state.Memberships.Any(m => m.Username == user.Username && m.WorkspaceId == invite.WorkspaceId)) throw new InvalidOperationException("Already a member.");
            var updated = user with { Stamp = Guid.NewGuid().ToString(), TenantId = invite.WorkspaceId };
            Save(WithAudit(state with { Users = state.Users.Select(u => u.Username == user.Username ? updated : u).ToArray(),
                Memberships = [.. state.Memberships, new(user.Username, invite.WorkspaceId, invite.Role)], Invitations = state.Invitations.Where(i => i.Id != invite.Id).ToArray() },
                user.Username, invite.WorkspaceId, "Invitation accepted", user.Username));
            return Principal(updated, invite.WorkspaceId);
        }
    }
    public void CancelInvitation(ClaimsPrincipal principal, string id)
    {
        lock (gate)
        {
            var user = RequireAdmin(principal); var tenant = principal.FindFirstValue("tenant")!;
            if (!state.Invitations.Any(i => i.Id == id && i.WorkspaceId == tenant)) throw new ArgumentException("Invitation unavailable.");
            Save(WithAudit(state with { Invitations = state.Invitations.Where(i => i.Id != id).ToArray() }, user.Username, tenant, "Invitation cancelled", id));
        }
    }
    public void SetMemberRole(ClaimsPrincipal principal, string username, string? role)
    {
        if (role is not null && !WorkspaceRoles.Contains(role)) throw new ArgumentException("Unsupported workspace role.");
        lock (gate)
        {
            var actor = RequireAdmin(principal); var tenant = principal.FindFirstValue("tenant")!;
            var member = state.Memberships.SingleOrDefault(m => Same(m.Username, username) && m.WorkspaceId == tenant) ?? throw new ArgumentException("Member unavailable.");
            if (member.Role == "Administrator" && role != "Administrator" && state.Memberships.Count(m => m.WorkspaceId == tenant && m.Role == "Administrator") <= 1)
                throw new InvalidOperationException("A workspace must retain at least one Administrator.");
            var memberships = state.Memberships.Where(m => m != member).ToArray();
            if (role is not null) memberships = [.. memberships, member with { Role = role }];
            var fallback = memberships.FirstOrDefault(m => m.Username == member.Username)?.WorkspaceId ?? "";
            Save(WithAudit(state with { Memberships = memberships,
                Invitations = role == "Administrator" ? state.Invitations : state.Invitations.Where(i => !(i.WorkspaceId == tenant && Same(i.CreatedBy, member.Username))).ToArray(),
                Users = state.Users.Select(u => u.Username == member.Username
                ? u with { Stamp = Guid.NewGuid().ToString(), TenantId = u.TenantId == tenant && role is null ? fallback : u.TenantId } : u).ToArray() }, actor.Username, tenant, role is null ? "Member removed" : "Member role changed to " + role, member.Username));
        }
    }
    public WorkspaceAudit[] AuditFor(ClaimsPrincipal principal)
    {
        lock (gate)
        {
            var user = RequireCurrent(principal);
            if (user.Role != "Owner" && !principal.IsInRole("Auditor")) RequireAdmin(principal);
            return state.Audit.Where(a => (user.Role == "Owner" && principal.FindFirstValue("tenant") == "") || a.WorkspaceId == principal.FindFirstValue("tenant")).TakeLast(100).Reverse().ToArray();
        }
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static AccountState WithAudit(AccountState next, string actor, string tenant, string action, string target) =>
        next with { Audit = [.. next.Audit.TakeLast(9999), new(DateTimeOffset.UtcNow, actor, tenant, action, target)] };
}
