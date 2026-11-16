using System.Net.WebSockets;
using System.Text.Json;

namespace PiDock.Core;

public sealed record RemoteDeviceProof(string CertificatePem, string Signature, string LocalProof);
public sealed record RemoteTunnelCommand(string Type, Guid Channel = default, string Token = "", Guid Session = default, string Actor = "", DateTimeOffset ExpiresAt = default);
public sealed record RemoteAuditEvent(DateTimeOffset At, string Event, string Actor, string Tenant, string Device, Guid Session, string Outcome);
public sealed class RemoteAudit
{
    private readonly object gate = new(); private readonly string path; private RemoteAuditEvent[] entries;
    public RemoteAudit(string directory) { path = Path.Combine(StateFiles.PrepareDirectory(directory), "remote-access-audit.json"); entries = File.Exists(path) ? JsonSerializer.Deserialize<RemoteAuditEvent[]>(File.ReadAllText(path), StateFiles.Json) ?? throw new IOException("Invalid remote audit state.") : []; }
    public void Record(string action, string actor, string tenant, string device, Guid session, string outcome)
    {
        if (new[] { action, actor, tenant, device, outcome }.Any(s => s.Length > 256 || s.Any(char.IsControl))) throw new ArgumentException("Invalid audit event.");
        lock (gate) { var next = entries.TakeLast(1999).Append(new RemoteAuditEvent(DateTimeOffset.UtcNow, action, actor, tenant, device, session, outcome)).ToArray(); StateFiles.WriteAtomic(path, JsonSerializer.Serialize(next, StateFiles.Json)); entries = next; }
    }
    public RemoteAuditEvent[] Read(string? tenant = null, string? device = null) { lock (gate) return entries.Where(e => (tenant is null || e.Tenant == tenant) && (device is null || e.Device == device)).TakeLast(40).Reverse().ToArray(); }
}
public static class RemoteProtocol
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public static byte[] ProofBytes(string nonce) => System.Text.Encoding.UTF8.GetBytes("PiDock.RemoteAccess.v1\n" + nonce);
    public static async Task Send<T>(WebSocket socket, T value, CancellationToken ct) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, StateFiles.Json), WebSocketMessageType.Text, true, ct);
    public static async Task<T> Read<T>(WebSocket socket, CancellationToken ct)
    {
        var bytes = new byte[16384]; int length = 0;
        while (true) {
            if (length == bytes.Length) throw new IOException("Remote control message too large.");
            ValueWebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(bytes.AsMemory(length), ct); } catch (ObjectDisposedException e) { throw new IOException("Remote connection closed.", e); }
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Invalid remote control frame.");
            length += result.Count;
            if (result.EndOfMessage) return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, length), StateFiles.Json) ?? throw new IOException("Empty remote message.");
        }
    }
    public static async Task Copy(WebSocket first, WebSocket second, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Pump(WebSocket from, WebSocket to) {
            var bytes = new byte[16384];
            while (!stop.IsCancellationRequested) { var r = await from.ReceiveAsync(bytes.AsMemory(), stop.Token); if (r.MessageType == WebSocketMessageType.Close) break; await to.SendAsync(bytes.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, stop.Token); }
        }
        var a = Pump(first, second); var b = Pump(second, first);
        await Task.WhenAny(a, b); await stop.CancelAsync(); first.Abort(); second.Abort();
        try { await Task.WhenAll(a, b); } catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException) { }
    }
}
// A bounded-buffer byte stream; no complete HTTP request, response or upload is accumulated in memory.
public sealed class RemoteSocketStream(WebSocket socket) : Stream
{
    public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
        if (buffer.Length == 0) return 0;
        while (true) { var r = await socket.ReceiveAsync(buffer, ct); if (r.MessageType == WebSocketMessageType.Close) return 0; if (r.MessageType != WebSocketMessageType.Binary) throw new IOException("Invalid tunnel data."); if (r.Count > 0) return r.Count; }
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { while (buffer.Length > 0) { int count = Math.Min(16384, buffer.Length); await socket.SendAsync(buffer[..count], WebSocketMessageType.Binary, true, ct); buffer = buffer[count..]; } }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Flush() { } public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { socket.Abort(); socket.Dispose(); } base.Dispose(disposing); }
}
