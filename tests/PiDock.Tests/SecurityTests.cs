using System.Text;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Web.Shared;
namespace PiDock.Tests;

public sealed class SecurityTests : IDisposable
{
    private readonly string root = Path.Combine((OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath()), "pidock-auth-test-" + Guid.NewGuid());
    private const string Password = "unit-test-only-password";
    private static readonly string Secret = Totp.Encode(Encoding.ASCII.GetBytes("12345678901234567890"));
    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    public void MatchesRfc6238Vectors(long seconds, string expected) => Assert.Equal(expected, Totp.Code(Secret, seconds / 30, 8));
    [Fact]
    public void OldFutureAndReplayedCodesAreRejected()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890); var step = now.ToUnixTimeSeconds() / 30;
        Assert.Equal(step, Totp.Match(Secret, Totp.Code(Secret, step), now));
        Assert.Null(Totp.Match(Secret, Totp.Code(Secret, step), now, step));
        Assert.Null(Totp.Match(Secret, Totp.Code(Secret, step + 2), now));
        Assert.Null(Totp.Match(Secret, "00000x", now));
    }
    [Fact]
    public void ReplayProtectionSurvivesStoreReloadAndSecretsAreProtected()
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
        var store = new UserStore(provider, root); var now = DateTimeOffset.UtcNow;
        store.Add("owner", Password, Secret, "local", "Owner", true, -1);
        var code = Totp.Code(Secret, now.ToUnixTimeSeconds() / 30);
        Assert.NotNull(store.Authenticate("owner", Password, code, now));
        var reopened = new UserStore(provider, root);
        Assert.Null(reopened.Authenticate("owner", Password, code, now));
        var raw = File.ReadAllText(Path.Combine(root, "accounts.protected")); Assert.DoesNotContain(Secret, raw); Assert.DoesNotContain(Password, raw);
        Assert.Throws<InvalidOperationException>(() => reopened.Add("another", Password, Secret, "local", "Owner", true, -1));
    }
    [Fact]
    public void FailedLoginsAreThrottledEvenWithCorrectNextAttempt()
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))); var store = new UserStore(provider, root); var now = DateTimeOffset.UtcNow;
        store.Add("owner", Password, Secret, "local", "Owner", true, -1);
        for (int i = 0; i < 5; i++) Assert.Null(store.Authenticate("owner", "wrong", "000000", now));
        Assert.Null(store.Authenticate("owner", Password, Totp.Code(Secret, now.ToUnixTimeSeconds() / 30), now));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
