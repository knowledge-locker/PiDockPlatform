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
