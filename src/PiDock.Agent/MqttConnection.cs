using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using MQTTnet;
using MQTTnet.Formatter;
using PiDock.Core;
using System.Text.Json;
namespace PiDock.Agent;

public sealed class MqttConnection : IDisposable
{
    private readonly IConfiguration settings;
    private readonly string? stateDirectory;
    private MqttConnection? active;
    public MqttConnection Current => Volatile.Read(ref active) ?? this;
    public MqttConnection(IConfiguration configuration, string? stateDirectory = null)
    {
        settings = new ConfigurationBuilder().AddInMemoryCollection(configuration.AsEnumerable().Where(p => p.Key.StartsWith("Mqtt:", StringComparison.OrdinalIgnoreCase))).Build();
        this.stateDirectory = stateDirectory ?? configuration["PiDock:StateDirectory"];
        if (this.stateDirectory is { } state && File.Exists(Path.Combine(state, "cloud-connection.json")))
            Activate(JsonSerializer.Deserialize<CloudConnection>(File.ReadAllText(Path.Combine(state, "cloud-connection.json")), StateFiles.Json)!);
    }
    public void Activate(CloudConnection connection)
    {
        var prepared = PrepareEnrollment(connection);
        try { ActivatePrepared(prepared); }
        catch { prepared.Dispose(); throw; }
    }
    private readonly List<MqttConnection> retired = [];
    internal MqttConnection PrepareEnrollment(CloudConnection connection, bool replacing = false)
    {
        lock (gate)
        {
            if (active is not null && !replacing) throw new InvalidOperationException("Cloud connection is already active.");
            if (retired.Count >= 8) throw new InvalidOperationException("Restart the appliance services before changing cloud again.");
            // Never replace certificate files used by an existing connection or in-flight delivery.
            var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(connection.CertificatePem)));
            var material = StateFiles.PrepareDirectory(Path.Combine(stateDirectory ?? throw new InvalidOperationException("Device state directory is required."), "cloud-mqtt", identity));
            StateFiles.WriteAtomic(Path.Combine(material, "device.crt"), connection.CertificatePem);
            StateFiles.WriteAtomic(Path.Combine(material, "device.key"), connection.PrivateKeyPem);
            StateFiles.WriteAtomic(Path.Combine(material, "ca.crt"), connection.CaPem);
            var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mqtt:Enabled"] = "true", ["Mqtt:Host"] = connection.MqttHost, ["Mqtt:Port"] = connection.MqttPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Mqtt:TenantId"] = connection.TenantId, ["Mqtt:DeviceId"] = connection.DeviceId,
                ["Mqtt:CertificatePath"] = Path.Combine(material, "device.crt"), ["Mqtt:PrivateKeyPath"] = Path.Combine(material, "device.key"), ["Mqtt:CaPath"] = Path.Combine(material, "ca.crt")
            }).Build();
            return new MqttConnection(configured);
        }
    }
    internal void ActivatePrepared(MqttConnection prepared, bool replacing = false)
    {
        lock (gate)
        {
            if (active is not null && !replacing) throw new InvalidOperationException("Cloud connection is already active.");
            if (active is not null) retired.Add(active);
            Volatile.Write(ref active, prepared);
        }
    }
    private string? scope;
    private readonly object gate = new();
    private MqttClientOptions? cached;
    private readonly List<X509Certificate2> ownedCertificates = [];
    public string RouteScope
    {
        get
        {
            lock (gate)
            {
                if (scope is not null) return scope;
                var host = settings["Mqtt:Host"] ?? throw new InvalidOperationException("Mqtt:Host is required.");
                var ca = settings["Mqtt:CaPath"] is { Length: > 0 } path ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : "system-trust";
                return scope = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{host.ToLowerInvariant()}:{settings.GetValue("Mqtt:Port", 8883)}|{ca}|{TenantId}|{DeviceId}")));
            }
        }
    }
    public bool Enabled => settings.GetValue<bool>("Mqtt:Enabled");
    public string TenantId => settings["Mqtt:TenantId"] ?? "local";
    public string DeviceId => settings["Mqtt:DeviceId"] ?? "pi5";
    public MqttClientOptions Options(string clientId)
    {
        lock (gate)
        {
            if (cached is not null) return cached;
            var host = settings["Mqtt:Host"] ?? throw new InvalidOperationException("Mqtt:Host is required.");
            var certPath = settings["Mqtt:CertificatePath"] ?? throw new InvalidOperationException("A device/worker certificate is required.");
            var keyPath = settings["Mqtt:PrivateKeyPath"] ?? throw new InvalidOperationException("A private key is required.");
            var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            ownedCertificates.Add(cert);
            try
            {
            return cached = new MqttClientOptionsBuilder().WithClientId(clientId).WithTcpServer(host, settings.GetValue("Mqtt:Port", 8883))
                .WithProtocolVersion(MqttProtocolVersion.V500).WithCleanSession().WithMaximumPacketSize(65536)
                .WithTlsOptions(tls =>
                {
                    tls.UseTls().WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13).WithClientCertificates([cert]);
                    if (settings["Mqtt:CaPath"] is { Length: > 0 } caPath)
                    {
                        var ca = X509CertificateLoader.LoadCertificateFromFile(caPath);
                        ownedCertificates.Add(ca);
                        tls.WithCertificateValidationHandler(context =>
                        {
                            if ((context.SslPolicyErrors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                            using var chain = new X509Chain();
                            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                            chain.ChainPolicy.CustomTrustStore.Add(ca);
                            // Private preview PKI is short-lived; automated revocation is a production release gate.
                            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                            chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
                            using var server = new X509Certificate2(context.Certificate);
                            return chain.Build(server);
                        });
                    }
                }).Build();
            }
            catch
            {
                foreach (var owned in ownedCertificates) owned.Dispose();
                ownedCertificates.Clear();
                throw;
            }
        }
    }
    public void Dispose() { lock (gate) { foreach (var cert in ownedCertificates) cert.Dispose(); active?.Dispose(); foreach (var previous in retired) previous.Dispose(); } }
}
