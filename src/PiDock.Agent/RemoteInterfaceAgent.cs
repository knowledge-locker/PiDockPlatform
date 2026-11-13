using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiDock.Core;

namespace PiDock.Agent;

public sealed class RemoteInterfaceAgent : BackgroundService
{
    private readonly CloudEnrollmentClient enrollment; private readonly string path, certificatePath; private readonly RemoteAudit audit; private readonly ILogger<RemoteInterfaceAgent> logger;
    private readonly object gate = new(); private bool allowed; private CancellationTokenSource? connectionStop;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> sessions = new();
    private readonly SemaphoreSlim slots = new(16);
    private readonly Func<Uri, string?, CancellationToken, Task<WebSocket>>? connector;
    public string LocalProof { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public bool Allowed { get { lock (gate) return allowed; } }
    public bool Connected { get; private set; }
    public RemoteAuditEvent[] Events => audit.Read();
    public RemoteInterfaceAgent(CloudEnrollmentClient enrollment, string directory, ILogger<RemoteInterfaceAgent> logger, string certificatePath = "/etc/pidock/appliance.crt", Func<Uri, string?, CancellationToken, Task<WebSocket>>? connector = null)
    {
        this.connector = connector; this.enrollment = enrollment; this.logger = logger; this.certificatePath = certificatePath;
        path = Path.Combine(StateFiles.PrepareDirectory(directory), "remote-interface-policy.json"); audit = new(directory);
        allowed = File.Exists(path) && JsonSerializer.Deserialize<bool>(File.ReadAllText(path), StateFiles.Json);
    }
    public void RecordImageConsent(string owner, CloudConnection connection, bool control, bool ui) =>
        audit.Record("ImageProvisioningConsent",owner,connection.TenantId,connection.DeviceId,Guid.Empty,$"Container control: {control}; remote interface: {ui}");
    public void SetAllowed(bool enabled, string actor)
    {
        lock (gate) {
            if (!enabled) { allowed = false; connectionStop?.Cancel(); foreach (var entry in sessions.Values) entry.Cancel(); }
            // Audit intent before enabling; an audit/storage failure must not create new access.
            try { audit.Record("PermissionChangeRequested", actor, "local", "local", Guid.Empty, enabled ? "Allow" : "Off"); }
            catch (IOException) when (!enabled) { logger.LogError("Remote interface permission audit unavailable during disable"); }
            StateFiles.WriteAtomic(path, JsonSerializer.Serialize(enabled)); allowed = enabled; connectionStop?.Cancel();
            try { audit.Record("PermissionChanged", actor, "local", "local", Guid.Empty, enabled ? "Allowed" : "Off"); }
            catch (IOException) {
                allowed = false; connectionStop?.Cancel();
                try { StateFiles.WriteAtomic(path, "false"); } catch (IOException) { logger.LogError("Remote interface permission could not be persisted"); }
                throw;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested) {
            if (Allowed && enrollment.Connection is { } registration) {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (gate) { connectionStop = stop; if (!allowed) stop.Cancel(); }
                try { await Connect(registration, stop.Token); }
                catch (Exception e) when (e is IOException or HttpRequestException or WebSocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException or CryptographicException or JsonException) { logger.LogInformation("Remote interface relay disconnected"); }
                finally { Connected = false; lock (gate) connectionStop = null; foreach (var item in sessions.Values) item.Cancel(); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    private static Uri Endpoint(CloudConnection c, string path) {
        var origin = new Uri(c.CloudUrl);
        if (origin.Scheme != "https" || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0) throw new IOException("Invalid registered cloud.");
        return new UriBuilder(new Uri(origin, path)) { Scheme = "wss" }.Uri;
    }
    private async Task Connect(CloudConnection c, CancellationToken ct)
    {
        using var handler = CloudEnrollmentClient.CreateHandler(c.ServerCaPem ?? c.CaPem, c.ServerCaPem is null);
        using var invoker = new HttpMessageInvoker(handler);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct); handshake.CancelAfter(TimeSpan.FromSeconds(15));
        using var socket = await ConnectSocket(Endpoint(c, "api/remote/agent"), invoker, null, handshake.Token);
        var challenge = await RemoteProtocol.Read<RemoteTunnelCommand>(socket, handshake.Token);
        if (challenge.Type != "challenge" || challenge.Token.Length != 64 || !challenge.Token.All(Uri.IsHexDigit)) throw new IOException("Invalid relay challenge.");
        using var key = RSA.Create(); key.ImportFromPem(c.PrivateKeyPem);
        await RemoteProtocol.Send(socket, new RemoteDeviceProof(c.CertificatePem, Convert.ToBase64String(key.SignData(RemoteProtocol.ProofBytes(challenge.Token), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)), LocalProof), handshake.Token);
        if ((await RemoteProtocol.Read<RemoteTunnelCommand>(socket, handshake.Token)).Type != "ready") throw new IOException("Remote identity rejected.");
        Connected = true;
        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new List<Task>();
        async Task Heartbeat() { while (!running.IsCancellationRequested) { await RemoteProtocol.Send(socket, new RemoteTunnelCommand("ping"), running.Token); await Task.Delay(TimeSpan.FromSeconds(10), running.Token); } }
        var heartbeat = Heartbeat();
        try {
            while (Allowed && !ct.IsCancellationRequested) {
                var command = await RemoteProtocol.Read<RemoteTunnelCommand>(socket, ct);
                if (command.Type == "close") { if (sessions.TryGetValue(command.Session, out var active)) active.Cancel(); continue; }
                var now = DateTimeOffset.UtcNow;
                if (command.Type != "open" || command.Channel == Guid.Empty || command.Session == Guid.Empty || command.Token.Length != 64 || !command.Token.All(Uri.IsHexDigit) || command.Actor.Length is < 1 or > 64 || command.Actor.Any(char.IsControl) || command.ExpiresAt <= now || command.ExpiresAt > now + RemoteProtocol.Lifetime + TimeSpan.FromSeconds(10) || !slots.Wait(0)) continue;
                tasks.RemoveAll(t => t.IsCompleted);
                tasks.Add(Bridge(c, command, invoker, ct));
            }
        }
        finally { running.Cancel(); socket.Abort(); foreach (var active in sessions.Values) active.Cancel(); try { await heartbeat; } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { } await Task.WhenAll(tasks); }
    }
    private async Task<WebSocket> ConnectSocket(Uri endpoint, HttpMessageInvoker invoker, string? token, CancellationToken ct)
    {
        if (connector is not null) return await connector(endpoint, token, ct);
        var socket = new ClientWebSocket();
        if (token is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        try { await socket.ConnectAsync(endpoint, invoker, ct); return socket; }
        catch { socket.Dispose(); throw; }
    }
    private async Task Bridge(CloudConnection c, RemoteTunnelCommand command, HttpMessageInvoker invoker, CancellationToken ct)
    {
        try {
            CancellationToken sessionToken;
            lock (gate) {
                foreach (var old in sessions.Where(e => e.Value.IsCancellationRequested).ToArray()) if (sessions.TryRemove(old.Key, out var removed)) removed.Dispose();
                if (!sessions.TryGetValue(command.Session, out var session)) {
                    if (!allowed || sessions.Count >= 8) throw new IOException("Remote session limit reached.");
                    audit.Record("SessionConnected", command.Actor, c.TenantId, c.DeviceId, command.Session, "Opened");
                    session = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    session.CancelAfter(command.ExpiresAt - DateTimeOffset.UtcNow);
                    session.Token.Register(() => { try { audit.Record("SessionClosed", command.Actor, c.TenantId, c.DeviceId, command.Session, "Closed or expired"); } catch (IOException) { logger.LogError("Remote interface close audit unavailable"); } });
                    sessions[command.Session] = session;
                }
                sessionToken = session.Token;
            }
            using var channel = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, ct); channel.CancelAfter(command.ExpiresAt - DateTimeOffset.UtcNow);
            using var data = await ConnectSocket(Endpoint(c, "api/remote/data/" + command.Channel), invoker, command.Token, channel.Token);
            using var tcp = new TcpClient(); await tcp.ConnectAsync(System.Net.IPAddress.Loopback, 7241, channel.Token);
            using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, errors) => {
                if (cert is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                using var anchor = X509CertificateLoader.LoadCertificateFromFile(certificatePath); using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(anchor); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")); return chain.Build(new X509Certificate2(cert));
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, channel.Token);
            using var stream = new RemoteSocketStream(data);
            var a = stream.CopyToAsync(tls, 16384, channel.Token); var b = tls.CopyToAsync(stream, 16384, channel.Token);
            await Task.WhenAny(a, b); channel.Cancel(); data.Abort(); tcp.Close();
            try { await Task.WhenAll(a, b); } catch (Exception e) when (e is IOException or OperationCanceledException or WebSocketException) { }
        }
        catch (Exception e) when (e is IOException or SocketException or CryptographicException or HttpRequestException or WebSocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException or ArgumentException) { logger.LogInformation("Remote interface channel closed"); }
        finally { slots.Release(); }
    }
    public override async Task StopAsync(CancellationToken cancellationToken) { lock (gate) connectionStop?.Cancel(); await base.StopAsync(cancellationToken); foreach (var entry in sessions.Values) entry.Dispose(); sessions.Clear(); }
}
