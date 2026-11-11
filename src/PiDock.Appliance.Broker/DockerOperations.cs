using System.Net.Http.Json;
using System.Text.Json;
using PiDock.Core;
namespace PiDock.Appliance.Broker;

public sealed class DockerOperations(HttpClient engine)
{
    private const string Label = "ai.knowledgelocker.pidock.managed";
    private const string Api = "/v1.47";
    private readonly SemaphoreSlim deploymentLock = new(1);
    public async Task<ApplicationSummary[]> List(CancellationToken ct)
    {
        var filter = Uri.EscapeDataString(JsonSerializer.Serialize(new { label = new[] { Label + "=true" } }));
        return await engine.GetFromJsonAsync<ApplicationSummary[]>($"{Api}/containers/json?all=1&filters={filter}", StateFiles.Json, ct) ?? [];
    }
    public async Task<string> Deploy(ApplicationManifest manifest, CancellationToken ct)
    {
        manifest.Validate(); await deploymentLock.WaitAsync(ct);
        string? networkId = null; bool created = false;
        try
        {
            if ((await List(ct)).Length >= 16) throw new InvalidOperationException("Preview is limited to 16 managed containers.");
            using var pullRequest = new HttpRequestMessage(HttpMethod.Post, $"{Api}/images/create?fromImage={Uri.EscapeDataString(manifest.Image)}&platform=linux%2Farm64");
            using var pull = await engine.SendAsync(pullRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            pull.EnsureSuccessStatusCode();
            // Docker can return HTTP 200 with an error in its progress stream.
            using var stream = await pull.Content.ReadAsStreamAsync(ct); using var reader = new StreamReader(stream);
            int size = 0;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                size += line.Length; if (size > 10_000_000) throw new InvalidOperationException("Image pull progress exceeded the preview limit.");
                using var json = JsonDocument.Parse(line); if (json.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("Image pull failed. Check registry access and ARM64 support.");
            }
            using var network = await engine.PostAsJsonAsync(Api + "/networks/create", new { Name = "pidock-" + manifest.Name + "-" + Guid.NewGuid().ToString("N"), Driver = "bridge", Labels = new Dictionary<string, string> { [Label] = "true" } }, ct);
            network.EnsureSuccessStatusCode(); networkId = (await network.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("Id").GetString()!;
            var ports = manifest.ContainerPort is { } port ? new Dictionary<string, object> { [$"{port}/tcp"] = new { } } : [];
            var bindings = manifest.ContainerPort is { } cp ? new Dictionary<string, object> { [$"{cp}/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = manifest.HostPort!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) } } } : [];
            var request = new
            {
                manifest.Image,
                User = "1654:1654",
                Labels = new Dictionary<string, string> { [Label] = "true" },
                ExposedPorts = ports,
                HostConfig = new
                {
                    ReadonlyRootfs = true,
                    Privileged = false,
                    CapDrop = new[] { "ALL" },
                    SecurityOpt = new[] { "no-new-privileges:true" },
                    Memory = (long)manifest.MemoryMiB << 20,
                    NanoCpus = (long)(manifest.CpuLimit * 1_000_000_000),
                    PidsLimit = 128,
                    NetworkMode = networkId,
                    PortBindings = bindings,
                    Tmpfs = new Dictionary<string, string> { ["/tmp"] = "rw,noexec,nosuid,size=16777216" },
                    LogConfig = new { Type = "local", Config = new Dictionary<string, string> { ["max-size"] = "5m", ["max-file"] = "2" } },
                    RestartPolicy = new { Name = "unless-stopped" }
                }
            };
            using var container = await engine.PostAsJsonAsync($"{Api}/containers/create?name=pidock-{Uri.EscapeDataString(manifest.Name)}", request, ct);
            container.EnsureSuccessStatusCode();
            string id = (await container.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("Id").GetString()!; created = true;
            using var start = await engine.PostAsync($"{Api}/containers/{id}/start", null, ct); start.EnsureSuccessStatusCode(); return id;
        }
        finally
        {
            try { if (!created && networkId is not null) { using var cleanup = await engine.DeleteAsync($"{Api}/networks/{networkId}", CancellationToken.None); } }
            finally { deploymentLock.Release(); }
        }
    }
    public async Task ChangeState(string id, bool start, CancellationToken ct)
    {
        if (id.Length != 64 || !id.All(char.IsAsciiHexDigit) || !(await List(ct)).Any(c => c.Id == id)) throw new ArgumentException("Unknown managed application.");
        using var result = await engine.PostAsync($"{Api}/containers/{id}/{(start ? "start" : "stop?t=10")}", null, ct); result.EnsureSuccessStatusCode();
    }
}
