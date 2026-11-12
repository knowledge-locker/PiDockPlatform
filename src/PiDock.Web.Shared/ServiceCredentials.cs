using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiDock.Core;

namespace PiDock.Web.Shared;

public sealed record ServiceCredential(string Id, string TokenSha256, DateTimeOffset ExpiresAt,
    string Subject, string[] Scopes, string? TenantId = null, string[]? ReadSources = null, int[]? AlarmPriorities = null);

public static class ServiceCredentials
{
    public const string AppScheme = "PiDockApp";
    public const string PublisherScheme = "PiDockPublisher";
    public static void AddServiceCredentials(this WebApplicationBuilder builder, bool cloud)
    {
        var scheme = cloud ? PublisherScheme : AppScheme;
        builder.Services.AddAuthentication().AddScheme<ServiceCredentialOptions, ServiceCredentialHandler>(scheme, options =>
        {
            options.Section = cloud ? "PiDock:Publishing:Credentials" : "PiDock:Apps:Credentials";
            options.Cloud = cloud;
        });
        builder.Services.AddAuthorization(options => options.AddPolicy(scheme, policy => policy.AddAuthenticationSchemes(scheme).RequireAuthenticatedUser()));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy(scheme, context => RateLimitPartition.GetFixedWindowLimiter(context.User.Identity?.Name ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
    public static RouteGroupBuilder ServiceGroup(this WebApplication app, string root, bool cloud)
    {
        var scheme = cloud ? PublisherScheme : AppScheme;
        var group = app.MapGroup(root).RequireAuthorization(scheme).RequireRateLimiting(scheme);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!context.HttpContext.Request.IsHttps) return Results.BadRequest(new { error = "https_required" });
            try { return await next(context); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_contract" }); }
            catch (PlatformConflictException) { return Results.Conflict(new { error = "immutable_version_or_event_conflict" }); }
            catch (PlatformCapacityException) { return Results.Problem("Platform capacity reached.", statusCode: 507); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Problem("Platform persistence unavailable.", statusCode: 503); }
        });
        return group;
    }
    public static RouteHandlerBuilder Scope(this RouteHandlerBuilder route, string scope) => route.RequireAuthorization(policy => policy.RequireClaim("scope", scope));
    public static bool CanRead(ClaimsPrincipal user, string app) => user.FindFirstValue("app") == app || user.HasClaim("read-source", app);
}
public sealed class ServiceCredentialOptions : AuthenticationSchemeOptions
{
    public string Section { get; set; } = "";
    public bool Cloud { get; set; }
}
public sealed class ServiceCredentialHandler(IOptionsMonitor<ServiceCredentialOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IConfiguration configuration, TimeProvider clock, IEnumerable<RegisteredAgentStore> registeredAgents)
    : AuthenticationHandler<ServiceCredentialOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.IsHttps || Request.Headers.Authorization.Count != 1) return Fail();
        var value = Request.Headers.Authorization.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || value.Length != 71 || !value[7..].All(char.IsAsciiHexDigit)) return Fail();
        if (!Options.Cloud)
        {
            var registration = registeredAgents.Select(store => store.Authenticate(value[7..])).FirstOrDefault(entry => entry is not null);
            if (registration is not null)
            {
                var registeredClaims = new List<Claim> { new(ClaimTypes.Name, registration.Id), new("app", registration.Subject) };
                registeredClaims.AddRange(registration.Scopes.Select(scope => new Claim("scope", scope)));
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(registeredClaims, Scheme.Name)), Scheme.Name)));
            }
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value[7..]));
        ServiceCredential[] entries;
        try { entries = configuration.GetSection(Options.Section).Get<ServiceCredential[]>() ?? []; }
        catch (InvalidOperationException) { return Fail(); }
        // Ambiguous token assignments fail closed instead of choosing a tenant or app by order.
        var matches = entries.Take(257).Where(c => c.TokenSha256 is { Length: 64 } && c.TokenSha256.All(char.IsAsciiHexDigit) &&
            CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(c.TokenSha256))).ToArray();
        if (entries.Length > 256 || matches.Length != 1) return Fail();
        var entry = matches[0];
        if (!IdentityRules.IsId(entry.Id) || !IdentityRules.IsId(entry.Subject) || entry.Subject == "pidockos" || entry.ExpiresAt <= clock.GetUtcNow() ||
            entry.Scopes is null || entry.Scopes.Length > 32 || entry.Scopes.Any(s => !ApplicationContractRules.Text(s, 80)) ||
            (Options.Cloud && !IdentityRules.IsId(entry.TenantId)) ||
            entry.ReadSources is { Length: > 32 } || entry.ReadSources?.Any(s => !IdentityRules.IsId(s)) == true) return Fail();
        var claims = new List<Claim> { new(ClaimTypes.Name, entry.Id), new("app", entry.Subject) };
        if (Options.Cloud) claims.Add(new("tenant", entry.TenantId!));
        claims.AddRange(entry.Scopes.Select(s => new Claim("scope", s)));
        claims.AddRange((entry.ReadSources ?? []).Select(s => new Claim("read-source", s)));
        claims.AddRange((entry.AlarmPriorities ?? []).Where(p => p is >= 1 and <= 4).Select(p => new Claim("alarm-priority", p.ToString(CultureInfo.InvariantCulture))));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
    private static Task<AuthenticateResult> Fail() => Task.FromResult(AuthenticateResult.Fail("Service credential invalid, expired or unavailable."));
}
