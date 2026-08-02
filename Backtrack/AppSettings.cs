using System;
using System.IO;
using System.Text.Json;

namespace Backtrack;

public sealed class AppSettings
{
    public bool LaunchWithWindows { get; set; }

    // Where clips live -- can be a local folder or a UNC network path
    // (\\STREAM-PC\Clips) when OBS runs on a different machine than this overlay.
    public string ClipsFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Backtrack");

    // Where "Copy to this PC" drops a local copy of a clip that's actually sitting
    // on a remote stream PC's share.
    public string LocalCopyFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Backtrack (copied)");

    // OBS connection target: defaults to "OBS is on this PC" (auto-detect the
    // password from this machine's own obs-websocket config). Set ObsIsRemote
    // to talk to OBS on a different PC instead (e.g. a dedicated stream PC) --
    // that machine's own config isn't reachable, so host/port/password are manual.
    public bool ObsIsRemote { get; set; }
    public string ObsHost { get; set; } = "127.0.0.1";
    public int ObsPort { get; set; } = 4455;
    public string ObsRemotePassword { get; set; } = "";

    // Stored as raw values (not the GlobalHotkey.Modifiers enum) so this class
    // doesn't need to reference the Interop namespace. Defaults to Ctrl+Alt+G.
    public int HotkeyModifiers { get; set; } = 0x1 | 0x2; // Alt | Control
    public int HotkeyVirtualKey { get; set; } = 'G';

    public bool ShowDisclaimer { get; set; } = true;

    // A stable identity for this install, shown to (and shown by) other Backtrack
    // instances during pairing -- generated once, not tied to the Windows machine
    // name alone since that can change.
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();

    // Host side: broadcasts this machine as pairable and answers pairing requests.
    public bool ShareClipsEnabled { get; set; }

    // Client side: the one peer this install is currently paired with. Only one at
    // a time -- this mirrors the existing single "OBS is on a different PC" model
    // rather than supporting a whole paired-device list, since that's the actual
    // use case (two of your own PCs), not a general file-sharing platform.
    public string? PairedPeerDeviceId { get; set; }
    public string? PairedPeerName { get; set; }
    public string? PairedPeerHost { get; set; }
    public int PairedPeerPort { get; set; }
    public string? PairedPeerSecret { get; set; }

    // Host side: who was actually approved to pull from this PC's own share.
    // Kept separate from PairedPeer* above since a single install could in theory
    // both share its own clips and pull from someone else's at once.
    public string? AuthorizedClientDeviceId { get; set; }
    public string? AuthorizedClientName { get; set; }
    public string? AuthorizedClientSecret { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Backtrack", "settings.json");

    // The app used to be called Capture Center, with settings stored under that
    // folder name. A one-time copy on first run under the new name means the
    // rename doesn't silently reset the hotkey, clips folder, OBS connection,
    // etc. back to defaults for anyone upgrading from that version.
    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaptureCenter", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath) && File.Exists(LegacyFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(LegacyFilePath, FilePath);
            }

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
