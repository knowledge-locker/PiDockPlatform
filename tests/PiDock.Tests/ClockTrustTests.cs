using Microsoft.AspNetCore.DataProtection;
using PiDock.Core;
using PiDock.Updater;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class ClockTrustTests
{
    private sealed class Clock : TimeProvider
    {
        public long Stamp = 1000;
        public DateTimeOffset Utc = DateTimeOffset.Parse("2026-11-12T22:00:00Z");
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Stamp;
        public override DateTimeOffset GetUtcNow() => Utc;
    }
    [Fact]
    public void ProofMustBeFreshFromThisBootAndConsistentWithMonotonicTime()
    {
        var clock = new Clock(); var boot = Guid.NewGuid().ToString(); var sample = new ClockTrustSample(boot,1000,clock.Utc,true);
        Assert.True(ClockTrust.Accept(sample,boot,clock));
        Assert.False(ClockTrust.Accept(sample,Guid.NewGuid().ToString(),clock));
        Assert.False(ClockTrust.Accept(sample with {Trusted=false},boot,clock));
        clock.Stamp += 11000; clock.Utc = clock.Utc.AddSeconds(11); Assert.False(ClockTrust.Accept(sample,boot,clock));
        clock.Stamp = 2000; clock.Utc = sample.Utc.AddMinutes(-5); Assert.False(ClockTrust.Accept(sample,boot,clock));
        clock.Utc = sample.Utc.AddMinutes(5); Assert.False(ClockTrust.Accept(sample,boot,clock));
        clock.Stamp = 999; clock.Utc = sample.Utc; Assert.False(ClockTrust.Accept(sample,boot,clock));
    }
    [Fact]
    public void UntrustedClockPausesLoginAndRememberedSessionsWithoutConsumingCode()
    {
        var directory = Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-clock-"+Guid.NewGuid()); bool trusted = true;
        try
        {
            var users = new UserStore(new EphemeralDataProtectionProvider(),directory,clockAllowed:()=>trusted);
            var secret = Totp.NewSecret(); var now = DateTimeOffset.UtcNow; const string password="long-test-owner-password";
            users.Add("owner",password,secret,"local","Owner",true,-1);
            trusted=false; var code=Totp.Code(secret,now.ToUnixTimeSeconds()/30);
            Assert.Null(users.Authenticate("owner",password,code,now));
            trusted=true; var owner=users.Authenticate("owner",password,code,now); Assert.NotNull(owner);
            var principal=users.Principal(owner!,session:users.CreateSession(owner!,true,now)); Assert.True(users.IsCurrent(principal));
            trusted=false; Assert.False(users.IsCurrent(principal));
            trusted=true; Assert.True(users.IsCurrent(principal));
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
    [Fact]
    public void ChronyMustSelectAnAuthenticatedRecentPreciseSource()
    {
        const string tracking="Root delay : 0.100 seconds\nRoot dispersion : 0.020 seconds\nLast offset : -0.001 seconds\nLeap status : Normal\nRef time (UTC) : Thu Nov 12 21:59:00 2026\n";
        const string packet="Authenticated : Yes\nLeap status : Normal\n";
        var now=DateTimeOffset.Parse("2026-11-12T22:00:00Z");
        Assert.Equal("192.0.2.1",HostTimeGuard.SelectedServer("^? 192.0.2.2 2 6\n^* 192.0.2.1 2 6\n"));
        Assert.Null(HostTimeGuard.SelectedServer("^* untrusted-command 2 6\n"));
        Assert.True(HostTimeGuard.ValidMeasurement(tracking,packet,now));
        Assert.False(HostTimeGuard.ValidMeasurement(tracking,packet.Replace("Yes","No"),now));
        Assert.False(HostTimeGuard.ValidMeasurement(tracking.Replace("0.020","NaN"),packet,now));
        Assert.False(HostTimeGuard.ValidMeasurement(tracking.Replace("0.020","2.0"),packet,now));
        Assert.False(HostTimeGuard.ValidMeasurement(tracking,packet,now.AddHours(1)));
        Assert.False(HostTimeGuard.ValidMeasurement(tracking,packet,now.AddHours(-1)));
        Assert.False(HostTimeGuard.ValidMeasurement(tracking.Replace("Normal","Not synchronised"),packet,now));
    }
}
