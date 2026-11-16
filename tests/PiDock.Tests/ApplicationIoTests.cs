using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PiDock.Agent;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationIoTests
{
    private static readonly ApplicationIoAuthority Pump = new("pump", "runtime", new string('A', 64));
    private static readonly ApplicationIoAuthority Other = new("other", "runtime", new string('B', 64));
    private static ApplicationIoGrant[] Grants() => [new("pump", "sim:bcm5", ApplicationIoDirection.Input),
        new("pump", "sim:bcm17", ApplicationIoDirection.Output), new("pump", "sim:bcm27", ApplicationIoDirection.Output, Fallback: true),
        new("other", "sim:bcm17", ApplicationIoDirection.Output)];
    [Fact]
    public void AtomicBatchesEnforceDirectionOwnershipAndRevision()
    {
        var clock = new Clock(); var validator = new Validator();
        var io = new ApplicationIoService(true, Grants, clock, validator);
        var signals = io.Signals(Pump);
        Assert.Equal(29, signals.Single(s => s.Alias == "sim:bcm5").HeaderPin);
        var lease = io.Acquire(Pump, new(["sim:bcm5", "sim:bcm17", "sim:bcm27"]));
        Assert.Throws<PlatformConflictException>(() => io.Acquire(Other, new(["sim:bcm17"])));
        Assert.Throws<ApplicationIoDeniedException>(() => io.Read(Other, lease.Id));
        Assert.Throws<ApplicationIoDeniedException>(() => io.Write(Pump, lease.Id, new(0, [new("sim:bcm17", true), new("sim:bcm5", true)])));
        Assert.False(io.Read(Pump, lease.Id).Values.Single(v => v.Alias == "sim:bcm17").Value);
        var frame = io.Write(Pump, lease.Id, new(0, [new("sim:bcm17", true), new("sim:bcm27", false)]));
        Assert.Equal(1, frame.Lease.Revision); Assert.All(frame.Values, v => Assert.Equal("simulated", v.Quality));
        Assert.Throws<PlatformConflictException>(() => io.Write(Pump, lease.Id, new(0, [new("sim:bcm17", false)])));
        var input = io.Write(Pump, lease.Id, new(1, [new("sim:bcm5", true)]), simulatedInputs: true);
        Assert.True(input.Values.Single(v => v.Alias == "sim:bcm5").Value);
        Assert.Null(input.Values.Single(v => v.Alias == "sim:bcm5").Commanded);
        Assert.Throws<ApplicationIoDeniedException>(() => io.Write(Pump, lease.Id, new(2, [new("sim:bcm17", true)]), simulatedInputs: true));
    }
    [Fact]
    public void MonotonicLeaseExpiryRevocationAndGrantChangesReleaseWithoutClientRequests()
    {
        var clock = new Clock(); var validator = new Validator(); var grants = Grants();
        var io = new ApplicationIoService(true, () => grants, clock, validator);
        var lease = io.Acquire(Pump, new(["sim:bcm17"], 3));
        io.Write(Pump, lease.Id, new(0, [new("sim:bcm17", true)]));
        clock.Advance(2); clock.Utc = clock.Utc.AddDays(-3);
        var renewed = io.Renew(Pump, lease.Id, new(1, 2));
        Assert.Equal(2, renewed.Revision);
        clock.Advance(2); io.Sweep();
        Assert.Throws<ApplicationIoDeniedException>(() => io.Read(Pump, lease.Id));
        var next = io.Acquire(Pump, new(["sim:bcm17"]));
        Assert.False(io.Read(Pump, next.Id).Values[0].Value);
        validator.Valid = false; io.Sweep(); validator.Valid = true;
        Assert.Throws<ApplicationIoDeniedException>(() => io.Read(Pump, next.Id));
        var changed = io.Acquire(Pump, new(["sim:bcm17"]));
        grants = []; io.Sweep();
        Assert.Throws<ApplicationIoDeniedException>(() => io.Read(Pump, changed.Id));
    }
    [Fact]
    public void ReservedAliasesBoundsAndRestartAreFailClosed()
    {
        var clock = new Clock(); var validator = new Validator();
        var io = new ApplicationIoService(true, Grants, clock, validator);
        foreach (var alias in new[] { "bcm17", "sim:bcm2", "sim:bcm017", "physical:bcm17", "sim:bcm+17" })
            Assert.Throws<ArgumentException>(() => io.Acquire(Pump, new([alias])));
        Assert.Throws<ArgumentException>(() => io.Acquire(Pump, new(["sim:bcm17"], 31)));
        Assert.Throws<ArgumentException>(() => io.Acquire(Pump, new(["sim:bcm17", "sim:bcm17"])));
        Assert.Throws<ApplicationIoDeniedException>(() => io.Acquire(Other, new(["sim:bcm5"])));
        var lease = io.Acquire(Pump, new(["sim:bcm17"]));
        var restarted = new ApplicationIoService(true, Grants, clock, validator);
        Assert.Throws<ApplicationIoDeniedException>(() => restarted.Read(Pump, lease.Id));
        Assert.Throws<ApplicationIoUnavailableException>(() => new ApplicationIoService(false, Grants, clock, validator).Acquire(Pump, new(["sim:bcm17"])));
        io.ReleaseAll(); Assert.Throws<ApplicationIoDeniedException>(() => io.Read(Pump, lease.Id));
    }
    [Fact]
    public async Task HttpsScopesAppIsolationAndLiveCredentialRevocationAreEnforced()
    {
        using var factory = new Factory();
        using var pump = factory.Client("pump"); using var other = factory.Client("other");
        using var reader = factory.Client("reader"); using var anonymous = factory.Client(null);
        const string root = "/api/apps/v1/io";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(root + "/signals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(root + "/leases", new ApplicationIoLeaseRequest(["sim:bcm17"]))).StatusCode);
        var signals = await pump.GetFromJsonAsync<ApplicationIoSignal[]>(root + "/signals");
        Assert.Contains(signals!, s => s.Alias == "sim:bcm5" && s.Bcm == 5);
        var leaseResponse = await pump.PostAsJsonAsync(root + "/leases", new ApplicationIoLeaseRequest(["sim:bcm17", "sim:bcm5"]));
        Assert.Equal(HttpStatusCode.OK, leaseResponse.StatusCode);
        var lease = (await leaseResponse.Content.ReadFromJsonAsync<ApplicationIoLease>())!;
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(root + "/leases/" + lease.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pump.PostAsJsonAsync(root + "/leases/" + lease.Id + "/simulated-inputs", new ApplicationIoWriteRequest(0, [new("sim:bcm5", true)]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pump.PostAsJsonAsync(root + "/leases/" + lease.Id + "/outputs", new ApplicationIoWriteRequest(0, [new("sim:bcm17", true)]))).StatusCode);
        // Revoke the credential without another app request, then drive the same sweep used by the hosted monitor.
        factory.Services.GetRequiredService<IConfiguration>()["PiDock:Apps:Credentials:0:ExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        factory.Services.GetRequiredService<ApplicationIoService>().Sweep();
        Assert.Equal(HttpStatusCode.Unauthorized, (await pump.GetAsync(root + "/leases/" + lease.Id)).StatusCode);
        var acquiredByOther = await other.PostAsJsonAsync(root + "/leases", new ApplicationIoLeaseRequest(["sim:bcm17"]));
        Assert.Equal(HttpStatusCode.OK, acquiredByOther.StatusCode);
        var otherLease = (await acquiredByOther.Content.ReadFromJsonAsync<ApplicationIoLease>())!;
        var frame = await other.GetFromJsonAsync<ApplicationIoFrame>(root + "/leases/" + otherLease.Id);
        Assert.False(frame!.Values[0].Value);
    }
    private sealed class Validator : IApplicationIoAuthorityValidator
    { public bool Valid = true; public bool IsCurrent(ApplicationIoAuthority authority) => Valid; }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public DateTimeOffset Utc = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => 1000;
        public void Advance(int seconds) { ticks += seconds * 1000; Utc = Utc.AddSeconds(seconds); }
    }
    private sealed class Factory : WebApplicationFactory<PiDock.Appliance.Web.ApplianceMarker>
    {
        private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-io-" + Guid.NewGuid());
        private readonly Dictionary<string, string> tokens = new() { ["pump"] = Token(), ["other"] = Token(), ["reader"] = Token() };
        private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development").UseSetting("PiDock:StateDirectory", root).UseSetting("Logging:LogLevel:Default", "Warning");
            var settings = new Dictionary<string, string?> { ["PiDock:Apps:Io:Enabled"] = "true" };
            int n = 0;
            foreach (var (id, token) in tokens)
            {
                var prefix = "PiDock:Apps:Credentials:" + n++ + ":";
                settings[prefix + "Id"] = id; settings[prefix + "Subject"] = id;
                settings[prefix + "TokenSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
                settings[prefix + "ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
                settings[prefix + "Scopes:0"] = "io.read";
                if (id != "reader") settings[prefix + "Scopes:1"] = "io.write";
            }
            var grants = Grants();
            for (int i = 0; i < grants.Length; i++)
            {
                var prefix = "PiDock:Apps:Io:Grants:" + i + ":";
                settings[prefix + "AppId"] = grants[i].AppId; settings[prefix + "Alias"] = grants[i].Alias;
                settings[prefix + "Direction"] = grants[i].Direction.ToString(); settings[prefix + "Fallback"] = grants[i].Fallback.ToString();
            }
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        }
        public HttpClient Client(string? id)
        {
            var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            if (id is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens[id]);
            return client;
        }
        protected override void Dispose(bool disposing)
        { base.Dispose(disposing); if (disposing && Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
