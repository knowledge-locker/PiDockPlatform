using Microsoft.Extensions.Configuration;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using PiDock.Core;
using PiDock.Web.Shared;
namespace PiDock.Tests;
public sealed class FactoryImageTests
{
    private static byte[] BlankImage() { var data=new byte[2<<20];Array.Fill(data,(byte)32,1<<20,1<<20);data[1<<20]=(byte)'{';data[(1<<20)+1]=(byte)'}';return data; }
    [Fact] public void SignedFactoryRejectsCorruptionAndWrongTrust()
    {
        var directory=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-factory-"+Guid.NewGuid());Directory.CreateDirectory(directory);
        try {
            using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var trust=Path.Combine(directory,"trust");Directory.CreateDirectory(trust);var publicFile=Path.Combine(trust,"factory-test.pem");File.WriteAllText(publicFile,key.ExportSubjectPublicKeyInfoPem());
            string image=Path.Combine(directory,"image"),package=Path.Combine(directory,"factory.zip");File.WriteAllBytes(image,BlankImage());
            var created=FactoryImages.Create(image,package,"1.0.0-test",1,true,"factory-test",key.ExportPkcs8PrivateKeyPem(),[new(1<<20,1<<20)],setupSchema:2);
            Assert.Equal(2,created.Manifest.SetupSchema);
            var legacyJson=System.Text.Json.JsonSerializer.Serialize(created.Manifest,StateFiles.Json).Replace(",\"setupSchema\":2", "");
            Assert.Equal(1,System.Text.Json.JsonSerializer.Deserialize<FactoryManifest>(legacyJson,StateFiles.Json)!.SetupSchema);
            Assert.Equal(created,FactoryImages.Verify(package,trust) with { Manifest=created.Manifest });
            using(var wrong=ECDsa.Create(ECCurve.NamedCurves.nistP256))File.WriteAllText(publicFile,wrong.ExportSubjectPublicKeyInfoPem());
            Assert.Throws<InvalidDataException>(()=>FactoryImages.Verify(package,trust));
            File.WriteAllText(publicFile,key.ExportSubjectPublicKeyInfoPem());
            using(var zip=ZipFile.Open(package,ZipArchiveMode.Update)) { var entry=zip.GetEntry("signature.bin")!;entry.Delete();using var stream=zip.CreateEntry("signature.bin",CompressionLevel.NoCompression).Open();stream.Write(new byte[64]); }
            Assert.Throws<InvalidDataException>(()=>FactoryImages.Verify(package,trust));
            Assert.Throws<InvalidDataException>(()=>FactoryImages.Validate(created.Manifest with { Provisioning=[new(0,1<<20),new(512,1)] }));
            Assert.Throws<InvalidDataException>(()=>FactoryImages.Validate(created.Manifest with { Provisioning=[null!] }));
        }finally { Directory.Delete(directory,true); }
    }
    [Fact] public async Task CloudFactoryTicketsAreBoundToWorkspaceUserAndSingleUse()
    {
        var directory=StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-catalog-"+Guid.NewGuid()));
        try {
            using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var trust=StateFiles.PrepareDirectory(Path.Combine(directory,"trust"));File.WriteAllText(Path.Combine(trust,"factory-test.pem"),key.ExportSubjectPublicKeyInfoPem());
            var file=Path.Combine(directory,"image");File.WriteAllBytes(file,BlankImage());var package=Path.Combine(directory,"package.zip");
            var signed=FactoryImages.Create(file,package,"1.0.0-test",1,true,"factory-test",key.ExportPkcs8PrivateKeyPem(),[new(1<<20,1<<20)]);
            var state=new StateDirectory(directory);
            var settings=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Updates:TrustDirectory"]=trust }).Build();
            var store=new PiDock.Cloud.Web.FactoryImageStore(state,new PiDock.Cloud.Web.FleetStore(state),TimeProvider.System,settings);
            static System.Security.Claims.ClaimsPrincipal User(string tenant="alpha",string name="owner",string role="Owner") => new(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.Name,name),new(System.Security.Claims.ClaimTypes.Role,role),new("tenant",tenant)],"test"));
            await using(var input=File.OpenRead(package))await store.Publish(User(),input);
            Assert.Single(store.List(User()));Assert.Empty(store.List(User("beta")));
            Assert.Throws<UnauthorizedAccessException>(()=>store.AuthorizeDownload(User("beta"),signed.Digest));
            Assert.Throws<UnauthorizedAccessException>(()=>store.List(User(role:"Viewer")));
            var ticket=store.AuthorizeDownload(User(),signed.Digest);
            Assert.Null(store.Download(User(name:"other"),signed.Digest,ticket));
            Assert.Null(store.Download(User("beta"),signed.Digest,ticket));
            Assert.NotNull(store.Download(User(),signed.Digest,ticket));Assert.Null(store.Download(User(),signed.Digest,ticket));
            var catalog=File.ReadAllText(Path.Combine(directory,"factory-images/catalog.json"));Assert.Contains("DownloadAuthorized",catalog);Assert.DoesNotContain(ticket,catalog);
        }finally { Directory.Delete(directory,true); }
    }
    [Fact] public void MediaOwnerIsInitialOnlyAndPasswordHashWorksWithExistingMfa()
    {
        var directory=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-media-"+Guid.NewGuid());
        try {
            const string password="private-initial-password";
            // Match the browser's Identity v3 PBKDF2-SHA512 format.
            var options=Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount=210000 });
            var hash=new PasswordHasher<string>(options).HashPassword("owner",password);
            var setup=new MediaSetup(1,"owner",hash,Totp.NewSecret(),-1,null);
            Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(setup));
            setup=setup with { LastStep=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30-1 };
            var store=new UserStore(new EphemeralDataProtectionProvider(),directory);store.ImportInitialMediaOwner(setup);
            Assert.NotNull(store.Authenticate("owner",password,Totp.Code(setup.Secret,setup.LastStep+1),DateTimeOffset.UtcNow));
            Assert.Throws<InvalidOperationException>(()=>store.ImportInitialMediaOwner(setup with { Username="replacement" }));
            Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(setup with { Wifi=new("00","bad","US") }));
            Assert.DoesNotContain(setup.Secret,File.ReadAllText(Path.Combine(directory,"accounts.protected")));
        }finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
}
