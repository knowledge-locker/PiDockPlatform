using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Text.Json;
using PiDock.Core;

namespace PiDock.Appliance.Broker;

public sealed partial class DockerOperations
{
    public ApplicationHealthSupport HealthSupport() => new(1,store is not null && delivery is not null,"pidock-heartbeat-v1",
        ApplicationHealthRules.ReportPath,ApplicationHealthRules.ProbePath,ApplicationHealthRules.CheckSeconds,ApplicationHealthRules.MaximumReportBytes,
        ["Apps must publish critical-work liveness and readiness every five seconds using managed bootstrap identity.",
         "App-reported health is not independent verification of hardware safety or work correctness.",
         "Health restarts require both app health restart policy and automatic recovery; readiness loss after initial ready does not restart a live app.",
         "Health state does not automatically gate UI hosting or commands; dependency conditions are separately advertised by app orchestration."],ApplicationHealthAssets.SourcePath,ApplicationHealthAssets.ReaderSha256);

    public async Task CheckHealth(CancellationToken ct)
    {
        if (!HealthSupport().Enabled) return;
        await deploymentLock.WaitAsync(ct);
        try
        {
            foreach (var app in (await List(ct)).Take(16))
            {
                if (app.State != "running" || app.Labels?.GetValueOrDefault(Label + ".resources") != "v1") continue;
                ManagedDeployment? record;
                try { record = store?.Find(app.Id); } catch (IOException) { continue; }
                if (record?.Manifest.Resources?.Health is not { } policy || Incomplete(record)) continue;
                if (record.Health?.CheckedAt is { } last && Now - last < TimeSpan.FromSeconds(ApplicationHealthRules.CheckSeconds)) continue;
                var (report, error) = await ReadHealth(app.Id,ct);
                var next = EvaluateHealth(record,policy,report,error);
                store!.Save(next);
            }
        }
        finally { deploymentLock.Release(); }
    }
    private async Task<(ApplicationHealthReport? Report,string? Error)> ReadHealth(string id,CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var created = await engine.PostAsJsonAsync($"{Api}/containers/{id}/exec",new {
                AttachStdout = true, AttachStderr = false, Tty = false, Privileged = false, User = "1654:1654",
                Cmd = new[] { ApplicationHealthRules.ProbePath }
            },deadline.Token);
            if (!created.IsSuccessStatusCode) return (null,"check_unavailable");
            var metadata = await created.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            var execId = metadata.GetProperty("Id").GetString();
            if (execId is not { Length: 64 } || !execId.All(char.IsAsciiHexDigit)) return (null,"check_unavailable");
            using var request = new HttpRequestMessage(HttpMethod.Post,$"{Api}/exec/{execId}/start") { Content = JsonContent.Create(new { Detach = false,Tty = false }) };
            using var response = await engine.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            if (!response.IsSuccessStatusCode) return (null,"check_unavailable");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            byte[]? bytes;
            try { bytes = await ReadHealthFrames(stream,deadline.Token); }
            catch (InvalidDataException) { return (null,"report_invalid"); }
            using var inspected = await engine.GetAsync($"{Api}/exec/{execId}/json",deadline.Token);
            if (!inspected.IsSuccessStatusCode) return (null,"check_unavailable");
            var state = await inspected.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            if (state.GetProperty("Running").GetBoolean()) return (null,"check_unavailable");
            int exitCode = state.GetProperty("ExitCode").GetInt32();
            if (exitCode != 0) return (null,exitCode == 2 ? "report_missing" : exitCode == 3 ? "report_invalid" : "probe_unavailable");
            if (bytes.Length == 0) return (null,"report_invalid");
            try
            {
                var report = JsonSerializer.Deserialize<ApplicationHealthReport>(bytes,new JsonSerializerOptions(StateFiles.Json){MaxDepth=4});
                return report is null ? (null,"report_invalid") : (report,null);
            }
            catch (JsonException) { return (null,"report_invalid"); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null,"check_unavailable"); }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return (null,"check_unavailable"); }
    }
    private static async Task<byte[]> ReadHealthFrames(Stream stream,CancellationToken ct)
    {
        using var output = new MemoryStream(); var header = new byte[8]; int total = 0,frames = 0;
        while (await stream.ReadAsync(header.AsMemory(0,1),ct) != 0)
        {
            try { await stream.ReadExactlyAsync(header.AsMemory(1),ct); }
            catch (EndOfStreamException) { throw new InvalidDataException("Invalid health framing."); }
            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            if (++frames > 8 || header[0] is not (1 or 2) || header[1] != 0 || header[2] != 0 || header[3] != 0 || length is < 0 or > 1024 || (total += length) > 2048 ||
                header[0] == 1 && output.Length + length > ApplicationHealthRules.MaximumReportBytes) throw new InvalidDataException("Health framing exceeds limits.");
            var data = new byte[length];
            try { await stream.ReadExactlyAsync(data,ct); }
            catch (EndOfStreamException) { throw new InvalidDataException("Invalid health framing."); }
            if (header[0] == 1) await output.WriteAsync(data,ct); // Never forward stderr or parse it as health.
        }
        return output.ToArray();
    }
    private ManagedDeployment EvaluateHealth(ManagedDeployment record,ApplicationHealthPolicy policy,ApplicationHealthReport? report,string? error)
    {
        var now = Now;
        bool fresh = report is not null && ApplicationHealthRules.Valid(report,record.LastDeliveryId ?? Guid.Empty,now,policy.FreshnessSeconds) && report.Sequence >= record.HealthSequence &&
            (report.Sequence != record.HealthSequence || record.LastHealthReport is null || report == record.LastHealthReport);
        bool grace = record.FirstReadyAt is null && now - record.LastDeliveredAt < TimeSpan.FromSeconds(policy.StartupSeconds);
        string state, reason; bool failure; bool? live;
        if (error == "check_unavailable") { state="unknown";reason=error;failure=false;live=null; }
        else if (!fresh)
        {
            reason=error ?? "report_stale_or_invalid";state=grace ? "starting" : "unhealthy";failure=!grace;live=null;
        }
        else if (!report!.Live) { state="unhealthy";reason="liveness_failed";failure=true;live=false; }
        else if (!report.Ready && record.FirstReadyAt is null && !grace) { state="unhealthy";reason="readiness_timeout";failure=true;live=true; }
        else { state=report.Ready ? "ready" : grace ? "starting" : "not_ready";reason=report.Ready ? "ready" : "not_ready";failure=false;live=true; }
        int failures = failure ? Math.Min(10,(record.Health?.Failures ?? 0)+1) : 0;
        bool ready = fresh && report!.Live && report.Ready && state == "ready";
        var status = new ApplicationHealthStatus(state,ready,live,now,fresh ? report!.ReportedAt : null,failures,reason,failure && failures >= policy.FailureThreshold && policy.RestartOnFailure);
        bool continuous = record.Health?.Ready == true && record.Health.CheckedAt >= now.AddSeconds(-policy.FreshnessSeconds) && record.Health.ReportedAt >= now.AddSeconds(-policy.FreshnessSeconds);
        // Probe observations invalidate recovery snapshots; stability requires fresh ready reports.
        return record with { Health=status,HealthSequence=fresh ? report!.Sequence : record.HealthSequence,
            FirstReadyAt=ready ? record.FirstReadyAt ?? now : record.FirstReadyAt,LastHealthReport=fresh ? report : record.LastHealthReport,
            StableSince=ready ? continuous ? record.StableSince ?? now : now : null,
            Outcome=ready && record.Outcome == "waiting_for_health" ? "observing" : !ready && record.Outcome is "stable" or "observing" ? "waiting_for_health" : record.Outcome,Revision=checked(record.Revision+1) };
    }
    private ApplicationHealthStatus ProjectHealth(ManagedDeployment record,string state)
    {
        if (record.Manifest.Resources?.Health is not { } policy) return new("not_configured",false,null,null,null,0,"not_configured",false);
        if (state != "running") return new("stopped",false,null,record.Health?.CheckedAt,null,record.Health?.Failures ?? 0,"not_running",false);
        if (Incomplete(record)) return new("starting",false,null,null,null,0,"bootstrap_incomplete",false);
        if (record.Health is not { } health) return new("unknown",false,null,null,null,0,"awaiting_report",false);
        if (health.CheckedAt is null || Now - health.CheckedAt > TimeSpan.FromSeconds(policy.FreshnessSeconds) ||
            health.Ready && (health.ReportedAt is null || Now - health.ReportedAt > TimeSpan.FromSeconds(policy.FreshnessSeconds)))
            return health with { State="unknown",Ready=false,Live=null,RecoveryRequired=false,Reason="observation_expired" };
        return health;
    }
}
