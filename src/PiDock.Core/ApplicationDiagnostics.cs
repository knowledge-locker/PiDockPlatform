using System.Net.Http.Json;

namespace PiDock.Core;

public sealed record ApplicationNotificationPolicy(string AppId, OperationalPriority MaximumPriority = OperationalPriority.Warning);
public sealed record ApplicationNotification(Guid Id, string AppId, Guid AlarmId, string Condition,
    OperationalPriority Priority, string Action, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt = null, string? ReadBy = null);
public sealed record ApplicationDiagnosticsSupport(int SchemaVersion, bool LocalNotificationsEnabled,
    int MaximumUnreadPerApp, int MaximumUnreadTotal, long RejectedNotifications, string[] Limitations);

public static class ApplicationNotificationRules
{
    public const int MaximumUnreadPerApp = 32, MaximumUnreadTotal = 128, MaximumRetainedRead = 128;
    public static void Validate(ApplicationNotificationPolicy[] policies)
    {
        if (policies is not { Length: <= 16 } || policies.Any(p => p is null || !IdentityRules.IsId(p.AppId) || !Enum.IsDefined(p.MaximumPriority)) ||
            policies.Select(p => p.AppId).Distinct(StringComparer.Ordinal).Count() != policies.Length)
            throw new ArgumentException("Configure at most sixteen unique app notification priority policies.");
    }
}

// Supply the application's existing authenticated HTTPS client. No external delivery is performed.
public sealed class ApplicationNotificationsClient(HttpClient http)
{
    private const string Root = "/api/apps/v1/notifications";
    public async Task<ApplicationNotification[]> Read(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ApplicationNotification[]>(Root, StateFiles.Json, ct) ?? throw new HttpRequestException("Invalid notification response.");
    public async Task<ApplicationNotification> Acknowledge(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync(Root + "/" + id.ToString("D") + "/acknowledge", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ApplicationNotification>(StateFiles.Json, ct) ?? throw new HttpRequestException("Invalid notification receipt.");
    }
}
