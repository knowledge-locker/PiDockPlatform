using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using PiDock.Agent;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Appliance.Web;

public static class ApplicationIoEndpoints
{
    public static void AddApplicationIo(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IApplicationIoAuthorityValidator, ApplicationIoCredentialValidator>();
        builder.Services.AddSingleton(sp => new ApplicationIoService(
            builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("PiDock:Apps:Io:Enabled"),
            () => builder.Configuration.GetSection("PiDock:Apps:Io:Grants").Get<ApplicationIoGrant[]>() ?? [],
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IApplicationIoAuthorityValidator>(), sp.GetRequiredService<ApplicationRuntimeAccessGate>()));
        builder.Services.AddHostedService<ApplicationIoSweepService>();
    }
    public static void MapApplicationIo(this WebApplication app)
    {
        var group = app.ServiceGroup("/api/apps/v1/io", cloud: false);
        group.AddEndpointFilter(async (context, next) =>
        {
            try { await context.HttpContext.RequestServices.GetRequiredService<ApplicationRuntimeAccessMonitor>().Refresh(context.HttpContext.RequestAborted, force: context.HttpContext.Request.Method != "GET"); return await next(context); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            catch (ApplicationIoDeniedException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            catch (ApplicationIoUnavailableException) { return Results.Problem("App I/O simulation is not enabled on this target.", statusCode: 503); }
        });
        group.MapGet("/support", (ApplicationIoService io) => io.Support()).Scope("io.read");
        group.MapGet("/signals", (HttpContext context, ApplicationIoService io) => io.Signals(Authority(context, "io.read"))).Scope("io.read");
        group.MapPost("/leases", (ApplicationIoLeaseRequest request, HttpContext context, ApplicationIoService io) => io.Acquire(Authority(context), request)).Scope("io.write");
        group.MapGet("/leases/{id:guid}", (Guid id, HttpContext context, ApplicationIoService io) => io.Read(Authority(context), id)).Scope("io.read");
        group.MapPost("/leases/{id:guid}/renew", (Guid id, ApplicationIoRenewRequest request, HttpContext context, ApplicationIoService io) => io.Renew(Authority(context), id, request)).Scope("io.write");
        group.MapPost("/leases/{id:guid}/outputs", (Guid id, ApplicationIoWriteRequest request, HttpContext context, ApplicationIoService io) => io.Write(Authority(context), id, request)).Scope("io.write");
        group.MapPost("/leases/{id:guid}/simulated-inputs", (Guid id, ApplicationIoWriteRequest request, HttpContext context, ApplicationIoService io) => io.Write(Authority(context), id, request, simulatedInputs: true)).Scope("io.simulate");
        group.MapDelete("/leases/{id:guid}", (Guid id, HttpContext context, ApplicationIoService io) =>
        { io.Release(Authority(context), id); return Results.NoContent(); }).Scope("io.write");
    }
    private static ApplicationIoAuthority Authority(HttpContext context, string requiredScope = "io.write")
    {
        var bearer = context.Request.Headers.Authorization.ToString();
        if (bearer.Length != 71 || !bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new ApplicationIoDeniedException();
        return new(context.User.FindFirstValue("app")!, context.User.Identity!.Name!,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer[7..]))), requiredScope, context.RuntimeBinding());
    }
}

// Rechecks the current server-side credential record without retaining a raw bearer in a lease.
// Discovery-only registered agents cannot receive these scopes and are deliberately excluded.
public sealed class ApplicationIoCredentialValidator(IConfiguration configuration, ApplicationCredentialVault vault,
    TimeProvider clock, ApplicationRuntimeAccessGate? runtimeAccess = null) : IApplicationIoAuthorityValidator
{
    public bool IsCurrent(ApplicationIoAuthority authority)
    {
        try { return IsCurrentCore(authority); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        { return false; }
    }
    private bool IsCurrentCore(ApplicationIoAuthority authority)
    {
        if (runtimeAccess is not null && !runtimeAccess.IsCurrent(authority.AppId, authority.Runtime)) return false;
        if (!IdentityRules.IsId(authority.AppId) || (!IdentityRules.IsId(authority.CredentialId) && !ApplicationResourceRules.CredentialId(authority.CredentialId)) || authority.Fingerprint is not { Length: 64 } ||
            authority.RequiredScope is not ("io.read" or "io.write")) return false;
        var managed = vault.Resolve(authority.AppId, authority.CredentialId);
        if (managed.Managed)
        {
            var credential = managed.Credential;
            return credential is not null && credential.Purpose == "platform" && credential.Scopes.Contains(authority.RequiredScope, StringComparer.Ordinal) &&
                Fingerprint(credential.Token) == authority.Fingerprint;
        }
        var configured = configuration.GetSection("PiDock:Apps:Credentials").Get<ServiceCredential[]>() ?? [];
        if (configured.Length > 256 || configured.Any(c => c is null)) return false;
        var matches = configured.Where(c => c.TokenSha256?.Equals(authority.Fingerprint, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        return matches is [var entry] && entry.Id == authority.CredentialId && entry.Subject == authority.AppId &&
            entry.ExpiresAt > clock.GetUtcNow() && entry.Scopes is not null && entry.Scopes.Contains(authority.RequiredScope, StringComparer.Ordinal);
    }
    private static string Fingerprint(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
