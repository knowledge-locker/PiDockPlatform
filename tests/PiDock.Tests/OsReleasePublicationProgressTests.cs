using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiDock.Cloud.Web;
using PiDock.Cloud.Web.Components.Pages;

namespace PiDock.Tests;

public sealed class OsReleasePublicationProgressTests
{
    [Fact]
    public async Task UploadBarExposesActualByteCountAndAccessiblePercentage()
    {
        var html = await Render(OsReleasePublicationPhase.Uploading, 250, 1000);
        Assert.Contains("value=\"250\"", html);
        Assert.Contains("max=\"1000\"", html);
        Assert.Contains("25% uploaded", html);
        Assert.Contains("Step 1 of 3", html);
        Assert.Contains("aria-busy=\"true\"", html);
    }

    [Theory]
    [InlineData(OsReleasePublicationPhase.Verifying, "Step 2 of 3", "Upload complete")]
    [InlineData(OsReleasePublicationPhase.Saving, "Step 3 of 3", "Verification passed")]
    public async Task PostUploadWorkIsIndeterminateRatherThanClaimingCompletion(OsReleasePublicationPhase phase, string step, string explanation)
    {
        var html = await Render(phase, 1000, 1000);
        Assert.Contains("<progress", html);
        Assert.DoesNotContain("value=", html);
        Assert.DoesNotContain("100% uploaded", html);
        Assert.Contains(step, html);
        Assert.Contains(explanation, html);
    }

    private static async Task<string> Render(OsReleasePublicationPhase phase, long bytes, long total)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ReleasePublicationProgress>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                [nameof(ReleasePublicationProgress.Progress)] = new OsReleasePublicationProgress(phase, bytes),
                [nameof(ReleasePublicationProgress.TotalBytes)] = total
            }));
            return component.ToHtmlString();
        });
    }
}
