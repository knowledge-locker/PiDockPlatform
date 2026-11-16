using System.Diagnostics;
using System.Text.Json;
using PiDock.Core;

internal sealed class WorkflowReview(string root,string privateDirectory,int port,int appPort,ApplicationCredentialFile bridge,string brokerSocket) : IAsyncDisposable
{
    private readonly string config=Path.Combine(privateDirectory,"host-settings.json");
    private Process? process;
    private Task<string>? output,error;
    public string Origin=>$"http://127.0.0.1:{port}";
    public void Configure(bool enabled,bool includeCredential=true)
    {
        var commands=new[]{"counter.increment","counter.reset","stream.disconnect","demo.start","demo.stop","demo.bank-toggle","demo.reset","engineering.force","engineering.release","engineering.release-all"};
        StateFiles.WriteAtomic(config,JsonSerializer.Serialize(new{PiDock=new{ApplicationUi=new{Registrations=new[]{new{
            Contribution=new{SchemaVersion=1,AppId="ui-sample",Id="main",Title="Docker workflow sample",EntryPath="index.html",RequiredUiVersion="1.0",RequiredStyleVersion="1.0"},
            ServiceOrigin=$"http://127.0.0.1:{appPort}/",Enabled=enabled,PublicAssets=new[]{"assets/components.css","assets/tokens.css","assets/client.js"},
            Bridge=new{Version="1.0",CredentialId="sample",CommandsPath="commands",Commands=commands,StreamPath="events"}}},
            Credentials=new{sample=new{Token=includeCredential?bridge.Token:null,ExpiresAt=includeCredential?bridge.ExpiresAt:(DateTimeOffset?)null}}}}},StateFiles.Json));
    }
    public void Start()
    {
        Configure(true);
        var start=new ProcessStartInfo("dotnet"){WorkingDirectory=Path.Combine(root,"tools/PiDock.UiReview"),RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        start.ArgumentList.Add(Path.Combine(start.WorkingDirectory,"bin/Release/net10.0/PiDock.UiReview.dll"));
        start.Environment["ASPNETCORE_ENVIRONMENT"]="Development";
        start.Environment["Review__Port"]=port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Review__RegistrationFile"]=config;
        start.Environment["Review__BrokerSocket"]=brokerSocket;
        start.Environment["Logging__LogLevel__Default"]="Warning";
        process=Process.Start(start)??throw new IOException("Cannot start the isolated PiDockOS shell.");
        output=process.StandardOutput.ReadToEndAsync(); error=process.StandardError.ReadToEndAsync();
    }
    public async Task Checkpoint(string phase)
    {
        if(process is null || process.HasExited) throw new IOException("The PiDockOS review shell stopped unexpectedly.");
        Console.WriteLine($"BROWSER CHECKPOINT {phase}: {Origin}/apps/ui-sample/main");
        Console.WriteLine("Inspect this phase in PiDockOS, then press Enter to continue (five-minute deadline).");
        var line=await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(5));
        if(line is null) throw new IOException("Interactive browser review is required for workflow evidence.");
    }
    public async ValueTask DisposeAsync()
    {
        if(process is null)return;
        if(!process.HasExited)
        {
            var stop=new ProcessStartInfo("/bin/kill"){UseShellExecute=false};stop.ArgumentList.Add("-TERM");stop.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var signal=Process.Start(stop); if(signal is not null)await signal.WaitForExitAsync();
            try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));}catch(TimeoutException){process.Kill(true);await process.WaitForExitAsync();}
        }
        if(output is not null)await output;if(error is not null)await error;
        process.Dispose(); process=null;
    }
}
