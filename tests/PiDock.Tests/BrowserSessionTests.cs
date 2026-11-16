using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class BrowserSessionTests : IDisposable
{
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-session-test-" + Guid.NewGuid());
    private const string Password = "session-test-password";
    [Theory]
    [InlineData(true, 168)]
    [InlineData(false, 12)]
    public void SessionsSurviveRestartAndRemainValidBeyondFifteenMinutesButExpire(bool remember, int hours)
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
        var store = new UserStore(provider, root); var now = DateTimeOffset.UtcNow;
        var user = Account(store, now);
        var session = store.CreateSession(user, remember, now); var principal = Principal(user, session);
        Assert.Equal(TimeSpan.FromHours(hours), session.ExpiresAt - now);
        var reloaded = new UserStore(provider, root);
        Assert.True(reloaded.IsCurrent(principal, now.AddMinutes(16)));
        Assert.True(reloaded.IsCurrent(principal, session.ExpiresAt.AddSeconds(-1)));
        Assert.False(reloaded.IsCurrent(principal, session.ExpiresAt));
        var raw = File.ReadAllText(Path.Combine(root, "sessions.protected"));
        Assert.DoesNotContain(session.Id, raw); Assert.DoesNotContain(user.Username, raw); Assert.DoesNotContain(Password, raw);
    }
    [Fact]
    public void SignOutRevokesOnlyThatBrowserAndRevocationSurvivesRestart()
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
        var store = new UserStore(provider, root); var now = DateTimeOffset.UtcNow; var user = Account(store, now);
        var first = Principal(user, store.CreateSession(user, true, now)); var second = Principal(user, store.CreateSession(user, true, now));
        store.RevokeSession(first);
        var reloaded = new UserStore(provider, root);
        Assert.False(reloaded.IsCurrent(first)); Assert.True(reloaded.IsCurrent(second));
        // A valid browser session does not satisfy a protected action's one-time-code check.
        Assert.Null(reloaded.Authenticate(user.Username, Password, Totp.Code(user.Secret, now.ToUnixTimeSeconds() / 30), now));
    }
    [Fact]
    public void UnknownSessionsExtendedExpiryAndAlteredAccountClaimsAreRejected()
    {
        var store = new UserStore(new EphemeralDataProtectionProvider(), root); var now = DateTimeOffset.UtcNow; var user = Account(store, now);
        var session = store.CreateSession(user, true, now);
        Assert.False(store.IsCurrent(new ClaimsPrincipal(new ClaimsIdentity(Principal(user, session).Claims))));
        Assert.False(store.IsCurrent(Principal(user, session with { Id = "missing" })));
        Assert.False(store.IsCurrent(Principal(user, session with { ExpiresAt = session.ExpiresAt.AddDays(1) })));
        Assert.False(store.IsCurrent(Principal(user with { Stamp = "changed" }, session)));
        Assert.False(store.IsCurrent(Principal(user with { TenantId = "other" }, session)));
        Assert.False(store.IsCurrent(Principal(user with { Role = "Administrator" }, session)));
        for (int i = 0; i < 20; i++) store.CreateSession(user, true, now.AddSeconds(i + 1));
        Assert.False(store.IsCurrent(Principal(user, session))); // Bound retained browser sessions per account.
    }
    private static LoginUser Account(UserStore store, DateTimeOffset now)
    {
        var secret = Totp.NewSecret(); store.Add("session-owner", Password, secret, "local", "Owner", true, -1);
        return store.Authenticate("session-owner", Password, Totp.Code(secret, now.ToUnixTimeSeconds() / 30), now)!;
    }
    private static ClaimsPrincipal Principal(LoginUser user, BrowserSession session) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.Name,user.Username), new Claim(ClaimTypes.Role,user.Role), new Claim("tenant",user.TenantId), new Claim("stamp",user.Stamp),
        new Claim("session",session.Id), new Claim("expires",session.ExpiresAt.ToUnixTimeSeconds().ToString())], "test"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
