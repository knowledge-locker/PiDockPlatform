using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PiDock.AppUi.Sample;
using PiDock.Core;

namespace PiDock.Tests;

public sealed class ApplicationConformanceFixtureTests
{
    [Fact]
    public void MonotonicDelayRepeatedStartAndBankGateHaveExplicitSemantics()
    {
        var clock = new Clock(); var sequence = new SampleSequence(clock);
        sequence.Inputs(true, false); clock.Advance(2);
        Assert.True(sequence.Read().RunRequest); Assert.False(sequence.Read().Done);
        sequence.Inputs(true, false); sequence.ToggleBank(); clock.Advance(1);
        Assert.True(sequence.Read().Done); Assert.False(sequence.Read().EffectiveOutput);
        sequence.ToggleBank(); Assert.True(sequence.Read().EffectiveOutput);
        sequence.Inputs(true, true);
        Assert.False(sequence.Read().RunRequest); Assert.Equal(0, sequence.Read().ElapsedSeconds);
        Assert.False(sequence.Read().Done); Assert.False(sequence.Read().NormalOutput);
        sequence.Inputs(true, false); clock.Advance(3);
        Assert.True(sequence.Read().NormalOutput);
        Assert.False(new SampleSequence(clock).Read().RunRequest);
    }

    [Fact]
    public async Task AuthenticatedHostCommandsStayResponsiveWhileWorkerRunsWithoutSubscribers()
    {
        using var factory = new FixtureFactory();
        using var client = factory.CreateClient();
        var denied = await client.PostAsJsonAsync("/commands", Command("demo.start"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token);
        var start = await Send(client, Command("demo.start"));
        Assert.True(start.Data!.Value.GetProperty("demo").GetProperty("runRequest").GetBoolean());
        var increment = Command("counter.increment");
        Assert.Equal(1, (await Send(client, increment)).Data!.Value.GetProperty("counter").GetInt32());
        Assert.Equal(1, (await Send(client, increment)).Data!.Value.GetProperty("counter").GetInt32());
        Assert.False((await Send(client, Command("demo.start"))).Data!.Value.GetProperty("demo").GetProperty("done").GetBoolean());
        factory.Clock.Advance(3);
        var runtime = factory.Services.GetRequiredService<SampleRuntime>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (runtime.Read(null)[0].Data.GetProperty("ticks").GetInt64() == 0)
            await Task.Delay(25, deadline.Token);
        var snapshot = runtime.Read(null)[0]; // No events subscription or browser has ever connected.
        Assert.True(snapshot.Data.GetProperty("demo").GetProperty("effectiveOutput").GetBoolean());
        Assert.Equal("simulated", snapshot.Data.GetProperty("demo").GetProperty("quality").GetString());
        var stop = await Send(client, Command("demo.inputs", new { start = true, stop = true }));
        Assert.False(stop.Data!.Value.GetProperty("demo").GetProperty("runRequest").GetBoolean());
        var html = await client.GetStringAsync("/index.html");
        Assert.Contains("simulation only", html); Assert.Contains("pd-panel", html);
        Assert.DoesNotContain(factory.Token, html);
    }

    [Fact]
    public async Task ExpiredCredentialAndChangedReplayCannotChangeDemonstration()
    {
        using var factory = new FixtureFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token);
        var original = Command("demo.start"); await Send(client, original);
        var replay = await client.PostAsJsonAsync("/commands", original with { Name = "demo.stop" });
        var result = (await replay.Content.ReadFromJsonAsync<ApplicationUiCommandResult>())!;
        Assert.False(result.Succeeded); Assert.Equal("command_id_conflict", result.ErrorCode);
        factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Sample:BridgeExpiresAt"] = DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/commands", Command("demo.stop"))).StatusCode);
        Assert.True(factory.Services.GetRequiredService<SampleRuntime>().Read(null)[0].Data.GetProperty("demo").GetProperty("runRequest").GetBoolean());
    }

    [Fact]
    public async Task ReconnectReplayIsPacedBelowTheSharedHostStreamLimit()
    {
        using var factory = new FixtureFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token);
        var runtime = factory.Services.GetRequiredService<SampleRuntime>();
        var initial = runtime.Read(null)[0];
        for (var index = 0; index < 32; index++) runtime.Command(Command("counter.increment"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/events");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", initial.Epoch + ":" + initial.Sequence);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var reader = new StreamReader(stream);
        var order = new ApplicationUiEventOrder(new(initial.Epoch, initial.Sequence));
        var elapsed = Stopwatch.StartNew();
        for (var index = 0; index < 21; index++)
        {
            var line = await reader.ReadLineAsync(deadline.Token);
            Assert.NotNull(line);
            Assert.True(order.Accept(JsonSerializer.Deserialize<ApplicationUiEvent>(line, StateFiles.Json)!));
        }
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(1.8), "Reconnect must not dump an accumulated journal in one host rate window.");
    }

    private static ApplicationUiCommand Command(string name, object? arguments = null) => new(1, Guid.NewGuid(), name, JsonSerializer.SerializeToElement(arguments ?? new { }));
    private static async Task<ApplicationUiCommandResult> Send(HttpClient client, ApplicationUiCommand command)
    {
        using var response = await client.PostAsJsonAsync("/commands", command);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ApplicationUiCommandResult>())!;
        Assert.True(result.Succeeded); return result;
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(int seconds) => Interlocked.Add(ref ticks, seconds * 1000L);
    }
    private sealed class FixtureFactory : WebApplicationFactory<SampleMarker>
    {
        public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public Clock Clock { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development")
            .UseSetting("Sample:BridgeToken", Token).UseSetting("Sample:BridgeExpiresAt", DateTimeOffset.UtcNow.AddHours(1).ToString("O"))
            .UseSetting("Logging:LogLevel:Default", "Warning").ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock));
    }
}
