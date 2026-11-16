using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

public interface IApplicationBootstrapDelivery
{
    Task Deliver(string containerId, ApplicationBootstrapPacket packet, Func<Task> start, CancellationToken ct);
}
// Docker's HTTP upgrade is a duplex byte stream, not a normal buffered HTTP response.
public sealed class ApplicationBootstrapDelivery(string socketPath) : IApplicationBootstrapDelivery
{
    public async Task Deliver(string containerId, ApplicationBootstrapPacket packet, Func<Task> start, CancellationToken ct)
    {
        if (containerId.Length != 64 || !containerId.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid container ID.");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var request = Encoding.ASCII.GetBytes($"POST /v1.47/containers/{containerId}/attach?stream=1&stdin=1&stdout=1&stderr=0 HTTP/1.1\r\nHost: localhost\r\nConnection: Upgrade\r\nUpgrade: tcp\r\nContent-Length: 0\r\n\r\n");
        await stream.WriteAsync(request, ct);
        var header = new List<byte>(); var single = new byte[1];
        while (header.Count < 8192)
        {
            await stream.ReadExactlyAsync(single, ct); header.Add(single[0]);
            if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
        }
        if (header.Count >= 8192 || !Encoding.ASCII.GetString(header.ToArray()).StartsWith("HTTP/1.1 101 ", StringComparison.Ordinal)) throw new IOException("Docker attach unavailable.");
        await start();
        var payload = JsonSerializer.SerializeToUtf8Bytes(packet, StateFiles.Json);
        if (payload.Length > ApplicationResourceRules.MaximumPacketBytes) throw new IOException("Bootstrap exceeds limit.");
        var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        await stream.WriteAsync(length, ct); await stream.WriteAsync(payload, ct); await stream.FlushAsync(ct);
        await ReadAcknowledgement(stream, packet, ct);
    }
    public static async Task ReadAcknowledgement(Stream stream, ApplicationBootstrapPacket packet, CancellationToken ct)
    {
        var line = new List<byte>(); var header = new byte[8];
        while (line.Count <= 1024)
        {
            await stream.ReadExactlyAsync(header, ct);
            int size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            if (header[0] != 1 || header[1] != 0 || header[2] != 0 || header[3] != 0 || size is < 1 or > 32768) throw new IOException("Invalid bootstrap acknowledgement.");
            var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, ct);
            foreach (var b in bytes)
            {
                if (b == 10)
                {
                    ApplicationBootstrapAck? ack;
                    try { ack = JsonSerializer.Deserialize<ApplicationBootstrapAck>(line.ToArray(), StateFiles.Json); }
                    catch (JsonException) { throw new IOException("Invalid bootstrap acknowledgement."); }
                    if (ack is null || ack.SchemaVersion != 1 || ack.DeliveryId != packet.DeliveryId || ack.AppId != packet.AppId || !ack.Ready) throw new IOException("App did not accept bootstrap.");
                    return;
                }
                if (line.Count >= 1024) throw new IOException("Bootstrap acknowledgement exceeds limit.");
                line.Add(b);
            }
        }
        throw new IOException("Bootstrap acknowledgement exceeds limit.");
    }
}
