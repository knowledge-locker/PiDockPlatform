using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Logging;
namespace PiDock.Web.Shared;

public sealed class SessionAuthenticationStateProvider(ILoggerFactory loggerFactory, UserStore users) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(10);
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) => Task.FromResult(users.IsCurrent(authenticationState.User));
}
