using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PiDock.Agent;
using PiDock.Appliance.Web;
using PiDock.Core;
using PiDock.Web.Shared;
namespace PiDock.Tests;

public sealed class MediaCloudTests
{
    [Theory][InlineData(true)][InlineData(false)]
    public async Task ConsumedConsentAndOwnerRecoveryCannotReplayImageRemotePermissions(bool consumed)
    {
        var directory=StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-media-cloud-"+Guid.NewGuid()));
        try {
            var provider=new EphemeralDataProtectionProvider();var users=new UserStore(provider,directory);
            users.Add("owner","a private initial password",Totp.NewSecret(),"local","Owner",true,-1);
            var plan=new MediaCloudPlan("owner","an-old-owner-stamp",new("https://cloud.example",new string('A',32),true,true),consumed);
            var protector=provider.CreateProtector("PiDock.MediaCloud.v1");string path=Path.Combine(directory,"media-cloud.protected");
            StateFiles.WriteAtomic(path,protector.Protect(JsonSerializer.Serialize(plan,StateFiles.Json)));
            var settings=new ConfigurationBuilder().Build();using var mqtt=new MqttConnection(settings,directory);
            var enrollment=new CloudEnrollmentClient(directory,mqtt,enabled:false);
            using var control=new RemoteApplicationAgent(settings,new Runtime(),mqtt,TimeProvider.System,directory);
            using var remote=new RemoteInterfaceAgent(enrollment,directory,NullLogger<RemoteInterfaceAgent>.Instance);
            var status=new MediaCloudStatus();using var service=new MediaCloudService(new(directory),provider,enrollment,control,remote,new ClockTrust(false),users,status);
            await service.StartAsync(CancellationToken.None);await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(control.Current.ControlEnabled);Assert.False(remote.Allowed);Assert.Null(enrollment.Connection);
            Assert.DoesNotContain(new string('A',32),File.ReadAllText(path));
            Assert.Equal("https://cloud.example",status.PreferredUrl);
            if(!consumed) {
                var saved=JsonSerializer.Deserialize<MediaCloudPlan>(protector.Unprotect(File.ReadAllText(path)),StateFiles.Json)!;
                Assert.True(saved.Consumed);Assert.Null(saved.Cloud.EnrollmentCode);Assert.False(saved.Cloud.RemoteControl);Assert.False(saved.Cloud.RemoteUi);
            }
        }finally{Directory.Delete(directory,true);}
    }
    private sealed class Runtime : IApplicationRuntime {
        public bool Configured=>false;
        public Task<ApplicationSummary[]> List(CancellationToken ct=default)=>Task.FromResult(Array.Empty<ApplicationSummary>());
        public Task ChangeState(string id,bool start,CancellationToken ct=default)=>throw new InvalidOperationException();
        public Task Restart(string id,CancellationToken ct=default)=>throw new InvalidOperationException();
    }
}
