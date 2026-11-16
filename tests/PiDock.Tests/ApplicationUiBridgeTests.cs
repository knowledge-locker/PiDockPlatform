using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PiDock.Core;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class ApplicationUiBridgeTests
{
    private static ApplicationUiCommand Command(string name = "counter.increment", string json = "{}", Guid? id = null) =>
        new(1, id ?? Guid.NewGuid(), name, JsonSerializer.Deserialize<JsonElement>(json));
    private static ApplicationUiEvent Event(long sequence, string kind = "update", string epoch = "epoch-one") =>
        new(1, epoch, sequence, kind, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { counter = sequence }));
    private static string Line(ApplicationUiEvent value) => JsonSerializer.Serialize(value, StateFiles.Json) + "\n";

    [Fact]
    public async Task NamedCommandsAreAuthorizedScopedAndUseOnlyServerCredentials()
    {
        using var fixture = new Fixture();
        var result = await fixture.Service.Command(fixture.User, "sample", "main", Command(), default);
        Assert.True(result.Succeeded);
        var call = Assert.Single(fixture.Backend.Calls);
        Assert.Equal("http://127.0.0.1:7361/commands", call.Uri);
        Assert.Equal(fixture.Token, call.Token); Assert.False(call.Cookie);
        Assert.DoesNotContain(fixture.Token, call.Body);
        Assert.False((await fixture.Service.Command(fixture.User, "other-app", "main", Command(), default)).Succeeded);
        Assert.Equal("command_denied", (await fixture.Service.Command(fixture.User, "sample", "main", Command("shell.execute"), default)).ErrorCode);
        Assert.Single(fixture.Backend.Calls);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("expired")]
    [InlineData("anonymous")]
    [InlineData("disabled")]
    [InlineData("expired-credential")]
    [InlineData("missing-credential")]
    public async Task MissingOwnerAuthorityOrLiveGrantDeniesCommandsAndStreams(string mode)
    {
        using var f = new Fixture(mode == "Viewer" ? "Viewer" : "Owner"); var user = f.User;
        if (mode == "anonymous") user = new();
        if (mode == "expired") { var identity = new ClaimsIdentity(user.Identity); identity.RemoveClaim(identity.FindFirst("expires")!); identity.AddClaim(new("expires", "1")); user = new(identity); }
        if (mode == "disabled") f.Configuration["PiDock:ApplicationUi:Registrations:0:Enabled"] = "false";
        if (mode == "expired-credential") f.Configuration["PiDock:ApplicationUi:Credentials:sample:ExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        if (mode == "missing-credential") f.Configuration["PiDock:ApplicationUi:Credentials:sample:Token"] = null;
        Assert.Empty(f.Service.Capabilities(user, "sample", "main").Commands);
        Assert.Equal("not_authorized", (await f.Service.Command(user, "sample", "main", Command(), default)).ErrorCode);
        var messages = await Collect(f.Service.Stream(user, "sample", "main", null, default));
        Assert.Equal("not_authorized", Assert.Single(messages).Status);
        Assert.Empty(f.Backend.Calls);
    }

    [Fact]
    public async Task DuplicateCommandsShareAResultAndConflictingIdsNeverExecuteTwice()
    {
        using var f = new Fixture(); var request = Command();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Service.Command(f.User, "sample", "main", request, default)));
        Assert.All(results, r => Assert.True(r.Succeeded)); Assert.Single(f.Backend.Calls);
        Assert.Equal("command_id_conflict", (await f.Service.Command(f.User, "sample", "main", Command(json:"{\"x\":1}",id:request.CommandId), default)).ErrorCode);
        f.Configuration["PiDock:ApplicationUi:Credentials:sample:Token"] = new string('b', 64);
        Assert.Equal("command_id_conflict", (await f.Service.Command(f.User, "sample", "main", request, default)).ErrorCode);
        Assert.Single(f.Backend.Calls);
    }

    [Fact]
    public async Task CommandsHavePayloadAndRateLimits()
    {
        using var f = new Fixture();
        Assert.Equal("payload_too_large", (await f.Service.Command(f.User, "sample", "main", Command(json:JsonSerializer.Serialize(new { value = new string('x',9000) })), default)).ErrorCode);
        Assert.Equal("invalid_arguments", (await f.Service.Command(f.User, "sample", "main", Command(json:"[]"), default)).ErrorCode);
        for (int i = 0; i < 60; i++) Assert.True((await f.Service.Command(f.User, "sample", "main", Command(), default)).Succeeded);
        Assert.Equal("rate_limited", (await f.Service.Command(f.User, "sample", "main", Command(), default)).ErrorCode);
        Assert.Equal(60, f.Backend.Calls.Count);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("server-error")]
    [InlineData("invalid-json")]
    [InlineData("wrong-id")]
    [InlineData("oversize")]
    public async Task UncertainCommandOutcomesAreNotRetriedOrExposedAsPrivateErrors(string mode)
    {
        using var f = new Fixture();
        f.Backend.Response = (_, _) => Task.FromResult(mode switch
        {
            "redirect" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("http://evil.test/") } },
            "server-error" => new(HttpStatusCode.InternalServerError) { Content = new StringContent("PRIVATE-SENTINEL") },
            "wrong-id" => Json(new ApplicationUiCommandResult(1, Guid.NewGuid(), true, null, null)),
            _ => new(HttpStatusCode.OK) { Content = new StringContent(mode == "oversize" ? new string('x',33000) : "PRIVATE-SENTINEL", Encoding.UTF8, "application/json") }
        });
        var result = await f.Service.Command(f.User, "sample", "main", Command(), default);
        Assert.Equal("outcome_unknown", result.ErrorCode); Assert.False(result.Succeeded); Assert.Null(result.Data);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result)); Assert.Single(f.Backend.Calls);
    }

    [Fact]
    public async Task StreamDeliversSnapshotsOrderedUpdatesAndCursorWithoutLeakingCredentials()
    {
        using var f = new Fixture();
        f.Backend.Response = (_, _) => Task.FromResult(Ndjson(Line(Event(0,"snapshot")) + Line(Event(1)) + Line(Event(1,"heartbeat"))));
        var messages = await Collect(f.Service.Stream(f.User, "sample", "main", null, default));
        Assert.Equal("connected", messages[0].Status);
        Assert.Equal(["snapshot","update","heartbeat"], messages.Where(m=>m.Type=="event").Select(m=>m.Event!.Kind).ToArray());
        Assert.Equal("disconnected", messages[^1].Status);
        f.Backend.Response = (_, _) => Task.FromResult(Ndjson(Line(Event(6))));
        var resumed = await Collect(f.Service.Stream(f.User, "sample", "main", new("epoch-one",5), default));
        Assert.Equal("epoch-one:5", f.Backend.Calls[^1].Cursor);
        Assert.Equal(6, resumed.Single(m=>m.Type=="event").Event!.Sequence);
        Assert.DoesNotContain(f.Token, JsonSerializer.Serialize(resumed));
    }

    [Theory]
    [InlineData("gap", "gap")]
    [InlineData("wrong-epoch", "gap")]
    [InlineData("malformed", "protocol_error")]
    [InlineData("oversize", "protocol_error")]
    [InlineData("truncated", "protocol_error")]
    public async Task StreamRejectsGapsAndBadFrames(string mode,string expected)
    {
        using var f = new Fixture();
        var tail = mode switch { "gap" => Line(Event(2)), "wrong-epoch" => Line(Event(1,epoch:"other")), "malformed" => "not-json\n", "oversize" => new string('x',33000)+"\n", _ => "partial" };
        f.Backend.Response = (_, _) => Task.FromResult(Ndjson(Line(Event(0,"snapshot")) + tail));
        var messages = await Collect(f.Service.Stream(f.User,"sample","main",null,default));
        Assert.Equal(expected,messages[^1].Status); Assert.Single(messages,m=>m.Type=="event");
    }

    [Fact]
    public async Task RevocationCancelsAnIdleStreamAndReleasesItsSlot()
    {
        using var f = new Fixture();
        f.Backend.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) { Headers = { ContentType = new("application/x-ndjson") } } });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var collect = Collect(f.Service.Stream(f.User,"sample","main",null,timeout.Token));
        await Task.Delay(100,timeout.Token);
        f.Configuration["PiDock:ApplicationUi:Registrations:0:Enabled"]="false";
        Assert.Equal("revoked",Assert.Single(await collect).Status);
        f.Configuration["PiDock:ApplicationUi:Registrations:0:Enabled"]="true";
        f.Backend.Response = (_,_)=>Task.FromResult(Ndjson(Line(Event(0,"snapshot"))));
        Assert.Contains(await Collect(f.Service.Stream(f.User,"sample","main",null,timeout.Token)),m=>m.Type=="event");
    }

    [Fact]
    public async Task SlowStreamsAreMarkedStaleAndOveractiveStreamsAreRateLimited()
    {
        using var f = new Fixture();
        f.Backend.Response=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) { Headers={ContentType=new("application/x-ndjson")} } });
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Assert.Equal("stale",Assert.Single(await Collect(f.Service.Stream(f.User,"sample","main",null,timeout.Token))).Status);
        f.Backend.Response=(_,_)=>Task.FromResult(Ndjson(Line(Event(0,"snapshot"))+string.Concat(Enumerable.Range(1,21).Select(i=>Line(Event(i))))));
        var flood=await Collect(f.Service.Stream(f.User,"sample","main",null,default));
        Assert.Equal("rate_limited",flood[^1].Status); Assert.Equal(20,flood.Count(m=>m.Type=="event"));
    }

    [Fact]
    public async Task ConcurrentSubscriptionsAreBoundedAndCanceledConsumersReleaseSlots()
    {
        using var f=new Fixture();
        f.Backend.Response=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StreamContent(new BlockingStream()) { Headers={ContentType=new("application/x-ndjson")} } });
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await using var first=f.Service.Stream(f.User,"sample","main",null,stop.Token).GetAsyncEnumerator();
        await using var second=f.Service.Stream(f.User,"sample","main",null,stop.Token).GetAsyncEnumerator();
        var one=first.MoveNextAsync().AsTask(); var two=second.MoveNextAsync().AsTask();
        Assert.Equal("busy",Assert.Single(await Collect(f.Service.Stream(f.User,"sample","main",null,default))).Status);
        stop.Cancel(); await Task.WhenAll(one,two);
        f.Backend.Response=(_,_)=>Task.FromResult(Ndjson(Line(Event(0,"snapshot"))));
        Assert.Contains(await Collect(f.Service.Stream(f.User,"sample","main",null,default)),m=>m.Type=="event");
    }

    [Fact]
    public void EventOrderHandlesRestartSnapshotsAndRejectsInvalidCursors()
    {
        var order=new ApplicationUiEventOrder(new("old",100));
        Assert.True(order.Accept(Event(0,"snapshot","new"))); Assert.True(order.Accept(Event(1,epoch:"new")));
        Assert.False(order.Accept(Event(0,"snapshot","new"))); Assert.False(order.Accept(Event(1,epoch:"new"))); Assert.False(order.Accept(Event(3,epoch:"new")));
        Assert.False(ApplicationUiBridgeRules.Cursor(new("epoch\r\nheader",1)));
        Assert.False(ApplicationUiBridgeRules.Cursor(new("epoch",9007199254740992)));
        Assert.False(ApplicationUiBridgeRules.Grant(new("sample","../admin",["run"],null)));
        Assert.True(ApplicationUiBridgeRules.Grant(new("sample",null,null!,"events")));
    }

    [Fact]
    public async Task ManagedBridgeCredentialOverridesLegacyAndRevocationNeverFallsBack()
    {
        var source = new ManagedSource();
        using var f = new Fixture(credentials: source);
        Assert.Empty(f.Service.Capabilities(f.User,"sample","main").Commands);
        source.Value = new(1,"sample","sample",1,"ui-bridge",new string('b',64),DateTimeOffset.UtcNow.AddHours(1),[]);
        var command = new ApplicationUiCommand(1,Guid.NewGuid(),"counter.increment",JsonSerializer.SerializeToElement(new{}));
        Assert.True((await f.Service.Command(f.User,"sample","main",command,default)).Succeeded);
        Assert.Equal(source.Value.Token,Assert.Single(f.Backend.Calls).Token);
        source.Value = source.Value with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        Assert.Empty(f.Service.Capabilities(f.User,"sample","main").Commands);
        source.Value = null;
        Assert.Equal("not_authorized",(await f.Service.Command(f.User,"sample","main",command with { CommandId = Guid.NewGuid() },default)).ErrorCode);
        Assert.Single(f.Backend.Calls);
    }
    private sealed class ManagedSource : IApplicationCredentialSource
    {
        public ApplicationCredentialFile? Value;
        public ApplicationCredentialResolution Resolve(string app,string id) => new(true,Value);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content=new StringContent(JsonSerializer.Serialize(value,StateFiles.Json),Encoding.UTF8,"application/json") };
    private static HttpResponseMessage Ndjson(string text) => new(HttpStatusCode.OK) { Content=new StringContent(text,Encoding.UTF8,"application/x-ndjson") };
    private static async Task<List<ApplicationUiStreamMessage>> Collect(IAsyncEnumerable<ApplicationUiStreamMessage> stream)
    { var result=new List<ApplicationUiStreamMessage>(); await foreach(var item in stream)result.Add(item);return result; }
    private sealed record Call(string Uri,string? Token,bool Cookie,string Body,string? Cursor);
    private sealed class Backend : HttpMessageHandler
    {
        public readonly List<Call> Calls=[];
        public Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>? Response;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var body=request.Content is null?"":await request.Content.ReadAsStringAsync(ct);
            Calls.Add(new(request.RequestUri!.AbsoluteUri,request.Headers.Authorization?.Parameter,request.Headers.Contains("Cookie"),body,request.Headers.TryGetValues("Last-Event-ID",out var values)?values.Single():null));
            if(Response is not null)return await Response(request,ct);
            var command=JsonSerializer.Deserialize<ApplicationUiCommand>(body,StateFiles.Json)!;
            return Json(new ApplicationUiCommandResult(1,command.CommandId,true,JsonSerializer.SerializeToElement(new {counter=1}),null));
        }
    }
    private sealed class Factory(HttpClient client):IHttpClientFactory { public HttpClient CreateClient(string name)=>client; }
    private sealed class Fixture:IDisposable
    {
        private readonly string root=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"pidock-bridge-test-"+Guid.NewGuid());
        public readonly string Token=new('a',64); public readonly Backend Backend=new(); public readonly IConfigurationRoot Configuration;
        public readonly ClaimsPrincipal User; public readonly ApplicationUiBridgeService Service; private readonly HttpClient client;
        public Fixture(string role="Owner", IApplicationCredentialSource? credentials = null)
        {
            var users=new UserStore(new EphemeralDataProtectionProvider(),root);
            var secret=Totp.NewSecret(); users.Add("bridge-user","integration-test-password",secret,"local",role,role=="Owner",-1);
            var login=users.Authenticate("bridge-user","integration-test-password",Totp.Code(secret,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30),DateTimeOffset.UtcNow)!;
            User=users.Principal(login);
            Configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["PiDock:ApplicationUi:Registrations:0:Contribution:SchemaVersion"]="1",
                ["PiDock:ApplicationUi:Registrations:0:Contribution:AppId"]="sample",
                ["PiDock:ApplicationUi:Registrations:0:Contribution:Id"]="main",
                ["PiDock:ApplicationUi:Registrations:0:Contribution:Title"]="Sample",
                ["PiDock:ApplicationUi:Registrations:0:Contribution:EntryPath"]="index.html",
                ["PiDock:ApplicationUi:Registrations:0:Contribution:RequiredUiVersion"]="1.0",
                ["PiDock:ApplicationUi:Registrations:0:Contribution:RequiredStyleVersion"]="1.0",
                ["PiDock:ApplicationUi:Registrations:0:ServiceOrigin"]="http://127.0.0.1:7361/",
                ["PiDock:ApplicationUi:Registrations:0:Bridge:CredentialId"]="sample",
                ["PiDock:ApplicationUi:Registrations:0:Bridge:CommandsPath"]="commands",
                ["PiDock:ApplicationUi:Registrations:0:Bridge:Commands:0"]="counter.increment",
                ["PiDock:ApplicationUi:Registrations:0:Bridge:StreamPath"]="events",
                ["PiDock:ApplicationUi:Credentials:sample:Token"]=Token,
                ["PiDock:ApplicationUi:Credentials:sample:ExpiresAt"]=DateTimeOffset.UtcNow.AddHours(1).ToString("O")
            }).Build();
            client=new(Backend){Timeout=Timeout.InfiniteTimeSpan};
            Service=new(new(Configuration),Configuration,users,new Factory(client),NullLogger<ApplicationUiBridgeService>.Instance, credentials);
        }
        public void Dispose(){client.Dispose();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private sealed class BlockingStream:Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){await Task.Delay(Timeout.Infinite,ct);return 0;}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
