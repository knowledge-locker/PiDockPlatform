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
    public static string Percent(double? value) => value is null ? "Unavailable" : $"{value:0.#}%";
}
