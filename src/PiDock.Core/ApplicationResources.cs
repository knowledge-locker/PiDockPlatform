using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiDock.Core;

public sealed record ApplicationResources(bool PersistentData = false, string[]? Credentials = null, bool AutomaticRecovery = false, ApplicationHealthPolicy? Health = null, ApplicationDependency[]? Dependencies = null, int ShutdownSeconds = 10, bool ManagedLifecycle = false, ApplicationUpdatePolicy? Updates = null)
{
    [JsonIgnore] public ApplicationDependency[] RequiredApps => Dependencies ?? [];
    [JsonIgnore] public string[] CredentialIds => Credentials ?? [];
    [JsonIgnore] public bool Requested => PersistentData || CredentialIds.Length > 0 || AutomaticRecovery || Health is not null || RequiredApps.Length > 0 || ShutdownSeconds != 10 || ManagedLifecycle || Updates is not null;
    public void Validate()
    {
        Health?.Validate();
        Updates?.Validate();
        if(Updates is not null && Health is null)throw new ArgumentException("App updates require declared health/readiness reporting.");
        ApplicationDependencyGraph.Validate(RequiredApps);
        if (ShutdownSeconds is < 1 or > 10) throw new ArgumentException("App shutdown grace must be 1–10 seconds.");
        if (CredentialIds.Length > 8 || CredentialIds.Any(id => !ApplicationResourceRules.CredentialId(id)) || CredentialIds.Distinct(StringComparer.Ordinal).Count() != CredentialIds.Length)
            throw new ArgumentException("Request at most eight distinct named application credentials.");
    }
}
public sealed record ApplicationCredentialInfo(string AppId, string Id, int Version, string Purpose, DateTimeOffset ExpiresAt, bool Revoked, string[] Scopes);
// Sensitive internal broker/startup transport. Never expose these records in browser APIs, schemas or logs.
public sealed record ApplicationCredentialFile(int SchemaVersion, string AppId, string Id, int Version, string Purpose, string Token, DateTimeOffset ExpiresAt, string[] Scopes);
public sealed record ApplicationCredentialResolution(bool Managed, ApplicationCredentialFile? Credential);
public interface IApplicationCredentialSource { ApplicationCredentialResolution Resolve(string appId, string id); }
public sealed record ApplicationBootstrapPacket(int SchemaVersion, Guid DeliveryId, string AppId, bool PersistentData, ApplicationCredentialFile[] Credentials);
public sealed record ApplicationBootstrapAck(int SchemaVersion, Guid DeliveryId, string AppId, bool Ready);
public sealed record ApplicationDeploymentRequest(ApplicationManifest Manifest, ApplicationCredentialFile[] Credentials);
public sealed record ApplicationResourceState(string AppId, string ContainerId, bool PersistentData, string[] CredentialIds, DateTimeOffset LastDeliveredAt);
public sealed record ApplicationResourceSupport(int SchemaVersion, bool Enabled, string DataDirectory, string CredentialDirectory, string DeliveryProtocol, string[] Limitations);

