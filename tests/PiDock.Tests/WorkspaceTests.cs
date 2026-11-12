using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PiDock.Cloud.Web;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-workspaces-" + Guid.NewGuid());
    private const string Password = "integration-test-password";
    private IDataProtectionProvider Provider => DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
    private UserStore Store() => new(Provider, root, cloud: true);
    private static string Code(string secret) => Totp.Code(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
    private static LoginUser Register(UserStore users, string name, string workspace)
    {
        string secret = Totp.NewSecret(); return users.RegisterWorkspace(name, Password, secret, Code(secret), workspace);
    }
    private static ClaimsPrincipal Bootstrap(UserStore users)
    {
        var secret = Totp.NewSecret(); users.Add("owner", Password, secret, "local", "Owner", false, -1);
        return users.Principal(users.Authenticate("owner", Password, Code(secret), DateTimeOffset.UtcNow)!);
    }
    [Fact]
    public void OwnerMustExistBeforeSignupAndCannotBeDuplicated()
    {
        var users = Store(); var secret = Totp.NewSecret();
        Assert.Throws<InvalidOperationException>(() => Register(users, "alice", "Alice lab"));
        var owner = Bootstrap(users);
        Assert.True(users.HasOwner); Assert.Equal("", owner.FindFirstValue("tenant")); Assert.Empty(users.WorkspacesFor(owner));
        Assert.Throws<InvalidOperationException>(() => users.Add("other-owner", Password, secret, "local", "Owner", false, -1));
        var alice = Register(users, "alice", "Alice lab");
        Assert.Equal("Administrator", users.Principal(alice).FindFirstValue(ClaimTypes.Role));
        Assert.Throws<InvalidOperationException>(() => Register(users, "ALICE", "Duplicate account"));
        Assert.Null(users.Authenticate("alice", Password, Code(alice.Secret), DateTimeOffset.UtcNow)); // signup code cannot be replayed
    }
    [Fact]
    public void MembershipRolesSwitchAndRevocationPersistWithoutMovingDevices()
    {
        var users = Store(); Bootstrap(users);
        var alice = Register(users, "alice", "Alice lab"); var bob = Register(users, "bob", "Bob lab");
        var a = users.Principal(alice); var b = users.Principal(bob);
        var invite = users.Invite(b, "alice", "Viewer");
        var selected = users.Accept(a, invite.Token);
        Assert.False(users.IsCurrent(a)); Assert.True(users.IsCurrent(selected));
        Assert.Equal(bob.TenantId, selected.FindFirstValue("tenant")); Assert.True(selected.IsInRole("Viewer"));
        Assert.Equal(2, users.WorkspacesFor(selected).Length);
        Assert.Throws<UnauthorizedAccessException>(() => users.Invite(selected, "other", "Administrator"));
        var own = users.Switch(selected, alice.TenantId);
        Assert.True(own.IsInRole("Administrator")); Assert.False(users.IsCurrent(selected));
        Assert.Throws<ArgumentException>(() => users.Accept(own, invite.Token));
        var fleet = new FleetStore(new(root), users);
        var now = DateTimeOffset.UtcNow; var storage = DataPolicyCalculator.Evaluate(new(), new(32L << 30, 16L << 30, 0));
        var snapshot = new HostSnapshot(now, "bob-pi", "Linux", 10, 1024, 512, 30, 10, storage, [], "Test");
        fleet.Accept(IdentityRules.Topic(bob.TenantId, "pi"), new(1, bob.TenantId, "pi", Guid.NewGuid(), Guid.NewGuid(), 1, snapshot));
        Assert.Empty(fleet.For(own));
        var bobView = users.Switch(own, bob.TenantId); Assert.Single(fleet.For(bobView));
        users.SetMemberRole(b, "alice", null);
        Assert.False(users.IsCurrent(bobView)); Assert.Empty(fleet.For(bobView)); Assert.Single(fleet.For(b));
        var reopened = Store(); Assert.False(reopened.IsCurrent(bobView)); Assert.Single(reopened.WorkspacesFor(b));
        Assert.Equal(2, reopened.AuditFor(b).Count(audit => audit.Action is "Member invited" or "Member removed"));
    }
    [Fact]
    public void InvitationIsSingleUseUsernameBoundCancellableAndCannotGrantOwner()
    {
        var users = Store(); Bootstrap(users);
        var alice = Register(users, "alice", "Alice"); var bob = Register(users, "bob", "Bob"); var eve = Register(users, "eve", "Eve");
        var a = users.Principal(alice); var b = users.Principal(bob); var e = users.Principal(eve);
        Assert.Throws<ArgumentException>(() => users.Invite(a, "bob", "Owner"));
        var invite = users.Invite(a, "bob", "Operator");
        Assert.Throws<ArgumentException>(() => users.Accept(e, invite.Token));
        Assert.Throws<ArgumentException>(() => users.CancelInvitation(b, invite.Invitation.Id));
        users.CancelInvitation(a, invite.Invitation.Id);
        Assert.Throws<ArgumentException>(() => users.Accept(b, invite.Token));
        var second = users.Invite(a, "bob", "Operator");
        var joined = users.Accept(b, second.Token); Assert.True(joined.IsInRole("Operator"));
        Assert.Throws<ArgumentException>(() => users.Accept(joined, second.Token));
        Assert.DoesNotContain(second.Token, File.ReadAllText(Path.Combine(root, "accounts.protected")));
    }
    [Fact]
    public void RoleChangesInvalidateSessionsAndLastAdministratorCannotBeRemoved()
    {
        var users = Store(); Bootstrap(users);
        var alice = Register(users, "alice", "Alice"); var bob = Register(users, "bob", "Bob");
        var a = users.Principal(alice); var b = users.Principal(bob);
        Assert.Throws<InvalidOperationException>(() => users.SetMemberRole(a, "alice", "Viewer"));
        Assert.Throws<InvalidOperationException>(() => users.SetMemberRole(a, "alice", null));
        var joined = users.Accept(b, users.Invite(a, "bob", "Administrator").Token);
        var delegated = users.Invite(joined, "carol", "Administrator");
        users.SetMemberRole(a, "bob", "Auditor"); Assert.False(users.IsCurrent(joined));
        Assert.DoesNotContain(users.Invitations(a), i => i.Id == delegated.Invitation.Id);
        Assert.Throws<UnauthorizedAccessException>(() => users.Switch(joined, alice.TenantId));
        Assert.Throws<ArgumentException>(() => users.SetMemberRole(a, "alice", "Owner"));
    }
    [Fact]
    public void OwnerSelectsAnyWorkspaceExplicitlyAndSupportAccessIsAudited()
    {
        var users = Store(); var owner = Bootstrap(users); var alice = Register(users, "alice", "Alice");
        Assert.Single(users.WorkspacesFor(owner));
        var support = users.Switch(owner, alice.TenantId);
        Assert.True(support.IsInRole("Owner")); Assert.False(users.IsCurrent(owner)); Assert.Single(users.Members(support));
        var platform = users.Switch(support, ""); Assert.Equal("", platform.FindFirstValue("tenant"));
        Assert.Equal(2, users.AuditFor(platform).Count(a => a.Action == "Owner support context changed"));
        Assert.Throws<UnauthorizedAccessException>(() => users.Switch(users.Principal(alice), "guessed-id"));
    }
    [Fact]
    public void LegacyAccountsMigrateWithTheirTenantIdsAndMultipleOwnersFailClosed()
    {
        StateFiles.PrepareDirectory(root);
        var legacy = new[] { new LoginUser("owner", "hash", "secret", "alpha", "Owner", "one"), new LoginUser("alice", "hash", "secret", "alpha", "Administrator", "two") };
        void Write(LoginUser[] users) => StateFiles.WriteAtomic(Path.Combine(root, "accounts.protected"), Provider.CreateProtector("PiDock.Accounts.v1").Protect(JsonSerializer.Serialize(users, StateFiles.Json)));
        Write(legacy); var migrated = Store(); Assert.True(migrated.MultiTenantSignupEnabled); var a = migrated.Principal(legacy[1]);
        Assert.Equal("alpha", Assert.Single(migrated.WorkspacesFor(a)).Id); Assert.True(a.IsInRole("Administrator"));
        var selected = migrated.CreateWorkspace(a, "New workspace"); Assert.Equal(2, Store().WorkspacesFor(selected).Length);
        Write([.. legacy, new("second-owner", "hash", "secret", "beta", "Owner", "three")]);
        Assert.Throws<InvalidOperationException>(() => Store());
    }
    [Fact]
    public void ExpiredInvitationAndInvalidSignupMfaCannotCreateMemberships()
    {
        var users = Store(); Bootstrap(users); var alice = Register(users, "alice", "Alice"); var bob = Register(users, "bob", "Bob");
        string secret = Totp.NewSecret();
        Assert.Throws<ArgumentException>(() => users.RegisterWorkspace("invalid", Password, secret, "bad", "Invalid"));
        var invitation = users.Invite(users.Principal(alice), "bob", "Viewer");
        var protector = Provider.CreateProtector("PiDock.Accounts.v1");
        var path = Path.Combine(root, "accounts.protected");
        var state = JsonSerializer.Deserialize<AccountState>(protector.Unprotect(File.ReadAllText(path)), StateFiles.Json)!;
        StateFiles.WriteAtomic(path, protector.Protect(JsonSerializer.Serialize(state with { Invitations = state.Invitations.Select(i => i with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }).ToArray() }, StateFiles.Json)));
        Assert.Throws<ArgumentException>(() => Store().Accept(users.Principal(bob), invitation.Token));
    }
    [Fact]
    public async Task HttpSignupGateMfaCsrfAndTeamAuthorizationWorkTogether()
    {
        using var factory = new WorkspaceFactory(); using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/auth/signup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/auth/signup", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Contains("signup is locked", await client.GetStringAsync("/auth/login"));
        var users = factory.Services.GetRequiredService<UserStore>(); Bootstrap(users);
        var signup = await client.GetStringAsync("/auth/signup");
        var setup = Field(signup, "setup"); var csrf = Field(signup, "__RequestVerificationToken");
        var secret = Regex.Match(signup, "class=\"setup-key\">([^<]+)").Groups[1].Value;
        Assert.NotEmpty(secret);
        var form = new Dictionary<string, string> { ["setup"] = setup, ["username"] = "alice", ["password"] = Password, ["confirmPassword"] = Password,
            ["workspace"] = "Alice team", ["code"] = Code(secret) };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/signup", new FormUrlEncodedContent(form))).StatusCode);
        form["__RequestVerificationToken"] = csrf;
        var created = await client.PostAsync("/auth/signup", new FormUrlEncodedContent(form)); Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal("/", created.Headers.Location!.OriginalString);
        var home = await client.GetStringAsync("/"); Assert.Contains("workspace-selector", home); Assert.Contains("Alice team", home);
        var workspaces = await client.GetAsync("/workspaces"); Assert.True(workspaces.Headers.CacheControl!.NoStore);
        var team = await workspaces.Content.ReadAsStringAsync(); Assert.Contains("Team members", team);
        var post = new Dictionary<string, string> { ["workspace"] = "forged" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/workspaces/switch", new FormUrlEncodedContent(post))).StatusCode);
        post["__RequestVerificationToken"] = Field(team, "__RequestVerificationToken");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/workspaces/switch", new FormUrlEncodedContent(post))).StatusCode);
        post["username"] = "bob"; post["role"] = "Owner";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/workspaces/invite", new FormUrlEncodedContent(post))).StatusCode);
        post["role"] = "Viewer";
        var invited = await client.PostAsync("/workspaces/invite", new FormUrlEncodedContent(post));
        Assert.Equal(HttpStatusCode.OK, invited.StatusCode); Assert.Contains("/auth/join?token=", await invited.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task HttpInvitationSwitchAndRemovalKeepDeviceDataWithinTheSelectedWorkspace()
    {
        using var factory = new WorkspaceFactory(); using var aliceClient = factory.Client(); using var bobClient = factory.Client();
        var users = factory.Services.GetRequiredService<UserStore>(); Bootstrap(users);
        var aliceSecret = Totp.NewSecret(); var bobSecret = Totp.NewSecret();
        users.Add("alice", Password, aliceSecret, "alpha", "Administrator", false, -1);
        users.Add("bob", Password, bobSecret, "beta", "Administrator", false, -1);
        async Task Login(HttpClient client, string name, string secret)
        {
            var login = await client.GetStringAsync("/auth/login");
            var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> {
                ["username"] = name, ["password"] = Password, ["code"] = Code(secret), ["__RequestVerificationToken"] = Field(login, "__RequestVerificationToken") }));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }
        async Task<HttpResponseMessage> Post(HttpClient client, string action, Dictionary<string, string> form)
        {
            form["__RequestVerificationToken"] = Field(await client.GetStringAsync("/workspaces"), "__RequestVerificationToken");
            return await client.PostAsync("/workspaces/" + action, new FormUrlEncodedContent(form));
        }
        await Login(aliceClient, "alice", aliceSecret); await Login(bobClient, "bob", bobSecret);
        var fleet = factory.Services.GetRequiredService<FleetStore>(); var now = DateTimeOffset.UtcNow;
        foreach (var tenant in new[] { "alpha", "beta" })
        {
            var snapshot = new HostSnapshot(now, tenant + "-host", "Linux", 10, 100, 50, 40, 10, DataPolicyCalculator.Evaluate(new(), new(32L << 30, 16L << 30, 0)), [], "test");
            fleet.Accept(IdentityRules.Topic(tenant, tenant + "-pi"), new(1, tenant, tenant + "-pi", Guid.NewGuid(), Guid.NewGuid(), 1, snapshot));
        }
        var invited = await Post(aliceClient, "invite", new() { ["username"] = "bob", ["role"] = "Viewer" });
        var token = Regex.Match(await invited.Content.ReadAsStringAsync(), "token=([A-F0-9]{64})").Groups[1].Value; Assert.NotEmpty(token);
        var accepted = await Post(bobClient, "accept", new() { ["token"] = token }); Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var devices = await bobClient.GetStringAsync("/api/devices"); Assert.Contains("alpha-host", devices); Assert.DoesNotContain("beta-host", devices);
        Assert.DoesNotContain("Add your first device", await bobClient.GetStringAsync("/"));
        var team = await bobClient.GetStringAsync("/workspaces"); Assert.DoesNotContain("<h2>Team members</h2>", team);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(bobClient, "invite", new() { ["username"] = "eve", ["role"] = "Administrator" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(bobClient, "role", new() { ["username"] = "bob", ["role"] = "Administrator" })).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(bobClient, "switch", new() { ["workspace"] = "beta" })).StatusCode);
        devices = await bobClient.GetStringAsync("/api/devices"); Assert.Contains("beta-host", devices); Assert.DoesNotContain("alpha-host", devices);
        Assert.Contains("<h2>Team members</h2>", await bobClient.GetStringAsync("/workspaces"));
        Assert.Equal(HttpStatusCode.Redirect, (await Post(aliceClient, "remove", new() { ["username"] = "bob" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bobClient.GetAsync("/api/devices")).StatusCode);
        Assert.Contains("alpha-host", await aliceClient.GetStringAsync("/api/devices"));
    }
    [Fact]
    public void SignupSettingDefaultsOnPersistsAndOnlyOwnerCanChangeIt()
    {
        var users = Store(); var owner = Bootstrap(users);
        Assert.True(users.MultiTenantSignupEnabled);
        var alice = Register(users, "alice", "Alice"); var a = users.Principal(alice);
        Assert.Throws<UnauthorizedAccessException>(() => users.SetMultiTenantSignup(a, false));
        users.SetMultiTenantSignup(owner, false);
        Assert.False(Store().MultiTenantSignupEnabled);
        Assert.Throws<UnauthorizedAccessException>(() => Register(users, "blocked", "Blocked"));
        Assert.Throws<UnauthorizedAccessException>(() => users.CreateWorkspace(a, "Blocked"));
        var token = users.Invite(a, "newcomer", "Viewer").Token;
        var secret = Totp.NewSecret();
        Assert.Throws<UnauthorizedAccessException>(() => users.RegisterInvitedAccount("wrong-user", Password, secret, Code(secret), token));
        var newUser = users.RegisterInvitedAccount("newcomer", Password, secret, Code(secret), token);
        var joined = users.Principal(newUser);
        Assert.True(joined.IsInRole("Viewer")); Assert.Equal(alice.TenantId, joined.FindFirstValue("tenant"));
        Assert.Single(users.WorkspacesFor(joined)); Assert.Single(users.WorkspacesFor(owner));
        Assert.Null(users.SignupInvitation(token));
        Assert.Throws<UnauthorizedAccessException>(() => users.RegisterInvitedAccount("replay", Password, secret, Code(secret), token));
        var ownerWorkspace = users.CreateWorkspace(owner, "Owner workspace");
        Assert.Equal(2, users.WorkspacesFor(ownerWorkspace).Length);
        users.SetMultiTenantSignup(ownerWorkspace, true);
        Assert.True(Store().MultiTenantSignupEnabled);
        Assert.NotNull(Register(users, "allowed", "Allowed"));
        Assert.Equal(2, users.AuditFor(users.Switch(ownerWorkspace, "")).Count(audit => audit.Action.StartsWith("Multi-tenant signup", StringComparison.Ordinal)));
    }
    [Fact]
    public async Task HttpOwnerCanDisableSignupWhileInvitedAccountsJoinWithoutCreatingTenants()
    {
        using var factory = new WorkspaceFactory(); using var ownerClient = factory.Client(); using var publicClient = factory.Client(); using var invitedClient = factory.Client();
        var users = factory.Services.GetRequiredService<UserStore>(); var ownerSecret = Totp.NewSecret();
        users.Add("owner", Password, ownerSecret, "local", "Owner", false, -1);
        var admin = Register(users, "admin", "Single workspace"); var administrator = users.Principal(admin);
        var oldSignup = await publicClient.GetStringAsync("/auth/signup");
        var oldSecret = Regex.Match(oldSignup, "class=\"setup-key\">([^<]+)").Groups[1].Value;
        var login = await ownerClient.GetStringAsync("/auth/login");
        var loggedIn = await ownerClient.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["username"] = "owner", ["password"] = Password, ["code"] = Code(ownerSecret), ["__RequestVerificationToken"] = Field(login, "__RequestVerificationToken") }));
        Assert.Equal(HttpStatusCode.Redirect, loggedIn.StatusCode);
        var settings = await ownerClient.GetStringAsync("/workspaces"); Assert.Contains("Allow multi-tenant signup", settings);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsync("/workspaces/signup-settings", new FormUrlEncodedContent(new Dictionary<string, string> { ["enabled"] = "false" }))).StatusCode);
        var disabled = await ownerClient.PostAsync("/workspaces/signup-settings", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["enabled"] = "false", ["__RequestVerificationToken"] = Field(settings, "__RequestVerificationToken") }));
        Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode); Assert.False(users.MultiTenantSignupEnabled);
        Assert.Equal(HttpStatusCode.Forbidden, (await publicClient.GetAsync("/auth/signup")).StatusCode);
        var signIn = await publicClient.GetStringAsync("/auth/login"); Assert.Contains("Public workspace signup is disabled", signIn); Assert.DoesNotContain("Create your account and workspace", signIn);
        var stalePost = await publicClient.PostAsync("/auth/signup", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["username"] = "outsider", ["password"] = Password, ["confirmPassword"] = Password, ["code"] = Code(oldSecret), ["workspace"] = "Blocked workspace",
            ["setup"] = Field(oldSignup, "setup"), ["__RequestVerificationToken"] = Field(oldSignup, "__RequestVerificationToken") }));
        Assert.Equal(HttpStatusCode.Forbidden, stalePost.StatusCode);
        var invite = users.Invite(administrator, "newcomer", "Viewer");
        Assert.Equal(HttpStatusCode.OK, (await invitedClient.GetAsync("/auth/join?token=" + invite.Token)).StatusCode);
        var signup = await invitedClient.GetStringAsync("/auth/signup?invite=" + invite.Token);
        Assert.Contains("Create account and join team", signup); Assert.DoesNotContain("Workspace name", signup);
        var secret = Regex.Match(signup, "class=\"setup-key\">([^<]+)").Groups[1].Value;
        var joined = await invitedClient.PostAsync("/auth/signup", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["username"] = "newcomer", ["password"] = Password, ["confirmPassword"] = Password, ["code"] = Code(secret), ["workspace"] = "Injected workspace ignored",
            ["setup"] = Field(signup, "setup"), ["__RequestVerificationToken"] = Field(signup, "__RequestVerificationToken") }));
        Assert.Equal(HttpStatusCode.Redirect, joined.StatusCode);
        var team = await invitedClient.GetStringAsync("/workspaces"); Assert.Contains("Single workspace", team); Assert.Contains("Viewer", team);
        Assert.DoesNotContain("Create another workspace", team); Assert.DoesNotContain("Workspace signup settings", team); Assert.DoesNotContain("Injected workspace", team);
        var csrf = Field(team, "__RequestVerificationToken");
        Assert.Equal(HttpStatusCode.Forbidden, (await invitedClient.PostAsync("/workspaces/create", new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "Bypass", ["__RequestVerificationToken"] = csrf }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await invitedClient.PostAsync("/workspaces/signup-settings", new FormUrlEncodedContent(new Dictionary<string, string> { ["enabled"] = "true", ["__RequestVerificationToken"] = csrf }))).StatusCode);
        Assert.Null(users.SignupInvitation(invite.Token));
        var ownerPage = await ownerClient.GetStringAsync("/workspaces"); Assert.Contains("Single workspace", ownerPage); Assert.DoesNotContain("Injected workspace", ownerPage);
    }
    private static string Field(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, "name=[\"']" + name + "[\"'] value=[\"']([^\"']*)").Groups[1].Value);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class WorkspaceFactory : WebApplicationFactory<CloudMarker>
    {
        private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-workspace-http-" + Guid.NewGuid());
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development").UseSetting("PiDock:StateDirectory", directory).UseSetting("Mqtt:Enabled", "false").UseSetting("Logging:LogLevel:Default", "Warning");
        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing && Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
