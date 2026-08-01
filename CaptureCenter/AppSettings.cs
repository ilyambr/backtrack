using System;
using System.IO;
using System.Text.Json;

namespace CaptureCenter;

public sealed class AppSettings
{
    public bool LaunchWithWindows { get; set; }

    // Where clips live -- can be a local folder or a UNC network path
    // (\\STREAM-PC\Clips) when OBS runs on a different machine than this overlay.
    public string ClipsFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Capture Center");

    // Where "Copy to this PC" drops a local copy of a clip that's actually sitting
    // on a remote stream PC's share.
    public string LocalCopyFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Capture Center (copied)");

    // OBS connection target: defaults to "OBS is on this PC" (auto-detect the
    // password from this machine's own obs-websocket config). Set ObsIsRemote
    // to talk to OBS on a different PC instead (e.g. a dedicated stream PC) --
    // that machine's own config isn't reachable, so host/port/password are manual.
    public bool ObsIsRemote { get; set; }
    public string ObsHost { get; set; } = "127.0.0.1";
    public int ObsPort { get; set; } = 4455;
    public string ObsRemotePassword { get; set; } = "";

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
