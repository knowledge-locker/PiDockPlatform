using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
namespace PiDock.Cloud.Web;

public static class TrustedProxy
{
    public static ForwardedHeadersOptions Options(IEnumerable<string> proxies, IEnumerable<string> networks)
    {
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
        options.KnownProxies.Clear(); options.KnownIPNetworks.Clear();
        foreach (var address in proxies) options.KnownProxies.Add(IPAddress.Parse(address));
        foreach (var network in networks)
        {
            var range = System.Net.IPNetwork.Parse(network);
            if (range.PrefixLength == 0) throw new ArgumentException("A trusted proxy network cannot include every address.");
            options.KnownIPNetworks.Add(range);
        }
        if (options.KnownProxies.Count + options.KnownIPNetworks.Count == 0) throw new ArgumentException("Configure at least one trusted proxy address or network.");
        return options;
    }
}
