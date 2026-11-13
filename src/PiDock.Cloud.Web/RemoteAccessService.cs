using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Cloud.Web;

public sealed record RemoteSessionView(Guid Id, string Actor, DateTimeOffset ExpiresAt, string LaunchPath);
public sealed class RemoteGrant(Guid id, ClaimsPrincipal user, string tenant, string device, string host, DateTimeOffset expires)
{
    public Guid Id { get; } = id; public ClaimsPrincipal User { get; } = new(user.Clone().Identities.Select(i => new ClaimsIdentity(i)));
    public string Tenant { get; } = tenant; public string Device { get; } = device; public string Host { get; } = host;
    public DateTimeOffset ExpiresAt { get; } = expires;
    internal string LaunchCode { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal string Cookie { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal bool Launched; internal int Channels;
    public CancellationTokenSource Stop { get; } = new();
}
public sealed class RemoteAccessService : BackgroundService
{
    private readonly object gate = new(); private readonly UserStore users; private readonly EnrollmentStore enrollments; private readonly TimeProvider clock; private readonly ILogger<RemoteAccessService> logger;
    private readonly RemoteAudit audit; private readonly Dictionary<Guid, RemoteGrant> grants = [];
    private readonly ConcurrentDictionary<(string Tenant, string Device), RemoteAgentConnection> devices = new();
    private readonly ConcurrentDictionary<Guid, PendingRemoteChannel> channels = new();
    public Uri? CloudOrigin { get; } public string? Domain { get; }
    public bool Configured => CloudOrigin is not null && Domain is not null;
    public RemoteAccessService(IConfiguration settings, UserStore users, EnrollmentStore enrollments, StateDirectory state, TimeProvider clock, ILogger<RemoteAccessService> logger)
    {
        this.users = users; this.enrollments = enrollments; this.clock = clock; this.logger = logger; audit = new(Path.Combine(state.Path, "remote-access"));
        var origin = settings["RemoteAccess:CloudOrigin"]; var domain = settings["RemoteAccess:Domain"]?.ToLowerInvariant();
        if (origin is null && domain is null) return;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            domain is null || Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.Contains('*') || uri.Host == domain || uri.Host.EndsWith("." + domain, StringComparison.Ordinal)) throw new InvalidOperationException("Configure a distinct HTTPS remote-access domain and cloud origin.");
        CloudOrigin = uri; Domain = domain;
    }
    public string DeviceHost(string tenant, string device) => "d-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenant + "/" + device)))[..24] + "." + Domain;
    public bool IsRemoteHost(string host) => Domain is not null && host.EndsWith("." + Domain, StringComparison.OrdinalIgnoreCase);
    public bool KnownHost(string host) => Configured && enrollments.RemoteRegistrations().Any(d => DeviceHost(d.Tenant, d.Device).Equals(host, StringComparison.OrdinalIgnoreCase));
    public bool Connected(string tenant, string device) => devices.TryGetValue((tenant, device), out var c) && c.Socket.State == WebSocketState.Open && clock.GetUtcNow() - c.LastSeen < TimeSpan.FromSeconds(30);
    public RemoteAuditEvent[] Audit(ClaimsPrincipal user, string device) => EnrollmentStore.CanManage(user) && users.IsCurrent(user) ? audit.Read(user.FindFirstValue("tenant"), device) : [];
    public RemoteSessionView Begin(ClaimsPrincipal user, string device)
    {
        if (!Configured || !users.IsCurrent(user) || !EnrollmentStore.CanManage(user) || user.FindFirstValue("session") is null) throw new UnauthorizedAccessException();
        var tenant = user.FindFirstValue("tenant")!;
        lock (gate) {
            if (!Connected(tenant, device) || !enrollments.RemoteRegistrations().Contains((tenant, device))) throw new InvalidOperationException("The Pi must locally allow remote interface access and be connected.");
            foreach (var previous in grants.Values.Where(g => g.Tenant == tenant && g.Device == device && SameBrowser(g, user)).ToArray()) Close(previous, "Replaced by fresh approval");
            if (grants.Count >= 64 || grants.Values.Count(g => g.Tenant == tenant && g.Device == device) >= 4) throw new InvalidOperationException("Remote session capacity reached.");
            var grant = new RemoteGrant(Guid.NewGuid(), user, tenant, device, DeviceHost(tenant, device), clock.GetUtcNow() + RemoteProtocol.Lifetime);
            audit.Record("SessionAuthorized", user.Identity!.Name!, tenant, device, grant.Id, "Fresh MFA; five minutes");
            grants.Add(grant.Id, grant); grant.Stop.CancelAfter(RemoteProtocol.Lifetime);
            return View(grant);
        }
    }
    private static bool SameBrowser(RemoteGrant grant, ClaimsPrincipal user) => grant.User.Identity?.Name == user.Identity?.Name && grant.User.FindFirstValue("session") == user.FindFirstValue("session");
    private static RemoteSessionView View(RemoteGrant grant) => new(grant.Id, grant.User.Identity!.Name!, grant.ExpiresAt, "/remote-access/launch/" + grant.Id);
    public RemoteSessionView[] Sessions(ClaimsPrincipal user, string device) { if (!EnrollmentStore.CanManage(user) || !users.IsCurrent(user)) return []; lock (gate) return grants.Values.Where(g => g.Tenant == user.FindFirstValue("tenant") && g.Device == device && Valid(g)).Select(g => View(g) with { LaunchPath = SameBrowser(g, user) ? View(g).LaunchPath : "" }).ToArray(); }
    public RemoteGrant? Launch(Guid id, ClaimsPrincipal user) { lock (gate) return grants.TryGetValue(id, out var grant) && SameBrowser(grant, user) && users.IsCurrent(user) && !grant.Launched && Valid(grant) ? grant : null; }
    public RemoteGrant? Consume(string host, string code)
    {
        if (code.Length != 64 || !code.All(Uri.IsHexDigit)) return null;
        lock (gate) {
            var grant = grants.Values.FirstOrDefault(g => g.Host == host && !g.Launched && Fixed(g.LaunchCode, code) && Valid(g));
            if (grant is null) return null;
            audit.Record("SessionOpened", grant.User.Identity!.Name!, grant.Tenant, grant.Device, grant.Id, "Browser grant consumed");
            grant.Launched = true; grant.LaunchCode = ""; return grant;
        }
    }
    public RemoteGrant? Authenticate(string host, string cookie) { if (cookie.Length != 64) return null; lock (gate) return grants.Values.FirstOrDefault(g => g.Host == host && g.Launched && Fixed(g.Cookie, cookie) && Valid(g)); }
    private bool Valid(RemoteGrant grant) => !grant.Stop.IsCancellationRequested && grant.ExpiresAt > clock.GetUtcNow() && users.IsCurrent(grant.User) && EnrollmentStore.CanManage(grant.User) && Connected(grant.Tenant, grant.Device);
    private static bool Fixed(string a, string b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public void Revoke(ClaimsPrincipal user, Guid id) { lock (gate) { if (!users.IsCurrent(user) || !EnrollmentStore.CanManage(user) || !grants.TryGetValue(id, out var grant) || grant.Tenant != user.FindFirstValue("tenant")) throw new UnauthorizedAccessException(); Close(grant, "Revoked by " + user.Identity!.Name); } }
    private void Close(RemoteGrant grant, string reason)
    {
        grants.Remove(grant.Id); grant.Stop.Cancel();
        if (devices.TryGetValue((grant.Tenant, grant.Device), out var device)) _ = NotifyClosed(device, grant.Id);
        try { audit.Record("SessionClosed", grant.User.Identity!.Name!, grant.Tenant, grant.Device, grant.Id, reason); } catch (IOException) { logger.LogError("Remote access close audit unavailable"); }
    }
    private static async Task NotifyClosed(RemoteAgentConnection device, Guid session) { try { await device.Send(new("close", Session: session), device.Stop.Token); } catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException) { } }
    internal async Task Agent(HttpContext context)
    {
        if (!Configured || !context.WebSockets.IsWebSocketRequest || context.Request.Host.Host != CloudOrigin!.Host || !context.Request.IsHttps) { context.Response.StatusCode = 404; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); handshake.CancelAfter(TimeSpan.FromSeconds(10));
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await RemoteProtocol.Send(socket, new RemoteTunnelCommand("challenge", Token: nonce), handshake.Token);
        var proof = await RemoteProtocol.Read<RemoteDeviceProof>(socket, handshake.Token);
        var identity = enrollments.AuthenticateRemoteDevice(proof.CertificatePem, nonce, proof.Signature);
        if (identity is null || proof.LocalProof.Length != 64 || !proof.LocalProof.All(Uri.IsHexDigit)) { socket.Abort(); return; }
        var key = identity.Value;
        using var connection = new RemoteAgentConnection(socket, proof.LocalProof, clock.GetUtcNow(), context.RequestAborted);
        if (devices.Count >= 1000) { socket.Abort(); return; }
        if (!devices.TryAdd(key, connection)) { socket.Abort(); return; }
        try {
            await connection.Send(new("ready"), handshake.Token);
            while (!connection.Stop.IsCancellationRequested) {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var ping = await RemoteProtocol.Read<RemoteTunnelCommand>(socket, timeout.Token);
                if (ping.Type != "ping" || !enrollments.RemoteRegistrations().Contains(key)) break;
                connection.LastSeen = clock.GetUtcNow();
            }
        }
        finally {
            devices.TryRemove(key, out _); connection.Stop.Cancel(); socket.Abort();
            lock (gate) foreach (var grant in grants.Values.Where(g => g.Tenant == key.Tenant && g.Device == key.Device).ToArray()) Close(grant, "Pi disconnected or permission revoked");
        }
    }
    internal async Task Data(HttpContext context, Guid id)
    {
        if (!Configured || !context.Request.IsHttps || context.Request.Host.Host != CloudOrigin!.Host || !context.WebSockets.IsWebSocketRequest || !channels.TryGetValue(id, out var pending)) { context.Response.StatusCode = 404; return; }
        var token = context.Request.Headers.Authorization.ToString();
        if (!Fixed("Bearer " + pending.Token, token) || !channels.TryRemove(id, out _) || !Valid(pending.Grant)) { context.Response.StatusCode = 404; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (!pending.Ready.TrySetResult(socket)) return;
        try { await Task.Delay(Timeout.InfiniteTimeSpan, pending.Stop.Token); } catch (OperationCanceledException) { } finally { socket.Abort(); }
    }
    public string Proof(RemoteGrant grant) => devices.TryGetValue((grant.Tenant, grant.Device), out var device) ? device.LocalProof : throw new IOException("Pi disconnected.");
    public async ValueTask<Stream> Open(RemoteGrant grant, CancellationToken ct)
    {
        lock (gate) { if (!Valid(grant) || grant.Channels >= 12) throw new IOException("Remote session unavailable."); grant.Channels++; }
        var id = Guid.NewGuid(); var pending = new PendingRemoteChannel(grant, ct);
        channels[id] = pending;
        try {
            if (!devices.TryGetValue((grant.Tenant, grant.Device), out var connection)) throw new IOException("Pi disconnected.");
            await connection.Send(new("open", id, pending.Token, grant.Id, grant.User.Identity!.Name!, grant.ExpiresAt), ct);
            var socket = await pending.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            return new OwnedRemoteStream(socket, () => { pending.Stop.Cancel(); pending.Stop.Dispose(); lock (gate) grant.Channels--; });
        }
        catch { pending.Stop.Cancel(); pending.Stop.Dispose(); lock (gate) grant.Channels--; throw; }
        finally { channels.TryRemove(id, out _); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); while (await timer.WaitForNextTickAsync(stoppingToken)) { lock (gate) foreach (var grant in grants.Values.Where(g => !Valid(g)).ToArray()) Close(grant, "Expired, signed out or disconnected"); } }
        catch (OperationCanceledException) { }
        finally { lock (gate) foreach (var grant in grants.Values.ToArray()) Close(grant, "Cloud restarting"); foreach (var connection in devices.Values) connection.Stop.Cancel(); }
    }
    private sealed class PendingRemoteChannel(RemoteGrant grant, CancellationToken ct)
    {
        public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); public RemoteGrant Grant { get; } = grant;
        public TaskCompletionSource<WebSocket> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(ct, grant.Stop.Token);
    }
    private sealed class OwnedRemoteStream(WebSocket socket, Action closed) : Stream
    {
        private readonly RemoteSocketStream stream = new(socket); private int disposed;
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => stream.ReadAsync(b, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => stream.WriteAsync(b, ct);
        public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => stream.ReadAsync(b, o, c, ct);
        public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) => stream.WriteAsync(b, o, c, ct);
        public override int Read(byte[] b, int o, int c) => stream.Read(b, o, c); public override void Write(byte[] b, int o, int c) => stream.Write(b, o, c);
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing && Interlocked.Exchange(ref disposed, 1) == 0) { stream.Dispose(); closed(); } base.Dispose(disposing); }
    }
}
internal sealed class RemoteAgentConnection(WebSocket socket, string proof, DateTimeOffset now, CancellationToken ct) : IDisposable
{
    public WebSocket Socket { get; } = socket; public string LocalProof { get; } = proof; public DateTimeOffset LastSeen { get; set; } = now;
    public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(ct);
    private readonly SemaphoreSlim send = new(1);
    public async Task Send(RemoteTunnelCommand command, CancellationToken ct) { await send.WaitAsync(ct); try { await RemoteProtocol.Send(Socket, command, ct); } finally { send.Release(); } }
    public void Dispose() { Stop.Cancel(); Stop.Dispose(); send.Dispose(); }
}
