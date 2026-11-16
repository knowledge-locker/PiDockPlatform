using System.Diagnostics;

// Owns only a newly created isolated test service. Never selects or restarts a user's service.
internal sealed class OwnedService(string assembly,string workingDirectory,Dictionary<string,string> environment) : IAsyncDisposable
{
    private Process? process;
    private Task<string>? output,error;
    public int Id=>process?.Id??throw new InvalidOperationException("Service is not running.");
    public void Start()
    {
        if(process is not null)throw new InvalidOperationException("Stop this owned process before starting another.");
        var info=new ProcessStartInfo("dotnet"){WorkingDirectory=workingDirectory,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(assembly);
        foreach(var pair in environment)info.Environment[pair.Key]=pair.Value;
        process=Process.Start(info)??throw new IOException("Cannot start owned test service.");
        output=process.StandardOutput.ReadToEndAsync();error=process.StandardError.ReadToEndAsync();
    }
    public async Task Stop()
    {
        if(process is null)return;
        if(!process.HasExited)
        {
            var info=new ProcessStartInfo("/bin/kill"){UseShellExecute=false};info.ArgumentList.Add("-TERM");info.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var signal=Process.Start(info);if(signal is not null)await signal.WaitForExitAsync();
            try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));}
            catch(TimeoutException){process.Kill(true);await process.WaitForExitAsync();throw new IOException("Owned service exceeded graceful shutdown deadline.");}
        }
        int code=process.ExitCode;process.Dispose();process=null;
        if(output is not null)await output;if(error is not null)await error;
        if(code!=0)throw new IOException("Owned service exited unsuccessfully; private output was not forwarded.");
    }
    public async ValueTask DisposeAsync()=>await Stop();
}
