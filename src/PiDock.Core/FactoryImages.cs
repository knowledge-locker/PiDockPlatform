using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PiDock.Core;

public sealed record FactoryPatch(long Offset,int Length);
public sealed record FactoryManifest(int Schema,string Version,string Hardware,long Sequence,bool Development,string KeyId,
    long ImageBytes,string ImageSha256,long CompressedBytes,string CompressedSha256,IReadOnlyList<FactoryPatch> Provisioning);
public sealed record FactoryImageInfo(string Digest,FactoryManifest Manifest);
public static class FactoryImages
{
    public const long MaximumCompressed=2L<<30,MaximumExpanded=16L<<30;
    private static readonly byte[] Domain=Encoding.ASCII.GetBytes("PiDockOS.Factory.v1\n");
    public static byte[] SigningBytes(byte[] manifest)=>[..Domain,..manifest];
    public static void Validate(FactoryManifest m)
    {
        if(m.Schema!=1||m.Hardware!="raspberrypi5"||m.Sequence<=0||!IdentityRules.IsId(m.KeyId)||m.Version is not{Length:>0 and <=64}||
            !m.Version.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '-' or '+')||m.ImageBytes<=0||m.ImageBytes>MaximumExpanded||m.CompressedBytes<=0||m.CompressedBytes>MaximumCompressed||
            !OsUpdateRules.Digest(m.ImageSha256)||!OsUpdateRules.Digest(m.CompressedSha256)||m.Provisioning is not{Count:>0 and <=32})throw new InvalidDataException("Invalid factory image metadata.");
        long end=0,total=0;
        foreach(var patch in m.Provisioning)
        {
            if(patch is null||patch.Offset<end||patch.Offset<(1L<<20)||patch.Offset+patch.Length>(193L<<20)||patch.Length<=0||patch.Offset>m.ImageBytes-patch.Length)throw new InvalidDataException("Invalid provisioning extent.");
            end=patch.Offset+patch.Length;total+=patch.Length;
        }
        if(total!=1<<20)throw new InvalidDataException("Factory image requires a bounded 1 MiB provisioning area.");
    }
    public static FactoryImageInfo Create(string image,string output,string version,long sequence,bool development,string keyId,string privateKey,IReadOnlyList<FactoryPatch> patches)
    {
        string temporary=output+".payload-"+Guid.NewGuid().ToString("N");
        try {
            var size=new FileInfo(image).Length;if(size<=0||size>MaximumExpanded)throw new InvalidDataException("Unsupported factory image size.");
            string imageHash;
            using(var source=File.OpenRead(image)) {
                imageHash=Convert.ToHexStringLower(SHA256.HashData(source));source.Position=0;
                using var destination=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                using var gzip=new GZipStream(destination,CompressionLevel.SmallestSize);source.CopyTo(gzip);
            }
            string compressedHash;using(var source=File.OpenRead(temporary))compressedHash=Convert.ToHexStringLower(SHA256.HashData(source));
            var manifest=new FactoryManifest(1,version,"raspberrypi5",sequence,development,keyId,size,imageHash,new FileInfo(temporary).Length,compressedHash,patches);Validate(manifest);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(manifest,StateFiles.Json);
            using var key=ECDsa.Create();key.ImportFromPem(privateKey);
            if(key.KeySize!=256||key.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7")throw new InvalidDataException("Factory signing requires P-256.");
            using(var destination=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var zip=new ZipArchive(destination,ZipArchiveMode.Create)) {
                using(var entry=zip.CreateEntry("factory.json",CompressionLevel.NoCompression).Open())entry.Write(bytes);
                using(var entry=zip.CreateEntry("signature.bin",CompressionLevel.NoCompression).Open())entry.Write(key.SignData(SigningBytes(bytes),HashAlgorithmName.SHA256));
                using var payload=zip.CreateEntry("factory.img.gz",CompressionLevel.NoCompression).Open();using var source=File.OpenRead(temporary);source.CopyTo(payload);
            }
            using var final=File.OpenRead(output);return new(Convert.ToHexStringLower(SHA256.HashData(final)),manifest);
        }finally { File.Delete(temporary); }
    }
    public static FactoryImageInfo Verify(string package,string trust,string? copyCompressedTo=null)
    {
        using var input=File.OpenRead(package);
        if(input.Length is <22 or >MaximumCompressed)throw new InvalidDataException("Invalid factory package size.");
        string digest=Convert.ToHexStringLower(SHA256.HashData(input));
        Span<byte> footer=stackalloc byte[22];input.Position=input.Length-22;input.ReadExactly(footer);
        if(BinaryPrimitives.ReadUInt32LittleEndian(footer)!=0x06054b50||BinaryPrimitives.ReadUInt32LittleEndian(footer[4..])!=0||
            BinaryPrimitives.ReadUInt16LittleEndian(footer[8..])!=3||BinaryPrimitives.ReadUInt16LittleEndian(footer[10..])!=3||BinaryPrimitives.ReadUInt16LittleEndian(footer[20..])!=0||
            BinaryPrimitives.ReadUInt32LittleEndian(footer[12..])>16384||(long)BinaryPrimitives.ReadUInt32LittleEndian(footer[16..])+BinaryPrimitives.ReadUInt32LittleEndian(footer[12..])!=input.Length-22)
            throw new InvalidDataException("Unsupported factory package directory.");
        input.Position=0;using var zip=new ZipArchive(input,ZipArchiveMode.Read);
        if(zip.Entries.Count!=3||zip.Entries.Select(x=>x.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=3)throw new InvalidDataException("Unexpected factory package entries.");
        static byte[] Small(ZipArchiveEntry? entry,int maximum)
        {
            if(entry is null||entry.Length<=0||entry.Length>maximum)throw new InvalidDataException("Invalid factory metadata entry.");
            using var stream=entry.Open();var bytes=new byte[(int)entry.Length];stream.ReadExactly(bytes);
            if(stream.ReadByte()!=-1)throw new InvalidDataException("Factory metadata exceeded declared length.");return bytes;
        }
        var bytes=Small(zip.GetEntry("factory.json"),16384);
        var manifest=JsonSerializer.Deserialize<FactoryManifest>(bytes,StateFiles.Json)??throw new InvalidDataException("Factory manifest missing.");Validate(manifest);
        using var key=ECDsa.Create();key.ImportFromPem(File.ReadAllText(Path.Combine(trust,manifest.KeyId+".pem")));
        if(key.KeySize!=256||key.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7"||!key.VerifyData(SigningBytes(bytes),Small(zip.GetEntry("signature.bin"),64),HashAlgorithmName.SHA256))throw new InvalidDataException("Factory signer is not trusted.");
        var payload=zip.GetEntry("factory.img.gz")??throw new InvalidDataException("Factory payload missing.");
        if(payload.Length!=manifest.CompressedBytes||((payload.ExternalAttributes>>16)&0xf000)is not(0 or 0x8000))throw new InvalidDataException("Invalid factory payload.");
        using(var compressed=payload.Open())
        {
            if(Convert.ToHexStringLower(SHA256.HashData(compressed))!=manifest.CompressedSha256)throw new InvalidDataException("Factory compressed digest mismatch.");
        }
        using(var compressed=payload.Open())
        using(var expanded=new GZipStream(compressed,CompressionMode.Decompress))
        using(var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer=new byte[1<<20];var setup=new byte[1<<20];long length=0;int count;
            while((count=expanded.Read(buffer))>0)
            {
                long setupOffset=0;
                foreach(var patch in manifest.Provisioning) {
                    var start=Math.Max(length,patch.Offset);var end=Math.Min(length+count,patch.Offset+patch.Length);
                    if(start<end)buffer.AsSpan((int)(start-length),(int)(end-start)).CopyTo(setup.AsSpan((int)(setupOffset+start-patch.Offset)));
                    setupOffset+=patch.Length;
                }
                length+=count;if(length>manifest.ImageBytes)throw new InvalidDataException("Factory image exceeds its signed size.");hash.AppendData(buffer,0,count);
            }
            if(Encoding.UTF8.GetString(setup).Trim()!="{}")throw new InvalidDataException("Shared factory contains private provisioning data.");
            if(length!=manifest.ImageBytes||Convert.ToHexStringLower(hash.GetHashAndReset())!=manifest.ImageSha256)throw new InvalidDataException("Factory image digest mismatch.");
        }
        if(copyCompressedTo is not null)
        {
            using var source=payload.Open();using var output=new FileStream(copyCompressedTo,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            source.CopyTo(output);output.Flush(true);
        }
        return new(digest,manifest);
    }
}
