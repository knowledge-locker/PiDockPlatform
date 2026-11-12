using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

public interface IDigitalLine : IDisposable { bool Read(); void Write(bool high); }
public interface IDigitalIoDriver
{
    (string Chip, DigitalLine[] Lines) Describe();
    IDigitalLine Open(int gpio, bool output, bool initialValue);
}

// GPIO character-device ABI v2. No /dev/mem access or assumptions about gpiochip numbering.
public sealed class LinuxDigitalIo(string devices = "/dev", string modelPath = "/proc/device-tree/model") : IDigitalIoDriver
{
    private const uint ChipInfo = 0x8044B401, LineInfo = 0xC100B405, RequestLine = 0xC250B407;
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, nuint request, [In, Out] byte[] data);
    private static void Call(SafeFileHandle handle, uint request, byte[] bytes)
    {
        if (ioctl(handle.DangerousGetHandle().ToInt32(), request, bytes) != 0) throw new IOException("GPIO request rejected by the kernel.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }
    private static string Text(byte[] bytes, int offset, int length) => Encoding.UTF8.GetString(bytes, offset, length).TrimEnd('\0');
    private string FindChip()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(modelPath) || !File.ReadAllText(modelPath).Contains("Raspberry Pi 5", StringComparison.Ordinal))
            throw new IOException("Physical I/O requires the supported Raspberry Pi 5 host.");
        foreach (var path in Directory.EnumerateFiles(devices, "gpiochip*").Order(StringComparer.Ordinal)) {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"\Agpiochip[0-9]{1,3}\z")) continue;
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var info = new byte[68]; Call(handle, ChipInfo, info);
            if (Text(info,32,32).Contains("rp1", StringComparison.OrdinalIgnoreCase) && BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(64)) >= 28) return path;
        }
        throw new IOException("The Pi 5 RP1 GPIO character device is unavailable.");
    }
    public (string Chip, DigitalLine[] Lines) Describe()
    {
        var path = FindChip(); using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        return (path, DigitalIoRules.HeaderGpios.Select(gpio => {
            var info = new byte[256]; BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(64), (uint)gpio); Call(handle, LineInfo, info);
            ulong flags = BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(72));
            return new DigitalLine(gpio, Text(info,0,32), Text(info,32,32), (flags & 1) != 0,
                (flags & 8) != 0 ? "output" : (flags & 4) != 0 ? "input" : "unknown",
                (flags & 256) != 0 ? "pull-up" : (flags & 512) != 0 ? "pull-down" : (flags & 1024) != 0 ? "disabled" : "unknown", (flags & 2) != 0);
        }).ToArray());
    }
    public IDigitalLine Open(int gpio, bool output, bool initialValue)
    {
        if (!DigitalIoRules.Assignable(gpio)) throw new ArgumentException("Reserved or invalid GPIO.");
        var path = FindChip(); using var chip = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var request = EncodeRequest(gpio, output, initialValue); Call(chip, RequestLine, request);
        int fd = BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(588));
        if (fd < 0) throw new IOException("Kernel did not return a GPIO line handle.");
        return new Line(new SafeFileHandle(new IntPtr(fd), true));
    }
    public static byte[] EncodeRequest(int gpio, bool output, bool high)
    {
        var bytes = new byte[592]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)gpio));
        Encoding.ASCII.GetBytes("pidock-maintenance").CopyTo(bytes,256);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(288), output ? 8UL : 4UL);
        if (output) {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(296),1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(320),2);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(328), high ? 1UL : 0UL);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(336),1);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(560),1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(588),-1); return bytes;
    }
    private sealed class Line(SafeFileHandle handle) : IDigitalLine
    {
        public bool Read() { var bytes = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8),1); Call(handle,0xC010B40E,bytes); return (BinaryPrimitives.ReadUInt64LittleEndian(bytes) & 1) != 0; }
        public void Write(bool high) { var bytes = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(bytes,high ? 1UL : 0UL); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8),1); Call(handle,0xC010B40F,bytes); }
        public void Dispose() => handle.Dispose();
    }
}
