using System.Text.Json;

namespace PiDock.Core;

public static class OsBuildDisplay
{
    public static string Appliance()
    {
        try
        {
            const string path="/etc/pidock/image-profile.json";
            if(File.Exists(path))
            {
                using var document=JsonDocument.Parse(File.ReadAllText(path));
                string? version=document.RootElement.GetProperty("version").GetString();
                if(version is{Length:>0 and <=64})
                    return (document.RootElement.GetProperty("development").GetBoolean()?"Development image · ":"PiDockOS · ")+version;
            }
            var release=OsUpdateRules.Installed(AppContext.BaseDirectory);
            return release is not null?"PiDockOS · "+release.Manifest.Version:"Development checkout";
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException){return "Build metadata unavailable";}
    }
}
