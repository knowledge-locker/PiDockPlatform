using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace PiDock.Core;

public sealed record NodeConfiguration(string Name, bool Monitoring = true, int SampleSeconds = 15)
{
    public void Validate() {
        if(Name is not {Length:>0 and <=64} || Name.Any(char.IsControl) || SampleSeconds is <5 or >300)throw new ArgumentException("Use a node name of 1–64 characters and a monitoring interval of 5–300 seconds.");
    }
}
public sealed record NodeDiscovery(int Schema,string Id,string Board,string Version,string? MainId,string? Challenge,string[] Capabilities);
public sealed record NodeAdoption(string Id,string Challenge,string MainId);
public sealed record NodeConfigurationRequest(Guid Operation,NodeConfiguration Configuration,long Revision);
public sealed record NodeConfigurationSnapshot(NodeConfiguration Configuration,long Revision);
public sealed record NodeReleaseRequest(Guid Operation);
public sealed record NodeMeasurement(Guid BootId,DateTimeOffset ObservedAt,int CpuCount,long MemoryBytes,double UptimeSeconds);
public sealed record NodeView(NodeDiscovery Device,string Interface,string KeyFingerprint,bool Adopted,bool Connected,string Status,NodeConfiguration? Configuration=null,NodeMeasurement? Measurement=null,long Revision=0);
public sealed record NodeAction(string Id,string Interface,string KeyFingerprint,string Actor);
public sealed record NodeConfigureAction(NodeAction Target,Guid Operation,NodeConfiguration Configuration,long Revision);
public sealed record NodeReleaseAction(NodeAction Target,Guid Operation);
public sealed record NodeAudit(DateTimeOffset At,string Actor,string Node,string Action,string Outcome);
public static class NodeSecurity
{
    public static bool Digest(string? value)=>value is {Length:64}&&value.All(char.IsAsciiHexDigit);
    public static string KeyId(X509Certificate2 certificate) {
        using var key=certificate.GetECDsaPublicKey();
        if(key is null||key.KeySize!=256||key.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7")throw new CryptographicException("Unsupported node identity.");
        return Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }
    public static X509Certificate2 CreateIdentity(string commonName) {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request=new CertificateRequest("CN="+commonName,key,HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,true));
        var eku=new OidCollection {new("1.3.6.1.5.5.7.3.1"),new("1.3.6.1.5.5.7.3.2")};
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku,true));
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName(commonName);request.CertificateExtensions.Add(san.Build());
        // Node authentication uses explicit key pins over isolated USB, independent of a node's untrusted clock.
        return request.CreateSelfSigned(new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero),new DateTimeOffset(2050,1,1,0,0,0,TimeSpan.Zero));
    }
    public static X509Certificate2 LoadOrCreate(string file,string commonName) {
        if(File.Exists(file))return ReadIdentity(file);
        using var created=CreateIdentity(commonName);
        StateFiles.WriteAtomic(file,Convert.ToBase64String(created.Export(X509ContentType.Pkcs12)));
        // Store as a protected-permission text bundle, never a downloadable credential.
        return X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(File.ReadAllText(file)),null);
    }
    public static X509Certificate2 ReadIdentity(string file) {
        var info=new FileInfo(file);
        if(!info.Exists||info.Length is <=0 or >32768||info.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("Invalid node identity file.");
        if(!OperatingSystem.IsWindows()&&(File.GetUnixFileMode(file)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute))!=0)throw new IOException("Node identity file permissions are unsafe.");
        return X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(File.ReadAllText(file)),null);
    }
}
