using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiDock.Agent.Mcp;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(_ => DiscoveryApiClient.FromEnvironment());
builder.Services.AddSingleton(sp => new CloudPublishingClient(sp.GetRequiredService<DiscoveryApiClient>()));
builder.Services.AddMcpServer().WithStdioServerTransport()
    .WithTools<DiscoveryTools>().WithTools<CloudPublishingTools>().WithResources<DiscoveryResources>();
await builder.Build().RunAsync();
