using System.Text.Json;
using System.Runtime.InteropServices;
namespace PiDock.Core;

public static class StateFiles
{
    public static string PrepareDirectory(string path)
    {
        path = Path.GetFullPath(path);
        // Never follow a symlink into an unexpected state directory.
        for (var item = new DirectoryInfo(path); item is not null; item = item.Parent)
            if (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("State directories cannot contain symbolic links.");
        var missing = new List<string>();
        for (var item = new DirectoryInfo(path); item is not null && !item.Exists; item = item.Parent) missing.Add(item.FullName);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var created in missing.AsEnumerable().Reverse())
        {
            SyncDirectory(created);
            SyncDirectory(Path.GetDirectoryName(created)!);
        }
        return path;
    }
    public static void WriteAtomic(string path, string text)
    {
        var directory = PrepareDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = Path.Combine(directory, Guid.NewGuid() + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                using var writer = new StreamWriter(stream, leaveOpen: true);
                writer.Write(text); writer.Flush(); stream.Flush(true);
            }
            File.Move(temporary, path, true);
            SyncDirectory(directory);
        }
        finally { File.Delete(temporary); }
    }
    private static void SyncDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        int descriptor = OpenDirectory(directory, 0);
        if (descriptor < 0) throw new IOException("Could not open state directory for synchronization.");
        try { if (Fsync(descriptor) != 0) throw new IOException("Could not synchronize state directory."); }
        finally { Close(descriptor); }
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}
