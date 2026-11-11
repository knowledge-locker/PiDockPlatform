namespace PiDock.Web.Shared;

public static class Format
{
    public static string Bytes(long? value)
    {
        if (value is null) return "Unavailable";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"]; double size = value.Value; int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }
    public static string Ago(DateTimeOffset value, DateTimeOffset? now = null)
    {
        var elapsed = (now ?? DateTimeOffset.UtcNow) - value;
        if (elapsed.TotalSeconds < 5) return "Just now";
        if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s ago";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }
    public static string Uptime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "Unavailable";
        var span = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 2));
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : span.TotalHours >= 1 ? $"{span.Hours}h {span.Minutes}m" : $"{span.Minutes}m {span.Seconds}s";
    }
    public static string Percent(double? value) => value is null ? "Unavailable" : $"{value:0.#}%";
}
