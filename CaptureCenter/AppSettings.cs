using System;
using System.IO;
using System.Text.Json;

namespace CaptureCenter;

public sealed class AppSettings
{
    public bool LaunchWithWindows { get; set; }
    public string ClipsFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Capture Center");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaptureCenter", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file -- fall back to defaults rather than crash.
        }
        return new AppSettings();
    }

    public void Save()
    {
        string dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}
