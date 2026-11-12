using System.Net;
using PiDock.Core;
using PiDock.Appliance.Broker;
var builder = WebApplication.CreateBuilder(args);
var socket = builder.Configuration["Broker:SocketPath"] ?? "/run/pidock/broker.sock";
if (!Path.IsPathFullyQualified(socket)) throw new InvalidOperationException("Broker socket must be an absolute path.");
var socketDirectory = StateFiles.PrepareDirectory(Path.GetDirectoryName(socket)!);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(socketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
if (File.Exists(socket)) throw new InvalidOperationException("Broker socket already exists; stop the other broker or remove a verified stale socket locally.");
builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 65536; k.ListenUnixSocket(socket); });
builder.Services.AddSingleton(_ => new DockerOperations(UnixHttp.Connect(builder.Configuration["Broker:DockerSocket"] ?? "/var/run/docker.sock", TimeSpan.FromMinutes(3))));
builder.Services.AddSingleton<IDigitalIoDriver, LinuxDigitalIo>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DigitalIoService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DigitalIoService>());
var installedRelease = OsUpdateRules.Installed(AppContext.BaseDirectory);
var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (ArgumentException e) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException or IOException or UnauthorizedAccessException)
    { app.Logger.LogWarning("Broker operation failed: {Type}", e.GetType().Name); context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "Host operation unavailable; check the runtime and broker logs." }); }
});
app.MapGet("/health/update", () => new OsUpdateHealth(installedRelease?.Digest, true));
app.MapGet("/applications", (DockerOperations docker, CancellationToken ct) => docker.List(ct));
app.MapGet("/gpio", (DigitalIoService gpio) => gpio.Status());
app.MapPost("/gpio/leases", (DigitalLeaseRequest request, DigitalIoService gpio) => { gpio.Claim(request); return Results.Ok(); });
app.MapDelete("/gpio/leases/{session:guid}", (Guid session, DigitalIoService gpio) => { gpio.Release(session); return Results.Ok(); });
if (builder.Configuration.GetValue<bool>("Broker:EnableDeployments"))
{
    app.MapPost("/applications", async (ApplicationManifest manifest, DockerOperations docker, CancellationToken ct) => Results.Ok(new { id = await docker.Deploy(manifest, ct) }));
}
if (builder.Configuration.GetValue<bool>("Broker:EnableLifecycle") || builder.Configuration.GetValue<bool>("Broker:EnableDeployments"))
{
    app.MapPost("/applications/{id}/restart", async (string id, DockerOperations docker, CancellationToken ct) => { await docker.Restart(id, ct); return Results.Ok(); });
    app.MapPost("/applications/{id}/start", async (string id, DockerOperations docker, CancellationToken ct) => { await docker.ChangeState(id, true, ct); return Results.Ok(); });
    app.MapPost("/applications/{id}/stop", async (string id, DockerOperations docker, CancellationToken ct) => { await docker.ChangeState(id, false, ct); return Results.Ok(); });
}
await app.StartAsync();
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
try { await app.WaitForShutdownAsync(); } finally { File.Delete(socket); }
