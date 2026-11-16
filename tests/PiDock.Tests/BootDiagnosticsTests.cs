using PiDock.Core;
using System.Text;

namespace PiDock.Tests;

public sealed class BootDiagnosticsTests
{
    [Fact]
    public void Wifi_rejection_report_never_copies_credentials_or_network_identifiers()
    {
        const string raw = "CTRL-EVENT-SSID-TEMP-DISABLED id=0 ssid=PrivateNetwork reason=WRONG_KEY password=do-not-export enrollment=private-code\nCTRL-EVENT-DISCONNECTED\nCTRL-EVENT-NETWORK-NOT-FOUND";
        string report = BootDiagnostics.WifiStatus(true, true, false, "ssid=PrivateNetwork\npsk=do-not-export", raw);
        Assert.Contains("authentication rejected", report);
        foreach (string secret in new[] { "PrivateNetwork", "do-not-export", "private-code", "ssid=", "psk=" }) Assert.DoesNotContain(secret, report);
    }

    [Fact]
    public void Current_connection_supersedes_old_failure_and_distinguishes_dhcp()
    {
        const string events = "reason=WRONG_KEY\nCTRL-EVENT-CONNECTED";
        Assert.Equal("associated; waiting for DHCP address", BootDiagnostics.WifiStatus(true, true, false, "wpa_state=COMPLETED", events));
        Assert.Equal("connected; address assigned", BootDiagnostics.WifiStatus(true, true, true, "wpa_state=COMPLETED", events));
        Assert.Equal("disabled by setup", BootDiagnostics.WifiStatus(false, false, false, "", events));
        Assert.Contains("device unavailable", BootDiagnostics.WifiStatus(true, false, false, "", events));
    }

    [Fact]
    public void Installer_log_rolls_at_a_utf8_byte_bound()
    {
        string log = "";
        for (int i = 0; i < 2000; i++) log = BootDiagnostics.AppendBounded(log, $"{i}: status — waiting for authenticated time\n");
        Assert.True(Encoding.UTF8.GetByteCount(log) <= BootDiagnostics.MaximumBytes);
        Assert.EndsWith("1999: status — waiting for authenticated time\n", log);
        Assert.DoesNotContain("\uFFFD", log);
    }
}