public static class ApplicationResourceRules
{
    public const string DataDirectory = "/var/lib/pidock/app-data";
    public const string CredentialDirectory = "/run/secrets/pidock";
    public const int MaximumPacketBytes = 32768;
    public static readonly IReadOnlyList<string> PlatformScopes = Array.AsReadOnly(new[] { "system.read", "logs.read", "logs.write", "telemetry.read", "telemetry.write", "alarms.read", "alarms.raise", "alarms.acknowledge", "io.read", "io.write", "io.simulate", "signals.runtime", "signals.engineer", "notifications.read", "notifications.acknowledge" });
    public static bool CredentialId(string? id) => id is { Length: > 0 and <= 40 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    public static bool Credential(ApplicationCredentialFile file, string app, DateTimeOffset now) => file.SchemaVersion == 1 && file.AppId == app && CredentialId(file.Id) && file.Version > 0 &&
        file.Purpose is "ui-bridge" or "platform" && file.Token is { Length: 64 } && file.Token.All(char.IsAsciiHexDigit) && file.ExpiresAt > now && file.Scopes is { Length: <= 8 } && file.Scopes.Distinct(StringComparer.Ordinal).Count() == file.Scopes.Length &&
        (file.Purpose == "ui-bridge" ? file.Scopes.Length == 0 : file.Scopes.Length > 0 && file.Scopes.All(s => PlatformScopes.Contains(s, StringComparer.Ordinal)));
    public static void Validate(ApplicationBootstrapPacket packet, DateTimeOffset now)
    {
        if (packet.SchemaVersion != 1 || packet.DeliveryId == Guid.Empty || !IdentityRules.IsId(packet.AppId) || packet.Credentials is not { Length: <= 8 } ||
            packet.Credentials.Any(c => c is null || !Credential(c,packet.AppId,now)) || packet.Credentials.Select(c=>c.Id).Distinct(StringComparer.Ordinal).Count()!=packet.Credentials.Length)
            throw new ArgumentException("Invalid or expired application bootstrap material.");
    }
}

// Apps call this before creating loggers, hosting services or touching GPIO. No private host mount is needed.
public static class ApplicationBootstrap
{
    public static async Task<ApplicationBootstrapPacket?> InitializeAsync(CancellationToken ct = default)
    {
        if (Environment.GetEnvironmentVariable("PIDOCK_BOOTSTRAP_PROTOCOL") != "1") return null;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var packet=await ReadPacket(Console.OpenStandardInput(),timeout.Token);
        try
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("PIDOCK_APP_ID") != packet.AppId) throw new IOException("Unsupported app bootstrap target.");
            if (packet.PersistentData)
            {
                if (!Directory.Exists(ApplicationResourceRules.DataDirectory)) throw new IOException("App image must prepare its data directory.");
                if (File.GetAttributes(ApplicationResourceRules.DataDirectory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Invalid data directory.");
                var probe=Path.Combine(ApplicationResourceRules.DataDirectory,".pidock-probe-"+Guid.NewGuid().ToString("N"));
                try { await File.WriteAllTextAsync(probe,"",timeout.Token); } finally { File.Delete(probe); }
            }
            if (packet.Credentials.Length>0)
            {
                bool tmpfs=IsMemoryDirectory(ApplicationResourceRules.CredentialDirectory);
                if (!tmpfs) throw new IOException("Credentials require a dedicated memory filesystem.");
                foreach(var file in packet.Credentials)
                {
                    var path=Path.Combine(ApplicationResourceRules.CredentialDirectory,file.Id+".json");
                    await using(var stream=new FileStream(path,new FileStreamOptions { Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite }))
                    { await JsonSerializer.SerializeAsync(stream,file,StateFiles.Json,timeout.Token);stream.Flush(true); }
                    File.SetUnixFileMode(path,UnixFileMode.UserRead);
                }
            }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new ApplicationBootstrapAck(1,packet.DeliveryId,packet.AppId,true),StateFiles.Json));
            await Console.Out.FlushAsync(timeout.Token);return packet;
        }
        catch
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new ApplicationBootstrapAck(1,packet.DeliveryId,packet.AppId,false),StateFiles.Json));
            await Console.Out.FlushAsync(CancellationToken.None);throw new IOException("Application bootstrap failed.");
        }
    }
    internal static bool IsMemoryDirectory(string path)
    {
        if (!Directory.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return false;
        return File.ReadLines("/proc/self/mountinfo").Any(line => {
            var halves = line.Split(" - ",2); var fields = halves[0].Split(' ');
            return halves.Length == 2 && fields.Length > 4 && fields[4] == path && halves[1].StartsWith("tmpfs ",StringComparison.Ordinal);
        });
    }
    public static async Task<ApplicationBootstrapPacket> ReadPacket(Stream input,CancellationToken ct)
    {
        var header=new byte[4];await input.ReadExactlyAsync(header,ct);int length=BinaryPrimitives.ReadInt32BigEndian(header);
        if(length is < 1 or > ApplicationResourceRules.MaximumPacketBytes)throw new IOException("Invalid bootstrap packet size.");
        var body=new byte[length];await input.ReadExactlyAsync(body,ct);
        var packet=JsonSerializer.Deserialize<ApplicationBootstrapPacket>(body,new JsonSerializerOptions(StateFiles.Json){MaxDepth=8})??throw new IOException("Missing bootstrap packet.");
        ApplicationResourceRules.Validate(packet,DateTimeOffset.UtcNow);return packet;
    }
    public static ApplicationCredentialFile? ReadCredential(string id)
    {
        if(!ApplicationResourceRules.CredentialId(id))return null;
        try
        {
            var path=Path.Combine(ApplicationResourceRules.CredentialDirectory,id+".json");
            if(!File.Exists(path)||File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)||new FileInfo(path).Length>4096)return null;
            var file=JsonSerializer.Deserialize<ApplicationCredentialFile>(File.ReadAllText(path),StateFiles.Json);
            return file is not null&&ApplicationResourceRules.Credential(file,Environment.GetEnvironmentVariable("PIDOCK_APP_ID")??"",DateTimeOffset.UtcNow)?file:null;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException){return null;}
    }
}
