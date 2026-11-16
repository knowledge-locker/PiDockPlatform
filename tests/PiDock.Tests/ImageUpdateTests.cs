using System.Security.Cryptography;
using PiDock.Core;
using PiDock.Updater;

namespace PiDock.Tests;

public sealed class ImageUpdateTests
{
    private sealed class Host : IImageHost
    {
        public ImageBoot Boot {get;set;}=new("A",false,10,5,"1.0.0-test.10");
        public int Writes,Trials,Promotions,Reverts;
        public bool Good=true,FailWrite;
        public Task WriteCandidate(string slot,string directory,OsRelease release,CancellationToken ct)
        {
            Assert.NotEqual(Boot.Slot,slot); Assert.All(OsUpdatePackage.ImageFiles,path=>Assert.True(File.Exists(Path.Combine(directory,path))));
            if(FailWrite)throw new IOException("simulated power interruption");
            Writes++;return Task.CompletedTask;
        }
        public Task Trial(string current,string candidate,CancellationToken ct){Assert.Equal(Boot.Slot,current);Assert.NotEqual(current,candidate);Trials++;return Task.CompletedTask;}
        public Task Promote(string slot,CancellationToken ct){Assert.Equal(Boot.Slot,slot);Promotions++;return Task.CompletedTask;}
        public Task RebootPrevious(CancellationToken ct){Reverts++;return Task.CompletedTask;}
        public Task<bool> Healthy(CancellationToken ct)=>Task.FromResult(Good);
    }
    private sealed class Fixture : IDisposable
    {
        public string Directory {get;}=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-image-"+Guid.NewGuid());
        public string Trust=>Path.Combine(Directory,"trust");
        public string State=>Path.Combine(Directory,"state");
        public Host Host {get;}=new();
        private readonly ECDsa key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public Fixture(){System.IO.Directory.CreateDirectory(Trust);File.WriteAllText(Path.Combine(Trust,"image-test.pem"),key.ExportSubjectPublicKeyInfoPem());}
        public (string Package,OsRelease Release) Package(long sequence=11,long minimum=10)
        {
            string source=Path.Combine(Directory,Guid.NewGuid().ToString());System.IO.Directory.CreateDirectory(Path.Combine(source,"system"));
            foreach(string path in OsUpdatePackage.ImageFiles)File.WriteAllBytes(Path.Combine(source,path),RandomNumberGenerator.GetBytes(4096));
            string package=Path.Combine(Directory,Guid.NewGuid()+".zip");
            return(package,OsUpdatePackage.CreateImage(source,package,"1.0.0-test."+sequence,sequence,minimum,"image-test",key.ExportPkcs8PrivateKeyPem()));
        }
        public ImageUpdateEngine Engine()=>new(State,Trust,Host);
        public async Task<OsUpdateOperation> Accept(ImageUpdateEngine engine,(string Package,OsRelease Release) release,string source="local")
        {
            await using var input=File.OpenRead(release.Package);await engine.Stage(input);
            return await engine.Install(new(Guid.NewGuid(),release.Release.Digest,source,"test-owner"));
        }
        public void Dispose(){key.Dispose();if(System.IO.Directory.Exists(Directory))System.IO.Directory.Delete(Directory,true);}
    }
    [Fact]
    public void ImagePackageRequiresExactSignedComponentsAndReportsItsKind()
    {
        using var fixture=new Fixture();var package=fixture.Package();
        var verified=OsUpdatePackage.Verify(package.Package,fixture.Trust,"linux-arm64");
        Assert.Equal("image",verified.Info.Kind);Assert.Equal(11,verified.Manifest.ImageSequence);Assert.Equal(3,verified.Manifest.Files.Count);
        // A digest check covers every image byte, not merely the bootloader signature sidecar.
        using(var archive=System.IO.Compression.ZipFile.Open(package.Package,System.IO.Compression.ZipArchiveMode.Update))
        {var entry=archive.GetEntry("system/rootfs.verity")!;entry.Delete();using var output=archive.CreateEntry("system/rootfs.verity").Open();output.Write(new byte[4096]);}
        Assert.Throws<InvalidDataException>(()=>OsUpdatePackage.Verify(package.Package,fixture.Trust,"linux-arm64"));
    }
    [Fact]
    public async Task TrialPromotesOnlyAfterRebootVersionAndHealthAgree()
    {
        using var fixture=new Fixture();var engine=fixture.Engine();await fixture.Accept(engine,fixture.Package());
        await engine.RunPending();Assert.Equal("Checking",engine.Snapshot.Operation!.State);Assert.Equal(1,fixture.Host.Writes);Assert.Equal(1,fixture.Host.Trials);Assert.Equal(0,fixture.Host.Promotions);
        fixture.Host.Boot=new("B",true,11,10,"1.0.0-test.11");
        var afterBoot=fixture.Engine();await afterBoot.Recover();
        Assert.Equal("Healthy",afterBoot.Snapshot.Operation!.State);Assert.Equal(1,fixture.Host.Promotions);Assert.Equal("1.0.0-test.11",afterBoot.Snapshot.InstalledVersion);
    }
    [Fact]
    public async Task FailedCandidateReturnsToPreviousSlotAndConfirmsItsHealth()
    {
        using var fixture=new Fixture();var engine=fixture.Engine();await fixture.Accept(engine,fixture.Package());await engine.RunPending();
        fixture.Host.Boot=new("B",true,11,10,"1.0.0-test.11");fixture.Host.Good=false;
        var trial=fixture.Engine();await trial.Recover();Assert.Equal("RollingBack",trial.Snapshot.Operation!.State);Assert.Equal(1,fixture.Host.Reverts);Assert.Equal(0,fixture.Host.Promotions);
        fixture.Host.Boot=new("A",false,10,5,"1.0.0-test.10");fixture.Host.Good=true;
        var restored=fixture.Engine();await restored.Recover();Assert.Equal("RolledBack",restored.Snapshot.Operation!.State);
    }
    [Fact]
    public async Task WrongBootedReleaseNeverPromotesEvenWhenServicesAreHealthy()
    {
        using var fixture=new Fixture();var engine=fixture.Engine();await fixture.Accept(engine,fixture.Package());await engine.RunPending();
        fixture.Host.Boot=new("B",true,12,10,"unrelated-image");await fixture.Engine().Recover();
        Assert.Equal(0,fixture.Host.Promotions);Assert.Equal(1,fixture.Host.Reverts);
    }
    [Theory]
    [InlineData(10,5)]
    [InlineData(9,5)]
    [InlineData(11,4)]
    [InlineData(11,11)]
    public async Task SignedPolicyRejectsDowngradesAndUpdatesWithoutAllowedHealthRollback(long sequence,long minimum)
    {
        using var fixture=new Fixture();var package=fixture.Package(sequence,minimum);await using var input=File.OpenRead(package.Package);
        await Assert.ThrowsAsync<InvalidDataException>(()=>fixture.Engine().Stage(input));Assert.Equal(0,fixture.Host.Writes);
    }
    [Fact]
    public async Task CloudPermissionIsSeparateAndRevocationBeforeWritesRejectsTheRequest()
    {
        using var fixture=new Fixture();var engine=fixture.Engine();var package=fixture.Package();
        await using(var input=File.OpenRead(package.Package))await engine.Stage(input);
        var request=new OsUpdateRequest(Guid.NewGuid(),package.Release.Digest,"cloud","cloud-owner");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>engine.Install(request));
        await engine.SetCloudEnabled(true);await engine.Install(request);await engine.SetCloudEnabled(false);await engine.RunPending();
        Assert.Equal("Rejected",engine.Snapshot.Operation!.State);Assert.Equal(0,fixture.Host.Writes);
    }
    [Fact]
    public async Task InterruptedInactiveWriteDoesNotRequestTrialOrPromote()
    {
        using var fixture=new Fixture();fixture.Host.FailWrite=true;var engine=fixture.Engine();await fixture.Accept(engine,fixture.Package());await engine.RunPending();
        Assert.Equal("Failed",engine.Snapshot.Operation!.State);Assert.Equal(0,fixture.Host.Trials);Assert.Equal(0,fixture.Host.Promotions);Assert.Equal("A",fixture.Host.Boot.Slot);
    }
    [Fact]
    public async Task LegacyServiceUpdaterRejectsWholeSystemImages()
    {
        using var fixture=new Fixture();System.IO.Directory.CreateDirectory(Path.Combine(fixture.Directory,"install"));
        var engine=new UpdateEngine(Path.Combine(fixture.Directory,"legacy"),Path.Combine(fixture.Directory,"install"),fixture.Trust,"linux-arm64",new LegacyHost());
        await using var input=File.OpenRead(fixture.Package().Package);
        await Assert.ThrowsAsync<InvalidDataException>(()=>engine.Stage(input));
    }
    private sealed class LegacyHost : IUpdateHost
    {
        public Task Start(CancellationToken ct)=>Task.CompletedTask;
        public Task Stop(CancellationToken ct)=>Task.CompletedTask;
        public Task<bool> Healthy(string? digest,CancellationToken ct)=>Task.FromResult(true);
    }
}
