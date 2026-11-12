using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using PiDock.Core;

namespace PiDock.Cloud.Web;

public sealed record DeviceSigningRequest(string Tenant, string Device, string CsrPem);

// Run in a separate, network-disabled container. Only that container mounts the CA key.
public static class IsolatedCertificateSigner
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var socket = builder.Configuration["Enrollment:SignerSocket"] ?? throw new InvalidOperationException("Signer socket required.");
        if (!Path.IsPathFullyQualified(socket)) throw new InvalidOperationException("Signer socket must be absolute.");
        builder.Configuration["AllowedHosts"] = "signer.internal";
        builder.WebHost.ConfigureKestrel(options => {
            options.ListenUnixSocket(socket);
            options.Limits.MaxRequestBodySize = 16384;
            options.Limits.MaxConcurrentConnections = 8;
        });
        var app = builder.Build();
        var issuer = new DeviceCertificateIssuer(builder.Configuration, TimeProvider.System);
        if (!issuer.Ready) throw new InvalidOperationException("Signer certificate configuration required.");
        var gate = new SemaphoreSlim(1);
        app.MapGet("/ready", () => Results.Ok());
        app.MapPost("/issue", async (DeviceSigningRequest request, HttpContext context) => {
            if (!await gate.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(429);
            try {
                // Rebuild the certificate with validated identity and client-only permissions.
                var reply = issuer.Issue(request.Tenant, request.Device, request.CsrPem);
                app.Logger.LogInformation("Issued device certificate for {Tenant}/{Device}; expires {Expiry}", request.Tenant, request.Device, reply.ExpiresAt);
                return Results.Ok(reply);
            }
            catch (Exception e) when (e is ArgumentException or CryptographicException) { return Results.BadRequest(); }
            finally { gate.Release(); }
        });
        app.Lifetime.ApplicationStarted.Register(() => {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        });
        return app;
    }
}

public sealed class SocketCertificateIssuer : IDeviceCertificateIssuer, IDisposable
{
    private readonly HttpClient client;
    public SocketCertificateIssuer(string socket)
    {
        if (!Path.IsPathFullyQualified(socket)) throw new InvalidOperationException("Signer socket must be absolute.");
        client = new(new SocketsHttpHandler {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (_, cancellation) => {
                var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await connection.ConnectAsync(new UnixDomainSocketEndPoint(socket), cancellation); return new NetworkStream(connection, ownsSocket: true); }
                catch { connection.Dispose(); throw; }
            }
        }) { BaseAddress = new("http://signer.internal"), Timeout = TimeSpan.FromSeconds(15) };
    }
    public bool Ready {
        get { try { using var result = client.GetAsync("/ready").GetAwaiter().GetResult(); return result.IsSuccessStatusCode; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; } }
    }
    public EnrollmentReply Issue(string tenant, string device, string csrPem)
    {
        _ = EnrollmentIdentity.CommonName(tenant, device);
        _ = EnrollmentIdentity.LoadRequest(csrPem);
        using var result = client.PostAsJsonAsync("/issue", new DeviceSigningRequest(tenant, device, csrPem)).GetAwaiter().GetResult();
        result.EnsureSuccessStatusCode();
        return result.Content.ReadFromJsonAsync<EnrollmentReply>().GetAwaiter().GetResult() ?? throw new IOException("No signer reply.");
    }
    public void Dispose() => client.Dispose();
}
