using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace PiDock.Core;

public sealed record OsUpdateFile(string Path, long Length, string Sha256);
public sealed record OsUpdateManifest(int SchemaVersion, string Version, string Runtime, string KeyId, IReadOnlyList<OsUpdateFile> Files,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? ImageSequence = null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? MinimumImageSequence = null);
public sealed record OsRelease(string Digest, OsUpdateManifest Manifest)
{
    [System.Text.Json.Serialization.JsonIgnore] public OsReleaseInfo Info => new(Digest, Manifest.Version, Manifest.Runtime, Manifest.KeyId, Manifest.SchemaVersion == 2 ? "image" : "services");
}
public sealed record OsReleaseInfo(string Digest, string Version, string Runtime, string KeyId, string Kind = "services");
public sealed record OsUpdateRequest(Guid Id, string Digest, string Source, string RequestedBy);
public sealed record OsUpdateOperation(Guid Id, string Digest, string Version, string State, string Message, DateTimeOffset ChangedAt, string Source = "local", string RequestedBy = "");
public sealed record OsUpdateSnapshot(DateTimeOffset ObservedAt, bool Available, bool CloudEnabled, string Runtime,
    string? InstalledVersion, string? InstalledDigest, OsReleaseInfo? Staged, OsUpdateOperation? Operation, string Status, string UpdateKind = "services");
public sealed record OsUpdateCommand(Guid Id, string TenantId, string DeviceId, string Digest, string DownloadToken,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string RequestedBy);
public sealed record OsUpdatePolicy(bool Enabled);
public sealed record OsUpdateHealth(string? Digest, bool Ready);

