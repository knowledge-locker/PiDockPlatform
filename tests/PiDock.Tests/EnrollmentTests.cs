using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PiDock.Agent;
using PiDock.Cloud.Web;
using PiDock.Core;
using PiDock.Web.Shared;
namespace PiDock.Tests;

public sealed class EnrollmentTests : IDisposable
{
    private readonly string root = StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-enrollment-" + Guid.NewGuid()));
    private readonly Clock clock = new(); private readonly RSA caKey = RSA.Create(2048); private readonly X509Certificate2 ca;
    private readonly IConfiguration configuration;
    private static ClaimsPrincipal User(string tenant = "alpha", string role = "Owner") => new(new ClaimsIdentity([
        new Claim(ClaimTypes.Name, "test-owner"), new Claim(ClaimTypes.Role, role), new Claim("tenant", tenant)], "test"));
    public EnrollmentTests()
    {
        var request = new CertificateRequest("CN=Test device CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(100));
        StateFiles.WriteAtomic(Path.Combine(root, "ca.crt"), ca.ExportCertificatePem()); StateFiles.WriteAtomic(Path.Combine(root, "ca.key"), caKey.ExportPkcs8PrivateKeyPem());
        configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Enrollment:IssuerCertificatePath"] = Path.Combine(root, "ca.crt"), ["Enrollment:IssuerKeyPath"] = Path.Combine(root, "ca.key"),
            ["Enrollment:BrokerCaPath"] = Path.Combine(root, "ca.crt"), ["Enrollment:MqttHost"] = "localhost"
        }).Build();
    }
    private EnrollmentStore Store(string? directory = null) => new(new StateDirectory(StateFiles.PrepareDirectory(directory ?? Path.Combine(root, "cloud"))), clock, new DeviceCertificateIssuer(configuration, clock));
    private static string Csr(RSA key, bool hostile = false)
    {
        var request = new CertificateRequest("CN=other/devices/admin", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if(hostile) { request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true)); request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true)); }
        return request.CreateSigningRequestPem();
    }
    [Fact]
    public void InvitationHashesSecretsAndEnforcesRolesAndTenants()
    {
        var store = Store(); Assert.Throws<UnauthorizedAccessException>(() => store.Create(User(role: "Viewer"), "Pi"));
        var code = store.Create(User(), "Workshop Pi"); Assert.Empty(store.For(User("other")));
        var view = Assert.Single(store.For(User())); Assert.Empty(view.CodeHash); Assert.Null(view.Reply);
        string persisted = File.ReadAllText(Path.Combine(root, "cloud/enrollments.json"));
        Assert.DoesNotContain(code.Code, persisted); Assert.DoesNotContain(code.Code.Replace("-", ""), persisted);
        Assert.False(store.Cancel(User("other"), code.Id)); Assert.True(store.Cancel(User(), code.Id));
        using var key = RSA.Create(2048); Assert.Null(store.Redeem(new(code.Code, Csr(key))));
    }
    [Fact]
    public void ServerControlsIdentityAndIgnoresRequestedCaAndServerPermissions()
    {
        var store = Store(); var code = store.Create(User(), "Pi"); using var key = RSA.Create(2048);
        var reply = store.Redeem(new(code.Code, Csr(key, true)))!; Assert.Equal("alpha", reply.TenantId); Assert.Equal(code.DeviceId, reply.DeviceId);
        CloudEnrollmentClient.ValidateReply(reply, key);
        using var cert = X509Certificate2.CreateFromPem(reply.CertificatePem);
        Assert.Equal(EnrollmentIdentity.CommonName("alpha", code.DeviceId), cert.GetNameInfo(X509NameType.SimpleName, false));
        Assert.False(cert.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Equal("1.3.6.1.5.5.7.3.2", cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages[0].Value);
    }
    [Fact]
    public void SingleUseSurvivesRestartAndOnlyTheSameKeyCanRecoverALostResponse()
    {
        var store = Store(); var code = store.Create(User(), "Pi"); using var key = RSA.Create(2048); using var other = RSA.Create(2048);
        var request = new EnrollmentRequest(code.Code, Csr(key)); var reply = store.Redeem(request);
        Assert.NotNull(reply); Assert.Null(Store().Redeem(new(code.Code, Csr(other))));
        Assert.Equal(reply, Store().Redeem(request)); Assert.Single(Store().For(User()));
        clock.Now += TimeSpan.FromMinutes(11); Assert.Null(Store().Redeem(request));
    }
    [Fact]
    public void ExpiredCodeCannotIssueACertificate()
    {
        var store = Store(); var code = store.Create(User(), "Pi"); clock.Now += TimeSpan.FromMinutes(11);
        using var key = RSA.Create(2048); Assert.Null(store.Redeem(new(code.Code, Csr(key)))); Assert.Equal("Expired", store.For(User()).Single().State);
    }
    [Fact]
    public async Task ConcurrentRedemptionCanBindOnlyOneDeviceKey()
    {
        var store = Store(); var code = store.Create(User(), "Pi"); using var a = RSA.Create(2048); using var b = RSA.Create(2048);
        var requests = new[] { new EnrollmentRequest(code.Code, Csr(a)), new EnrollmentRequest(code.Code, Csr(b)) };
        var replies = await Task.WhenAll(requests.Select(r => Task.Run(() => store.Redeem(r)))); Assert.Single(replies, r => r is not null);
    }
    [Fact]
    public void InvalidRequestDoesNotConsumeCodeAndWriteFailureCanBeRetried()
    {
        var store = Store(); var code = store.Create(User(), "Pi");
        Assert.ThrowsAny<CryptographicException>(() => store.Redeem(new(code.Code, "bad csr")));
        using var key = RSA.Create(2048); var request = new EnrollmentRequest(code.Code, Csr(key));
        string path = Path.Combine(root, "cloud/enrollments.json"); File.Delete(path); Directory.CreateDirectory(path);
        Assert.ThrowsAny<IOException>(() => store.Redeem(request)); Directory.Delete(path);
        Assert.NotNull(store.Redeem(request)); Assert.Equal("Registered", Store().For(User()).Single().State);
    }
    [Fact]
    public void CertificateForAnotherKeyOrIdentityIsRejectedByAppliance()
    {
        var store = Store(); using var key = RSA.Create(2048); using var other = RSA.Create(2048);
        var code = store.Create(User(), "Pi"); var reply = store.Redeem(new(code.Code, Csr(key)))!;
        Assert.Throws<InvalidOperationException>(() => CloudEnrollmentClient.ValidateReply(reply, other));
        Assert.Throws<InvalidOperationException>(() => CloudEnrollmentClient.ValidateReply(reply with { TenantId = "other" }, key));
    }
    [Fact]
    public async Task ApplianceConnectsOverHttpsKeepsKeyLocalAndActivatesWithoutRestart()
    {
        await ExerciseClient(lostResponse: false);
    }
    [Fact]
    public async Task ApplianceRetriesALostResponseWithTheSamePersistedKey()
    {
        await ExerciseClient(lostResponse: true);
    }
    [Fact]
    public async Task LocalConnectionWriteFailureDoesNotActivateAndCanRecoverTheSameRegistration()
    {
        await ExerciseClient(lostResponse: false, failedLocalWrite: true);
    }
    private async Task ExerciseClient(bool lostResponse, bool failedLocalWrite = false)
    {
        using var serverKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var tls = issued.CopyWithPrivateKey(serverKey);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.UseHttps(tls)));
        var store = Store(); var code = store.Create(User(), "Test Pi"); int attempts = 0;
        await using var app = builder.Build();
        app.MapPost("/api/enrollment/redeem", (EnrollmentRequest r) =>
        {
            var reply = store.Redeem(r);
            return lostResponse && ++attempts == 1 ? Results.StatusCode(503) : Results.Ok(reply);
        });
        await app.StartAsync();
        try
        {
            string url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            string state = Path.Combine(root, "appliance"); var config = new ConfigurationBuilder().Build();
            using var mqtt = new MqttConnection(config, state); var client = new CloudEnrollmentClient(state, mqtt);
            if(lostResponse) await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(url, code.Code, ca.ExportCertificatePem()));
            if(failedLocalWrite)
            {
                Directory.CreateDirectory(Path.Combine(state, "cloud-connection.json"));
                await Assert.ThrowsAnyAsync<IOException>(() => client.ConnectAsync(url, code.Code, ca.ExportCertificatePem()));
                Assert.False(mqtt.Current.Enabled); Directory.Delete(Path.Combine(state, "cloud-connection.json"));
            }
            var result = await client.ConnectAsync(url, code.Code, ca.ExportCertificatePem());
            Assert.True(mqtt.Current.Enabled); Assert.Equal(code.DeviceId, mqtt.Current.DeviceId); Assert.Equal("alpha", mqtt.Current.TenantId);
            using var restarted = new MqttConnection(config, state); Assert.Equal(mqtt.Current.RouteScope, restarted.Current.RouteScope);
            Assert.Equal(result.DeviceId, client.Connection!.DeviceId); Assert.False(File.Exists(Path.Combine(state, "cloud-enrollment-pending.json")));
            Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(Path.Combine(root, "cloud/enrollments.json")));
            Assert.DoesNotContain(code.Code.Replace("-", ""), File.ReadAllText(Path.Combine(state, "cloud-connection.json")));
            Assert.Single(store.For(User()));
        }
        finally { await app.StopAsync(); }
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    public void Dispose() { ca.Dispose(); caKey.Dispose(); Directory.Delete(root, true); }
}
