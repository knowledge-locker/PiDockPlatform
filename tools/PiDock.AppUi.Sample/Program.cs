using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiDock.Core;
using PiDock.Web.Shared;
using PiDock.AppUi.Sample;

var bootstrap = await ApplicationBootstrap.InitializeAsync();
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new SampleStorage(bootstrap?.PersistentData == true ? Path.Combine(ApplicationResourceRules.DataDirectory,"counter.json") : null,ApplicationHealthReporter.Create(bootstrap),builder.Configuration["Sample:Version"]??"local"));
if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("The app UI fixture is Development-only.");
bool dockerFixture = builder.Configuration.GetValue<bool>("Sample:DockerFixture");
builder.WebHost.ConfigureKestrel(options => { options.Listen(dockerFixture ? IPAddress.Any : IPAddress.Loopback, builder.Configuration.GetValue<int?>("Sample:Port") ?? 7361); options.Limits.MaxRequestBodySize = 8192; });
if (dockerFixture)
{
    if (bootstrap is null) throw new InvalidOperationException("Docker conformance requires managed bootstrap.");
    string Credential(string name) => ApplicationBootstrap.ReadCredential(name) is { Purpose: "platform" } file ? file.Token : throw new IOException("Managed fixture credential unavailable.");
    builder.Services.AddSingleton(SampleConnectedSession.Create(new(
        builder.Configuration["Sample:ServicesOrigin"] ?? "", builder.Configuration["Sample:CertificateSha256"] ?? "",
        Credential("sample-io"), Credential("sample-runtime"), Credential("sample-engineer"), bootstrap.DeliveryId, DockerFixture: true)));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SampleConnectedSession>());
}
if (builder.Configuration["Sample:ConnectionFile"] is { Length: > 0 } connectionFile)
{
    if (dockerFixture) throw new InvalidOperationException("Choose one fixture delivery mechanism.");
    if (!Path.IsPathFullyQualified(connectionFile)) throw new InvalidOperationException("Private connection path must be absolute.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    while (!File.Exists(connectionFile)) await Task.Delay(100, deadline.Token);
    if (File.GetAttributes(connectionFile).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(connectionFile).Length > 16384 ||
        (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(connectionFile) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0))
        throw new IOException("Private fixture connection permissions are invalid.");
    var settings = JsonSerializer.Deserialize<SampleConnection>(File.ReadAllBytes(connectionFile), StateFiles.Json) ?? throw new IOException("Private connection unavailable.");
    builder.Services.AddSingleton(SampleConnectedSession.Create(settings));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SampleConnectedSession>());
}
builder.Services.AddSingleton<SampleRuntime>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<SampleRuntime>());
var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<SampleRuntime>().Readiness(true));
app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<SampleRuntime>().Readiness(false));
bool Authorized(HttpContext context)
{
    var file = bootstrap is null ? null : ApplicationBootstrap.ReadCredential(builder.Configuration["Sample:CredentialName"] ?? "bridge");
    var token = bootstrap is null ? builder.Configuration["Sample:BridgeToken"] : file?.Purpose == "ui-bridge" ? file.Token : null;
    var header = context.Request.Headers.Authorization.ToString();
    return token is { Length: 64 } && token.All(char.IsAsciiHexDigit) && header.StartsWith("Bearer ", StringComparison.Ordinal) &&
        header.Length == 71 && DateTimeOffset.TryParse(bootstrap is null ? builder.Configuration["Sample:BridgeExpiresAt"] : file?.ExpiresAt.ToString("O"), out var expiry) && expiry > DateTimeOffset.UtcNow &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(header[7..])));
}
// Opt-in disposable Docker fault injection; never registered by the ordinary sample or UI bridge.
if (dockerFixture && builder.Configuration.GetValue<bool>("Sample:HealthFaults"))
    app.MapPost("/fixture/readiness", (HttpContext context, JsonElement value, SampleRuntime runtime) =>
    {
        if (!Authorized(context)) return Results.Unauthorized();
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Results.BadRequest();
        runtime.Readiness(value.GetBoolean()); return Results.NoContent();
    });
app.MapGet("/assets/{name}", (string name) => name == "client.js" ? Results.Text(ApplicationUiBridgeAssets.Read(name)!, "text/javascript") :
    ApplicationUiAssets.Read(name) is { } css ? Results.Text(css, "text/css") : Results.NotFound());