public static class OsUpdateRules
{
    public const long MaximumPackageBytes = 512L << 20;
    public static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool Busy(string? state) => state is "Accepted" or "Preparing" or "Installing" or "Checking" or "RollingBack";
    public static bool Valid(OsUpdateCommand c, string tenant, string device, DateTimeOffset now) =>
        c.Id != Guid.Empty && IdentityRules.IsId(tenant) && IdentityRules.IsId(device) && c.TenantId == tenant && c.DeviceId == device && Digest(c.Digest) &&
        c.DownloadToken is { Length: 64 } && c.DownloadToken.All(char.IsAsciiHexDigit) && c.RequestedBy is { Length: > 0 and <= 64 } &&
        c.IssuedAt <= now.AddSeconds(30) && c.ExpiresAt > now && c.ExpiresAt > c.IssuedAt && c.ExpiresAt - c.IssuedAt <= TimeSpan.FromMinutes(10);
    public static string Runtime => System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
    public static OsRelease? Installed(string directory)
    {
        var path = Path.Combine(directory, "pidock-release.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<OsRelease>(File.ReadAllText(path), StateFiles.Json) : null;
    }
}

// The signature covers the exact manifest bytes; every payload byte is covered by a signed length and digest.
// Package paths are confined to program directories; packages cannot address external trust, service definitions or persistent data.
public static class OsUpdatePackage
{
    public static readonly IReadOnlyList<string> ImageFiles = Array.AsReadOnly(new[] { "system/rootfs.verity", "system/boot-A.vfat", "system/boot-B.vfat" });
    private static bool PayloadPath(string path, bool image) => image ? ImageFiles.Contains(path) : path.Length <= 240 && !path.Contains('\\') &&
        path.Split('/') is { Length: >= 2 } parts && parts[0] is "appliance" or "broker" &&
        parts.All(p => p.Length > 0 && p is not "." and not ".." && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) &&
        !path.EndsWith("/pidock-release.json", StringComparison.Ordinal);
    public static OsRelease Verify(string package, string trustDirectory, string? runtime = null, string? extractTo = null)
    {
        var info = new FileInfo(package);
        if (!info.Exists || info.Length is <= 0 or > OsUpdateRules.MaximumPackageBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Invalid package size or path.");
        using var stream = File.OpenRead(package);
        string digest = Convert.ToHexStringLower(SHA256.HashData(stream)); stream.Position = 0;
        // Bound the central directory before ZipArchive allocates its entry index. This format excludes ZIP64 and archive comments.
        if (stream.Length < 22) throw new InvalidDataException("Invalid package directory.");
        Span<byte> footer = stackalloc byte[22]; stream.Position = stream.Length - footer.Length; stream.ReadExactly(footer);
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(footer[10..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != 0x06054b50 || BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) != 0 ||
            entries is < 4 or > 20002 || BinaryPrimitives.ReadUInt16LittleEndian(footer[8..]) != entries || BinaryPrimitives.ReadUInt16LittleEndian(footer[20..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]) > 8 << 20 ||
            (long)BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]) + BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]) != stream.Length - 22) throw new InvalidDataException("Unsupported or oversized package directory.");
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count is < 4 or > 20002 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
            throw new InvalidDataException("Invalid or duplicate package entries.");
        var manifestBytes = ReadSmall(zip.GetEntry("manifest.json"), 1 << 20);
        var manifest = JsonSerializer.Deserialize<OsUpdateManifest>(manifestBytes, StateFiles.Json) ?? throw new InvalidDataException("Missing manifest.");
        bool image = manifest.SchemaVersion == 2;
        if (manifest.SchemaVersion is not (1 or 2) || !IdentityRules.IsId(manifest.KeyId) || manifest.Version is not { Length: > 0 and <= 64 } ||
            !manifest.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') || manifest.Runtime is not ("linux-arm64" or "linux-x64") ||
            (runtime is not null && manifest.Runtime != runtime) || manifest.Files is not { Count: >= 2 and <= 20000 }) throw new InvalidDataException("Unsupported release or target runtime.");
        if (image ? manifest.Runtime != "linux-arm64" || manifest.ImageSequence is not > 0 || manifest.MinimumImageSequence is not >= 0 || manifest.MinimumImageSequence > manifest.ImageSequence
            : manifest.ImageSequence is not null || manifest.MinimumImageSequence is not null) throw new InvalidDataException("Invalid image version policy.");
        var keyPath = Path.Combine(trustDirectory, manifest.KeyId + ".pem");
        if (!File.Exists(keyPath)) throw new InvalidDataException("The release signer is not trusted on this system.");
        using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(keyPath));
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" || !key.VerifyData(manifestBytes, ReadSmall(zip.GetEntry("signature.bin"), 256), HashAlgorithmName.SHA256)) throw new InvalidDataException("Release signature verification failed.");
        if (manifest.Files.Any(f => f is null || f.Path is not { Length: > 0 } || !PayloadPath(f.Path,image) || !OsUpdateRules.Digest(f.Sha256))) throw new InvalidDataException("Invalid package file metadata.");
        if (manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count ||
            (image ? manifest.Files.Count != ImageFiles.Count || ImageFiles.Any(path => !manifest.Files.Any(f => f.Path == path))
                : !manifest.Files.Any(f => f.Path == "appliance/PiDock.Appliance.Web.dll") || !manifest.Files.Any(f => f.Path == "broker/PiDock.Appliance.Broker.dll"))) throw new InvalidDataException("Required release components are missing.");
        if (zip.Entries.Count != manifest.Files.Count + 2) throw new InvalidDataException("Unexpected package content.");
        long total = 0;
        long maximumExpanded = image ? 2L << 30 : OsUpdateRules.MaximumPackageBytes;
        foreach (var file in manifest.Files)
        {
            if (!PayloadPath(file.Path,image) || !OsUpdateRules.Digest(file.Sha256) || file.Length < 0 || file.Length > maximumExpanded ||
                file.Length > maximumExpanded - total) throw new InvalidDataException("Invalid package file.");
            total += file.Length;
            var entry = zip.GetEntry(file.Path) ?? throw new InvalidDataException("A signed file is missing.");
            if (entry.Length != file.Length || ((entry.ExternalAttributes >> 16) & 0xf000) is not (0 or 0x8000)) throw new InvalidDataException("Links and special files are not supported.");
            using var input = entry.Open(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            FileStream? output = null;
            try
            {
                if (extractTo is not null) { string path = Path.Combine(extractTo, file.Path); StateFiles.PrepareDirectory(Path.GetDirectoryName(path)!); output = new(path, FileMode.CreateNew); }
                var buffer = new byte[65536]; long length = 0; int count;
                while ((count = input.Read(buffer)) > 0) { length += count; if (length > file.Length) throw new InvalidDataException("Expanded file exceeds its signed size."); hash.AppendData(buffer, 0, count); output?.Write(buffer, 0, count); }
                if (length != file.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256) throw new InvalidDataException("Package file digest mismatch.");
                output?.Flush(true);
            }
            finally { output?.Dispose(); }
        }
        return new(digest, manifest);
    }
    private static byte[] ReadSmall(ZipArchiveEntry? entry, int maximum)
    {
        if (entry is null || entry.Length is <= 0 || entry.Length > maximum) throw new InvalidDataException("Invalid release metadata.");
        using var input = entry.Open(); using var output = new MemoryStream();
        var buffer = new byte[4096]; int count;
        while ((count = input.Read(buffer)) > 0) { if (output.Length + count > maximum) throw new InvalidDataException("Release metadata is too large."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    public static async Task CopyBounded(Stream source, Stream target, CancellationToken ct = default)
    {
        var buffer = new byte[65536]; long length = 0; int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0) { length += count; if (length > OsUpdateRules.MaximumPackageBytes) throw new InvalidDataException("Package exceeds 512 MiB."); await target.WriteAsync(buffer.AsMemory(0, count), ct); }
    }
    public static OsRelease Create(string directory, string output, string version, string runtime, string keyId, string privateKeyPem)
    {
        var files = new List<OsUpdateFile>();
        foreach (string part in new[] { "appliance", "broker" })
            foreach (string path in Directory.GetFiles(Path.Combine(directory, part), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Package inputs cannot be links.");
                using var input = File.OpenRead(path);
                files.Add(new(Path.GetRelativePath(directory, path).Replace('\\', '/'), input.Length, Convert.ToHexStringLower(SHA256.HashData(input))));
            }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new OsUpdateManifest(1, version, runtime, keyId, files), StateFiles.Json);
        using var key = ECDsa.Create(); key.ImportFromPem(privateKeyPem);
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new InvalidDataException("A P-256 signing key is required.");
        using (var zip = ZipFile.Open(output, ZipArchiveMode.Create))
        {
            using (var target = zip.CreateEntry("manifest.json").Open()) target.Write(bytes);
            using (var target = zip.CreateEntry("signature.bin").Open()) target.Write(key.SignData(bytes, HashAlgorithmName.SHA256));
            foreach (var file in files) zip.CreateEntryFromFile(Path.Combine(directory, file.Path), file.Path, CompressionLevel.Fastest);
        }
        using var package = File.OpenRead(output);
        return new(Convert.ToHexStringLower(SHA256.HashData(package)), JsonSerializer.Deserialize<OsUpdateManifest>(bytes, StateFiles.Json)!);
    }
    public static OsRelease CreateImage(string directory, string output, string version, long sequence, long minimum, string keyId, string privateKeyPem)
    {
        if (sequence <= 0 || minimum < 0 || minimum > sequence) throw new ArgumentException("Invalid signed image version policy.");
        var files = ImageFiles.Select(path => {
            var info = new FileInfo(Path.Combine(directory,path));
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Image inputs must be regular files.");
            using var input = info.OpenRead();
            return new OsUpdateFile(path,input.Length,Convert.ToHexStringLower(SHA256.HashData(input)));
        }).ToArray();
        if (files.Sum(f => f.Length) > 2L << 30) throw new InvalidDataException("Expanded image update exceeds 2 GiB.");
        var manifest = new OsUpdateManifest(2,version,"linux-arm64",keyId,files,sequence,minimum);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest,StateFiles.Json);
        using var key = ECDsa.Create(); key.ImportFromPem(privateKeyPem);
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new InvalidDataException("A P-256 signing key is required.");
        using (var zip = ZipFile.Open(output,ZipArchiveMode.Create))
        {
            using (var entry=zip.CreateEntry("manifest.json").Open()) entry.Write(bytes);
            using (var entry=zip.CreateEntry("signature.bin").Open()) entry.Write(key.SignData(bytes,HashAlgorithmName.SHA256));
            foreach (var file in files) zip.CreateEntryFromFile(Path.Combine(directory,file.Path),file.Path,CompressionLevel.SmallestSize);
        }
        using var package=File.OpenRead(output);
        if (package.Length > OsUpdateRules.MaximumPackageBytes) throw new InvalidDataException("Compressed image update exceeds 512 MiB.");
        return new(Convert.ToHexStringLower(SHA256.HashData(package)),manifest);
    }
}
