using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class ApplicationUiTests
{
    private static ApplicationUiContribution Contribution => new(1, "sample", "main", "Sample workspace", "index.html", "1.0", "1.0");

    [Theory]
    [InlineData("../secret")]
    [InlineData("/index.html")]
    [InlineData("https://evil.test/x")]
    [InlineData("assets/%2e%2e/secret")]
    [InlineData("assets\\secret")]
    [InlineData("assets//app.js")]
    [InlineData("index.html?token=x")]
    public void ContributionRejectsUnsafeResourcePaths(string path) => Assert.False(ApplicationUiRules.Valid(Contribution with { EntryPath = path }));

    [Theory]
    [InlineData("https://example.test/")]
    [InlineData("http://localhost:7361/")]
    [InlineData("http://127.0.0.1:80/")]
    [InlineData("http://user:secret@127.0.0.1:7361/")]
    [InlineData("http://127.0.0.1:7361/path")]
    [InlineData("http://127.0.0.1:7361/?key=x")]
    [InlineData("http://169.254.169.254:7361/")]
    public void RegistrationRejectsNonLoopbackOrCredentialBearingTargets(string origin) => Assert.False(ApplicationUiRegistry.ValidOrigin(origin));

    [Fact]
    public void RegistrationValidatesVersionsAndReloadsRevocationFailClosed()
    {
        Assert.True(ApplicationUiRules.Valid(Contribution));
        Assert.True(ApplicationUiRegistry.ValidOrigin("http://[::1]:7361/"));
        Assert.False(ApplicationUiRules.Valid(Contribution with { RequiredUiVersion = "2.0" }));
        Assert.False(ApplicationUiRules.Valid(Contribution with { RequiredStyleVersion = "9.0" }));
        var config = Configuration(); var registry = new ApplicationUiRegistry(config);
        Assert.Single(registry.Read());
        config["PiDock:ApplicationUi:Registrations:0:Enabled"] = "false";
        Assert.Empty(registry.Read());
        config["PiDock:ApplicationUi:Registrations:0:Enabled"] = "true";
        config["PiDock:ApplicationUi:Registrations:0:ServiceOrigin"] = "http://example.test:7361/";
        Assert.Empty(registry.Read());
    }

    [Fact]
    public async Task DiscoveryAndPublicScopedStylesHaveMatchingIntegrityAndMcpSchemas()
    {
        using var factory = new UiFactory(); using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(PlatformContracts.ApiRoot + "/ui")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token);
        var ui = await client.GetFromJsonAsync<ApplicationUiDiscovery>(PlatformContracts.ApiRoot + "/ui");
        Assert.Equal("isolated-ui-preview", ui!.HostingMode);
        Assert.Equal(["GET", "HEAD"], ui.SupportedMethods);
        foreach (var asset in ui.Assets)
        {
            using var anonymous = factory.Client(); var response = await anonymous.GetAsync(asset.Path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/css", response.Content.Headers.ContentType!.MediaType);
            Assert.True(response.Headers.CacheControl!.Public);
            Assert.Equal(asset.Sha256, Convert.ToHexStringLower(SHA256.HashData(await response.Content.ReadAsByteArrayAsync())));
            Assert.Equal(ApplicationUiAssets.Read(asset.Name), await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(PlatformContracts.ApiRoot + "/ui/styles/1.0/unknown.css")).StatusCode);
        Assert.Contains("pd-button", ApplicationUiAssets.Read("components.css"));
        Assert.NotNull(PlatformContracts.Schema("application-ui-contribution"));
        Assert.NotNull(PlatformContracts.Schema("application-ui-discovery"));
        var bridge=await client.GetFromJsonAsync<ApplicationUiBridgeDiscovery>(PlatformContracts.ApiRoot+"/ui/bridge");
        Assert.Equal(8192,bridge!.MaximumCommandBytes);
        foreach(var asset in bridge.Assets)
        {
            using var anonymous=factory.Client();var script=await anonymous.GetAsync(asset.Path);
            Assert.Equal("text/javascript",script.Content.Headers.ContentType!.MediaType);
            Assert.Equal(asset.Sha256,Convert.ToHexStringLower(SHA256.HashData(await script.Content.ReadAsByteArrayAsync())));
        }
        Assert.NotNull(PlatformContracts.Schema("application-ui-command"));
        Assert.NotNull(PlatformContracts.Schema("application-ui-event"));
    }

    [Fact]
    public async Task OwnerCanLaunchIsolatedWorkspaceButDiscoveryTokensCannotLaunchOrReadAppContent()
    {
        using var factory = new UiFactory(); using var agent = factory.Client();
        agent.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token);
        Assert.NotEqual(HttpStatusCode.OK, (await agent.GetAsync("/api/app-ui/v1/contributions")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await agent.GetAsync("/app-ui/sample/main/index.html")).StatusCode);
        using var owner = await factory.Login();
        var list = await owner.GetFromJsonAsync<ApplicationUiContribution[]>("/api/app-ui/v1/contributions");
        Assert.Single(list!);
        var shell = await owner.GetStringAsync("/apps/sample/main");
        Assert.Contains("Sample workspace", shell);
        Assert.Contains("sandbox=\"allow-scripts\"", shell);
        Assert.DoesNotContain("allow-same-origin", shell);
        Assert.Contains("/app-ui/sample/main/index.html", shell);
        Assert.Contains("/_content/PiDock.Web.Shared/pidock.css", shell);
        var resource = await owner.GetAsync("/app-ui/sample/main/index.html");
        Assert.Equal(HttpStatusCode.OK, resource.StatusCode);
        Assert.Contains("sandbox allow-scripts", resource.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("connect-src 'none'", resource.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(resource.Headers.CacheControl!.NoStore);
        Assert.False(resource.Headers.Contains("Set-Cookie"));
        Assert.False(factory.Upstream.HadAuthorization);
        Assert.False(factory.Upstream.HadCookie);
        Assert.Equal("http://127.0.0.1:7361/index.html", factory.Upstream.LastUri!.AbsoluteUri);
        factory.Services.GetRequiredService<IConfiguration>()["PiDock:ApplicationUi:Registrations:0:Enabled"] = "false";
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/app-ui/sample/main/index.html")).StatusCode);
        Assert.Contains("Application unavailable", await owner.GetStringAsync("/apps/sample/main"));
    }

    [Fact]
    public async Task ProxyRejectsQueriesMutationsRedirectsOversizeAndUnsupportedResources()
    {
        using var factory = new UiFactory(); using var owner = await factory.Login();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/app-ui/sample/main/index.html?target=evil")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.PostAsync("/app-ui/sample/main/index.html", new StringContent("x"))).StatusCode);
        factory.Upstream.Status = HttpStatusCode.Redirect;
        var redirect = await owner.GetAsync("/app-ui/sample/main/index.html");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, redirect.StatusCode);
        Assert.Null(redirect.Headers.Location);
        factory.Upstream.Status = HttpStatusCode.OK; factory.Upstream.MediaType = "application/octet-stream";
        Assert.Equal(HttpStatusCode.BadGateway, (await owner.GetAsync("/app-ui/sample/main/index.html")).StatusCode);
        factory.Upstream.MediaType = "text/html"; factory.Upstream.Body = new string('x', 2 * 1024 * 1024 + 1);
        Assert.Equal(HttpStatusCode.BadGateway, (await owner.GetAsync("/app-ui/sample/main/index.html")).StatusCode);
        factory.Upstream.Body = "<p>Fixture</p>";
        var head = await owner.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/app-ui/sample/main/index.html"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(HttpMethod.Head, factory.Upstream.LastMethod);
    }

    [Fact]
    public async Task OnlyExplicitNonSecretStaticAssetsLoadWithoutOwnerCookies()
    {
        using var factory = new UiFactory(); using var anonymous = factory.Client();
        factory.Upstream.MediaType = "text/css"; factory.Upstream.Body = ".pidock-app { color: red; }";
        var css = await anonymous.GetAsync("/app-ui/sample/main/assets/components.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("*", css.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/app-ui/sample/main/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/app-ui/sample/main/assets/private.css")).StatusCode);
        factory.Upstream.MediaType = "text/html";
        Assert.Equal(HttpStatusCode.BadGateway, (await anonymous.GetAsync("/app-ui/sample/main/assets/components.css")).StatusCode);
        factory.Services.GetRequiredService<IConfiguration>()["PiDock:ApplicationUi:Registrations:0:PublicAssets:0"] = "index.html";
        Assert.Empty(factory.Services.GetRequiredService<ApplicationUiRegistry>().Read());
    }

    [Fact]
    public async Task ViewerCannotUseOwnerUiSurface()
    {
        using var factory = new UiFactory(); using var viewer = await factory.Login("Viewer");
        Assert.NotEqual(HttpStatusCode.OK, (await viewer.GetAsync("/api/app-ui/v1/contributions")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await viewer.GetAsync("/app-ui/sample/main/index.html")).StatusCode);
        Assert.Equal(0, factory.Upstream.Calls);
    }

    private static IConfigurationRoot Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["PiDock:ApplicationUi:Registrations:0:Contribution:SchemaVersion"] = "1",
        ["PiDock:ApplicationUi:Registrations:0:Contribution:AppId"] = "sample",
        ["PiDock:ApplicationUi:Registrations:0:Contribution:Id"] = "main",
        ["PiDock:ApplicationUi:Registrations:0:Contribution:Title"] = "Sample workspace",
        ["PiDock:ApplicationUi:Registrations:0:Contribution:EntryPath"] = "index.html",
        ["PiDock:ApplicationUi:Registrations:0:Contribution:RequiredUiVersion"] = "1.0",
        ["PiDock:ApplicationUi:Registrations:0:Contribution:RequiredStyleVersion"] = "1.0",
        ["PiDock:ApplicationUi:Registrations:0:ServiceOrigin"] = "http://127.0.0.1:7361/",
        ["PiDock:ApplicationUi:Registrations:0:Enabled"] = "true",
        ["PiDock:ApplicationUi:Registrations:0:PublicAssets:0"] = "assets/components.css"
    }).Build();

    private sealed class UiFactory : WebApplicationFactory<PiDock.Appliance.Web.ApplianceMarker>
    {
        private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-appui-" + Guid.NewGuid());
        public string Token { get; } = new string('A', 64);
        public UpstreamHandler Upstream { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development")
            .UseSetting("PiDock:StateDirectory", root).UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting("PiDock:Discovery:TokenSha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Token))))
            .UseSetting("PiDock:Discovery:ExpiresAt", DateTimeOffset.UtcNow.AddHours(1).ToString("O"))
            .ConfigureAppConfiguration((_, config) => config.AddConfiguration(Configuration()))
            .ConfigureServices(services => services.AddHttpClient("PiDockAppUi").ConfigurePrimaryHttpMessageHandler(() => Upstream));
        public HttpClient Client() => CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        public async Task<HttpClient> Login(string role = "Owner")
        {
            var client = Client(); var secret = Totp.NewSecret();
            Services.GetRequiredService<UserStore>().Add("ui-user", "integration-test-password", secret, "local", role, role == "Owner", -1);
            var html = await client.GetStringAsync("/auth/login");
            var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "ui-user", ["password"] = "integration-test-password", ["code"] = Totp.Code(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), ["__RequestVerificationToken"] = csrf
            }));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); return client;
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing && Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class UpstreamHandler : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string MediaType = "text/html", Body = "<p>Fixture</p>";
        public Uri? LastUri; public HttpMethod? LastMethod; public bool HadCookie, HadAuthorization; public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; LastUri = request.RequestUri; LastMethod = request.Method;
            HadCookie = request.Headers.Contains("Cookie"); HadAuthorization = request.Headers.Authorization is not null;
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Body) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaType);
            response.Headers.TryAddWithoutValidation("Set-Cookie", "do-not-forward=secret");
            response.Headers.Location = new("http://example.test/redirect");
            return Task.FromResult(response);
        }
    }
}
