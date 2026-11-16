using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Web.Shared;

public static class ApplicationUiEndpoints
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly string[] ContentTypes = ["text/html", "text/css", "text/javascript", "application/javascript", "application/json", "image/png", "image/jpeg", "image/webp", "image/svg+xml", "image/x-icon", "font/woff", "font/woff2"];
    public static void AddApplicationUi(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ApplicationUiRegistry>();
        builder.Services.AddSingleton<ApplicationUiBridgeService>();
        builder.Services.AddHttpClient("PiDockAppUiBridge", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
                MaxConnectionsPerServer = 12, AutomaticDecompression = System.Net.DecompressionMethods.None });
        builder.Services.AddHttpClient("PiDockAppUi", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, AutomaticDecompression = System.Net.DecompressionMethods.None });
        builder.Services.AddRateLimiter(options => options.AddPolicy("AppUiResources", context =>
            RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
        builder.Services.AddAuthorization(options => options.AddPolicy("AppUiOwner", policy =>
            policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser().RequireRole("Owner")));
    }

    public static void MapApplicationUi(this WebApplication app)
    {
        // Public immutable non-secret CSS can be used inside a sandbox without exposing session context.
        app.MapGet(PlatformContracts.ApiRoot + "/ui/styles/1.0/{name}", (string name, HttpContext context) =>
        {
            var css = ApplicationUiAssets.Read(name);
            if (css is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Text(css, "text/css");
        }).AllowAnonymous();
        app.MapGet(PlatformContracts.ApiRoot + "/ui/bridge/1.0/{name}", (string name, HttpContext context) =>
        {
            var script = ApplicationUiBridgeAssets.Read(name);
            if (script is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.AccessControlAllowOrigin = "*";
            return Results.Text(script, "text/javascript");
        }).AllowAnonymous();
        app.MapGet("/api/app-ui/v1/contributions", (HttpContext context, ApplicationUiRegistry registry) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return registry.Read().Select(r => r.Contribution).ToArray();
        }).RequireAuthorization("AppUiOwner");
        app.MapMethods("/app-ui/{appId}/{id}/{**path}", ["GET", "HEAD"], Proxy).AllowAnonymous().RequireRateLimiting("AppUiResources");
    }

    private static IResult Unavailable() => Results.Content("""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body><h1>Application interface unavailable</h1><p>Restore the app service, then use Reload interface in PiDockOS. No application data was changed.</p></body></html>
        """, "text/html", statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> Proxy(string appId, string id, string? path, HttpContext context,
        ApplicationUiRegistry registry, IHttpClientFactory clients, UserStore users)
    {
        context.Response.Headers.CacheControl = "no-store";
        var registered = registry.Find(appId, id);
        if (registered is null) return Results.NotFound();
        if (!ApplicationUiRules.SafePath(path) || context.Request.QueryString.HasValue) return Results.BadRequest();
        // Opaque-origin frames do not receive owner cookies for subresources. Only individually
        // approved, non-secret static assets bypass session authentication; never HTML or runtime data.
        bool publicAsset = (registered.PublicAssets ?? []).Contains(path!, StringComparer.Ordinal);
        if (!publicAsset)
        {
            var authentication = await context.AuthenticateAsync();
            if (context.Request.Headers.ContainsKey("Authorization") || !authentication.Succeeded || authentication.Principal is null || !users.IsCurrent(authentication.Principal)) return Results.Unauthorized();
            if (!authentication.Principal.IsInRole("Owner")) return Results.Forbid();
        }
        if (publicAsset) context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'none'; form-action 'none'; base-uri 'none'; frame-ancestors 'self'; sandbox allow-scripts";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), usb=()";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethods.IsHead(context.Request.Method) ? HttpMethod.Head : HttpMethod.Get,
                new Uri(new Uri(registered.ServiceOrigin), path));
            // No browser Cookie, Authorization, Origin, forwarding headers or upstream response headers are copied.
            using var response = await clients.CreateClient("PiDockAppUi").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return Unavailable();
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is null || !ContentTypes.Contains(type, StringComparer.OrdinalIgnoreCase) || (publicAsset && !ApplicationUiRegistry.PublicAssetType(path!, type)) || response.Content.Headers.ContentEncoding.Count != 0)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            if (response.Content.Headers.ContentLength > MaximumBytes) return Results.StatusCode(StatusCodes.Status502BadGateway);
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[16384];
            while (true)
            {
                int read = await stream.ReadAsync(bytes, timeout.Token);
                if (read == 0) break;
                if (buffer.Length + read > MaximumBytes) return Results.StatusCode(StatusCodes.Status502BadGateway);
                await buffer.WriteAsync(bytes.AsMemory(0, read), timeout.Token);
            }
            return Results.Bytes(buffer.ToArray(), response.Content.Headers.ContentType!.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return Unavailable();
        }
    }
}
