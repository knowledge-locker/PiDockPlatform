using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using PiDock.Agent;
using PiDock.Cloud.Web;
using PiDock.Core;
using PiDock.Web.Shared;
using PiDock.UiReview;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("The isolated UI review tool can run only in Development.");
var surface = typeof(ReviewSurface).Assembly.GetName().Name!.Contains("Cloud", StringComparison.Ordinal) ? "cloud" : "appliance";
var state = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-ui-review-" + Guid.NewGuid());
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["PiDock:StateDirectory"] = state, ["Mqtt:Enabled"] = "false", ["Broker:SocketPath"] = null, ["AllowedHosts"] = "localhost;127.0.0.1" });
builder.AddPiDockAuthentication("Review-" + surface);
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, surface == "appliance" ? 7351 : 7352));
builder.Services.AddAuthentication(o => { o.DefaultAuthenticateScheme = "ReviewFixture"; o.DefaultChallengeScheme = "ReviewFixture"; })
    .AddScheme<AuthenticationSchemeOptions, ReviewAuthenticationHandler>("ReviewFixture", _ => { });
builder.Services.AddSingleton<ReviewIdentity>();
builder.Services.AddSingleton(new ReviewSurface(surface));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
builder.Services.AddSingleton<ICapacityProbe, ReviewCapacity>();
builder.Services.AddSingleton(sp => new HistoryStore(state, sp.GetRequiredService<ICapacityProbe>()));
builder.Services.AddSingleton<HostMonitor>(); builder.Services.AddSingleton<BrokerClient>();
builder.Services.AddSingleton<IApplicationRuntime>(sp => sp.GetRequiredService<BrokerClient>());
builder.Services.AddSingleton(sp => new RemoteApplicationAgent(builder.Configuration, sp.GetRequiredService<IApplicationRuntime>(), sp.GetRequiredService<MqttConnection>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<StateDirectory>().Path));
builder.Services.AddHostedService(sp => sp.GetRequiredService<RemoteApplicationAgent>());
builder.Services.AddSingleton(new GpioSimulator(true)); builder.Services.AddSingleton<MqttConnection>();
builder.Services.AddSingleton(sp => new CloudEnrollmentClient(state, sp.GetRequiredService<MqttConnection>(), enabled: false));
builder.Services.AddSingleton<IDeviceCertificateIssuer, DeviceCertificateIssuer>();
builder.Services.AddSingleton<EnrollmentStore>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<AgentSession>();
builder.Services.AddSingleton<DeliveryStatus>();
builder.Services.AddSingleton(sp => new AlarmMonitor(state, sp.GetRequiredService<HistoryStore>(), sp.GetRequiredService<TimeProvider>(), builder.Configuration.GetSection("Alarms:Rules").Get<AlarmRule[]>()));
builder.Services.AddSingleton<TelemetryDeliveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryDeliveryService>());
builder.Services.AddSingleton<MonitoringService>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitoringService>());
builder.Services.AddSingleton<FleetStore>(); builder.Services.AddSingleton<ApplicationCommandStore>(); builder.Services.AddSingleton<IngestionService>();
if (surface == "cloud") builder.Services.AddHostedService<ReviewFleetUpdates>();
var app = builder.Build();
var users = app.Services.GetRequiredService<UserStore>();
string secret = Totp.NewSecret(), password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
users.Add("review-owner", password, secret, "review", "Owner", true, -1);
var user = users.Authenticate("review-owner", password, Totp.Code(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), DateTimeOffset.UtcNow)!;
app.Services.GetRequiredService<ReviewIdentity>().User = new ClaimsPrincipal(new ClaimsIdentity([
    new Claim(ClaimTypes.Name,user.Username),new Claim(ClaimTypes.Role,user.Role),new Claim("tenant",user.TenantId),new Claim("stamp",user.Stamp),
    new Claim("expires",DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds().ToString())], "ReviewFixture"));
ReviewSamples.Seed(state);
app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery(); app.MapStaticAssets();
if (surface == "cloud") DeviceHistory.MapExport(app);
app.MapRazorComponents<ReviewApp>().AddInteractiveServerRenderMode();
app.MapPost("/auth/logout", () => Results.Redirect("/"));
app.Logger.LogInformation("Isolated UI review on loopback: {Surface}. Only disposable sample state is used.", surface);
try { await app.RunAsync(); } finally { await app.DisposeAsync(); if (Directory.Exists(state)) Directory.Delete(state, true); }
