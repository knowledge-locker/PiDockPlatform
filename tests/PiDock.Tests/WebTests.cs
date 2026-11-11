using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PiDock.Web.Shared;
namespace PiDock.Tests;

public sealed class WebTests
{
    [Fact]
    public async Task ApplianceRequiresMfaProtectsRoutesAndRejectsCsrf()
    {
        using var factory = new PreviewFactory<PiDock.Appliance.Web.ApplianceMarker>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var store = factory.Services.GetRequiredService<UserStore>(); var secret = Totp.NewSecret();
        store.Add("test-owner", "integration-test-password", secret, "local", "Owner", true, -1);
        var badCsrf = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "test-owner", ["password"] = "integration-test-password", ["code"] = "000000" }));
        Assert.Equal(HttpStatusCode.BadRequest, badCsrf.StatusCode);
        var code = Totp.Code(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var result = await Login(client, "test-owner", code);
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode); Assert.Equal("/", result.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
        foreach (var route in new[] { "/", "/storage", "/gpio", "/interfaces", "/applications", "/cloud" })
        {
            var page = await client.GetAsync(route); Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("PiDock", await page.Content.ReadAsStringAsync());
        }
        await client.PostAsync("/auth/logout", new FormUrlEncodedContent(new Dictionary<string, string>())); // Missing CSRF cannot sign the user out.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
        // Same valid code cannot create a second authenticated session.
        using var other = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var replay = await Login(other, "test-owner", code); Assert.Contains("failed=1", replay.Headers.Location!.OriginalString);
    }
    [Fact]
    public async Task EnrollmentRedemptionRequiresHttpsAndIsRateLimitedWhileManagementRequiresLogin()
    {
        using var factory = new PreviewFactory<PiDock.Cloud.Web.CloudMarker>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/enrollment")).StatusCode);
        using var plain = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.PostAsJsonAsync("/api/enrollment/redeem", new PiDock.Core.EnrollmentRequest(new string('A', 32), "bad"))).StatusCode);
        var statuses = new List<HttpStatusCode>();
        for(int i = 0; i < 22; i++) statuses.Add((await client.PostAsJsonAsync("/api/enrollment/redeem", new PiDock.Core.EnrollmentRequest(new string('A', 32), "bad"))).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
    [Fact]
    public async Task CloudHasNoAnonymousFleetOrNetworkResetEndpoint()
    {
        using var factory = new PreviewFactory<PiDock.Cloud.Web.CloudMarker>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/devices/pi4/history")).StatusCode);
        Assert.Contains((await client.PostAsync("/auth/reset", new StringContent(""))).StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Contains((await client.PostAsync("/api/enroll", new StringContent(""))).StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }
    [Fact]
    public async Task DeviceHistoryExportsRequireLoginAndStayWithinTheCallersTenant()
    {
        using var factory = new PreviewFactory<PiDock.Cloud.Web.CloudMarker>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect=false, BaseAddress=new Uri("https://localhost") });
        string range=$"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"))}";
        string endpoint="/api/devices/pi5/export?format=csv&signals=cpu,memory&"+range;
        Assert.Equal(HttpStatusCode.Redirect,(await client.GetAsync(endpoint)).StatusCode);
        var secret=Totp.NewSecret();factory.Services.GetRequiredService<UserStore>().Add("history-viewer","integration-test-password",secret,"alpha","Viewer",false,-1);
        var now=DateTimeOffset.UtcNow;var storage=PiDock.Core.DataPolicyCalculator.Evaluate(new(),new(32L<<30,16L<<30,0));
        var fleet=factory.Services.GetRequiredService<PiDock.Cloud.Web.FleetStore>();
        foreach(string tenant in new[]{"alpha","other"}) {
            var sample=new PiDock.Core.HostSnapshot(now,tenant+"-host","Linux",25,1024,512,40,123,storage,[],"Test");
            fleet.Accept(PiDock.Core.IdentityRules.Topic(tenant,"pi5"),new(1,tenant,"pi5",Guid.NewGuid(),Guid.NewGuid(),1,sample));
        }
        await Login(client,"history-viewer",Totp.Code(secret,now.ToUnixTimeSeconds()/30));
        var response=await client.GetAsync(endpoint);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl!.NoStore);
        var csv=await response.Content.ReadAsStringAsync();Assert.Contains("alpha-host",csv);Assert.DoesNotContain("other-host",csv);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(endpoint.Replace("/pi5/","/other-device/"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(endpoint.Replace("signals=cpu,memory","signals=password"))).StatusCode);
        var page=await client.GetAsync("/devices/pi5");Assert.Equal(HttpStatusCode.OK,page.StatusCode);Assert.Contains("Resource trends",await page.Content.ReadAsStringAsync());
    }
    private static async Task<HttpResponseMessage> Login(HttpClient client, string username, string code)
    {
        var html = await client.GetStringAsync("/auth/login");
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        return await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = "integration-test-password", ["code"] = code, ["__RequestVerificationToken"] = token }));
    }
    [Fact]
    public async Task ProductionHttpOnlyServesLivenessAndDoesNotTrustSpoofedProxyHeaders()
    {
        using var factory = new PreviewFactory<PiDock.Cloud.Web.CloudMarker>("Production");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, (await client.GetAsync("/auth/login")).StatusCode);
    }
    private sealed class PreviewFactory<T>(string environment = "Development") : WebApplicationFactory<T> where T : class
    {
        private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-web-" + Guid.NewGuid());
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(environment).UseSetting("PiDock:StateDirectory", root).UseSetting("Logging:LogLevel:Default", "Warning");
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing && Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
