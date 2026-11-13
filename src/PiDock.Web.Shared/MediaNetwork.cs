using System.Net;
using System.Net.Sockets;
using PiDock.Core;
namespace PiDock.Web.Shared;

public sealed record MediaInterface(bool Enabled, string Method = "dhcp", string? Address = null, string? Gateway = null, string[]? Dns = null);
public sealed record MediaNetwork(MediaInterface Ethernet, MediaInterface Wifi);

public static class MediaNetworking
{
    public static void Validate(MediaNetwork network, MediaWifi? wifi, bool node = false)
    {
        if (network.Ethernet is null || network.Wifi is null) throw new InvalidDataException("Both network interfaces must be configured.");
        foreach (var port in new[] { network.Ethernet, network.Wifi })
        {
            if (port.Method is not ("dhcp" or "static") || port.Dns is { Length: > 4 }) throw new InvalidDataException("Invalid interface configuration.");
            if (!port.Enabled || port.Method == "dhcp")
            {
                if (port.Address is not null || port.Gateway is not null || port.Dns is { Length: > 0 }) throw new InvalidDataException("Only static interfaces accept addresses, gateways and DNS servers.");
                continue;
            }
            var parts = port.Address?.Split('/') ?? [];
            if (parts.Length != 2 || !Ipv4(parts[0], out var address) || !int.TryParse(parts[1], out var prefix) || prefix is < 1 or > 30)
                throw new InvalidDataException("Enter a static IPv4 address with a prefix from 1 to 30.");
            uint mask = uint.MaxValue << (32 - prefix), host = address & ~mask;
            if (host == 0 || host == ~mask) throw new InvalidDataException("A static address cannot be a network or broadcast address.");
            if (port.Gateway is not null && (!Ipv4(port.Gateway, out var gateway) || (gateway & mask) != (address & mask) || gateway == address || (gateway & ~mask) == 0 || (gateway & ~mask) == ~mask))
                throw new InvalidDataException("The gateway must be another host on the static subnet.");
            foreach (var dns in port.Dns ?? [])
                if (!IPAddress.TryParse(dns, out var ip) || ip.ScopeIdSafe() || ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] is 0 or 127 or >= 224 || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                    throw new InvalidDataException("Invalid DNS server address.");
        }
        if (node)
        {
            if (network.Ethernet.Enabled || network.Wifi.Enabled || wifi is not null) throw new InvalidDataException("Unpaired nodes cannot configure external network access.");
        }
        else if (!network.Ethernet.Enabled && !network.Wifi.Enabled) throw new InvalidDataException("Enable Ethernet or Wi-Fi for a headless Main device.");
        if (network.Wifi.Enabled != (wifi is not null)) throw new InvalidDataException("Enabled Wi-Fi requires wireless credentials.");
    }
    private static bool ScopeIdSafe(this IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.ScopeId != 0 || ip.IsIPv6Multicast);
    private static bool Ipv4(string text, out uint value)
    {
        value = 0;
        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        foreach (var part in parts)
        {
            if (part.Length is < 1 or > 3 || part.Any(c => !char.IsAsciiDigit(c)) || part.Length > 1 && part[0] == '0' || !byte.TryParse(part, out var octet)) return false;
            value = (value << 8) | octet;
        }
        return value >> 24 is > 0 and < 224 && value >> 24 != 127;
    }
    public static string UsbLink(bool node) =>
        "[Match]\n"+(node?"Name=usb0\n":"Name=pn*\nProperty=PIDOCK_USB_NODE=1\n")+
        "[Link]\nRequiredForOnline=no\n[Network]\nDHCP=no\nIPv6AcceptRA=no\nLinkLocalAddressing=ipv6\nMulticastDNS=no\nLLMNR=no\nIPForward=no\nConfigureWithoutCarrier=yes\nAddress="+(node?"fe80::1/64\n":"fe80::2/64\n");
    public static string UsbFallback => "[Match]\nProperty=PIDOCK_USB_NODE=1\n[Link]\nActivationPolicy=always-down\n[Network]\nDHCP=no\nIPv6AcceptRA=no\nLinkLocalAddressing=no\n";
    public static string Render(MediaInterface port, bool wireless)
    {
        // Called only after validating the complete setup; canonical IP formatting prevents config injection.
        string text = $"[Match]\nName={(wireless ? "wlan0" : "eth0 end0 en*")}\n";
        if (!port.Enabled) return text + "[Link]\nActivationPolicy=always-down\n[Network]\nDHCP=no\nIPv6AcceptRA=no\nLinkLocalAddressing=no\n";
        text += "[Network]\nMulticastDNS=yes\n";
        if (port.Method == "dhcp") return text + "DHCP=yes\nIPv6AcceptRA=yes\n";
        var address = port.Address!.Split('/');
        text += $"DHCP=no\nIPv6AcceptRA=no\nAddress={IPAddress.Parse(address[0])}/{int.Parse(address[1])}\n";
        if (port.Gateway is not null) text += $"Gateway={IPAddress.Parse(port.Gateway)}\n";
        foreach (var dns in port.Dns ?? []) text += $"DNS={IPAddress.Parse(dns)}\n";
        return text;
    }
}
