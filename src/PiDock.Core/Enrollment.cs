using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace PiDock.Core;

public sealed record EnrollmentRequest(string Code, string CsrPem);
public sealed record EnrollmentReply(string TenantId, string DeviceId, string CertificatePem, string CaPem,
    string MqttHost, int MqttPort, DateTimeOffset ExpiresAt);
public sealed record CloudConnection(string CloudUrl, string TenantId, string DeviceId, string CertificatePem,
    string PrivateKeyPem, string CaPem, string MqttHost, int MqttPort, DateTimeOffset ExpiresAt);
public static class EnrollmentIdentity
{
    public static string CommonName(string tenant, string device)
    {
        _ = IdentityRules.Topic(tenant, device);
        return $"{tenant}/devices/{device}";
    }
    public static string Fingerprint(string csrPem)
    {
        var request = LoadRequest(csrPem);
        return Convert.ToHexString(SHA256.HashData(request.PublicKey.ExportSubjectPublicKeyInfo()));
    }
    public static CertificateRequest LoadRequest(string csrPem)
    {
        if (csrPem is not { Length: > 0 and <= 8192 }) throw new ArgumentException("Invalid device request.");
        // Default verifies the CSR signature and does not import requested extensions.
        var request = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        using var key = request.PublicKey.GetRSAPublicKey();
        if (key is null || key.KeySize is < 2048 or > 4096) throw new ArgumentException("An RSA 2048–4096 device key is required.");
        return request;
    }
}
