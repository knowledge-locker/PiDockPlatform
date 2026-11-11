using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiDock.Cloud.Web;
namespace PiDock.Tests;

public sealed class TrustedProxyTests
{
    [Theory]
    [InlineData("192.0.2.10", true)]
    [InlineData("192.0.2.20", false)]
    public async Task OnlyConfiguredProxyCanAssertHttps(string remote, bool expected)
    {
        var context = new DefaultHttpContext(); context.Request.Scheme = "http"; context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Headers["X-Forwarded-Proto"] = "https"; context.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(TrustedProxy.Options(["192.0.2.10"], [])));
        await middleware.Invoke(context); Assert.Equal(expected, context.Request.IsHttps);
    }
    [Fact]
    public void EmptyOrUniversalProxyTrustIsRejected()
    {
        Assert.Throws<ArgumentException>(() => TrustedProxy.Options([], []));
        Assert.Throws<ArgumentException>(() => TrustedProxy.Options([], ["0.0.0.0/0"]));
    }
}
