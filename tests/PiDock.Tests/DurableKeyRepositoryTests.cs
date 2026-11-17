using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class DurableKeyRepositoryTests
{
    [Fact]
    public void ProtectedStateCanBeReadByAFreshFrameworkProvider()
    {
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var services = new ServiceCollection();
            services.AddDataProtection().SetApplicationName("PiDock.Appliance");
            services.Configure<KeyManagementOptions>(options => options.XmlRepository = new DurableKeyRepository(directory));
            using var provider = services.BuildServiceProvider();
            var ciphertext = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("PiDock.Accounts.v1").Protect("existing-owner-state");
            var files = Directory.GetFiles(directory, "*.xml");
            Assert.Single(files);
            Assert.Equal("key", XElement.Load(files[0]).Name.LocalName);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files[0]));
            var fresh = DataProtectionProvider.Create(new DirectoryInfo(directory), builder => builder.SetApplicationName("PiDock.Appliance"));
            Assert.Equal("existing-owner-state", fresh.CreateProtector("PiDock.Accounts.v1").Unprotect(ciphertext));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void CorruptExistingKeysAreNotSilentlyIgnored()
    {
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var repository = new DurableKeyRepository(directory);
            File.WriteAllText(Path.Combine(directory, "damaged.xml"), "");
            Assert.ThrowsAny<Exception>(() => repository.GetAllElements());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void FrameworkFriendlyNamesCannotEscapeOrReplaceTheRepository()
    {
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var repository = new DurableKeyRepository(directory);
            repository.StoreElement(new XElement("revocation", new XAttribute("version", 1)), "../outside");
            repository.StoreElement(new XElement("revocation", new XAttribute("version", 2)), "../outside");
            Assert.Equal(2, Directory.GetFiles(directory, "*.xml").Length);
            Assert.Equal(2, repository.GetAllElements().Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
