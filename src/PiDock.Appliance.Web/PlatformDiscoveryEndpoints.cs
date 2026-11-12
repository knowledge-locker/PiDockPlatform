using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PiDock.Agent;
using PiDock.Core;

namespace PiDock.Appliance.Web;

public static class PlatformDiscoveryEndpoints
{
    private const string Policy = "PlatformDiscovery";
    public static void AddPlatformDiscovery(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<PlatformDiscovery>();
        builder.Services.AddSingleton(sp => new PiDock.Web.Shared.RegisteredAgentStore(
            sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
            sp.GetRequiredService<PiDock.Web.Shared.StateDirectory>(), sp.GetRequiredService<PiDock.Web.Shared.UserStore>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, DiscoveryTokenHandler>(DiscoveryTokenHandler.SchemeName, _ => { })
            .AddPolicyScheme("PlatformDiscoveryAccess", null, options =>
            {
                options.ForwardDefaultSelector = context => context.Request.Headers.ContainsKey("Authorization")
                    ? DiscoveryTokenHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme;
                // Discovery clients need an HTTP status even for endpoints that return plain text.
                options.ForwardChallenge = DiscoveryTokenHandler.SchemeName;
                options.ForwardForbid = DiscoveryTokenHandler.SchemeName;
            });
        builder.Services.AddAuthorization(options => options.AddPolicy(Policy, policy =>
        {
            policy.AddAuthenticationSchemes("PlatformDiscoveryAccess").RequireAuthenticatedUser();
            policy.RequireAssertion(context => context.User.IsInRole("Owner") || context.User.HasClaim("scope", "platform.discovery.read"));
        }));
        builder.Services.AddRateLimiter(options => options.AddPolicy(Policy, context =>
            RateLimitPartition.GetFixedWindowLimiter(context.User.Identity?.Name ?? "anonymous", _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
    }

    public static void MapPlatformDiscovery(this WebApplication app)
    {
        var group = app.MapGroup(PlatformContracts.ApiRoot).RequireAuthorization(Policy).RequireRateLimiting(Policy);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!context.HttpContext.Request.IsHttps) return Results.BadRequest(new { error = "https_required" });
            return await next(context);
        });
        group.MapGet("/discovery", (PlatformDiscovery discovery) => discovery.Read());
        group.MapGet("/contracts", () => PlatformContracts.List());
        group.MapGet("/contracts/{id}", (string id) => PlatformContracts.Schema(id) is { } schema
            ? Results.Json(schema, contentType: "application/schema+json") : Results.NotFound(new { error = "unknown_contract" }));
        group.MapGet("/authoring", () => Results.Text(PlatformContracts.AuthoringGuide, "text/plain"));
    }
}

// A locally provisioned, expiring credential for this read-only policy only. It is not an owner,
// deployment, cloud publishing, or app credential and cannot authenticate other appliance routes.
public sealed class DiscoveryTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration, TimeProvider clock, PiDock.Web.Shared.RegisteredAgentStore agents)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PlatformDiscoveryToken";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization;
        if (!Request.IsHttps || header.Count != 1) return Failed();
        var value = header.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Failed();
        var token = value[7..];
        if (token.Length != 64 || !token.All(char.IsAsciiHexDigit)) return Failed();
        var registration = agents.Authenticate(token);
        if (registration is not null && registration.Scopes.Contains("platform.discovery.read"))
        {
            var registeredIdentity = new ClaimsIdentity([new Claim(ClaimTypes.Name, registration.Id),
                new Claim("scope", "platform.discovery.read")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(registeredIdentity), SchemeName)));
        }
        var hash = configuration["PiDock:Discovery:TokenSha256"];
        if (hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit) ||
            !DateTimeOffset.TryParse(configuration["PiDock:Discovery:ExpiresAt"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var expiry) || expiry <= clock.GetUtcNow() ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), Convert.FromHexString(hash))) return Failed();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "platform-discovery-agent"),
            new Claim("scope", "platform.discovery.read")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
    private static Task<AuthenticateResult> Failed() => Task.FromResult(AuthenticateResult.Fail("Discovery credential is unavailable, invalid or expired."));
}
