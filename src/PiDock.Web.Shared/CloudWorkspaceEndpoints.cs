using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace PiDock.Web.Shared;

public static class CloudWorkspaceEndpoints
{
    private sealed record SignupSetup(string Secret, DateTimeOffset ExpiresAt, string Invite);
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Page(string title, string body) => $"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{E(title)} · PiDockCloud</title><link rel="stylesheet" href="/_content/PiDock.Web.Shared/pidock.css"></head>
        <body><main class="account-page"><a class="brand" href="/">◈ PiDockCloud <span>PiDockPlatform</span></a>{body}</main></body></html>
        """;
    private static string Csrf(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return $"<input type=\"hidden\" name=\"{E(tokens.FormFieldName)}\" value=\"{E(tokens.RequestToken)}\">";
    }
    private static IResult Html(string title, string body) => Results.Content(Page(title, body), "text/html");
    private static bool Token(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    public static void MapCloudWorkspaces(this WebApplication app)
    {
        // Setup secrets and invitation responses must never enter browser/shared caches or referrer headers.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/auth") || context.Request.Path.StartsWithSegments("/workspaces"))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            }
            await next(context);
        });
        app.MapGet("/auth/join", (HttpContext context, UserStore users) =>
        {
            var invite = context.Request.Query["token"].ToString();
            if (!Token(invite) || users.SignupInvitation(invite) is null) return Results.StatusCode(403);
            var query = "?invite=" + Uri.EscapeDataString(invite);
            return Html("Join a workspace", $"<h1>Join your team</h1><p>Use the username your workspace Administrator invited.</p><p><a href='/auth/login{query}'>Sign in to accept the invitation</a></p><p><a href='/auth/signup{query}'>Create your invited account</a></p>");
        }).AllowAnonymous();
        app.MapGet("/auth/signup", (HttpContext context, UserStore users, IAntiforgery antiforgery, IDataProtectionProvider protection) =>
        {
            if (!users.HasOwner) return Results.Problem("Workspace signup is locked until the platform Owner is established from the server console.", statusCode: 403);
            var secret = Totp.NewSecret(); var invite = context.Request.Query["invite"].ToString();
            var invitation = users.SignupInvitation(invite);
            if ((invite.Length > 0 && invitation is null) || (!users.MultiTenantSignupEnabled && invitation is null)) return Results.StatusCode(403);
            var heading = invitation is null ? "Create your workspace" : "Join your team";
            var description = invitation is null ? "Your account can join multiple workspaces. You will be Administrator of your own workspace." : "Create your account to join the invited workspace. Your Administrator has assigned your role.";
            var workspaceField = invitation is null ? "<label>Workspace name<input name='workspace' required maxlength='80'></label>" : "";
            var button = invitation is null ? "Create account and workspace" : "Create account and join team";
            var setup = protection.CreateProtector("PiDock.Cloud.Signup.v1").Protect(JsonSerializer.Serialize(new SignupSetup(secret, DateTimeOffset.UtcNow.AddMinutes(15), Token(invite) ? invite : "")));
            return Html("Create account", $"""
                <h1>{heading}</h1><p>{description}</p>
                <form method="post" action="/auth/signup">{Csrf(context, antiforgery)}<input type="hidden" name="setup" value="{E(setup)}">
                <label>Username<input name="username" value="{E(invitation?.Username)}" required maxlength="64" pattern="[A-Za-z0-9-]+" autocomplete="username"></label><small>Letters, numbers and hyphens. If invited, use the invited username.</small>
                <label>Password<input type="password" name="password" required minlength="14" maxlength="256" autocomplete="new-password"></label>
                <label>Confirm password<input type="password" name="confirmPassword" required minlength="14" maxlength="256" autocomplete="new-password"></label>
                {workspaceField}
                <h2>Set up your authenticator</h2><p>Add a time-based account named PiDockCloud to your authenticator using this private setup key:</p><p><code class="setup-key">{E(secret)}</code></p><p>Six digits, every 30 seconds. Keep this key private. Setup expires in 15 minutes.</p>
                <label>Authenticator code<input name="code" required pattern="[0-9][0-9][0-9][0-9][0-9][0-9]" maxlength="6" inputmode="numeric" autocomplete="one-time-code"></label>
                <button>{button}</button></form><p><a href="/auth/login">Already have an account? Sign in</a></p>
                """);
        }).RequireRateLimiting("login").AllowAnonymous();
        app.MapPost("/auth/signup", async (HttpContext context, UserStore users, IAntiforgery antiforgery, IDataProtectionProvider protection) =>
        {
            // Check the bootstrap gate on POST as well as on the page that displays the signup form.
            if (!users.HasOwner) return Results.StatusCode(403);
            SignupSetup? setup = null;
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync();
                setup = JsonSerializer.Deserialize<SignupSetup>(protection.CreateProtector("PiDock.Cloud.Signup.v1").Unprotect(form["setup"].ToString()));
                if (setup is null || setup.ExpiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("Signup setup expired. Start again.");
                if (form["password"].ToString() != form["confirmPassword"].ToString()) throw new ArgumentException("Passwords do not match.");
                var user = setup.Invite.Length > 0
                    ? users.RegisterInvitedAccount(form["username"].ToString(), form["password"].ToString(), setup.Secret, form["code"].ToString(), setup.Invite)
                    : users.RegisterWorkspace(form["username"].ToString(), form["password"].ToString(), setup.Secret, form["code"].ToString(), form["workspace"].ToString());
                var session = users.CreateSession(user, false, DateTimeOffset.UtcNow);
                await context.SignInAsync(users.Principal(user, session: session), new AuthenticationProperties { IsPersistent = false, ExpiresUtc = session.ExpiresAt, AllowRefresh = false });
                return Results.Redirect("/");
            }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or CryptographicException or JsonException)
            {
                context.Response.StatusCode = 400;
                var retry = "/auth/signup" + (setup is { Invite.Length: > 0 } ? "?invite=" + Uri.EscapeDataString(setup.Invite) : "");
                return Html("Account setup", $"<h1>Account setup incomplete</h1><p role='alert'>{E(ex is ArgumentException or InvalidOperationException ? ex.Message : "Setup is invalid. Please start again.")}</p><a href='{E(retry)}'>Start account setup again</a>");
            }
        }).RequireRateLimiting("login").AllowAnonymous();
        app.MapGet("/workspaces", (HttpContext context, UserStore users, IAntiforgery antiforgery) =>
        {
            var principal = context.User;
            if (!users.IsCurrent(principal)) return Results.Unauthorized();
            var csrf = Csrf(context, antiforgery); var tenant = principal.FindFirstValue("tenant") ?? "";
            var owner = principal.IsInRole("Owner"); var admin = tenant.Length > 0 && (owner || principal.IsInRole("Administrator"));
            string Form(string action, string body) => $"<form method='post' action='/workspaces/{action}'>{csrf}{body}</form>";
            var body = new StringBuilder("<p><a href='/'>← Fleet overview</a></p><h1>Workspaces &amp; teams</h1>");
            body.Append($"<p>Signed in as <strong>{E(principal.Identity?.Name)}</strong>. Permissions apply to your selected workspace.</p>");
            if (owner) body.Append("<section class='card'><h2>Workspace signup settings</h2><p>Allow multi-tenant signup is enabled by default. Turn it off for an invitation-only installation. Existing workspaces and team invitations remain available. The Owner can still create the initial workspace.</p>" + Form("signup-settings", $"<label>Allow multi-tenant signup<select name='enabled'><option value='true' {(users.MultiTenantSignupEnabled ? "selected" : "")}>Enabled</option><option value='false' {(!users.MultiTenantSignupEnabled ? "selected" : "")}>Disabled — invitation only</option></select></label><button>Save signup settings</button>") + "</section>");
            if (owner) body.Append("<p class='notice'>Platform Owner · Entering a workspace gives you support access. Workspace selections and team changes are audited.</p>" + Form("switch", "<input type='hidden' name='workspace' value=''><button>Return to platform administration</button>"));
            body.Append("<section class='card'><h2>Your workspaces</h2>");
            foreach (var w in users.WorkspacesFor(principal)) body.Append(Form("switch", $"<input type='hidden' name='workspace' value='{E(w.Id)}'><p><strong>{E(w.Name)}</strong> · {E(w.Role)} {(w.Id == tenant ? "· Current workspace" : "")} <button class='secondary'>Switch to workspace</button></p>"));
            body.Append("</section>");
            var invite = context.Request.Query["invite"].ToString();
            body.Append("<section class='card'><h2>Join a workspace</h2><p>Accept a single-use invitation issued to your username.</p>" + Form("accept", $"<label>Invitation token<input name='token' value='{E(Token(invite) ? invite : "")}' required maxlength='64'></label><button>Accept invitation</button>") + "</section>");
            if (users.MultiTenantSignupEnabled || owner) body.Append("<section class='card'><h2>Create another workspace</h2>" + Form("create", "<label>Workspace name<input name='name' required maxlength='80'></label><button>Create workspace</button>") + "</section>");
            if (admin)
            {
                string Roles(string? selected = null) => string.Join("", UserStore.WorkspaceRoles.Select(r => $"<option value='{r}' {(r == selected ? "selected" : "")}>{r}</option>"));
                body.Append("<section class='card'><h2>Team members</h2><p>Administrator: team, device enrollment, releases and operations. Operator and Viewer: fleet and history reading. Auditor: fleet, history and workspace audit reading; device and team changes require Administrator. Removing a member preserves workspace devices and data.</p>");
                foreach (var m in users.Members(principal))
                {
                    var fields = $"<input type='hidden' name='username' value='{E(m.Username)}'>";
                    body.Append(Form("role", fields + $"<p><strong>{E(m.Username)}</strong></p><label>Role for {E(m.Username)}<select name='role'>{Roles(m.Role)}</select></label><button class='secondary'>Save role</button>"));
                    body.Append(Form("remove", fields + $"<button class='text-button'>Remove {E(m.Username)}</button>"));
                }
                body.Append("<h3>Invite a teammate</h3><p>Invitations expire after seven days and work only for the specified username. Share the generated link privately.</p>" + Form("invite", $"<label>Teammate username<input name='username' required maxlength='64' pattern='[A-Za-z0-9-]+'></label><label>Role<select name='role'>{Roles("Viewer")}</select></label><button>Create invitation</button>"));
                foreach (var i in users.Invitations(principal)) body.Append(Form("cancel", $"<input type='hidden' name='id' value='{E(i.Id)}'><p>{E(i.Username)} · {E(i.Role)} · expires {E(i.ExpiresAt.ToString("u"))} <button class='secondary'>Cancel invitation</button></p>"));
                body.Append("</section>");
            }
            if (admin || owner || principal.IsInRole("Auditor"))
            {
                body.Append("<section class='card'><h2>Recent access and team activity</h2><ul>");
                foreach (var a in users.AuditFor(principal)) body.Append($"<li>{E(a.At.ToString("u"))} · {E(a.Actor)} · {E(a.Action)} · {E(a.Target)}</li>");
                body.Append("</ul></section>");
            }
            return Html("Workspaces & teams", body.ToString());
        }).RequireAuthorization();
        app.MapPost("/workspaces/{action}", async (string action, HttpContext context, UserStore users, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync();
                ClaimsPrincipal? next = null;
                switch (action)
                {
                    case "signup-settings":
                        if (!bool.TryParse(form["enabled"].ToString(), out var enabled)) throw new ArgumentException("Select a valid signup setting.");
                        users.SetMultiTenantSignup(context.User, enabled); break;
                    case "switch": next = users.Switch(context.User, form["workspace"].ToString()); break;
                    case "create": next = users.CreateWorkspace(context.User, form["name"].ToString()); break;
                    case "accept":
                        if (!Token(form["token"].ToString())) throw new ArgumentException("Invalid invitation token.");
                        next = users.Accept(context.User, form["token"].ToString()); break;
                    case "invite":
                        var invitation = users.Invite(context.User, form["username"].ToString(), form["role"].ToString());
                        // Relative URL avoids trusting an inbound Host header when producing invitation links.
                        var link = "/auth/join?token=" + invitation.Token;
                        return Html("Invitation created", $"<h1>Invitation created</h1><p>Share this link privately with {E(invitation.Invitation.Username)}. It expires in seven days.</p><p><a href='{E(link)}'>{E(link)}</a></p><label>Invitation token<input readonly value='{invitation.Token}'></label><p><a href='/workspaces'>Back to team management</a></p>");
                    case "role": users.SetMemberRole(context.User, form["username"].ToString(), form["role"].ToString()); break;
                    case "remove": users.SetMemberRole(context.User, form["username"].ToString(), null); break;
                    case "cancel": users.CancelInvitation(context.User, form["id"].ToString()); break;
                    default: return Results.NotFound();
                }
                if (next is not null) await context.SignInAsync(next, (await context.AuthenticateAsync()).Properties ?? new AuthenticationProperties { IsPersistent = false });
                return Results.Redirect(next is not null ? "/" : "/workspaces");
            }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                context.Response.StatusCode = 400;
                return Html("Workspace action", $"<h1>Workspace action incomplete</h1><p role='alert'>{E(ex.Message)}</p><a href='/workspaces'>Return to workspaces</a>");
            }
        }).RequireRateLimiting("workspace-management").RequireAuthorization();
    }
}
