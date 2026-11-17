#pragma warning disable BL0006 // Exercise real Razor events and the shared confirmation component.
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using PiDock.Appliance.Web.Components.Pages;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class AppCredentialPageTests
{
    [Fact]
    public async Task InvalidSettingsStayOnPageAndValidSettingsOpenCancelableDialog()
    {
        await using var f = new Fixture(); await f.Mount();
        await (await f.StartIssue()).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(f.Vault.Read()); Assert.DoesNotContain(f.DialogFrames(), x => x.AttributeName == "onsubmit");
        Assert.Contains("Use a named app credential", f.PageText());
        await f.Settings(); var action = await f.StartIssue();
        Assert.Contains("logic-engine / bridge", f.DialogText());
        Assert.DoesNotContain(f.PageFrames(), x => x.AttributeName == "type" && x.AttributeValue as string == "password");
        Assert.Empty(f.Vault.Read());
        await f.DialogEvent("oncancel"); await action.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(f.Vault.Read());
        Assert.Equal("logic-engine", f.FieldValue("credential-app-id"));
        Assert.Equal("bridge", f.FieldValue("credential-name"));
    }

    [Fact]
    public async Task ConfirmationCreatesOnceAndUsesTheReviewedIdentityDespiteLaterInput()
    {
        await using var f = new Fixture(); await f.Mount(); await f.Settings();
        var action = await f.StartIssue(); Assert.Empty(f.Vault.Read());
        // Even a forged change event while the background controls are disabled cannot retarget confirmation.
        await f.Input("credential-app-id", "other-app");
        await f.Credentials(f.Code()); await f.DialogEvent("onsubmit"); await action.WaitAsync(TimeSpan.FromSeconds(2));
        var entry = Assert.Single(f.Vault.Read());
        Assert.Equal("logic-engine", entry.AppId); Assert.Equal("bridge", entry.Id); Assert.Equal(1, entry.Version);
        Assert.Equal("ui-bridge", entry.Purpose); Assert.Empty(entry.Scopes);
        Assert.Contains("Credential updated", f.PageText());
        Assert.Equal("", f.FieldValue("credential-app-id"));
        Assert.Equal("", f.FieldValue("credential-name"));
        Assert.Equal("30", f.FieldValue("credential-days"));
        // The callback authenticated exactly once; replay cannot revoke the credential.
        Assert.Throws<UnauthorizedAccessException>(() => f.Vault.Revoke(f.Identity.User, Fixture.Password, f.Code(), entry.AppId, entry.Id));
        Assert.False(Assert.Single(f.Vault.Read()).Revoked);
    }

    [Fact]
    public async Task RevokeCancellationAndMfaReplayLeaveAuthorityUntilFreshConfirmation()
    {
        await using var f = new Fixture(); await f.Mount(); await f.Settings();
        var create = await f.StartIssue(); await f.Credentials(f.Code()); await f.DialogEvent("onsubmit"); await create.WaitAsync(TimeSpan.FromSeconds(2));
        var cancel = await f.StartRevoke(); await f.DialogEvent("oncancel"); await cancel.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(Assert.Single(f.Vault.Read()).Revoked);
        var replay = await f.StartRevoke(); await f.Credentials(f.Code()); await f.DialogEvent("onsubmit");
        Assert.False(replay.IsCompleted); Assert.False(Assert.Single(f.Vault.Read()).Revoked);
        await f.DialogEvent("oncancel"); await replay.WaitAsync(TimeSpan.FromSeconds(2));
        await f.Input("credential-app-id", "another-app"); await f.Input("credential-name", "draft-name");
        var revoke = await f.StartRevoke(); await f.Credentials(f.Code(1)); await f.DialogEvent("onsubmit"); await revoke.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Assert.Single(f.Vault.Read()).Revoked);
        Assert.Equal("another-app", f.FieldValue("credential-app-id")); Assert.Equal("draft-name", f.FieldValue("credential-name"));
    }

    [Fact]
    public async Task CompletedMutationIsReportedEvenIfBrowserDialogCloseFails()
    {
        await using var f = new Fixture(); f.Script.FailClose = true;
        await f.Mount(); await f.Settings(); var action = await f.StartIssue();
        await f.Credentials(f.Code()); await f.DialogEvent("onsubmit"); await action.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Assert.Single(f.Vault.Read()).Version);
        Assert.Contains("Credential updated", f.PageText());
    }

    private sealed class Identity : AuthenticationStateProvider
    {
        public ClaimsPrincipal User { get; set; } = new();
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(User));
    }
    private sealed class Script : IJSRuntime, IJSObjectReference
    {
        public bool FailClose { get; set; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            if (identifier == "close" && FailClose) throw new JSException("Disposable close failure.");
            return ValueTask.FromResult(identifier == "import" ? (T)(object)this : default!);
        }
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class PageRenderer(IServiceProvider services, ILoggerFactory logging) : Renderer(services, logging)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private int pageId;
        public Task Mount() => Dispatcher.InvokeAsync(async () => {
            var component = InstantiateComponent(typeof(AppResources)); pageId = AssignRootComponentId(component);
            await RenderRootComponentAsync(pageId, ParameterView.Empty);
        });
        private RenderTreeFrame[] Frames(int id) { var frames = GetCurrentRenderTreeFrames(id); return frames.Array.Take(frames.Count).ToArray(); }
        public RenderTreeFrame[] PageFrames() => Frames(pageId);
        public RenderTreeFrame[] DialogFrames() => Frames(PageFrames().Single(f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == typeof(ReauthenticationDialog)).ComponentId);
        public string FieldValue(string id)
        {
            var frames = PageFrames(); var index = Array.FindIndex(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "id" && f.AttributeValue as string == id);
            return frames.Skip(index + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).FirstOrDefault(f => f.AttributeName == "value").AttributeValue?.ToString() ?? "";
        }
        public Task Input(string id, string value) => Dispatcher.InvokeAsync(() => {
            var frames = PageFrames(); var attribute = Array.FindIndex(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "id" && f.AttributeValue as string == id);
            var handler = frames.Skip(attribute + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).Single(f => f.AttributeName == "oninput");
            return DispatchEventAsync(handler.AttributeEventHandlerId, null, new ChangeEventArgs { Value = value });
        });
        public async Task<Task> Start(bool revoke)
        {
            Task? action = null;
            await Dispatcher.InvokeAsync((Action)(() => {
                var handlers = PageFrames().Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onclick").ToArray();
                action = DispatchEventAsync((revoke ? handlers[0] : handlers[^1]).AttributeEventHandlerId, null, EventArgs.Empty);
            }));
            return action!;
        }
        public Task DialogEvent(string name, EventArgs? args = null, int index = 0) => Dispatcher.InvokeAsync(() => DispatchEventAsync(DialogFrames().Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == name).ElementAt(index).AttributeEventHandlerId, null, args ?? EventArgs.Empty));
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Page render failed", exception);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public const string Password = "disposable-page-test-password";
        private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-credential-page-" + Guid.NewGuid());
        private readonly ServiceProvider services;
        private readonly PageRenderer renderer;
        private readonly string secret = Totp.NewSecret();
        private readonly long counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        public Identity Identity { get; } = new();
        public Script Script { get; } = new();
        public ApplicationCredentialVault Vault { get; }
        public Fixture()
        {
            var protection = new EphemeralDataProtectionProvider(); var users = new UserStore(protection, directory);
            users.Add("test-owner", Password, secret, "local", "Owner", false, -1);
            var user = users.Authenticate("test-owner", Password, Code(-1), DateTimeOffset.UtcNow)!;
            Identity.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role), new Claim("tenant", user.TenantId), new Claim("stamp", user.Stamp), new Claim("expires", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString())], "test"));
            services = new ServiceCollection().AddLogging().AddSingleton<IDataProtectionProvider>(protection).AddSingleton(new StateDirectory(directory)).AddSingleton(users).AddSingleton<TimeProvider>(TimeProvider.System).AddSingleton<AuthenticationStateProvider>(Identity).AddSingleton<IJSRuntime>(Script).AddSingleton<ApplicationCredentialVault>().BuildServiceProvider();
            Vault = services.GetRequiredService<ApplicationCredentialVault>(); renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        }
        public string Code(int offset = 0) => Totp.Code(secret, counter + offset);
        public Task Mount() => renderer.Mount();
        public Task Input(string id, string value) => renderer.Input(id, value);
        public async Task Settings() { await Input("credential-app-id", "logic-engine"); await Input("credential-name", "bridge"); await Input("credential-days", "1"); }
        public Task<Task> StartIssue() => renderer.Start(false);
        public Task<Task> StartRevoke() => renderer.Start(true);
        public RenderTreeFrame[] PageFrames() => renderer.PageFrames();
        public string FieldValue(string id) => renderer.FieldValue(id);
        public RenderTreeFrame[] DialogFrames() => renderer.DialogFrames();
        private static string Text(IEnumerable<RenderTreeFrame> frames) => string.Concat(frames.Where(f => f.FrameType == RenderTreeFrameType.Text).Select(f => f.TextContent));
        public string PageText() => Text(PageFrames());
        public string DialogText() => Text(DialogFrames());
        public Task DialogEvent(string name) => renderer.DialogEvent(name);
        public async Task Credentials(string code) { await renderer.DialogEvent("oninput", new ChangeEventArgs { Value = Password }); await renderer.DialogEvent("oninput", new ChangeEventArgs { Value = code }, 1); }
        public async ValueTask DisposeAsync() { await renderer.DisposeAsync(); await services.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
