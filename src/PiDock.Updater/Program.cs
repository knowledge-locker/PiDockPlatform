using PiDock.Core;
using PiDock.Updater;

// An offline packaging command is available on every development host. Private signing keys never enter a web service.
if (args is ["pack", var directory, var output, var version, var runtime, var keyId, var keyFile])
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(OsUpdatePackage.Create(directory, output, version, runtime, keyId, File.ReadAllText(keyFile)).Info, StateFiles.Json));
    return;
}
if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("The privileged updater service requires Linux; packaging is available on other hosts.");
var builder = WebApplication.CreateBuilder(args);
const string socket = "/run/pidock-updater/update.sock";
Directory.CreateDirectory(Path.GetDirectoryName(socket)!);
if (File.Exists(socket)) throw new InvalidOperationException("Updater socket exists; verify that no updater is running before removing it.");
builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = OsUpdateRules.MaximumPackageBytes; k.ListenUnixSocket(socket); });
builder.Services.AddSingleton<IUpdateHost>(_ => new SystemUpdateHost("/etc/pidock/appliance.crt"));
builder.Services.AddSingleton(sp => new UpdateEngine("/var/lib/pidock/updater", "/usr/lib/pidock", "/etc/pidock/update-trust", OsUpdateRules.Runtime, sp.GetRequiredService<IUpdateHost>()));
var app = builder.Build();
var engine = app.Services.GetRequiredService<UpdateEngine>();
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
