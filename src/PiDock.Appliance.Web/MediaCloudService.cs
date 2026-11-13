using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Agent;
using PiDock.Core;
using PiDock.Web.Shared;
namespace PiDock.Appliance.Web;

// Only the first-boot importer creates this owner-bound, protected intent. It cannot change an existing registration.
public sealed class MediaCloudService : BackgroundService
{
    private readonly string path;
    private readonly IDataProtector protector;
    private readonly CloudEnrollmentClient enrollment;
    private readonly RemoteApplicationAgent control;
    private readonly RemoteInterfaceAgent remote;
    private readonly ClockTrust clock;
    private readonly UserStore users;
    private MediaCloudPlan? plan;
    private readonly MediaCloudStatus state;
    private string Status { set => state.Status=value; }
    public MediaCloudService(StateDirectory directory, IDataProtectionProvider protection, CloudEnrollmentClient enrollment,
        RemoteApplicationAgent control, RemoteInterfaceAgent remote, ClockTrust clock, UserStore users, MediaCloudStatus state)
    {
        path = Path.Combine(directory.Path,"media-cloud.protected");
        protector = protection.CreateProtector("PiDock.MediaCloud.v1");
        this.state=state;this.enrollment=enrollment;this.control=control;this.remote=remote;this.clock=clock;this.users=users;
        if(File.Exists(path)) {
            if(new FileInfo(path).Length>32768)throw new InvalidDataException("Invalid initial cloud plan.");
            plan=JsonSerializer.Deserialize<MediaCloudPlan>(protector.Unprotect(File.ReadAllText(path)),StateFiles.Json) ?? throw new InvalidDataException("Invalid initial cloud plan.");
            state.PreferredUrl=plan.Cloud.Url;
            Status=plan.Consumed ? "Image cloud setup consumed. Use the current settings below." : plan.Cloud.EnrollmentCode is null ? "Cloud URL prepared. Generate an invitation to register this device." : "Image registration waiting for network and authenticated time.";
        }
    }
    private void Consume()
    {
        plan=plan! with { Consumed=true, Cloud=new(plan.Cloud.Url) };
        StateFiles.WriteAtomic(path,protector.Protect(JsonSerializer.Serialize(plan,StateFiles.Json)));
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(plan is null || plan.Consumed || plan.Cloud.EnrollmentCode is null)return;
        while(!stoppingToken.IsCancellationRequested) {
            try {
                if(!MediaProvisioning.OwnerCurrent(users,plan)) { Consume();Status="Image cloud setup cancelled after an ownership change.";return; }
                if(enrollment.Connection is not null) { Consume();Status="A registration already exists. Image remote permissions were not replayed; review the current local settings.";return; }
                if(!clock.Allowed) { await Task.Delay(TimeSpan.FromSeconds(3),stoppingToken);continue; }
                var requested=plan;
                await enrollment.ConnectInitialAsync(requested.Cloud.Url,requested.Cloud.EnrollmentCode!,connection => {
                    // Consume before permission grants so a crash or later local revocation can never replay them.
                    Consume();
                    if(!clock.Allowed || !MediaProvisioning.OwnerCurrent(users,requested)) {
                        Status="Registered. Review remote permissions locally.";return;
                    }
                    remote.RecordImageConsent(requested.Owner,connection,requested.Cloud.RemoteControl,requested.Cloud.RemoteUi);
                    control.SetEnabled(requested.Cloud.RemoteControl);
                    remote.SetAllowed(requested.Cloud.RemoteUi,requested.Owner);
                    Status="Image cloud registration completed. Remote settings applied from your explicit image setup choices.";
                },stoppingToken);
                return;
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { return; }
            catch(Exception e) when(e is HttpRequestException or IOException or InvalidOperationException or ArgumentException or CryptographicException or OperationCanceledException) {
                // No secrets or server messages in UI/logs. An expired invitation can be replaced in the existing local flow.
                Status="Image registration or initial permissions incomplete. Review current local settings; check network/time or enter a fresh invitation below. Initial permissions will not be replayed after registration.";
                if(plan.Consumed)return;
                await Task.Delay(TimeSpan.FromSeconds(30),stoppingToken);
            }
        }
    }
}