app.MapPost("/commands", async (HttpContext context, SampleRuntime runtime) =>
{
    if (!Authorized(context)) return Results.Unauthorized();
    try
    {
        var command = await JsonSerializer.DeserializeAsync<ApplicationUiCommand>(context.Request.Body, new JsonSerializerOptions(StateFiles.Json) { MaxDepth = 16 }, context.RequestAborted);
        return command is null ? Results.BadRequest() : Results.Json(await runtime.CommandAsync(command, context.RequestAborted), StateFiles.Json);
    }
    catch (JsonException) { return Results.BadRequest(); }
});
app.MapGet("/events", async (HttpContext context, SampleRuntime runtime) =>
{
    if (!Authorized(context)) { context.Response.StatusCode = 401; return; }
    ApplicationUiCursor? cursor = null;
    var raw = context.Request.Headers["Last-Event-ID"].ToString().Split(':');
    if (raw.Length == 2 && long.TryParse(raw[1], out var seq)) cursor = new(raw[0], seq);
    if (!ApplicationUiBridgeRules.Cursor(cursor)) { context.Response.StatusCode = 400; return; }
    var generation = runtime.Generation;
    context.Response.ContentType = "application/x-ndjson"; context.Response.Headers.CacheControl = "no-store";
    try
    {
        while (!context.RequestAborted.IsCancellationRequested && Authorized(context) && generation == runtime.Generation)
        {
            foreach (var item in runtime.Read(cursor))
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(item, StateFiles.Json) + "\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                cursor = new(item.Epoch, item.Sequence);
                // A reconnect may replay the full bounded journal. Pace it below the host's 20/s limit.
                await Task.Delay(100, context.RequestAborted);
            }
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(500, context.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
});
app.MapGet("/index.html", () => Results.Content("""
    <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>PiDockOS authenticated UI sample</title><link rel="stylesheet" href="assets/components.css"></head>
    <body class="pidock-app pd-stack" style="margin:0">
    <div class="pd-row"><span class="pd-badge">Isolated UI fixture · simulation only</span><span class="pd-badge" id="connection" role="status" aria-live="polite">Connecting</span></div>
    <h1 class="pd-title">Commands and live data, inside PiDockOS</h1><span class="pd-badge" id="build-version">Version local</span>
    <p class="pd-muted">PiDockOS authenticates your commands. This frame receives neither your session cookie nor the app service credential.</p>
    <div class="pd-grid"><section class="pd-panel pd-stack"><h2 class="pd-heading">Authenticated app commands</h2>
    <p class="pd-muted">These controls change the sample app's server-side counter. They do not control hardware.</p>
    <div class="pd-row"><button class="pd-button" id="increment" disabled>Increment app counter</button><button class="pd-button" data-variant="secondary" id="reset" disabled>Reset app counter</button></div>
    <button class="pd-button" data-variant="secondary" id="disconnect" disabled>Disconnect sample stream</button>
    <span id="result" role="status" aria-live="polite">Waiting for host authorization.</span></section>
    <section class="pd-panel pd-stack"><h2 class="pd-heading">Live data from the app</h2>
    <div class="pd-table-wrap"><table class="pd-table"><thead><tr><th>Value</th><th>Current reading</th></tr></thead><tbody><tr><td>Server counter</td><td id="counter">—</td></tr><tr><td>Server ticks</td><td id="ticks">—</td></tr><tr><td>Stream sequence</td><td id="sequence">—</td></tr></tbody></table></div>
    <span class="pd-muted" id="freshness">No live snapshot yet.</span></section></div>
    <section class="pd-panel pd-stack" id="local-demo"><h2 class="pd-heading">Latch and delayed output</h2>
    <p class="pd-muted">A Start pulse latches a request. Three seconds later the simulated bank turns on. Stop clears the request and timer. The server keeps working while this interface is closed.</p>
    <div class="pd-row"><button class="pd-button" id="demo-start" disabled>Pulse Start</button><button class="pd-button" data-variant="secondary" id="demo-stop" disabled>Pulse Stop</button><button class="pd-button" data-variant="secondary" id="demo-bank" disabled>Toggle bank enable</button><button class="pd-button" data-variant="secondary" id="demo-reset" disabled>Reset demonstration</button></div>
    <div class="pd-table-wrap"><table class="pd-table"><thead><tr><th>Signal</th><th>Normal</th><th>Effective</th><th>Quality</th></tr></thead><tbody>
    <tr><td>Run request</td><td id="run-normal">—</td><td id="run-effective">—</td><td>Simulated</td></tr>
    <tr><td>Bank enabled</td><td id="bank-normal">—</td><td id="bank-effective">—</td><td>Simulated</td></tr>
    <tr><td>Timer elapsed / done</td><td id="timer-normal">—</td><td id="timer-effective">—</td><td>Simulated</td></tr>
    <tr><td>Output bank</td><td id="output-normal">—</td><td id="output-effective">—</td><td>Simulated</td></tr>
    </tbody></table></div><p class="pd-muted">No physical GPIO or engineering override is connected to this demonstration. Normal and effective values agree. Shared I/O leases and engineering controls have separate conformance tests.</p></section>
    <section class="pd-panel pd-stack" id="shared-demo" hidden>
    <div class="pd-row"><h2 class="pd-heading">Shared simulated I/O and engineering</h2><span class="pd-badge" id="shared-status" role="status">Connecting</span><span class="pd-badge" id="force-count">0 forces</span></div>
    <p class="pd-muted">Real platform services, simulated pins. The low-rate control cycle runs every 1.5 seconds; the timer waits at least three seconds without blocking other work.</p>
    <div class="pd-row"><button class="pd-button" id="shared-start" disabled>Pulse shared Start</button><button class="pd-button" data-variant="secondary" id="shared-stop" disabled>Pulse shared Stop</button><button class="pd-button" data-variant="secondary" id="shared-bank" disabled>Set bank enable once</button><button class="pd-button" data-variant="secondary" id="shared-reset" disabled>Reset shared runtime</button></div>
    <p id="shared-revision" class="pd-muted"></p>
    <div class="pd-table-wrap"><table class="pd-table"><thead><tr><th>Signal</th><th>Normal</th><th>Effective</th><th>Force / quality</th></tr></thead><tbody id="shared-values"></tbody></table></div>
    <p class="pd-muted" id="applied-values">No simulated GPIO batch has been confirmed.</p>
    <div class="pd-grid"><label class="pd-field" for="force-signal">Signal to test<select class="pd-input" id="force-signal"><option value="start">Start input · sim:bcm5</option><option value="stop">Stop input · sim:bcm6</option><option value="run-request">Run request</option><option value="bank-enabled">Bank enabled</option><option value="output-bank">Output bank</option></select></label>
    <label class="pd-field" for="force-value">Force value<select class="pd-input" id="force-value"><option value="true">On / true</option><option value="false">Off / false</option></select></label>
    <label class="pd-field" for="force-seconds">Duration (seconds)<input class="pd-input" id="force-seconds" type="number" min="1" max="300" value="15"></label></div>
    <div class="pd-row"><button class="pd-button" id="force-apply" disabled>Force selected signal</button><button class="pd-button" data-variant="secondary" id="force-release" disabled>Release selected signal</button><button class="pd-button" data-variant="secondary" id="force-release-all" disabled>Release all forces</button></div>
    <p class="pd-muted">Forces are owned, audited and expired by PiDockOS. Release resumes the normal value; releasing Start does not undo a latched request. Forcing Output bank can keep simulated outputs on after Stop. Applied GPIO is shown separately from an accepted force. No physical GPIO is connected.</p>
    </section>
    <script src="assets/client.js"></script><script>
    const result=document.getElementById('result'), connection=document.getElementById('connection');
    async function run(name,args={}){
      result.textContent='Sending command…';
      try{await PiDockUi.command(name,args);result.textContent='Command accepted. Shared changes reach simulated GPIO at the next control cycle.';}
      catch(e){result.textContent=e.message==='outcome_unknown'?'Result uncertain. Check live values before retrying.':'Command rejected: '+e.message;}
    }
    document.getElementById('increment').onclick=()=>run('counter.increment');
    document.getElementById('reset').onclick=()=>run('counter.reset');
    document.getElementById('disconnect').onclick=()=>run('stream.disconnect');
    const sharedCommands={'shared-start':'demo.start','shared-stop':'demo.stop','shared-bank':'demo.bank-toggle','shared-reset':'demo.reset','force-apply':'engineering.force','force-release':'engineering.release','force-release-all':'engineering.release-all'};
    let displayedRuntime=null;
    const engineeringContext=()=>({instanceId:displayedRuntime?.instanceId,revision:displayedRuntime?.revision});
    const demos={'demo-start':'demo.start','demo-stop':'demo.stop','demo-bank':'demo.bank-toggle','demo-reset':'demo.reset'};
    Object.entries(demos).forEach(([id,name])=>document.getElementById(id).onclick=()=>run(name));
    ['shared-start','shared-stop','shared-bank','shared-reset'].forEach(id=>document.getElementById(id).onclick=()=>run(sharedCommands[id]));
    document.getElementById('force-apply').onclick=()=>run('engineering.force',{...engineeringContext(),signalId:document.getElementById('force-signal').value,value:document.getElementById('force-value').value==='true',seconds:Number(document.getElementById('force-seconds').value)});
    document.getElementById('force-release').onclick=()=>run('engineering.release',{...engineeringContext(),signalId:document.getElementById('force-signal').value});
    document.getElementById('force-release-all').onclick=()=>run('engineering.release-all',engineeringContext());
    PiDockUi.ready().then(async caps=>{
      document.getElementById('increment').disabled=!caps.commands.includes('counter.increment');
      document.getElementById('reset').disabled=!caps.commands.includes('counter.reset');
      document.getElementById('disconnect').disabled=!caps.commands.includes('stream.disconnect');
      Object.entries(demos).forEach(([id,name])=>document.getElementById(id).disabled=!caps.commands.includes(name));
      Object.entries(sharedCommands).forEach(([id,name])=>document.getElementById(id).disabled=!caps.commands.includes(name));
      result.textContent=caps.commands.length?'Host grants loaded. Commands are ready.':'No command grant is configured.';
      if(!caps.streaming){connection.textContent='No stream grant';return;}
      await PiDockUi.subscribe(event=>{
        if(event.kind==='heartbeat')return;
        document.getElementById('counter').textContent=event.data.counter;
        document.getElementById('build-version').textContent='Version '+event.data.version;
        const shared=event.data.shared;
        document.getElementById('shared-demo').hidden=!shared;
        document.getElementById('local-demo').hidden=!!shared;
        if(shared){
          displayedRuntime=shared;
          document.getElementById('shared-status').textContent=shared.status;
          document.getElementById('force-count').textContent=(shared.status==='connected'?'':'Last observed · ')+shared.forceCount+(shared.forceCount===1?' force':' forces');
          document.getElementById('shared-revision').textContent='Revision '+shared.revision+' · frame '+shared.sequence+' · runtime '+shared.instanceId;
          const body=document.getElementById('shared-values');body.replaceChildren();
          shared.signals.forEach(signal=>{
            const row=document.createElement('tr');
            const details=signal.force?'Forced by '+signal.force.actor+' until '+new Date(signal.force.expiresAt).toLocaleTimeString():shared.status;
            [signal.declaration.id,JSON.stringify(signal.normalValue),JSON.stringify(signal.effectiveValue),details].forEach(text=>{const cell=document.createElement('td');cell.textContent=text;row.appendChild(cell);});body.appendChild(row);
          });
          document.getElementById('applied-values').textContent=shared.outputs.length?'Last confirmed simulated GPIO batch: '+shared.outputs.map(v=>v.alias+' = '+v.commanded).join(', ')+' · '+shared.status:'No simulated GPIO batch has been confirmed.';
        }
        const d=event.data.demo;
        if(d){
          for(const side of ['normal','effective']){
            document.getElementById('run-'+side).textContent=d.runRequest?'On':'Off';
            document.getElementById('bank-'+side).textContent=d.bankEnabled?'Enabled':'Disabled';
            document.getElementById('timer-'+side).textContent=d.elapsedSeconds.toFixed(1)+' s / '+(d.done?'Done':'Waiting');
            document.getElementById('output-'+side).textContent=(side==='normal'?d.normalOutput:d.effectiveOutput)?'On':'Off';
          }
        }
        document.getElementById('ticks').textContent=event.data.ticks;
        document.getElementById('sequence').textContent=event.sequence;
        document.getElementById('freshness').textContent='Observed '+new Date(event.observedAt).toLocaleTimeString();
      },status=>{
        connection.textContent=status;connection.dataset.status=status==='connected'?'healthy':status==='revoked'?'unavailable':'stale';
        if(status!=='connected'&&displayedRuntime){
          document.getElementById('shared-status').textContent=status+' · readings unconfirmed';
          document.getElementById('force-count').textContent='Unconfirmed · last observed '+displayedRuntime.forceCount+' forces';
          document.querySelectorAll('#shared-values tr').forEach(row=>{if(row.lastElementChild)row.lastElementChild.textContent='Last observed · '+status;});
          document.getElementById('applied-values').textContent='GPIO readings are unconfirmed. Use the host lifecycle panel for authoritative resource status.';
        }
      });
    }).catch(()=>{result.textContent='Host bridge unavailable.';connection.textContent='Disconnected';});
    </script></body></html>
    """, "text/html"));
app.Run();
