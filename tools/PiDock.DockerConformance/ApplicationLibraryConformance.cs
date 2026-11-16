using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using PiDock.Agent;
using PiDock.Cloud.Web;
using PiDock.Core;
using PiDock.Web.Shared;

internal static class ApplicationLibraryConformance
{
    public static async Task<ApplicationReleaseRequest> PublishAndDownload(string directory, ApplicationManifest runtime, X509Certificate2 certificate)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var state = StateFiles.PrepareDirectory(Path.Combine(directory, "library-cloud"));
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
        b.Logging.ClearProviders();
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["PiDock:StateDirectory"] = state,
            ["PiDock:Publishing:Credentials:0:Id"] = "fixture-publisher",
            ["PiDock:Publishing:Credentials:0:Subject"] = runtime.Name,
            ["PiDock:Publishing:Credentials:0:TenantId"] = "fixture",
            ["PiDock:Publishing:Credentials:0:TokenSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            ["PiDock:Publishing:Credentials:0:ExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O"),
            ["PiDock:Publishing:Credentials:0:Scopes:0"] = "packages.publish",
            ["PiDock:Publishing:Credentials:0:Scopes:1"] = "packages.read"
        });
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        b.AddPiDockAuthentication("Cloud"); b.AddApplicationCatalog();
        b.Services.AddAntiforgery();
        b.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        b.Services.AddSingleton<FleetStore>(); b.Services.AddSingleton<ApplicationLibrary>();
        await using var cloud = b.Build();
        cloud.UseAuthentication(); cloud.UseAuthorization(); cloud.UseRateLimiter(); cloud.UseAntiforgery();
        cloud.MapPiDockAuthentication("Cloud"); cloud.MapApplicationCatalog(); cloud.MapApplicationLibrary();
        var users = cloud.Services.GetRequiredService<UserStore>();
        var secret = Totp.NewSecret(); var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        users.Add("fixture-owner", password, Totp.NewSecret(), "local", "Owner", false, -1);
        users.Add("fixture-admin", password, secret, "fixture", "Administrator", false, -1);
        await cloud.StartAsync();
        var origin = cloud.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) => cert is not null &&
                (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0 &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData), SHA256.HashData(certificate.RawData)) };
        using var http = new HttpClient(handler) { BaseAddress = new(origin), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var definition = new ApplicationReleaseRequest(1, new(1, runtime.Name, "1.0.0", "v1", runtime.Image, []),
            new string('x', 4096), new(1, runtime, RequiredCapabilities: []));
        using var published = await http.PostAsJsonAsync(ApplicationCatalog.Root + "/releases", definition, StateFiles.Json);
        Require(published.StatusCode == HttpStatusCode.Created, "Real publisher API rejected definition.");
        var release = await published.Content.ReadFromJsonAsync<PublishedApplicationRelease>(StateFiles.Json) ?? throw new IOException("Publication missing.");
        using var repeated = await http.PostAsJsonAsync(ApplicationCatalog.Root + "/releases", definition, StateFiles.Json);
        Require(repeated.StatusCode == HttpStatusCode.OK, "Identical publication must be idempotent.");
        using var changed = await http.PostAsJsonAsync(ApplicationCatalog.Root + "/releases", definition with { ReleaseNotes = "changed" }, StateFiles.Json);
        Require(changed.StatusCode == HttpStatusCode.Conflict, "Changed release versions must be rejected.");
        http.DefaultRequestHeaders.Authorization = null;
        var path = $"/api/application-library/{runtime.Name}/1.0.0/definition";
        using var anonymous = await http.GetAsync(path);
        Require(!anonymous.IsSuccessStatusCode, "Anonymous definition download must be rejected.");
        var login = await http.GetStringAsync("/auth/login");
        var csrf = WebUtility.HtmlDecode(Regex.Match(login, "name=[\"']__RequestVerificationToken[\"'] value=[\"']([^\"']*)").Groups[1].Value);
        using var signedIn = await http.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["username"] = "fixture-admin", ["password"] = password,
            ["code"] = Totp.Code(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), ["__RequestVerificationToken"] = csrf }));
        Require(signedIn.StatusCode == HttpStatusCode.Redirect, "Disposable browser login failed.");
        using var downloaded = await http.GetAsync(path); downloaded.EnsureSuccessStatusCode();
        Require(downloaded.Headers.CacheControl?.NoStore == true && downloaded.Content.Headers.ContentDisposition?.DispositionType == "attachment", "Definition download headers required.");
        var bytes = await downloaded.Content.ReadAsByteArrayAsync();
        await File.WriteAllBytesAsync(Path.Combine(directory, "downloaded.pidock.json"), bytes);
        await using var localFile = File.OpenRead(Path.Combine(directory, "downloaded.pidock.json"));
        var imported = await ApplicationDefinition.Read(localFile);
        Require(ApplicationContractRules.Digest(imported) == release.Digest, "Cloud download and local import must retain the exact definition digest.");
        await cloud.StopAsync();
        Console.WriteLine("PASS real HTTPS publication, immutable versions, browser session download and local file import.");
        return imported;
    }

    public static async Task InstallThroughCloud(string directory, ApplicationReleaseRequest definition, BrokerClient broker)
    {
        var cloudState = new StateDirectory(StateFiles.PrepareDirectory(Path.Combine(directory, "command-cloud")));
        var fleet = new FleetStore(cloudState);
        var library = new ApplicationLibrary(new(cloudState, TimeProvider.System), fleet);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new("tenant", "fixture"), new(ClaimTypes.Name, "fixture-admin"), new(ClaimTypes.Role, "Administrator")], "fixture"));
        var release = library.Publish(user, definition);
        var now = DateTimeOffset.UtcNow;
        var sample = new HostSnapshot(now, "fixture", "Linux ARM64", 0, 1L << 30, 1L << 30, null, 60,
            DataPolicyCalculator.Evaluate(new(), new(32L << 30, 16L << 30, 0)), [], "Fixture clock");
        var frame = new TelemetryEnvelope(1, "fixture", "fixture-pi", Guid.NewGuid(), Guid.NewGuid(), 1, sample,
            Applications: new(now, true, "Ready", [], []));
        Require(fleet.Accept(IdentityRules.Topic("fixture", "fixture-pi"), frame).Stored, "Fixture inventory must persist.");
        var commands = new ApplicationCommandStore(cloudState, fleet, TimeProvider.System);
        bool denied = false;
        try { commands.QueueInstall(user, "fixture-pi", release, library); } catch (InvalidOperationException) { denied = true; }
        Require(denied, "Existing container control cannot authorize installation.");
        frame = frame with { Sequence = 2, MessageId = Guid.NewGuid(), Applications = frame.Applications! with { InstallEnabled = true } };
        Require(fleet.Accept(IdentityRules.Topic("fixture", "fixture-pi"), frame).Stored, "Opted-in inventory must persist.");
        var command = commands.QueueInstall(user, "fixture-pi", release, library);
        var receipt = new TelemetryReceipt(1, "fixture", "fixture-pi", frame.MessageId, commands.ForReceipt(frame));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, StateFiles.Json);
        Require(bytes.Length > 4096 && bytes.Length < ApplicationCommands.MaximumReceiptBytes, "Installation receipt must exercise the expanded bounded transport.");
        var received = JsonSerializer.Deserialize<TelemetryReceipt>(bytes, StateFiles.Json)!.Command!;
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["PiDock:StateDirectory"] = Path.Combine(directory, "command-agent"), ["Mqtt:Enabled"] = "true",
            ["Mqtt:Host"] = "unused-fixture", ["Mqtt:TenantId"] = "fixture", ["Mqtt:DeviceId"] = "fixture-pi"
        }).Build();
        using var mqtt = new MqttConnection(settings);
        using var agent = new RemoteApplicationAgent(settings, broker, mqtt, TimeProvider.System);
        agent.SetEnabled(true);
        // The cloud's saved report cannot bypass the current local policy.
        await agent.ProcessAsync(received);
        Require(agent.Current.Results.Single().State == "Rejected", "Local installation opt-in must be rechecked.");
        commands.AcceptResults(frame with { Applications = frame.Applications! with { Results = agent.Current.Results } });
        agent.SetInstallEnabled(true);
        command = commands.QueueInstall(user, "fixture-pi", release, library);
        await agent.ProcessAsync(command);
        Require(agent.Current.Results.Last().State == "Completed", "Cloud installation failed at the real broker.");
        var id = (await broker.List()).Single().Id;
        await agent.ProcessAsync(command);
        Require((await broker.List()).Single().Id == id, "Command replay must not install another container.");
        commands.AcceptResults(frame with { Applications = frame.Applications! with { Results = agent.Current.Results } });
        Require(commands.For(user, "fixture-pi").First().Result?.State == "Completed", "Cloud must retain the final install result.");
        Console.WriteLine("PASS separate cloud/local permission checks, cold registry install, command replay denial and cloud result receipt.");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
