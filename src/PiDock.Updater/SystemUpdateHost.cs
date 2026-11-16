using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using PiDock.Core;

namespace PiDock.Updater;

public sealed class SystemUpdateHost : IUpdateHost, IDisposable
{
    private readonly HttpClient appliance, broker;
    public SystemUpdateHost(string certificatePath)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
            using var root = X509CertificateLoader.LoadCertificateFromFile(certificatePath); using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(certificate);
        };
        appliance = new(handler) { BaseAddress = new("https://localhost:7241"), Timeout = TimeSpan.FromSeconds(3) };
        broker = UnixHttp.Connect("/run/pidock/broker.sock", TimeSpan.FromSeconds(3));
    }
    public Task Stop(CancellationToken ct) => Systemctl("stop", ct);
    public Task Start(CancellationToken ct) => Systemctl("start", ct);
    private static async Task Systemctl(string action, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/systemctl") { ArgumentList = { action, "pidock-broker.service", "pidock-appliance.service" }, UseShellExecute = false }) ?? throw new IOException("Could not start service operation.");
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new IOException("Service operation failed.");
    }
    public async Task<bool> Healthy(string? digest, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (digest is null)
                {
                    using var live = await appliance.GetAsync("/health/live", ct);
                    using var legacyBroker = await broker.GetAsync("/applications", ct);
                    if (live.IsSuccessStatusCode && legacyBroker.IsSuccessStatusCode) return true;
                }
                else
                {
                    var web = await appliance.GetFromJsonAsync<OsUpdateHealth>("/health/update", ct);
                    var runtime = await broker.GetFromJsonAsync<OsUpdateHealth>("/health/update", ct);
                    if (web is { Ready: true } && runtime is { Ready: true } && web.Digest == digest && runtime.Digest == digest) return true;
                }
            }
            catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException) { if (ct.IsCancellationRequested) throw; }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return false;
    }
    public void Dispose() { appliance.Dispose(); broker.Dispose(); }
}
