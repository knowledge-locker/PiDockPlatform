using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PiDock.Core;
namespace PiDock.Agent;

public sealed record PendingCloudKey(string CloudUrl, string CodeHash, string PrivateKeyPem);
public sealed class CloudEnrollmentClient
{
    private readonly string directory; private readonly MqttConnection mqtt; private readonly SemaphoreSlim gate = new(1, 1);
    public bool Enabled { get; }
    public CloudEnrollmentClient(string directory, MqttConnection mqtt, bool enabled = true) { this.directory = StateFiles.PrepareDirectory(directory); this.mqtt = mqtt; Enabled = enabled; }
    public CloudConnection? Connection
    {
        get
        {
            var path = Path.Combine(directory, "cloud-connection.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<CloudConnection>(File.ReadAllText(path), StateFiles.Json) : null;
        }
    }
    public async Task<CloudConnection> ConnectAsync(string server, string code, string? serverCaPem = null, CancellationToken ct = default)
    {
        if (!Enabled) throw new InvalidOperationException("Enrollment is disabled in the isolated UI review.");
        if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/") throw new ArgumentException("Enter an HTTPS server address without a path, username or query.");
        string raw = code.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        if (raw.Length != 32 || !raw.All(Uri.IsHexDigit)) throw new ArgumentException("Enter the full enrollment code from PiDockCloud.");
        await gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20)); ct = timeout.Token;
            if (Connection is not null) throw new InvalidOperationException("This device already has a cloud connection. Moving servers requires a separate local approval flow.");
            var pendingPath = Path.Combine(directory, "cloud-enrollment-pending.json");
            string origin = uri.AbsoluteUri, hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            var pending = File.Exists(pendingPath) ? JsonSerializer.Deserialize<PendingCloudKey>(File.ReadAllText(pendingPath), StateFiles.Json) : null;
            using var key = RSA.Create(2048);
            if (pending?.CloudUrl == origin && pending.CodeHash == hash) key.ImportFromPem(pending.PrivateKeyPem);
            else
            {
                pending = new(origin, hash, key.ExportPkcs8PrivateKeyPem());
                StateFiles.WriteAtomic(pendingPath, JsonSerializer.Serialize(pending, StateFiles.Json));
            }
            var csr = new CertificateRequest("CN=PiDockOS pending device", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
            using var handler = CreateHandler(serverCaPem);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "api/enrollment/redeem")) { Content = JsonContent.Create(new EnrollmentRequest(raw, csr), options: StateFiles.Json) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.BadRequest
                ? "The code is invalid, expired or already used. Check PiDockCloud and try again." : "Cloud enrollment is unavailable. Retry using the same code.");
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            byte[] buffer = new byte[32769]; int length = 0;
            while (length < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(length), ct); if (count == 0) break; length += count;
            }
            if (length > 32768) throw new InvalidOperationException("Invalid cloud enrollment response.");
            var reply = JsonSerializer.Deserialize<EnrollmentReply>(buffer.AsSpan(0, length), StateFiles.Json) ?? throw new InvalidOperationException("Invalid cloud enrollment response.");
            ValidateReply(reply, key);
            var connection = new CloudConnection(origin, reply.TenantId, reply.DeviceId, reply.CertificatePem, pending!.PrivateKeyPem, reply.CaPem, reply.MqttHost, reply.MqttPort, reply.ExpiresAt);
            // Stage runtime files first; commit the record before making the profile active.
            var prepared = mqtt.PrepareEnrollment(connection);
            try { StateFiles.WriteAtomic(Path.Combine(directory, "cloud-connection.json"), JsonSerializer.Serialize(connection, StateFiles.Json)); }
            catch { prepared.Dispose(); throw; }
            mqtt.ActivatePrepared(prepared);
            try { File.Delete(pendingPath); } catch (IOException) { /* One bounded redundant key file can be cleaned up locally. */ }
            return connection;
        }
        finally { gate.Release(); }
    }
    public static HttpClientHandler CreateHandler(string? caPem)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        if (!string.IsNullOrWhiteSpace(caPem))
        {
            // Import a public trust anchor, never a server-provided trust override.
            using var ca = X509Certificate2.CreateFromPem(caPem);
            byte[] publicCa = ca.RawData;
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                using var root = X509CertificateLoader.LoadCertificate(publicCa); using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                return chain.Build(certificate);
            };
        }
        return handler;
    }
    public static void ValidateReply(EnrollmentReply reply, RSA key)
    {
        if (!IdentityRules.IsId(reply.TenantId) || !IdentityRules.IsId(reply.DeviceId) || Uri.CheckHostName(reply.MqttHost) == UriHostNameType.Unknown || reply.MqttPort is < 1 or > 65535)
            throw new InvalidOperationException("Invalid cloud identity or MQTT endpoint.");
        using var certificate = X509Certificate2.CreateFromPem(reply.CertificatePem); using var ca = X509Certificate2.CreateFromPem(reply.CaPem);
        using var publicKey = certificate.GetRSAPublicKey();
        using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        if (publicKey is null || !CryptographicOperations.FixedTimeEquals(publicKey.ExportSubjectPublicKeyInfo(), key.ExportSubjectPublicKeyInfo()) ||
            certificate.GetNameInfo(X509NameType.SimpleName, false) != EnrollmentIdentity.CommonName(reply.TenantId, reply.DeviceId) ||
            certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority) || !chain.Build(certificate) ||
            reply.ExpiresAt <= DateTimeOffset.UtcNow || Math.Abs((reply.ExpiresAt.UtcDateTime - certificate.NotAfter.ToUniversalTime()).TotalSeconds) > 2)
            throw new InvalidOperationException("The issued device certificate could not be verified.");
    }
}
