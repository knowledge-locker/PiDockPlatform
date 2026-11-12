using PiDock.Core;
using Microsoft.AspNetCore.Components.Authorization;
using PiDock.Agent;
using PiDock.Cloud.Web;
using PiDock.Cloud.Web.Components;
using PiDock.Web.Shared;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
if (args.Contains("--certificate-signer")) {
    await using var signer = IsolatedCertificateSigner.Build(builder);
    await signer.RunAsync();
    return;
}
builder.AddPiDockAuthentication("Cloud");
builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
builder.Services.AddSingleton<FleetStore>();
builder.Services.AddSingleton<ApplicationCommandStore>();
builder.Services.AddSingleton<OsUpdateStore>();
builder.Services.AddSingleton<FactoryImageStore>();
builder.Services.AddSingleton<RemoteAccessService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RemoteAccessService>());
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IDeviceCertificateIssuer>(sp => builder.Configuration["Enrollment:SignerSocket"] is { Length: > 0 } socket
    ? new SocketCertificateIssuer(socket)
    : new DeviceCertificateIssuer(builder.Configuration, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<EnrollmentStore>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("remote-agent", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("enrollment", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddSingleton<MqttConnection>();
builder.Services.AddSingleton<IngestionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestionService>());
var app = builder.Build();
if (app.Commission(args, singleOwner: false)) return;
if (builder.Configuration.GetValue<bool>("ReverseProxy:Enabled"))
    app.UseForwardedHeaders(TrustedProxy.Options(builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [], builder.Configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? []));
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error"); app.UseHsts();
    app.UseWhen(context => context.Request.Path != "/health/live", branch => branch.UseHttpsRedirection());
}
app.UseWebSockets();
app.Use(RemoteAccessGateway.Handle);
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.UseAntiforgery();
app.MapPiDockAuthentication("Cloud"); app.MapStaticAssets();
app.MapDeviceEnrollment();
app.MapOsUpdates(); app.MapFactoryImages(); app.MapRemoteAccess();
app.MapGet("/api/devices", (System.Security.Claims.ClaimsPrincipal user, FleetStore store) => store.For(user)).RequireAuthorization();
app.MapGet("/api/devices/{device}/history", (string device, System.Security.Claims.ClaimsPrincipal user, FleetStore store) => store.HistoryFor(user, device)).RequireAuthorization();
DeviceHistory.MapExport(app);
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization();
app.Run();
public partial class Program;
