using System.Text.Json;
using PiDock.Core;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: PiDock.AppAuthoring.Check <plan.json|->. Offline validation only; no deployment or target approval.");
    return 2;
}
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await using var source = args[0] == "-" ? Console.OpenStandardInput() : File.OpenRead(args[0]);
    var bytes = new byte[ApplicationAuthoring.MaximumInputBytes + 1]; int count = 0;
    while (count < bytes.Length)
    {
        int read = await source.ReadAsync(bytes.AsMemory(count), deadline.Token).AsTask().WaitAsync(deadline.Token);
        if (read == 0) break;
        count += read;
    }
    var report = ApplicationAuthoring.Check(bytes.AsMemory(0, count));
    Console.WriteLine(JsonSerializer.Serialize(report, StateFiles.Json));
    return report.Valid ? 0 : 2;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or NotSupportedException)
{
    // Do not echo input bytes, paths, exception messages, credentials or machine configuration.
    Console.Error.WriteLine("Authoring input is unavailable or exceeded the read deadline. No target was contacted.");
    return 2;
}
