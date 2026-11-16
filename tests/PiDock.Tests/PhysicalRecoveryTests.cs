using Microsoft.AspNetCore.DataProtection;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class PhysicalRecoveryTests
{
    [Fact]
    public void RecoveryCannotBeInvokedFromTheDevelopmentOrRedirectedTerminal()
    {
        Assert.Throws<InvalidOperationException>(()=>PhysicalConsoleGuard.RequireRecovery());
    }
    [Fact]
    public void LocalRecoveryReplacesOwnerAndAuthenticatorAndRevokesAllSessions()
    {
        string directory=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-recovery-"+Guid.NewGuid());
        try
        {
            var store=new UserStore(new EphemeralDataProtectionProvider(),directory);
            var oldSecret=Totp.NewSecret();var now=DateTimeOffset.UtcNow;const string oldPassword="old-owner-strong-password";
            store.Add("old-owner",oldPassword,oldSecret,"local","Owner",true,-1);
            var old=store.Authenticate("old-owner",oldPassword,Totp.Code(oldSecret,now.ToUnixTimeSeconds()/30),now)!;
            var persistent=store.Principal(old,session:store.CreateSession(old,true,now));
            var shortSession=store.Principal(old);Assert.True(store.IsCurrent(persistent));Assert.True(store.IsCurrent(shortSession));
            var newSecret=Totp.NewSecret();const string newPassword="new-owner-strong-password";
            store.RecoverLocalOwner(new PhysicalPresence(),"new-owner",newPassword,newSecret,-1);
            Assert.False(store.IsCurrent(persistent));Assert.False(store.IsCurrent(shortSession));
            Assert.Null(store.Authenticate("old-owner",oldPassword,Totp.Code(oldSecret,now.ToUnixTimeSeconds()/30+1),now));
            Assert.NotNull(store.Authenticate("new-owner",newPassword,Totp.Code(newSecret,now.ToUnixTimeSeconds()/30),now));
            string audit=File.ReadAllText(Path.Combine(directory,"physical-recovery-audit.json"));
            Assert.Contains("OwnerCredentialsReplaced",audit);Assert.DoesNotContain(newPassword,audit);Assert.DoesNotContain(newSecret,audit);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact]
    public void AppliancePhysicalRecoveryCannotReplaceACloudOwner()
    {
        string directory=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-cloud-recovery-"+Guid.NewGuid());
        try
        {
            var store=new UserStore(new EphemeralDataProtectionProvider(),directory,cloud:true);
            Assert.Throws<InvalidOperationException>(()=>store.RecoverLocalOwner(new PhysicalPresence(),"owner","strong-cloud-password",Totp.NewSecret(),-1));
            Assert.False(store.HasOwner);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
