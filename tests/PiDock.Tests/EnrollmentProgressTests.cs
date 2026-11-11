using PiDock.Cloud.Web;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class EnrollmentProgressTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T12:00:00Z");
    private static DeviceInvitation Invitation(string state = "Registered") => new(Guid.NewGuid(), "alpha", "pi5", "Pi", "owner", Now.AddMinutes(-1), Now.AddMinutes(9), "", state);
    private static StoredDevice Device(string tenant = "alpha", string device = "pi5", bool live = true, DateTimeOffset? received = null) =>
        new(tenant, device, Guid.NewGuid(), received ?? Now, null!, HasLiveContact: live);

    [Fact]
    public void IssuedCertificateAloneDoesNotConfirmConnectivity() =>
        Assert.Equal(EnrollmentConnectionState.AwaitingTelemetry, EnrollmentProgress.Evaluate(Invitation(), null, Now, Now));
    [Fact]
    public void PendingInvitationDoesNotUseExistingTelemetry() =>
        Assert.Equal(EnrollmentConnectionState.AwaitingRegistration, EnrollmentProgress.Evaluate(Invitation("Pending"), Device(), Now, Now));
    [Fact]
    public void MatchingRecentLiveContactConfirmsConnectivity() =>
        Assert.Equal(EnrollmentConnectionState.Connected, EnrollmentProgress.Evaluate(Invitation(), Device(), Now, Now));
    [Theory]
    [InlineData("other", "pi5", true)]
    [InlineData("alpha", "other", true)]
    [InlineData("alpha", "pi5", false)]
    public void OtherIdentitiesAndReplayDoNotConfirmConnectivity(string tenant, string device, bool live) =>
        Assert.Equal(EnrollmentConnectionState.AwaitingTelemetry, EnrollmentProgress.Evaluate(Invitation(), Device(tenant, device, live), Now, Now));
    [Theory]
    [InlineData(-91)]
    [InlineData(31)]
    public void StaleAndFutureContactDoNotConfirmConnectivity(int seconds) =>
        Assert.Equal(EnrollmentConnectionState.AwaitingTelemetry, EnrollmentProgress.Evaluate(Invitation(), Device(received: Now.AddSeconds(seconds)), Now, Now));
    [Fact]
    public void MissingRegistrationTimesOutAndCanStartAgain()
    {
        Assert.Equal(EnrollmentConnectionState.TimedOut, EnrollmentProgress.Evaluate(Invitation("Pending"), null, Now, Now.AddMinutes(-2)));
        Assert.Equal(EnrollmentConnectionState.AwaitingRegistration, EnrollmentProgress.Evaluate(Invitation("Pending"), null, Now, Now));
    }
}
