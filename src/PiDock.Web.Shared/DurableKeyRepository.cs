using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using PiDock.Core;

namespace PiDock.Web.Shared;

// A protected account can only survive a power loss if its key is durable first.
// Keep the framework's XML format and fail-closed reader; never skip damaged keys.
public sealed class DurableKeyRepository : IXmlRepository
{
    private readonly string directory;
    private readonly FileSystemXmlRepository reader;
    public DurableKeyRepository(string directory)
    {
        this.directory = StateFiles.PrepareDirectory(directory);
        reader = new FileSystemXmlRepository(new DirectoryInfo(this.directory), NullLoggerFactory.Instance);
    }
    public IReadOnlyCollection<XElement> GetAllElements() => reader.GetAllElements();
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        // Filenames are not part of the key format. A unique name avoids replacing
        // existing key/revocation material and ignores untrusted path components.
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".xml");
        StateFiles.WriteAtomic(path, element.ToString(SaveOptions.DisableFormatting));
    }
}
