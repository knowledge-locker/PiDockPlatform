using PiDock.Core;

namespace PiDock.Cloud.Web;

public enum EnrollmentConnectionState { AwaitingRegistration, AwaitingTelemetry, Connected, TimedOut }

public static class EnrollmentProgress
{
    public static EnrollmentConnectionState Evaluate(DeviceInvitation? invitation, StoredDevice? device, DateTimeOffset now, DateTimeOffset started)
    {
        bool registered = invitation?.State == "Registered";
        if (registered && device is { HasLiveContact: true } && device.TenantId == invitation!.TenantId && device.DeviceId == invitation.DeviceId &&
            device.ReceivedAt >= now.AddSeconds(-90) && device.ReceivedAt <= now.AddSeconds(30))
            return EnrollmentConnectionState.Connected;
        if (now - started >= TimeSpan.FromMinutes(2)) return EnrollmentConnectionState.TimedOut;
        return registered ? EnrollmentConnectionState.AwaitingTelemetry : EnrollmentConnectionState.AwaitingRegistration;
    }
}
