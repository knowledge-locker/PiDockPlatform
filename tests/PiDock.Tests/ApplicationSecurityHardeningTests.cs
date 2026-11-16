using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using PiDock.Appliance.Web;
using PiDock.Agent;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class ApplicationSecurityHardeningTests
{
    [Fact]
    public void SuccessfulReregistrationClearsTransientControlsEvenWhenInstanceIdIsReused()
    {
        var now = DateTimeOffset.UtcNow;
        var audits = new List<ApplicationEngineeringAudit>();
        var service = new ApplicationOverrideService(TimeProvider.System, _ => true, audits.Add);
        var runtime = new ApplicationEngineeringAuthority("pump", "runtime", 1, now.AddHours(1), "signals.runtime");
        var engineer = runtime with { Actor = "engineer", Scope = "signals.engineer" };
        var registration = new ApplicationSignalRegistration(Guid.NewGuid(), "revision-one",
            [new("enabled", ApplicationSignalType.Boolean, ApplicationSignalAccess.ReadWrite, JsonSerializer.SerializeToElement(false), true)]);
        service.SetMaintenance(false);
        service.Register(runtime, registration);
        service.Publish(runtime, new(registration.InstanceId, registration.Revision, 1, [new("enabled", JsonSerializer.SerializeToElement(false))], []));
        service.Force(engineer, new(registration.InstanceId, registration.Revision, "enabled", JsonSerializer.SerializeToElement(true), 60));
        service.SetOnce(engineer, new(registration.InstanceId, registration.Revision, "enabled", JsonSerializer.SerializeToElement(true)));
        var next = service.Register(runtime, registration);
        Assert.Null(Assert.Single(next.Signals).Force);
        Assert.False(next.Signals[0].EffectiveValue.GetBoolean());
        Assert.Empty(next.PendingWrites);
        Assert.Equal(1, next.Sequence); // A replay does not reset producer sequence/fence stale frames.
        Assert.Contains(audits, a => a.Action == "runtime-registration-release");
        Assert.Contains(audits, a => a.Action == "runtime-registration-write-cancelled");
        Assert.Throws<ArgumentException>(() => service.Publish(runtime,
            new(registration.InstanceId, registration.Revision, 1, [new("enabled", JsonSerializer.SerializeToElement(true))], [])));
    }

    [Fact]
    public async Task MalformedReloadedCredentialRevokesIdleLeaseWithoutThrowing()
    {
        using var factory = new Factory();
        using var client = factory.Client();
        const string root = "/api/apps/v1/io";
        var response = await client.PostAsJsonAsync(root + "/leases", new ApplicationIoLeaseRequest(["sim:bcm17"]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lease = (await response.Content.ReadFromJsonAsync<ApplicationIoLease>())!;
        var validator = factory.Services.GetRequiredService<IApplicationIoAuthorityValidator>();
        Assert.True(validator.IsCurrent(factory.Authority));
        factory.Services.GetRequiredService<IConfiguration>()["PiDock:Apps:Credentials:0:ExpiresAt"] = "invalid-expiry";
        Assert.False(validator.IsCurrent(factory.Authority));
        var io = factory.Services.GetRequiredService<ApplicationIoService>();
        io.Sweep();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(root + "/leases/" + lease.Id)).StatusCode);
        factory.Services.GetRequiredService<IConfiguration>()["PiDock:Apps:Credentials:0:ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
        Assert.Throws<ApplicationIoDeniedException>(() => io.Read(factory.Authority, lease.Id));
    }

    [Fact]
    public void UnreadableCredentialConfigurationFailsClosed()
    {
        using var factory = new Factory();
        var validator = new ApplicationIoCredentialValidator(new UnavailableConfiguration(),
            factory.Services.GetRequiredService<ApplicationCredentialVault>(), TimeProvider.System);
        Assert.False(validator.IsCurrent(factory.Authority));
    }

    private sealed class UnavailableConfiguration : IConfiguration
    {
        public string? this[string key] { get => throw new IOException("Private provider unavailable."); set => throw new IOException("Private provider unavailable."); }
        public IEnumerable<IConfigurationSection> GetChildren() => throw new IOException("Private provider unavailable.");
        public IChangeToken GetReloadToken() => throw new IOException("Private provider unavailable.");
        public IConfigurationSection GetSection(string key) => throw new IOException("Private provider unavailable.");
    }
    private sealed class Factory : WebApplicationFactory<PiDock.Appliance.Web.ApplianceMarker>
    {
        private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-security-" + Guid.NewGuid());
        private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public ApplicationIoAuthority Authority => new("pump", "runtime", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development").UseSetting("PiDock:StateDirectory", root).UseSetting("Logging:LogLevel:Default", "Warning");
            var entry = new ServiceCredential("runtime", Authority.Fingerprint, DateTimeOffset.UtcNow.AddHours(1), "pump", ["io.read", "io.write"]);
            var json = JsonSerializer.SerializeToUtf8Bytes(new { PiDock = new { Apps = new { Credentials = new[] { entry },
                Io = new { Enabled = true, Grants = new[] { new ApplicationIoGrant("pump", "sim:bcm17", ApplicationIoDirection.Output) } } } } });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddJsonStream(new MemoryStream(json)));
        }
        public HttpClient Client()
        {
            var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Authorization = new("Bearer", token); return client;
        }
        protected override void Dispose(bool disposing)
        { base.Dispose(disposing); if (disposing && Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
