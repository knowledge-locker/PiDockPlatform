using System.Text.RegularExpressions;
namespace PiDock.Core;

public sealed record ApplicationManifest(string Name, string Image, int MemoryMiB = 128, double CpuLimit = 0.5,
    int? ContainerPort = null, int? HostPort = null)
{
    public void Validate()
    {
        if (!IdentityRules.IsId(Name) || Name.Length > 40 ||
            !Regex.IsMatch(Image, @"\A[a-z0-9][a-z0-9./:_-]{0,200}@sha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant) ||
            MemoryMiB is < 32 or > 2048 || !double.IsFinite(CpuLimit) || CpuLimit is < 0.1 or > 4 ||
            (ContainerPort.HasValue != HostPort.HasValue) || ContainerPort is < 1 or > 65535 || HostPort is < 1024 or > 65535)
            throw new ArgumentException("Use a simple application name, a Linux ARM64 image pinned by sha256 digest, memory 32–2048 MiB, CPU 0.1–4, and optional paired ports (host ≥1024).");
    }
}
public sealed record ApplicationSummary(string Id, string[] Names, string Image, string State, string Status);
public static class UnixHttp
{
    public static HttpClient Connect(string socketPath, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellation) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath), cancellation); return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = timeout ?? TimeSpan.FromSeconds(15) };
    }
}
