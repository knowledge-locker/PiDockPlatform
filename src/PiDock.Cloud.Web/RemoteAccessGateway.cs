using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using PiDock.Core;

namespace PiDock.Cloud.Web;

public static class RemoteAccessGateway
{
    private const string CookieName = "__Host-PiDockRemote";
    private static readonly HashSet<string> Hop = new(StringComparer.OrdinalIgnoreCase) { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Cookie", "Authorization", "Origin" };
    public static void MapRemoteAccess(this WebApplication app)
    {
        app.Map("/api/remote/agent", async (HttpContext context, RemoteAccessService service) => { try { await service.Agent(context); } catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or System.Text.Json.JsonException) { context.Abort(); } }).AllowAnonymous().RequireRateLimiting("remote-agent");
        app.Map("/api/remote/data/{id:guid}", async (HttpContext context, Guid id, RemoteAccessService service) => { try { await service.Data(context, id); } catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException) { context.Abort(); } }).AllowAnonymous();
        app.MapGet("/api/remote/tls-allow", (string domain, RemoteAccessService service) => service.KnownHost(domain.ToLowerInvariant()) ? Results.Ok() : Results.NotFound()).AllowAnonymous();
        app.MapGet("/remote-access/launch/{id:guid}", (HttpContext context, Guid id, RemoteAccessService service) => {
            var grant = service.Launch(id, context.User);
            if (grant is null) return (IResult)Results.NotFound();
            // A no-referrer form navigation sends Origin: null. Retain only the public
            // cloud origin so the bootstrap can validate it without revealing the launch path.
            context.Response.Headers.CacheControl = "no-store"; context.Response.Headers["Referrer-Policy"] = "strict-origin";
            string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; form-action https://{grant.Host}; script-src 'nonce-{nonce}'; base-uri 'none'; frame-ancestors 'none'";
            var action = HtmlEncoder.Default.Encode("https://" + grant.Host + "/_pidock/session");
            return Results.Content($"<!doctype html><html><head><title>Opening PiDockOS</title></head><body><form method=post action=\"{action}\"><input type=hidden name=code value=\"{grant.LaunchCode}\"><button>Open PiDockOS</button></form><script nonce=\"{nonce}\">document.forms[0].submit()</script></body></html>", "text/html");
        }).RequireAuthorization();
    }
    public static async Task Handle(HttpContext context, RequestDelegate next)
    {
        var service = context.RequestServices.GetRequiredService<RemoteAccessService>();
        if (!service.IsRemoteHost(context.Request.Host.Host)) {
            if (service.Configured) {
                var cloudRequestOrigin = context.Request.Headers.Origin.ToString();
                bool agent = context.Request.Path.StartsWithSegments("/api/remote/agent") || context.Request.Path.StartsWithSegments("/api/remote/data");
                // A device's isolated site must never open an authenticated cloud Blazor socket.
                if ((cloudRequestOrigin.Length > 0 && cloudRequestOrigin != service.CloudOrigin!.GetLeftPart(UriPartial.Authority)) || (context.WebSockets.IsWebSocketRequest && !agent && cloudRequestOrigin != service.CloudOrigin!.GetLeftPart(UriPartial.Authority))) { context.Response.StatusCode = 403; return; }
            }
            await next(context); return;
        }
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers["Referrer-Policy"] = "strict-origin";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff"; context.Response.Headers["X-Frame-Options"] = "DENY";
        if (!context.Request.IsHttps || !context.Request.Host.Port.GetValueOrDefault(443).Equals(443) || !service.KnownHost(context.Request.Host.Host)) { context.Response.StatusCode = 404; return; }
        var origin = "https://" + context.Request.Host.Host;
        if (context.Request.Path == "/_pidock/session") {
            if (context.Request.Method != "POST" || context.Request.Headers.Origin != service.CloudOrigin!.GetLeftPart(UriPartial.Authority) || !context.Request.HasFormContentType || context.Request.ContentLength is not (> 0 and <= 4096)) { context.Response.StatusCode = 403; return; }
            var grant = service.Consume(context.Request.Host.Host, (await context.Request.ReadFormAsync(context.RequestAborted))["code"].ToString());
            if (grant is null) { context.Response.StatusCode = 403; return; }
            context.Response.Cookies.Append(CookieName, grant.Cookie, new CookieOptions { HttpOnly = true, Secure = true, SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict, Path = "/", Expires = grant.ExpiresAt, MaxAge = grant.ExpiresAt - DateTimeOffset.UtcNow });
            context.Response.Redirect("/"); return;
        }
        var session = service.Authenticate(context.Request.Host.Host, context.Request.Cookies[CookieName] ?? "");
        if (session is null) { context.Response.StatusCode = 401; context.Response.ContentType = "text/html"; await context.Response.WriteAsync("<!doctype html><title>Remote session ended</title><h1>Remote session ended</h1><p>Return to PiDockCloud and approve a new session with fresh authentication.</p>"); return; }
        if (context.Request.Method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS")) { context.Response.StatusCode = 405; return; }
        var sentOrigin = context.Request.Headers.Origin.ToString();
        if ((sentOrigin.Length > 0 && sentOrigin != origin) || ((context.WebSockets.IsWebSocketRequest || context.Request.Method is not ("GET" or "HEAD")) && sentOrigin != origin)) { context.Response.StatusCode = 403; return; }
        // This permission can only be changed from the local interface, including in an existing Blazor circuit.
        if (context.Request.Path.StartsWithSegments("/remote-access")) { context.Response.StatusCode = 403; return; }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Stop.Token);
        var handler = new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15), ConnectCallback = (_, ct) => service.Open(session, ct) };
        using var invoker = new HttpMessageInvoker(handler);
        try {
            if (context.WebSockets.IsWebSocketRequest) {
                using var upstream = new ClientWebSocket();
                string cookie = ApplianceCookies(context);
                if (cookie.Length > 0) upstream.Options.SetRequestHeader("Cookie", cookie);
                upstream.Options.SetRequestHeader("Host", "localhost:7241"); upstream.Options.SetRequestHeader("Origin", "https://localhost:7241");
                upstream.Options.SetRequestHeader("X-PiDock-Remote-Proof", service.Proof(session)); upstream.Options.SetRequestHeader("X-PiDock-Remote-Id", session.Id.ToString());
                var protocols = context.WebSockets.WebSocketRequestedProtocols;
                if (protocols.Count > 8 || protocols.Any(p => p.Length > 128)) { context.Response.StatusCode = 400; return; }
                foreach (var protocol in protocols) upstream.Options.AddSubProtocol(protocol);
                await upstream.ConnectAsync(new Uri("ws://pidock.internal" + Target(context)), invoker, stop.Token);
                using var browser = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
                await RemoteProtocol.Copy(browser, upstream, stop.Token); return;
            }
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), "http://pidock.internal" + Target(context));
            if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0) request.Content = new StreamContent(context.Request.Body, 16384);
            var extraHop = context.Request.Headers.Connection.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var header in context.Request.Headers) {
                if (Hop.Contains(header.Key) || extraHop.Contains(header.Key, StringComparer.OrdinalIgnoreCase) || header.Key.StartsWith("X-PiDock-", StringComparison.OrdinalIgnoreCase) || header.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) || header.Key.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            request.Headers.Host = "localhost:7241"; request.Headers.ConnectionClose = true;
            request.Headers.TryAddWithoutValidation("Cookie", ApplianceCookies(context)); request.Headers.TryAddWithoutValidation("Origin", "https://localhost:7241");
            request.Headers.Add("X-PiDock-Remote-Proof", service.Proof(session)); request.Headers.Add("X-PiDock-Remote-Id", session.Id.ToString());
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers)) {
                if (Hop.Contains(header.Key) || header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)) continue;
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            context.Response.Headers.CacheControl = "no-store"; context.Response.Headers["Referrer-Policy"] = "strict-origin"; context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers.Append("Content-Security-Policy", "frame-ancestors 'none'");
            if (response.Headers.Location is { } location) {
                if (location.IsAbsoluteUri && (location.Host != "localhost" || location.Port != 7241 || location.Scheme != "https")) throw new IOException("Invalid appliance redirect.");
                var path = location.IsAbsoluteUri ? location.PathAndQuery + location.Fragment : location.OriginalString;
                if (!path.StartsWith('/') || path.StartsWith("//") || path.Contains('\\')) throw new IOException("Invalid appliance redirect.");
                context.Response.Headers.Location = path;
            }
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies)) foreach (var raw in cookies) {
                var value = SetCookieHeaderValue.Parse(raw);
                if (!IsApplianceCookie(value.Name.ToString())) continue;
                value.Domain = default; value.Path = "/"; value.Secure = true; value.HttpOnly = true; value.SameSite = Microsoft.Net.Http.Headers.SameSiteMode.Strict;
                context.Response.Headers.Append("Set-Cookie", value.ToString());
            }
            await response.Content.CopyToAsync(context.Response.Body, stop.Token);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or WebSocketException or OperationCanceledException) {
            if (context.Response.HasStarted) context.Abort(); else context.Response.StatusCode = stop.IsCancellationRequested ? 401 : 502;
        }
    }
    private static string Target(HttpContext c) {
        var target = c.Request.Path.ToUriComponent() + c.Request.QueryString.ToUriComponent();
        if (!target.StartsWith('/') || target.StartsWith("//") || target.Contains('\\') || target.Length > 8192) throw new IOException("Invalid remote target.");
        return target;
    }
    internal static bool IsApplianceCookie(string name) => name == "PiDock.Appliance" || name.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal);
    private static string ApplianceCookies(HttpContext context) => string.Join("; ", context.Request.Cookies.Where(c => IsApplianceCookie(c.Key)).Select(c => c.Key + "=" + c.Value));
}
