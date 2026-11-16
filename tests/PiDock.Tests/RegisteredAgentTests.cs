using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class RegisteredAgentTests
{
    [Fact]
    public async Task EncryptedRetrievableRegistrationAuthenticatesAndRevokesWithoutRestart()
    {
        using var factory = new AgentFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var users = factory.Services.GetRequiredService<UserStore>();
        var (owner, secret) = Owner(users, factory.Clock);
        var agents = factory.Services.GetRequiredService<RegisteredAgentStore>();
        factory.Clock.Advance();
        var registration = agents.Create(owner, Password, Code(secret, factory.Clock), "Third-party agent", "https://localhost/", 30);
        Assert.Equal(64, registration.Token.Length);
        Assert.Equal("https://localhost/api/platform/v1/discovery", registration.Application.DiscoveryUrl);
        Assert.Contains(registration.Token, registration.Prompt);
        Assert.Contains("Authorization: Bearer", registration.Prompt);
        Assert.Contains("PIDOCK_DISCOVERY_TOKEN", registration.Prompt);
        var encrypted = File.ReadAllText(Path.Combine(factory.Root, "registered-agents.protected"));
        Assert.DoesNotContain(registration.Token, encrypted);
        Assert.DoesNotContain("Third-party agent", encrypted);
        Assert.DoesNotContain(registration.Token, JsonSerializer.Serialize(agents.List(owner), StateFiles.Json));
        var reopened = new RegisteredAgentStore(factory.Services.GetRequiredService<IDataProtectionProvider>(), new(factory.Root), users, factory.Clock);
        factory.Clock.Advance();
        Assert.Equal(registration.Token, reopened.Reveal(owner, Password, Code(secret, factory.Clock), registration.Application.Id).Token);
        client.DefaultRequestHeaders.Authorization = new("Bearer", registration.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/platform/v1/discovery")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/apps/v1/system")).StatusCode);
        Assert.Contains((await client.GetAsync("/agent-access")).StatusCode, new[] { HttpStatusCode.Redirect, HttpStatusCode.Unauthorized });
        factory.Clock.Advance();
        agents.Revoke(owner, Password, Code(secret, factory.Clock), registration.Application.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/platform/v1/discovery")).StatusCode);
        factory.Clock.Advance();
        Assert.Throws<ArgumentException>(() => agents.Reveal(owner, Password, Code(secret, factory.Clock), registration.Application.Id));
    }
    [Fact]
    public async Task RotationExpiryAndScopesAreEnforcedAtBothApiPolicies()
    {
        using var factory = new AgentFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var users = factory.Services.GetRequiredService<UserStore>();
        var (owner, secret) = Owner(users, factory.Clock);
        var agents = factory.Services.GetRequiredService<RegisteredAgentStore>();
        factory.Clock.Advance();
        var created = agents.Create(owner, Password, Code(secret, factory.Clock), "Resource agent", "https://localhost/", 1, systemRead: true);
        client.DefaultRequestHeaders.Authorization = new("Bearer", created.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/apps/v1/system")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/apps/v1/logs")).StatusCode);
        factory.Clock.Advance();
        var rotated = agents.Rotate(owner, Password, Code(secret, factory.Clock), created.Application.Id, 1);
        Assert.NotEqual(created.Token, rotated.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/platform/v1/discovery")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", rotated.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/platform/v1/discovery")).StatusCode);
        using var plain = factory.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        plain.DefaultRequestHeaders.Authorization = new("Bearer", rotated.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await plain.GetAsync("/api/platform/v1/discovery")).StatusCode);
        factory.Clock.Now = rotated.Application.ExpiresAt.AddSeconds(1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/platform/v1/discovery")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/apps/v1/system")).StatusCode);
    }
    [Fact]
    public void OwnerAndFreshMfaAreRequiredForEverySecretOperation()
    {
        using var factory = new AgentFactory();
        _ = factory.Services;
        var users = factory.Services.GetRequiredService<UserStore>();
        var (owner, secret) = Owner(users, factory.Clock);
        var agents = factory.Services.GetRequiredService<RegisteredAgentStore>();
        Assert.Throws<UnauthorizedAccessException>(() => agents.List(new ClaimsPrincipal()));
        factory.Clock.Advance();
        Assert.Throws<UnauthorizedAccessException>(() => agents.Create(owner, "incorrect-password", Code(secret, factory.Clock), "Agent", "https://localhost/", 30));
        var created = agents.Create(owner, Password, Code(secret, factory.Clock), "Agent", "https://localhost/", 30);
        Assert.Throws<UnauthorizedAccessException>(() => agents.Reveal(owner, Password, Code(secret, factory.Clock), created.Application.Id));
        factory.Clock.Advance();
        Assert.Throws<UnauthorizedAccessException>(() => agents.Rotate(owner, "incorrect-password", Code(secret, factory.Clock), created.Application.Id, 30));
        Assert.Throws<UnauthorizedAccessException>(() => agents.Revoke(owner, "incorrect-password", Code(secret, factory.Clock), created.Application.Id));
        Assert.NotNull(agents.Authenticate(created.Token));
        var viewer = new ClaimsPrincipal(new ClaimsIdentity(owner.Claims.Where(c => c.Type != ClaimTypes.Role).Append(new(ClaimTypes.Role, "Viewer")), "test"));
        Assert.Throws<UnauthorizedAccessException>(() => agents.List(viewer));
        Assert.Throws<UnauthorizedAccessException>(() => agents.Reveal(viewer, Password, Code(secret, factory.Clock), created.Application.Id));
        foreach (var origin in new[] { "http://localhost/", "https://user:password@localhost/", "https://localhost/?token=x", "https://localhost/path" })
            Assert.Throws<ArgumentException>(() => RegisteredAgentStore.ValidateOrigin(origin));
        var copiedState = StateFiles.PrepareDirectory(Path.Combine(factory.Root, "elsewhere"));
        File.Copy(Path.Combine(factory.Root, "registered-agents.protected"), Path.Combine(copiedState, "registered-agents.protected"));
        var wrongProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(factory.Root, "other-keys")));
        Assert.Throws<CryptographicException>(() => new RegisteredAgentStore(wrongProvider, new(copiedState), users, factory.Clock));
    }
    [Fact]
    public void RemoteOwnerCannotCreateRevealRotateOrRevokeAgentCredentials()
    {
        using var factory=new AgentFactory();
        var users=factory.Services.GetRequiredService<UserStore>();
        var (owner,secret)=Owner(users,factory.Clock);var agents=factory.Services.GetRequiredService<RegisteredAgentStore>();
        factory.Clock.Advance();var created=agents.Create(owner,Password,Code(secret,factory.Clock),"Local agent","https://localhost/",1);
        var remote=new ClaimsPrincipal(new ClaimsIdentity(owner.Claims.Append(new Claim("pidock_remote",Guid.NewGuid().ToString())),"Cookies"));
        factory.Clock.Advance();var otp=Code(secret,factory.Clock);
        Assert.Throws<UnauthorizedAccessException>(()=>agents.List(remote));
        Assert.Throws<UnauthorizedAccessException>(()=>agents.Create(remote,Password,otp,"Remote agent","https://localhost/",1));
        Assert.Throws<UnauthorizedAccessException>(()=>agents.Reveal(remote,Password,otp,created.Application.Id));
        Assert.Throws<UnauthorizedAccessException>(()=>agents.Rotate(remote,Password,otp,created.Application.Id,1));
        Assert.Throws<UnauthorizedAccessException>(()=>agents.Revoke(remote,Password,otp,created.Application.Id));
        // Remote rejection must not consume the local owner's next code.
        Assert.Equal(created.Token,agents.Reveal(owner,Password,otp,created.Application.Id).Token);
    }
    [Fact]
    public async Task OwnerRecoveryInvalidatesPreviouslyIssuedAgentTokens()
    {
        using var factory=new AgentFactory();using var client=factory.CreateClient(new(){BaseAddress=new("https://localhost"),AllowAutoRedirect=false});
        var users=factory.Services.GetRequiredService<UserStore>();var (owner,secret)=Owner(users,factory.Clock);
        var agents=factory.Services.GetRequiredService<RegisteredAgentStore>();factory.Clock.Advance();
        var registration=agents.Create(owner,Password,Code(secret,factory.Clock),"Owner-bound","https://localhost/",1,true);
        client.DefaultRequestHeaders.Authorization=new("Bearer",registration.Token);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/apps/v1/system")).StatusCode);
        var replacementSecret=Totp.NewSecret();
        users.RecoverLocalOwner(new PhysicalPresence(),"replacement","new-owner-private-password",replacementSecret,-1);
        Assert.Null(agents.Authenticate(registration.Token));
        var replacement=users.Authenticate("replacement","new-owner-private-password",Code(replacementSecret,factory.Clock),factory.Clock.Now)!;
        var newOwner=users.Principal(replacement);
        Assert.False(Assert.Single(agents.List(newOwner)).OwnerCurrent);
        factory.Clock.Advance();
        Assert.Throws<ArgumentException>(()=>agents.Reveal(newOwner,"new-owner-private-password",Code(replacementSecret,factory.Clock),registration.Application.Id));
        factory.Clock.Advance();
        var newRegistration=agents.Create(newOwner,"new-owner-private-password",Code(replacementSecret,factory.Clock),"Owner-bound","https://localhost/",1);
        Assert.NotNull(agents.Authenticate(newRegistration.Token));
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/apps/v1/system")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/platform/v1/discovery")).StatusCode);
    }
    [Fact]
    public void ClockPauseRejectsRegisteredTokensWithoutDeletingTheirState()
    {
        string directory=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-clock-agent-"+Guid.NewGuid());
        try {
            bool allowed=true;var provider=new EphemeralDataProtectionProvider();
            var users=new UserStore(provider,directory,clockAllowed:()=>allowed);var clock=new Clock();var (owner,secret)=Owner(users,clock);
            var agents=new RegisteredAgentStore(provider,new(directory),users,clock);clock.Advance();
            var registration=agents.Create(owner,Password,Code(secret,clock),"Clock-bound","https://localhost/",1);
            Assert.NotNull(agents.Authenticate(registration.Token));allowed=false;Assert.Null(agents.Authenticate(registration.Token));
            allowed=true;Assert.NotNull(agents.Authenticate(registration.Token));
        }finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
    private const string Password = "integration-test-password";
    private static string Code(string secret, Clock clock) => Totp.Code(secret, clock.Now.ToUnixTimeSeconds() / 30);
    private static (ClaimsPrincipal User, string Secret) Owner(UserStore users, Clock clock)
    {
        var secret = Totp.NewSecret(); users.Add("agent-owner", Password, secret, "local", "Owner", true, -1);
        var user = users.Authenticate("agent-owner", Password, Code(secret, clock), clock.Now)!;
        return (new(new ClaimsIdentity([new(ClaimTypes.Name, user.Username), new(ClaimTypes.Role, "Owner"), new("tenant", "local"),
            new("stamp", user.Stamp), new("expires", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString())], "test")), secret);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public void Advance() => Now = Now.AddSeconds(30);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class AgentFactory : WebApplicationFactory<PiDock.Appliance.Web.ApplianceMarker>
    {
        public string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-agent-registration-" + Guid.NewGuid());
        public Clock Clock { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development").UseSetting("PiDock:StateDirectory", Root).UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock); });
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing && Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
