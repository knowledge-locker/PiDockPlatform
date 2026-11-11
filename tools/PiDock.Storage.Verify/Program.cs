using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using PiDock.Cloud.Web;
using PiDock.Core;
using PiDock.Web.Shared;

// This utility is never published into PiDockCloud. Only disposable validation volumes may use it.
if (Environment.GetEnvironmentVariable("PIDOCK_STORAGE_TEST") != "1" || args.Length != 2 || args[0] is not ("seed" or "verify"))
    throw new InvalidOperationException("Use seed|verify <disposable-state-path> with PIDOCK_STORAGE_TEST=1.");
string root = StateFiles.PrepareDirectory(args[1]), fixturePath = Path.Combine(root, "storage-test-fixture.json");
var provider = DataProtectionProvider.Create(new DirectoryInfo(StateFiles.PrepareDirectory(Path.Combine(root, "keys"))), options => options.SetApplicationName("PiDock.Cloud"));
var users = new UserStore(provider, root); var fleet = new FleetStore(new StateDirectory(root));
var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Enrollment:IssuerCertificatePath"] = Path.Combine(root, "fixture-ca.crt"), ["Enrollment:IssuerKeyPath"] = Path.Combine(root, "fixture-ca.key"),
    ["Enrollment:BrokerCaPath"] = Path.Combine(root, "fixture-ca.crt"), ["Enrollment:MqttHost"] = "localhost"
}).Build();
var invitations = new EnrollmentStore(new StateDirectory(root), TimeProvider.System, new DeviceCertificateIssuer(settings, TimeProvider.System));
var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "storage-owner"), new Claim(ClaimTypes.Role, "Owner"), new Claim("tenant", "storage-check")], "test"));
if (args[0] == "seed")
{
    if (users.HasUsers || File.Exists(fixturePath) || fleet.For(principal).Count != 0) throw new InvalidOperationException("Disposable storage must not already contain users or devices.");
    string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), secret = Totp.NewSecret();
    users.Add("storage-owner", password, secret, "storage-check", "Owner", true, -1);
    using var caKey = RSA.Create(2048);
    var caRequest = new CertificateRequest("CN=Disposable storage validation CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
    using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    StateFiles.WriteAtomic(Path.Combine(root, "fixture-ca.crt"), ca.ExportCertificatePem()); StateFiles.WriteAtomic(Path.Combine(root, "fixture-ca.key"), caKey.ExportPkcs8PrivateKeyPem());
    var code = invitations.Create(principal, "Disposable storage device"); using var deviceKey = RSA.Create(2048);
    var csr = new CertificateRequest("CN=storage-check", deviceKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
    var reply = invitations.Redeem(new(code.Code, csr)) ?? throw new InvalidOperationException("Fixture registration failed.");
    var budget = DataPolicyCalculator.Evaluate(new(), new(32L << 30, 16L << 30, 0));
    var sample = new HostSnapshot(DateTimeOffset.UtcNow, "disposable-storage-device", "Linux ARM64", 5, 4L << 30, 3L << 30, 40, 1, budget, [], "Fixture clock");
    var frame = new TelemetryEnvelope(1, "storage-check", reply.DeviceId, Guid.NewGuid(), Guid.NewGuid(), 1, sample);
    if (!fleet.Accept(IdentityRules.Topic(frame.TenantId, frame.DeviceId), frame).Stored) throw new InvalidOperationException("Fixture telemetry failed.");
    StateFiles.WriteAtomic(fixturePath, JsonSerializer.Serialize(new Fixture(password, secret, reply.DeviceId, frame.MessageId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + 1), StateFiles.Json));
    Console.WriteLine("Fixture seeded; no credentials printed.");
}
else
{
    var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(fixturePath), StateFiles.Json)!;
    var testTime = DateTimeOffset.FromUnixTimeSeconds(fixture.NextStep * 30 + 5);
    if (users.Authenticate("storage-owner", fixture.Password, Totp.Code(fixture.Secret, fixture.NextStep), testTime) is null)
        throw new InvalidOperationException("Persisted account/key-ring authentication failed.");
    var registration = invitations.For(principal).Single(); var device = fleet.For(principal).Single();
    if (registration.State != "Registered" || registration.DeviceId != fixture.DeviceId || device.LastMessageId != fixture.MessageId || fleet.HistoryFor(principal, fixture.DeviceId).Count != 1)
        throw new InvalidOperationException("Persisted device registration/telemetry changed.");
    StateFiles.WriteAtomic(fixturePath, JsonSerializer.Serialize(fixture with { NextStep = fixture.NextStep + 1 }, StateFiles.Json));
    Console.WriteLine("PASS: protected account, key ring, registration, fleet and telemetry survived.");
}
internal sealed record Fixture(string Password, string Secret, string DeviceId, Guid MessageId, long NextStep);
