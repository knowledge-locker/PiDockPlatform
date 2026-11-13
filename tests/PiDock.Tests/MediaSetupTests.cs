using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using PiDock.Core;
using PiDock.Web.Shared;
namespace PiDock.Tests;

public sealed class MediaSetupTests
{
    private static MediaSetup MainSetup() => new(2,"owner",new PasswordHasher<string>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount=210000 })).HashPassword("owner","a private initial password"),Totp.NewSecret(),1,null,
        Network:new(new(true),new(false)));
    [Fact] public void MainSupportsIndependentStaticAndDhcpInterfaces()
    {
        var setup=MainSetup() with { Wifi=new("54657374",new string('1',64),"US"),Network=new(new(true,"static","192.168.5.20/24","192.168.5.1",["1.1.1.1"]),new(true)) };
        MediaProvisioning.Validate(setup);
        string wired=MediaNetworking.Render(setup.Network!.Ethernet,false),wireless=MediaNetworking.Render(setup.Network.Wifi,true);
        Assert.Contains("Address=192.168.5.20/24\n",wired);Assert.Contains("Gateway=192.168.5.1\n",wired);Assert.Contains("DNS=1.1.1.1\n",wired);
        Assert.Contains("DHCP=no\n",wired);Assert.Contains("DHCP=yes\n",wireless);Assert.DoesNotContain("192.168.5.",wireless);
        Assert.Contains("ActivationPolicy=always-down",MediaNetworking.Render(new(false),true));
    }
    [Theory]
    [InlineData("192.168.1.0/24","192.168.1.1")]
    [InlineData("192.168.1.255/24","192.168.1.1")]
    [InlineData("192.168.1.4/24","192.168.2.1")]
    [InlineData("192.168.1.4/24","192.168.1.4")]
    [InlineData("192.168.1.4/24","192.168.1.255")]
    [InlineData("192.168.1.4/24\nDNS=evil","192.168.1.1")]
    [InlineData("127.0.0.2/24","127.0.0.1")]
    [InlineData("192.168.01.4/24","192.168.1.1")]
    public void InvalidStaticAddressesCannotReachNetworkConfiguration(string address,string gateway)
        =>Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(MainSetup() with {Network=new(new(true,"static",address,gateway),new(false))}));
    [Fact] public void HeadlessMainRequiresNetworkAndWifiRequiresCredentials()
    {
        Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(MainSetup() with {Network=new(new(false),new(false))}));
        Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(MainSetup() with {Network=new(new(false),new(true))}));
        Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(MainSetup() with {Network=new(new(true,"dhcp","192.168.1.2/24"),new(false))}));
    }
    [Fact] public void NodeOmitsHumanOwnerExternalNetworkingAndCloudPermissions()
    {
        var node=new MediaSetup(2,"","","",0,null,"Node",new(new(false),new(false)));
        MediaProvisioning.Validate(node);
        Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(node with {Username="owner"}));
        Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(node with {Cloud=new("https://cloud.example")}));
        Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(node with {Network=new(new(true),new(false))}));
        Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(node with {Mode="anything"}));
    }
    [Fact] public void CloudConsentRequiresValidHttpsRegistrationAndIsSeparate()
    {
        var main=MainSetup() with {Cloud=new("https://cloud.example",new string('A',32),true,false)};
        MediaProvisioning.Validate(main);MediaProvisioning.Validate(main with {Cloud=main.Cloud! with {RemoteControl=false,RemoteUi=true}});
        foreach(var url in new[]{"http://cloud.example","https://cloud.example/path","https://owner@cloud.example","https://cloud.example/?secret=1","https://cloud.example/#secret"})
            Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(main with {Cloud=main.Cloud! with {Url=url}}));
        Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(main with {Cloud=main.Cloud! with {EnrollmentCode=null}}));
        Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(main with {Cloud=main.Cloud! with {EnrollmentCode="bad"}}));
        MediaProvisioning.Validate(MainSetup() with {Cloud=new("https://cloud.example")});
    }
    [Fact] public void OlderMediaDefaultsToMainAndRejectsExpandedSettings()
    {
        var main=MainSetup();var old=main with {Schema=1,Network=null};
        MediaProvisioning.Validate(old);
        var json=JsonSerializer.Serialize(old,StateFiles.Json);
        var legacy=JsonSerializer.Deserialize<MediaSetup>(json.Replace(",\"mode\":\"Main\"",""),StateFiles.Json)!;
        Assert.Equal("Main",legacy.Mode);
        Assert.Throws<InvalidDataException>(()=>MediaProvisioning.Validate(main with {Schema=1}));
    }
    [Fact] public void PersistedModeAndNetworkKeepPrivateFilesPrivateAndParentTraversable()
    {
        var root=StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-media-files-"+Guid.NewGuid()));
        try {
            var setup=MainSetup() with { Wifi=new("54657374",new string('a',64),"US"),Network=new(new(true),new(true)) };
            MediaProvisioning.WriteSystemConfiguration(setup,root);
            string state=Path.Combine(root,"var/lib/pidock"),network=Path.Combine(state,"network");
            Assert.Contains("psk=",File.ReadAllText(Path.Combine(network,"wpa_supplicant.conf")));
            Assert.DoesNotContain(new string('a',64),File.ReadAllText(Path.Combine(network,"interfaces.json")));
            Assert.False(File.Exists(Path.Combine(state,"node-mode")));
            if(!OperatingSystem.IsWindows()) {
                Assert.Equal((UnixFileMode)0x1ed,File.GetUnixFileMode(state));
                Assert.Equal((UnixFileMode)0x180,File.GetUnixFileMode(Path.Combine(network,"wpa_supplicant.conf")));
            }
            Assert.Contains("Address=fe80::1/64",MediaNetworking.UsbLink(true));
            Assert.Contains("Property=PIDOCK_USB_NODE=1",MediaNetworking.UsbLink(false));
            Assert.DoesNotContain("DHCP=yes",MediaNetworking.UsbLink(false));
            Assert.Contains("IPForward=no",MediaNetworking.UsbLink(true));
        } finally {Directory.Delete(root,true);}
    }
    [Fact] public void InitialCloudPlanIsInvalidatedByOwnerRecovery()
    {
        var directory=StateFiles.PrepareDirectory(Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-setup-"+Guid.NewGuid()));
        try {
            var store=new UserStore(new EphemeralDataProtectionProvider(),directory);
            string stamp=store.ImportInitialMediaOwner(MainSetup());
            var plan=new MediaCloudPlan("owner",stamp,new("https://cloud.example",new string('A',32),true,true));
            Assert.True(MediaProvisioning.OwnerCurrent(store,plan));
            store.RecoverLocalOwner(new PhysicalPresence(),"replacement","a new private owner password",Totp.NewSecret(),-1);
            Assert.False(MediaProvisioning.OwnerCurrent(store,plan));
        } finally {Directory.Delete(directory,true);}
    }
}
