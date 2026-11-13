using Microsoft.AspNetCore.Hosting;
using PiDock.Core;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
namespace PiDock.Web.Shared;

public static class Authentication
{
    public static void AddPiDockAuthentication(this WebApplicationBuilder builder, string product)
    {
        var directory = StateFiles.PrepareDirectory(builder.Configuration["PiDock:StateDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "data"));
        builder.Services.AddSingleton(new StateDirectory(directory));
        builder.Services.AddSingleton<MediaCloudStatus>();
        builder.Services.AddDataProtection().SetApplicationName("PiDock." + product).PersistKeysToFileSystem(new DirectoryInfo(StateFiles.PrepareDirectory(Path.Combine(directory, "keys"))));
        var clockSetting=builder.Configuration["PiDock:RequireTrustedClock"];
        if(clockSetting is not null&&!bool.TryParse(clockSetting,out _))throw new InvalidOperationException("PiDock:RequireTrustedClock must be true or false.");
        bool image=product=="Appliance"&&File.Exists("/etc/pidock/image-profile.json");
        builder.Services.AddSingleton(new ClockTrust(product == "Appliance" && (image || bool.TryParse(clockSetting,out var enforceClock)&&enforceClock)));
        builder.Services.AddSingleton(sp => new UserStore(sp.GetRequiredService<IDataProtectionProvider>(), directory, cloud: product == "Cloud", clockAllowed: () => sp.GetRequiredService<ClockTrust>().Allowed));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = "PiDock." + product; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.LoginPath = "/auth/login"; o.ExpireTimeSpan = TimeSpan.FromDays(7); o.SlidingExpiration = false;
            o.Events.OnValidatePrincipal = context =>
            {
                if (!context.HttpContext.RequestServices.GetRequiredService<UserStore>().IsCurrent(context.Principal!)) context.RejectPrincipal();
                return Task.CompletedTask;
            };
        });
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 65536);
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => { o.ValueLengthLimit = 1024; o.ValueCountLimit = 16; o.KeyLengthLimit = 128; });
        builder.Services.AddAuthorization(); builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("workspace-management", context => RateLimitPartition.GetFixedWindowLimiter(context.User.Identity?.Name ?? "unauthenticated",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
    public static void MapPiDockAuthentication(this WebApplication app, string product)
    {
        var appName = product == "Cloud" ? "PiDockCloud" : "PiDockOS";
        app.MapGet("/auth/login", (HttpContext context, IAntiforgery antiforgery, UserStore users, ClockTrust clock) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var tokens = antiforgery.GetAndStoreTokens(context);
            var token = WebUtility.HtmlEncode(tokens.RequestToken);
            var message = users.HasUsers ? "Use your password and external authenticator." : "No account provisioned. Run this service with --commission from a trusted local console.";
            if (!clock.Allowed) message = "Sign-in is paused until this appliance verifies authenticated network time. Check its internet connection or use the attached keyboard and display to repair its clock. No password or authenticator reset is available over the network.";
            var signup = product == "Cloud" ? users.HasOwner
                ? users.MultiTenantSignupEnabled
                ? $"<p>New to PiDockCloud? <a href='/auth/signup{(context.Request.Query.ContainsKey("invite") ? "?invite=" + Uri.EscapeDataString(context.Request.Query["invite"].ToString()) : "")}'>Create your account and workspace</a>.</p>"
                : users.SignupInvitation(context.Request.Query["invite"].ToString()) is not null
                    ? $"<p><a href='/auth/signup?invite={Uri.EscapeDataString(context.Request.Query["invite"].ToString())}'>Create your invited account</a></p>"
                : "<p>Public workspace signup is disabled. Ask your Administrator for a team invitation to create an account.</p>"
                : "<p role='status'>Workspace signup is locked until the platform Owner completes setup from the server console.</p>" : "";
            var invite = product == "Cloud" ? WebUtility.HtmlEncode(context.Request.Query["invite"].ToString()) : "";
            var error = context.Request.Query.ContainsKey("failed") ? "<p role='alert'>Sign-in failed or temporarily throttled. Check your credentials and device time.</p>" : "";
            return Results.Content($"""
                <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Sign in · {appName}</title><link rel="stylesheet" href="/_content/PiDock.Web.Shared/pidock.css"></head>
                <body><main class="login"><div class="brand">◈ {appName} <span>PiDockPlatform</span></div><h1>Sign in to {appName}</h1><p>{message}</p>{error}<form method="post" action="/auth/login">
                <input type="hidden" name="{tokens.FormFieldName}" value="{token}"><input type="hidden" name="invite" value="{invite}"><label>Username<input name="username" autocomplete="username" required maxlength="64"></label>
                <label>Password<input type="password" name="password" autocomplete="current-password" required maxlength="256"></label><label>Authenticator code<input name="code" inputmode="numeric" autocomplete="one-time-code" pattern="[0-9]+" minlength="6" maxlength="6" required></label>
                <label class="live-switch"><input type="checkbox" name="remember" value="true" checked> Keep me signed in on this browser for 7 days</label><p><small>Uncheck on a shared browser. Protected actions still require your password and a fresh authenticator code.</small></p>
                <button type="submit">Sign in securely →</button></form>{signup}<small>External authenticator required · No network account reset</small></main></body></html>
                """, "text/html");
        }).AllowAnonymous();
        app.MapPost("/auth/login", async (HttpContext context, IAntiforgery antiforgery, UserStore store) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var form = await context.Request.ReadFormAsync();
            if (form["username"].ToString().Length > 64 || form["password"].ToString().Length > 256 || form["code"].ToString().Length != 6) return Results.Redirect("/auth/login?failed=1");
            var user = store.Authenticate(form["username"].ToString(), form["password"].ToString(), form["code"].ToString(), DateTimeOffset.UtcNow);
            if (user is null) return Results.Redirect("/auth/login?failed=1");
            bool remember = form["remember"] == "true";
            var session = store.CreateSession(user, remember, DateTimeOffset.UtcNow);
            await context.SignInAsync(store.Principal(user, session: session), new AuthenticationProperties { IsPersistent = remember, ExpiresUtc = session.ExpiresAt, AllowRefresh = false });
            var invite = form["invite"].ToString();
            return Results.Redirect(product == "Cloud" && invite.Length == 64 && invite.All(char.IsAsciiHexDigit)
                ? "/workspaces?invite=" + Uri.EscapeDataString(invite) : "/");
        }).RequireRateLimiting("login").AllowAnonymous();
        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery, UserStore store) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            store.RevokeSession(context.User);
            await context.SignOutAsync(); return Results.Redirect("/auth/login");
        }).RequireAuthorization();
        if (product == "Cloud") app.MapCloudWorkspaces();
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive", stage = "engineering-preview" })).AllowAnonymous();
    }
    public static bool Commission(this WebApplication app, string[] args, bool singleOwner)
    {
        bool recovery=args.Contains("--recover-owner");
        if (!args.Contains("--commission") && !recovery) return false;
        PhysicalPresence? presence=null;
        if(singleOwner&&File.Exists("/etc/pidock/image-profile.json"))PhysicalConsole.RequireAttached();
        if (recovery)
        {
            if (!singleOwner) throw new InvalidOperationException("Cloud account recovery is a separate operation.");
            presence=PhysicalConsole.RequireRecovery();
            Console.WriteLine("This replaces the appliance owner password and authenticator and revokes all sessions. Application data is preserved.");
            Console.Write("Type RESET OWNER to continue: ");
            if (Console.ReadLine() != "RESET OWNER") { Console.WriteLine("Recovery cancelled."); return true; }
        }
        if (presence is null && !app.Services.GetRequiredService<ClockTrust>().Allowed) throw new InvalidOperationException("Authenticated time is required before commissioning. Repair the clock at the physical console if network TLS cannot start.");
        if (Console.IsInputRedirected || Console.IsOutputRedirected) throw new InvalidOperationException("Commissioning requires an interactive trusted local terminal.");
        if (!recovery && singleOwner && app.Services.GetRequiredService<UserStore>().HasUsers) throw new InvalidOperationException("This appliance already has an owner. Use physical recovery to replace its credentials.");
        if (!singleOwner && app.Services.GetRequiredService<UserStore>().HasOwner)
            throw new InvalidOperationException("The platform Owner is already established. Create tenant accounts through workspace signup and invitations.");
        Console.WriteLine("PiDock local account provisioning. No reset operation is exposed over HTTP.");
        Console.Write("Username: "); var name = Console.ReadLine() ?? "";
        Console.Write("Password (14+ characters): "); var password = ReadSecret();
        Console.Write("Confirm password: "); if (ReadSecret() != password) throw new InvalidOperationException("Passwords do not match.");
        string tenant = "local", role = "Owner";
        var secret = Totp.NewSecret();
        Console.WriteLine("Add this setup secret to a time-based account in your external authenticator. Keep it private:"); Console.WriteLine(secret);
        Console.WriteLine("Enter the six-digit verification code GENERATED BY your authenticator, not the setup secret above.");
        var debugCodes = args.Contains("--debug-commission-codes");
        if (debugCodes) Console.WriteLine("Commissioning diagnostics enabled: clock metadata only; codes remain hidden.");
        var first = ReadCommissioningCode(secret, "Enter its current six-digit code: ", -1, ReadSecret, () => DateTimeOffset.UtcNow, Console.Write, debugCodes);
        var second = first.HasValue
            ? ReadCommissioningCode(secret, "Wait for the NEXT code, then enter it: ", first.Value, ReadSecret, () => DateTimeOffset.UtcNow, Console.Write, debugCodes)
            : null;
        if (!second.HasValue)
        {
            Console.WriteLine("Three unsuccessful attempts. Account setup aborted; no account was created. Run setup again to start with a new secret.");
            Environment.ExitCode = 1;
            return true;
        }
        var users=app.Services.GetRequiredService<UserStore>();
        if (presence is not null) users.RecoverLocalOwner(presence,name,password,secret,second.Value);
        else users.Add(name, password, secret, tenant, role, singleOwner, second.Value);
        Console.WriteLine("Account provisioned. Wait for a new authenticator code before signing in. Restart normally to serve the UI."); return true;
    }
    internal static long? ReadCommissioningCode(string secret, string prompt, long lastAccepted,
        Func<string> readCode, Func<DateTimeOffset> utcNow, Action<string> write, bool debugCodes = false)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            write(prompt);
            var code = readCode();
            var now = utcNow();
            if (debugCodes)
            {
                var counter = now.ToUnixTimeSeconds() / 30;
                write($"DEBUG clock source: Pi system UTC (DateTimeOffset.UtcNow), sampled after input\n");
                write($"DEBUG submission UTC: {now:O}; Unix seconds: {now.ToUnixTimeSeconds()}; 30-second counter: {counter}\n");
                write($"DEBUG input characters: {code.Length}\n");
                if (lastAccepted >= 0)
                    write($"DEBUG second step requires counter {lastAccepted + 1} within the current clock window\n");
            }
            var step = Totp.Match(secret, code, now, lastAccepted);
            if (step.HasValue && (lastAccepted < 0 || step.Value == lastAccepted + 1)) return step;
            if (attempt < 3)
            {
                var reason = lastAccepted < 0 ? "Code invalid or expired." : "Code invalid, already used, or not the next consecutive code.";
                write($"{reason} {3 - attempt} attempt(s) remaining. Try again.\n");
            }
        }
        return null;
    }
    private static string ReadSecret()
    {
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (key.KeyChar == '\u001b' || !char.IsControl(key.KeyChar))
            {
                // Remove complete terminal paste markers immediately so Backspace still edits the user's text.
                value.Append(key.KeyChar);
                var text = value.ToString();
                if (text.EndsWith("\u001b[200~", StringComparison.Ordinal) || text.EndsWith("\u001b[201~", StringComparison.Ordinal)) value.Length -= 6;
            }
        }
        Console.WriteLine();
        return value.ToString().Replace("\u001b", "", StringComparison.Ordinal);
    }
}
public sealed record StateDirectory(string Path);
