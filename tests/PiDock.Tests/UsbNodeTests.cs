using PiDock.Appliance.Broker;
using PiDock.Core;
namespace PiDock.Tests;

public sealed class UsbNodeTests : IDisposable
{
    private readonly string root=StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-usb-node-"+Guid.NewGuid()));
    private const string Main="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",Key="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",OtherMain="cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",OtherKey="dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private static void Adopt(UsbNodeState node,string main=Main,string key=Key) {var discovery=node.Discovery();node.Adopt(new(discovery.Id,discovery.Challenge??"",main),key);}
    [Fact] public void DiscoveryAndIdentityPersistWithoutASeparateHumanAccount()
    {
        string id;
        using(var node=new UsbNodeState(root)) {
            var discovery=node.Discovery();id=discovery.Id;Assert.Equal(NodeSecurity.KeyId(node.Identity),id);Assert.Null(discovery.MainId);
            Assert.Throws<UnauthorizedAccessException>(()=>node.Configuration(Key));Assert.Throws<UnauthorizedAccessException>(()=>node.Measurement(Key));
            Adopt(node);Assert.True(node.Authorized(Key));
        }
        using(var restarted=new UsbNodeState(root)){Assert.Equal(id,restarted.Id);Assert.Equal(Main,restarted.Discovery().MainId);Assert.True(restarted.Authorized(Key));Assert.Null(restarted.Discovery().Challenge);}
        Assert.False(File.Exists(Path.Combine(root,"accounts.protected")));
        if(!OperatingSystem.IsWindows())Assert.Equal((UnixFileMode)0x180,File.GetUnixFileMode(Path.Combine(root,"identity.protected")));
    }
    [Fact] public void OnlyOwningMainCanConfigureReadOrRelease()
    {
        using var node=new UsbNodeState(root);var discovery=node.Discovery();
        Assert.Throws<UnauthorizedAccessException>(()=>node.Adopt(new(discovery.Id,"wrong",Main),Key));
        Assert.Throws<UnauthorizedAccessException>(()=>node.Adopt(new(OtherKey,discovery.Challenge!,Main),Key));
        Adopt(node);
        Assert.Throws<UnauthorizedAccessException>(()=>Adopt(node,OtherMain,OtherKey));
        Assert.Throws<UnauthorizedAccessException>(()=>node.Configuration(OtherKey));
        Assert.Throws<UnauthorizedAccessException>(()=>node.Configure(new(Guid.NewGuid(),new("stolen"),1),OtherKey));
        Assert.Throws<UnauthorizedAccessException>(()=>node.Release(new(Guid.NewGuid()),OtherKey));
        Assert.Equal(Main,node.Discovery().MainId);
    }
    [Fact] public void ConfigurationRevisionsPreventDelayedRequestsFromRevertingNewerSettings()
    {
        using var node=new UsbNodeState(root);Adopt(node);
        var first=new NodeConfigurationRequest(Guid.NewGuid(),new("First",true,30),node.Configuration(Key).Revision);
        node.Configure(first,Key);node.Configure(first,Key);
        Assert.Throws<ArgumentException>(()=>node.Configure(first with {Configuration=new("changed payload")},Key));
        var second=new NodeConfigurationRequest(Guid.NewGuid(),new("Second",false,60),node.Configuration(Key).Revision);
        node.Configure(second,Key);
        Assert.Throws<InvalidOperationException>(()=>node.Configure(first,Key));
        Assert.Equal("Second",node.Configuration(Key).Configuration.Name);Assert.Null(node.Measurement(Key));
    }
    [Fact] public void AcknowledgedReleaseAllowsAnotherMainAndOldRetriesCannotRevokeIt()
    {
        using var node=new UsbNodeState(root);Adopt(node);string oldChallenge=node.Discovery().Challenge??"";
        var release=new NodeReleaseRequest(Guid.NewGuid());node.Release(release,Key);
        Assert.False(node.Authorized(Key));Assert.Null(node.Discovery().MainId);Assert.NotEqual(oldChallenge,node.Discovery().Challenge);
        Adopt(node,OtherMain,OtherKey);node.Release(release,Key);
        Assert.True(node.Authorized(OtherKey));Assert.Equal(OtherMain,node.Discovery().MainId);
        Assert.Throws<UnauthorizedAccessException>(()=>node.Release(new(Guid.NewGuid()),Key));
        Assert.Throws<UnauthorizedAccessException>(()=>Adopt(node));
    }
    [Fact] public void ExpiredDiscoveryChallengesRotateWithoutResettingOwnership()
    {
        var clock=new Clock();using var node=new UsbNodeState(root,clock);var old=node.Discovery();clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Throws<UnauthorizedAccessException>(()=>node.Adopt(new(old.Id,old.Challenge!,Main),Key));
        var fresh=node.Discovery();Assert.NotEqual(old.Challenge,fresh.Challenge);Adopt(node);clock.Advance(TimeSpan.FromDays(1));Assert.True(node.Authorized(Key));
    }
    [Theory][InlineData(4)][InlineData(301)] public void MonitoringLimitsAreEnforcedOnTheNode(int seconds)
    {
        using var node=new UsbNodeState(root);Adopt(node);Assert.Throws<ArgumentException>(()=>node.Configure(new(Guid.NewGuid(),new("Node",true,seconds),1),Key));
    }
    private sealed class Clock : TimeProvider {private long timestamp;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>timestamp;public void Advance(TimeSpan time)=>timestamp+=(long)time.TotalMilliseconds;}
    public void Dispose()=>Directory.Delete(root,true);
}
