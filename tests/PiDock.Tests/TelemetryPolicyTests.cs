using System.Text.Json;
using PiDock.Agent;
using PiDock.Cloud.Web;
using PiDock.Core;
using PiDock.Web.Shared;
namespace PiDock.Tests;

public sealed class TelemetryPolicyTests
{
    private static TelemetryEnvelope Frame() => new(1,"tenant","pi",Guid.NewGuid(),Guid.NewGuid(),1,
        new(DateTimeOffset.UtcNow,"pi","Linux",40,800,400,50,3600,new(100,10,20,60,false,new Dictionary<HistoryClass,long>(),200),
            [new("eth0","Ethernet","Up",123,456,0)],"Synchronized"),
        Applications:new(DateTimeOffset.UtcNow,true,"Ready",[],[]));
    [Fact]
    public void DisabledChannelsRemoveValuesAndPreserveManagementAndLocalSample()
    {
        var original=Frame(); var filtered=new TelemetryChannels(false,false,false,false,false,false,false).Filter(original);
        Assert.Null(filtered.Snapshot.CpuPercent); Assert.Null(filtered.Snapshot.MemoryTotalBytes);
        Assert.Null(filtered.Snapshot.MemoryAvailableBytes); Assert.Null(filtered.Snapshot.TemperatureC);
        Assert.Empty(filtered.Snapshot.Interfaces); Assert.Empty(filtered.Alarms!);
        Assert.Equal(0,filtered.Snapshot.Storage.FreeBytes); Assert.Empty(filtered.Snapshot.Storage.ClassLimits);
        Assert.Null(DeviceHistory.Value(filtered.Snapshot,"uptime")); Assert.Null(DeviceHistory.Value(filtered.Snapshot,"storage"));
        Assert.Same(original.Applications,filtered.Applications); Assert.Equal(original.MessageId,filtered.MessageId);
        Assert.Equal(40,original.Snapshot.CpuPercent); Assert.Equal(3600,original.Snapshot.UptimeSeconds);
        Assert.DoesNotContain(",1\r\n",DeviceHistory.Csv([filtered],["uptime"]));
    }
    [Fact]
    public void PolicyDefaultsToAllChannelsAndSurvivesRestart()
    {
        var directory=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-policy-"+Guid.NewGuid());
        try {
            var policy=new TelemetryPolicy(directory); Assert.Equal(new TelemetryChannels(),policy.Current);
            var selected=new TelemetryChannels(Cpu:false,Interfaces:false); policy.Save(selected);
            Assert.Equal(selected,new TelemetryPolicy(directory).Current);
            var frame=Frame(); var filtered=new TelemetryChannels().Filter(frame);
            Assert.Equal(frame.Snapshot.Storage,filtered.Snapshot.Storage); Assert.Equal(frame.Snapshot.UptimeSeconds,filtered.Snapshot.UptimeSeconds);
            Assert.Null(JsonSerializer.Deserialize<HostSnapshot>(JsonSerializer.Serialize(frame.Snapshot,StateFiles.Json),StateFiles.Json)!.Channels);
        } finally { Directory.Delete(directory,true); }
    }
    [Fact]
    public void ControlStatusDistinguishesMissingOffAllowedAndStale()
    {
        var frame=Frame(); var now=DateTimeOffset.UtcNow;
        var device=new StoredDevice("tenant","pi",frame.MessageId,now,frame.Snapshot,Applications:frame.Applications);
        Assert.Equal("Allowed",Format.CloudControl(device,now));
        Assert.Equal("Off",Format.CloudControl(device with {Applications=frame.Applications! with {ControlEnabled=false}},now));
        Assert.Equal("Not reported",Format.CloudControl(device with {Applications=null},now));
        Assert.Equal("Allowed · stale",Format.CloudControl(device,now.AddMinutes(2)));
        Assert.Equal("Allowed · stale",Format.CloudControl(device with {HasLiveContact=false},now));
        Assert.Equal("Allowed · stale",Format.CloudControl(device with {Applications=frame.Applications! with {ObservedAt=now.AddMinutes(-2)}},now));
    }
}
