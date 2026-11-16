using System.Buffers.Binary;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PiDock.Appliance.Broker;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class DigitalIoTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
    }
    private sealed class Line : IDigitalLine
    {
        public bool High, Closed;
        public List<bool> Writes { get; } = [];
        public bool Read() => High;
        public void Write(bool high) { High = high; Writes.Add(high); }
        public void Dispose() => Closed = true;
    }
    private sealed class Driver : IDigitalIoDriver
    {
        public readonly List<Line> Opened = [];
        public bool InUse;
        public (string Chip, DigitalLine[] Lines) Describe() => ("/dev/gpiochip0", [new(17,"GPIO17","",InUse,"input","disabled",false)]);
        public IDigitalLine Open(int gpio, bool output, bool initialValue)
        {
            if (InUse) throw new IOException("busy");
            var line = new Line { High = initialValue }; Opened.Add(line); return line;
        }
    }
    private static DigitalIoService Service(Driver driver, Clock clock, params (string, string)[] settings) => new(driver,
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(x => new KeyValuePair<string,string?>(x.Item1,x.Item2))).Build(), clock, NullLogger<DigitalIoService>.Instance);

    [Fact]
    public void EmptyPolicyDetectsHeaderButNeverClaimsPins()
    {
        var driver = new Driver(); using var service = Service(driver, new());
        var state = service.Status(); Assert.True(state.Available); Assert.False(state.Lines.Single().CanOutput);
        Assert.Throws<ArgumentException>(() => service.Claim(new(Guid.NewGuid(),17,"output",true)));
        Assert.Throws<ArgumentException>(() => service.Claim(new(Guid.NewGuid(),17,"input")));
        Assert.Empty(driver.Opened);
    }
    [Fact]
    public void OutputExpiresUsingMonotonicTimeAndAppliesFallbackBeforeClose()
    {
        var driver = new Driver(); var clock = new Clock(); using var service = Service(driver, clock, ("Gpio:Outputs:17","false"));
        service.Claim(new(Guid.NewGuid(),17,"output",true,5)); var line = driver.Opened.Single(); Assert.True(line.High);
        clock.Ticks = 4999; Assert.Single(service.Status().Leases); Assert.False(line.Closed);
        clock.Ticks = 5000; Assert.Empty(service.Status().Leases); Assert.Equal(new[] {false},line.Writes); Assert.True(line.Closed);
    }
    [Fact]
    public void ReleaseIsScopedToSessionAndInputNeverWrites()
    {
        var driver = new Driver(); using var service = Service(driver, new(), ("Gpio:Inputs:0","17")); var session = Guid.NewGuid();
        service.Claim(new(session,17,"input")); var line = driver.Opened.Single();
        service.Release(Guid.NewGuid()); Assert.False(line.Closed);
        Assert.Throws<ArgumentException>(() => service.Claim(new(Guid.NewGuid(),17,"input")));
        service.Release(session); Assert.True(line.Closed); Assert.Empty(line.Writes);
    }
    [Fact]
    public void ReservedPinsInvalidDurationsAndKernelConflictsAreRejected()
    {
        var driver = new Driver { InUse = true }; using var service = Service(driver, new(), ("Gpio:Inputs:0","17"));
        Assert.Throws<ArgumentException>(() => service.Claim(new(Guid.NewGuid(),2,"output")));
        Assert.Throws<ArgumentException>(() => service.Claim(new(Guid.NewGuid(),17,"input",false,61)));
        Assert.Throws<ArgumentException>(() => service.Claim(new(Guid.Empty,17,"input")));
        Assert.Throws<IOException>(() => service.Claim(new(Guid.NewGuid(),17,"input")));
        Assert.Empty(service.Status().Leases); Assert.Empty(driver.Opened);
    }
    [Fact]
    public void PinPolicyPersistsWithoutDrivingOutputsAndPatchesOnlyTheApprovedPin()
    {
        string directory=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-gpio-"+Guid.NewGuid());
        string path=Path.Combine(directory,"gpio.json");var driver=new Driver();
        try
        {
            using(var service=Service(driver,new(),("Gpio:PolicyPath",path)))
            {
                service.ConfigurePin(new(17,true,false,false));
                Assert.Empty(driver.Opened);Assert.True(service.Status().Lines.Single().CanInput);Assert.False(service.Status().Lines.Single().CanOutput);
                service.Claim(new(Guid.NewGuid(),17,"input"));
                Assert.Throws<InvalidOperationException>(()=>service.ConfigurePin(new(18,false,true,false)));
            }
            using var restored=Service(driver,new(),("Gpio:PolicyPath",path));
            restored.ConfigurePin(new(18,false,true,false));
            Assert.True(restored.Status().Lines.Single().CanInput);
            restored.ConfigurePin(new(17,false,false,false));
            Assert.False(restored.Status().Lines.Single().CanInput);
            restored.Claim(new(Guid.NewGuid(),18,"output",true));Assert.True(driver.Opened.Last().High);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact]
    public void InvalidSavedPolicyIsNeverCachedAndCannotPartiallyEnableValidPins()
    {
        var driver=new Driver();using var service=Service(driver,new(),("Gpio:Inputs:0","17"),("Gpio:Outputs:2","false"));
        Assert.Throws<ArgumentException>(()=>service.Status());
        Assert.Throws<ArgumentException>(()=>service.Claim(new(Guid.NewGuid(),17,"input")));
        Assert.Throws<ArgumentException>(()=>service.Claim(new(Guid.NewGuid(),17,"input")));
        Assert.Empty(driver.Opened);
    }
    [Fact]
    public async Task NormalServiceShutdownAppliesOutputFallbackAndClosesClaim()
    {
        var driver = new Driver(); using var service = Service(driver, new(), ("Gpio:Outputs:17","true"));
        await service.StartAsync(CancellationToken.None);
        service.Claim(new(Guid.NewGuid(),17,"output",false)); var line = driver.Opened.Single();
        await service.StopAsync(CancellationToken.None);
        Assert.True(line.Closed); Assert.Equal(new[] {true},line.Writes);
    }
    [Fact]
    public void KernelRequestSetsInitialOutputAtomicallyAndClearsPadding()
    {
        var bytes = LinuxDigitalIo.EncodeRequest(17,true,true);
        Assert.Equal(592,bytes.Length);
        Assert.Equal(17U,BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(8UL,BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(288)));
        Assert.Equal(1U,BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(296)));
        Assert.Equal(2U,BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(320)));
        Assert.Equal(1UL,BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(328)));
        Assert.Equal(1UL,BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(336)));
        Assert.Equal(1U,BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(560)));
        Assert.Equal(-1,BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(588)));
        Assert.All(bytes[300..320], value => Assert.Equal((byte)0,value));
        var input = LinuxDigitalIo.EncodeRequest(17,false,false);
        Assert.Equal(4UL,BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(288)));
        Assert.Equal(0U,BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(296)));
    }
}
