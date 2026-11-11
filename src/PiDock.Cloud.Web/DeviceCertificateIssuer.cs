using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PiDock.Core;
namespace PiDock.Cloud.Web;

public interface IDeviceCertificateIssuer
{
    bool Ready { get; }
    EnrollmentReply Issue(string tenant, string device, string csrPem);
}
public sealed class DeviceCertificateIssuer(IConfiguration configuration, TimeProvider clock) : IDeviceCertificateIssuer
{
    public bool Ready => new[] { "Enrollment:IssuerCertificatePath", "Enrollment:IssuerKeyPath", "Enrollment:BrokerCaPath", "Enrollment:MqttHost" }
        .All(key => !string.IsNullOrWhiteSpace(configuration[key]));
    public EnrollmentReply Issue(string tenant, string device, string csrPem)
    {
        if (!Ready) throw new InvalidOperationException("Device enrollment is not configured on this server.");
        var loaded = EnrollmentIdentity.LoadRequest(csrPem);
        using var issuer = X509Certificate2.CreateFromPemFile(configuration["Enrollment:IssuerCertificatePath"]!, configuration["Enrollment:IssuerKeyPath"]!);
        if (!issuer.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority)) throw new InvalidOperationException("Enrollment issuer must be a CA.");
        var now = clock.GetUtcNow();
        if (issuer.NotBefore.ToUniversalTime() > now.UtcDateTime || issuer.NotAfter.ToUniversalTime() <= now.AddMinutes(10).UtcDateTime)
            throw new InvalidOperationException("Enrollment issuer is not currently valid.");
        var subject = new X500DistinguishedNameBuilder(); subject.AddCommonName(EnrollmentIdentity.CommonName(tenant, device));
        // Subject and permissions always come from the invitation, never the CSR.
        var request = new CertificateRequest(subject.Build(), loaded.PublicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var expiry = new DateTimeOffset(issuer.NotAfter.ToUniversalTime());
        if (expiry > now.AddDays(90)) expiry = now.AddDays(90);
        using var certificate = request.Create(issuer, now.AddMinutes(-1) < issuer.NotBefore.ToUniversalTime() ? issuer.NotBefore.ToUniversalTime() : now.AddMinutes(-1), expiry, RandomNumberGenerator.GetBytes(16));
        var host = configuration["Enrollment:MqttHost"]!; int port = configuration.GetValue("Enrollment:MqttPort", 8883);
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535) throw new InvalidOperationException("Invalid enrollment MQTT endpoint.");
        return new(tenant, device, certificate.ExportCertificatePem(), File.ReadAllText(configuration["Enrollment:BrokerCaPath"]!), host, port, expiry);
    }
}
