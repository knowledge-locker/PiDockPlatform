#pragma warning disable BL0006 // Exercise the shared component's actual event handlers without a browser.
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class ReauthenticationDialogTests
{
    [Fact]
    public async Task CancelAndReopenNeverApproveAndDoNotRetainCredentials()
    {
        await using var fixture = new Fixture();
        await fixture.Mount();
        var first = await fixture.Request();
        await fixture.Input("oninput", 0, "private password");
        await fixture.Input("oninput", 1, "123456");
        await fixture.Event("onclick");
        Assert.False(await first);
        var second = await fixture.Request();
        Assert.DoesNotContain(fixture.Frames(), f => f.AttributeValue is string s && s is "private password" or "123456");
        await fixture.Event("oncancel");
        Assert.False(await second);
    }

    [Fact]
    public async Task ApprovalRequiresFreshCodeForEveryActionAndRejectsExpiredSessions()
    {
        await using var fixture = new Fixture();
        await fixture.Mount();
        var first = await fixture.Request();
        var code = Totp.Code(fixture.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        await fixture.Credentials(code);
        await fixture.Event("onsubmit");
        Assert.True(await first);
        var second = await fixture.Request();
        await fixture.Credentials(code);
        await fixture.Event("onsubmit");
        Assert.False(second.IsCompleted); // An already-used code cannot approve another action.
        await fixture.Event("oncancel");
        Assert.False(await second);
        fixture.Identity.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "test-owner"), new Claim(ClaimTypes.Role, "Owner"),
            new Claim("expires", "0")], "test"));
        var expired = await fixture.Request();
        await fixture.Credentials(Totp.Code(fixture.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + 1));
        await fixture.Event("onsubmit");
        Assert.False(expired.IsCompleted);
        await fixture.Event("oncancel");
        Assert.False(await expired);
    }

    [Theory]
    [InlineData("Administrator", false, false)]
    [InlineData("Administrator", true, true)]
    [InlineData("Viewer", true, false)]
    public async Task OnlyTheRequiredOwnerOrCloudAdministratorCanApprove(string role, bool cloud, bool approved)
    {
        await using var fixture = new Fixture(role);
        await fixture.Mount(cloud);
        var request = await fixture.Request();
        await fixture.Credentials(Totp.Code(fixture.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30));
        await fixture.Event("onsubmit");
        if (!approved) { Assert.False(request.IsCompleted); await fixture.Event("oncancel"); }
        Assert.Equal(approved, await request);
    }

    [Fact]
    public async Task CredentialActionRunsOnceWithFreshMfaAndIsRemovedAfterCancellation()
    {
        await using var fixture=new Fixture();await fixture.Mount();int actions=0;
        bool Confirm(ClaimsPrincipal user,string password,string code) {
            if(fixture.Users.Authenticate(user.Identity!.Name!,password,code,DateTimeOffset.UtcNow) is null)return false;
            actions++;return true;
        }
        var request=await fixture.Request(Confirm);
        var code=Totp.Code(fixture.Secret,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30);
        await fixture.Credentials(code);await fixture.Event("onsubmit");Assert.True(await request);Assert.Equal(1,actions);
        var replay=await fixture.Request(Confirm);await fixture.Credentials(code);await fixture.Event("onsubmit");
        Assert.False(replay.IsCompleted);Assert.Equal(1,actions);await fixture.Event("oncancel");Assert.False(await replay);
        var cancelled=await fixture.Request((_,_,_)=>{actions++;return true;});await fixture.Event("oncancel");Assert.False(await cancelled);
        var normal=await fixture.Request();await fixture.Credentials(Totp.Code(fixture.Secret,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30+1));
        await fixture.Event("onsubmit");Assert.True(await normal);Assert.Equal(1,actions);
    }
    private sealed class Identity : AuthenticationStateProvider
    {
        public ClaimsPrincipal User { get; set; } = new();
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(User));
    }
    private sealed class JavaScript : IJSRuntime, IJSObjectReference
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(identifier == "import" ? (T)(object)this : default!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class TestRenderer(IServiceProvider services, ILoggerFactory logging) : Renderer(services, logging)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public ReauthenticationDialog Component { get; private set; } = default!;
        private int id;
        public Task Mount(bool cloud) => Dispatcher.InvokeAsync(async () => {
            Component = (ReauthenticationDialog)InstantiateComponent(typeof(ReauthenticationDialog));
            id = AssignRootComponentId(Component);
            await RenderRootComponentAsync(id, ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Component.Cloud)] = cloud }));
        });
        public RenderTreeFrame[] Frames() { var frames = GetCurrentRenderTreeFrames(id); return frames.Array.Take(frames.Count).ToArray(); }
        public Task Event(string name, EventArgs args, int index = 0) => Dispatcher.InvokeAsync(() => DispatchEventAsync(Frames().Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == name).ElementAt(index).AttributeEventHandlerId, null, args));
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Component render failed", exception);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private const string Password = "integration-test-password";
        private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pidock-reauth-" + Guid.NewGuid());
        private readonly ServiceProvider services;
        private readonly TestRenderer renderer;
        public UserStore Users {get;}
        public string Secret { get; } = Totp.NewSecret();
        public Identity Identity { get; } = new();
        public Fixture(string role = "Owner")
        {
            var users = Users = new UserStore(new EphemeralDataProtectionProvider(), directory);
            users.Add("test-owner", Password, Secret, "local", role, false, -1);
            var user = users.Authenticate("test-owner", Password, Totp.Code(Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 - 1), DateTimeOffset.UtcNow)!;
            Identity.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.Name,user.Username), new Claim(ClaimTypes.Role,user.Role), new Claim("tenant",user.TenantId), new Claim("stamp",user.Stamp),
                new Claim("expires",DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString())], "test"));
            services = new ServiceCollection().AddLogging().AddSingleton(users).AddSingleton<AuthenticationStateProvider>(Identity).AddSingleton<IJSRuntime>(new JavaScript()).BuildServiceProvider();
            renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        }
        public Task Mount(bool cloud = false) => renderer.Mount(cloud);
        public async Task<Task<bool>> Request(Func<ClaimsPrincipal,string,string,bool>? action=null) { Task<bool>? result = null; await renderer.Dispatcher.InvokeAsync(() => { result = renderer.Component.Request("Test action", "Only this action is approved.",action); }); return result!; }
        public RenderTreeFrame[] Frames() => renderer.Frames();
        public Task Event(string name) => renderer.Event(name, EventArgs.Empty);
        public Task Input(string name, int index, string value) => renderer.Event(name, new ChangeEventArgs { Value = value }, index);
        public async Task Credentials(string code) { await Input("oninput", 0, Password); await Input("oninput", 1, code); }
        public async ValueTask DisposeAsync() { await renderer.DisposeAsync(); await services.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
