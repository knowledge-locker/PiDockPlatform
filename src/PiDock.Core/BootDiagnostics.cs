using System.Text;

namespace PiDock.Core;

// Only fixed status values cross the boundary from private system logs to the
// owner-readable installer report. Never return a raw journal or wpa_cli line.
public static class BootDiagnostics
{
    public const int MaximumBytes = 65536;
    public const string DirectoryPath = "/var/lib/pidock/diagnostics";
    public const string LogPath = DirectoryPath + "/install.log";
    public const string CardLogPath = "/boot/control/pidock-install.log";

    public static string WifiStatus(bool enabled, bool devicePresent, bool hasAddress, string controlStatus, string events)
    {
        if (!enabled) return "disabled by setup";
        if (!devicePresent) return "device unavailable; check driver or firmware";
        string status = hasAddress ? "connected; address assigned" : "connecting";
        foreach (var line in events.Split('\n'))
        {
            if (line.Contains("reason=WRONG_KEY", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("pre-shared key may be incorrect", StringComparison.OrdinalIgnoreCase))
                status = "authentication rejected; check Wi-Fi password and access-point security settings";
            else if (line.Contains("CTRL-EVENT-AUTH-REJECT", StringComparison.Ordinal) || line.Contains("CTRL-EVENT-ASSOC-REJECT", StringComparison.Ordinal))
                status = "association rejected by access point";
            else if (line.Contains("CTRL-EVENT-NETWORK-NOT-FOUND", StringComparison.Ordinal) && !status.Contains("rejected", StringComparison.Ordinal)) status = "configured network not found";
            else if (line.Contains("CTRL-EVENT-CONNECTED", StringComparison.Ordinal)) status = hasAddress ? "connected; address assigned" : "associated; waiting for DHCP address";
            else if (line.Contains("CTRL-EVENT-DISCONNECTED", StringComparison.Ordinal) && !status.Contains("rejected", StringComparison.Ordinal)) status = "disconnected; retrying";
        }
        if (controlStatus.Split('\n').Any(x => x.Trim() == "wpa_state=COMPLETED"))
            return hasAddress ? "connected; address assigned" : "associated; waiting for DHCP address";
        if (controlStatus.Split('\n').Any(x => x.Trim() == "wpa_state=INTERFACE_DISABLED")) return "radio disabled";
        return status;
    }

    public static string AppendBounded(string previous, string entry)
    {
        // Discard complete oldest lines, keeping UTF-8 text and a strict size bound.
        string value = previous + entry;
        while (Encoding.UTF8.GetByteCount(value) > MaximumBytes)
        {
            int newline = value.IndexOf('\n');
            if (newline < 0) return "";
            value = value[(newline + 1)..];
        }
        return value;
    }
}
