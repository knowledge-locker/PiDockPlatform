using PiDock.Core;
using PiDock.Updater;

if(args is ["host-runtime-check"])
{
    if(!OperatingSystem.IsLinux()||System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture!=System.Runtime.InteropServices.Architecture.Arm64)
        throw new InvalidOperationException("Image runtime acceptance requires Linux ARM64.");
    byte[] sample=System.Text.Encoding.UTF8.GetBytes("PiDockOS runtime acceptance");
    using var signing=System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    var signature=signing.SignData(sample,System.Security.Cryptography.HashAlgorithmName.SHA256);
    if(!signing.VerifyData(sample,signature,System.Security.Cryptography.HashAlgorithmName.SHA256))throw new InvalidOperationException("Runtime cryptography failed.");
    _=DateTimeOffset.UtcNow.ToString("F",System.Globalization.CultureInfo.GetCultureInfo("en-US"));
    Console.WriteLine("Runtime cryptography and globalization checks passed on Linux ARM64.");
    return;
}

if(args is ["host-time-check"])
{
    Environment.ExitCode=new ClockTrust(true).Allowed?0:2;
    return;
}

if(args is ["recover-image",var recoveryPackage])
{
    await ImageRecovery.Restore(recoveryPackage);
    return;
}

if (args is ["host-time-guard"])
{
    if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("The image time guard requires Linux.");
    using var stopGuard = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopGuard.Cancel(); };
    try { await HostTimeGuard.Run(stopGuard.Token); } catch (OperationCanceledException) when (stopGuard.IsCancellationRequested) { }
    return;
}

// An offline packaging command is available on every development host. Private signing keys never enter a web service.
if (args is ["pack", var directory, var output, var version, var runtime, var keyId, var keyFile])
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(OsUpdatePackage.Create(directory, output, version, runtime, keyId, File.ReadAllText(keyFile)).Info, StateFiles.Json));
    return;
}
if(args is ["verify-factory",var factoryPackage,var factoryTrust])
{
    var verified=FactoryImages.Verify(factoryPackage,factoryTrust);
    Console.WriteLine($"Verified factory {verified.Manifest.Version}: {verified.Digest}");return;
}
if(args is [var factoryCommand,var factoryImage,var extentsFile,var factoryOutput,var factoryVersion,var factorySequence,var development,var factoryKeyId,var factoryKeyFile] && factoryCommand is "pack-factory" or "pack-factory-v2")
{
    var patches=System.Text.Json.JsonSerializer.Deserialize<FactoryPatch[]>(File.ReadAllText(extentsFile),StateFiles.Json)??throw new InvalidDataException("Factory extents missing.");
    var result=FactoryImages.Create(factoryImage,factoryOutput,factoryVersion,long.Parse(factorySequence,System.Globalization.CultureInfo.InvariantCulture),bool.Parse(development),factoryKeyId,File.ReadAllText(factoryKeyFile),patches,factoryCommand == "pack-factory-v2" ? 2 : 1);
    Console.WriteLine($"Signed factory {result.Manifest.Version}: {result.Digest}");return;
}
if (args is ["pack-image",var imageDirectory,var imageOutput,var imageVersion,var sequenceText,var minimumText,var imageKeyId,var imageKeyFile])
{
    if(!long.TryParse(sequenceText,out var sequence)||!long.TryParse(minimumText,out var minimum))throw new ArgumentException("Image sequence values must be integers.");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(OsUpdatePackage.CreateImage(imageDirectory,imageOutput,imageVersion,sequence,minimum,imageKeyId,File.ReadAllText(imageKeyFile)).Info,StateFiles.Json));
    return;
}
if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("The privileged updater service requires Linux; packaging is available on other hosts.");
var builder = WebApplication.CreateBuilder(args);
const string socket = "/run/pidock-updater/update.sock";
Directory.CreateDirectory(Path.GetDirectoryName(socket)!);
if (File.Exists(socket)) throw new InvalidOperationException("Updater socket exists; verify that no updater is running before removing it.");
builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = OsUpdateRules.MaximumPackageBytes; k.ListenUnixSocket(socket); });
builder.Services.AddSingleton<IUpdateHost>(_ => new SystemUpdateHost("/etc/pidock/appliance.crt"));
builder.Services.AddSingleton<NativeImageHost>();
builder.Services.AddSingleton<IOsUpdateEngine>(sp => File.Exists("/etc/pidock/image-profile.json")
    ? new ImageUpdateEngine("/var/lib/pidock/updater","/etc/pidock/update-trust",sp.GetRequiredService<NativeImageHost>())
    : new UpdateEngine("/var/lib/pidock/updater", "/usr/lib/pidock", "/etc/pidock/update-trust", OsUpdateRules.Runtime, sp.GetRequiredService<IUpdateHost>()));
var app = builder.Build();
var engine = app.Services.GetRequiredService<IOsUpdateEngine>();
await engine.Recover();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
    { context.Response.StatusCode = e is InvalidOperationException ? 409 : 400; await context.Response.WriteAsJsonAsync(new { error = "Update rejected. Check package trust, target, free space and updater status." }); }
});
app.MapGet("/status", () => engine.Snapshot);
app.MapPost("/policy", async (OsUpdatePolicy policy, CancellationToken ct) => { await engine.SetCloudEnabled(policy.Enabled, ct); return Results.Ok(engine.Snapshot); });
app.MapPost("/packages", async (HttpRequest request, CancellationToken ct) => Results.Ok((await engine.Stage(request.Body, ct)).Info));
app.MapPost("/install", async (OsUpdateRequest request, CancellationToken ct) => Results.Ok(await engine.Install(request, ct)));
await app.StartAsync();
File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
using var stop = new CancellationTokenSource();
var worker = Task.Run(async () => { try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); while (await timer.WaitForNextTickAsync(stop.Token)) await engine.RunPending(stop.Token); } catch (OperationCanceledException) { } });
try { await app.WaitForShutdownAsync(); }
finally { await stop.CancelAsync(); await worker; File.Delete(socket); }
