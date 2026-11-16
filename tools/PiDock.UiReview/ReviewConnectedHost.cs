using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PiDock.Appliance.Web;
using PiDock.Core;

namespace PiDock.UiReview;

// Isolated fixture setup. The actual shared endpoints/authentication/lease and engineering services run unchanged.
public sealed class ReviewConnectedHost(string state, string connectionFile, int port, X509Certificate2 certificate) : IDisposable
{
    public static ReviewConnectedHost? Configure(WebApplicationBuilder builder, string state)
    {
        if (!builder.Configuration.GetValue<bool>("Review:ConnectSharedServices")) return null;
        if (!builder.Environment.IsDevelopment() || OperatingSystem.IsWindows()) throw new InvalidOperationException("Connected review requires a Development Unix fixture.");
        string file = builder.Configuration["Review:ConnectionFile"] ?? throw new InvalidOperationException("Private fixture delivery path required.");
        if (!Path.IsPathFullyQualified(file) || !Path.GetFileName(Path.GetDirectoryName(file)!).StartsWith("pidock-connected-review-", StringComparison.Ordinal) || File.Exists(file))
            throw new InvalidOperationException("Use a fresh private fixture delivery directory.");
        int port = builder.Configuration.GetValue<int?>("Review:ServicesPort") ?? 7356;
        if (port is < 1024 or > 65535) throw new InvalidOperationException("Use an unprivileged review service port.");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=PiDock isolated services review", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(cert)); k.ListenUnixSocket(Path.Combine(state, "review-orchestration.sock")); });
        var config = new Dictionary<string, string?> { ["Broker:SocketPath"] = Path.Combine(state, "review-orchestration.sock"),
            ["PiDock:Apps:RuntimeAccess:Enabled"] = "true", ["PiDock:Apps:Io:Enabled"] = "true", ["PiDock:Engineering:Enabled"] = "true" };
        var pins = new[] { 5, 6, 17, 27, 22 };
        for (int i = 0; i < pins.Length; i++)
        {
            string prefix = "PiDock:Apps:Io:Grants:" + i + ":";
            config[prefix + "AppId"] = "ui-sample"; config[prefix + "Alias"] = "sim:bcm" + pins[i];
            config[prefix + "Direction"] = i < 2 ? "Input" : "Output";
        }
        builder.Configuration.AddInMemoryCollection(config);
        builder.AddSharedApplicationServices(); builder.AddApplicationIo(); builder.AddApplicationEngineering(); builder.AddApplicationRuntimeAccess();
        builder.Services.AddSingleton<ReviewRuntimeController>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<ReviewRuntimeController>());
        return new(state, file, port, cert);
    }
    public void SeedAndMap(WebApplication app)
    {
        // Trusted console fixture provisioning into a fresh, isolated vault; no public issuance or Owner bypass API.
        string vaultPath = Path.Combine(state, "app-credentials.protected");
        if (File.Exists(vaultPath)) throw new InvalidOperationException("Fixture credential state must be new.");
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var credentials = new[] { Credential("sample-io", ["io.read", "io.write", "io.simulate"]),
            Credential("sample-runtime", ["signals.runtime"]), Credential("sample-engineer", ["signals.engineer"]) };
        ApplicationCredentialFile Credential(string id, string[] scopes) => new(1, "ui-sample", id, 1, "platform", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), expiry, scopes);
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("PiDock.ApplicationCredentials.v1");
        StateFiles.WriteAtomic(vaultPath, protector.Protect(JsonSerializer.Serialize(credentials, StateFiles.Json)));
        var controller = app.Services.GetRequiredService<ReviewRuntimeController>();
        controller.SetDelivery(delivery => StateFiles.WriteAtomic(connectionFile, JsonSerializer.Serialize(new { origin = $"https://127.0.0.1:{port}/",
            certificateSha256 = Convert.ToHexString(SHA256.HashData(certificate.RawData)), ioToken = credentials[0].Token,
            runtimeToken = credentials[1].Token, engineeringToken = credentials[2].Token, deliveryId = delivery }, StateFiles.Json)));
        // Private process adapter: same access/lifecycle protocol; no Docker or physical device operations.
        app.Use(async (context, next) =>
        {
            if (context.Connection.LocalIpAddress is not null) { await next(); return; }
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Method == "GET" && context.Request.Path == "/applications/runtime-access") await context.Response.WriteAsJsonAsync(controller.Access());
            else if (context.Request.Method == "GET" && context.Request.Path == "/applications/orchestration/status") await context.Response.WriteAsJsonAsync(controller.Orchestration());
            else if (context.Request.Method == "GET" && context.Request.Path == "/applications") await context.Response.WriteAsJsonAsync(Array.Empty<ApplicationSummary>());
            else if (context.Request.Method == "POST")
            {
                string? action = context.Request.Path.Value switch
                {
                    "/applications/orchestration/pause" => "pause", "/applications/orchestration/resume" => "resume",
                    "/applications/" + ReviewRuntimeController.Id + "/start" => "start",
                    "/applications/" + ReviewRuntimeController.Id + "/stop" => "stop",
                    "/applications/" + ReviewRuntimeController.Id + "/restart" => "restart", _ => null
                };
                if (action is null) context.Response.StatusCode = 404;
                else try { await controller.Control(action, context.RequestAborted); context.Response.StatusCode = 200; }
                catch (Exception e) when (e is InvalidOperationException or IOException) { context.Response.StatusCode = 409; }
            }
            else context.Response.StatusCode = 404;
        });
    }
    public void Dispose() { certificate.Dispose(); File.Delete(connectionFile); }
}
