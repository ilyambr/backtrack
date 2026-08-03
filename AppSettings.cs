using System;
using System.Collections.Generic;
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

    // RAM disk for OBS's replay buffer output (via ImDisk): mounted on Backtrack
    // startup and unmounted on exit, not left mounted independent of this app --
    // opt-in and off by default since it installs a kernel driver the first time
    // it's turned on. Size starts at a conservative flat default rather than
    // whatever a full buffer-duration estimate would compute to (that number can
    // get large -- see Obs/ReplayBufferSizing.cs), and is meant to be raised by
    // hand once the feature is confirmed working.
    public bool RamDiskEnabled { get; set; }
    public char RamDiskDriveLetter { get; set; } = 'R';
    public int RamDiskSizeMb { get; set; } = 2048;

    // Shown once, the first time the RAM disk is actually mounted -- OBS has no
    // API to set the Replay Buffer output path, so this is a one-time manual
    // step in OBS's own Settings > Output > Replay Buffer, not something
    // Backtrack can do for the user on every launch.
    public bool RamDiskInstructionShown { get; set; }

    // Pushed to every Source Record filter's own replay_duration via the
    // replay-slider bridge (set_buffer_duration) -- this is the buffer that
    // actually gets flushed to disk (the RAM disk, if enabled) on every save,
    // not the trimmed clip length. 30 min default; UI allows up to 60 min
    // with a RAM allocation warning since a full flush at that length can
    // exceed a modest RAM disk's size.
    public int ReplayBufferMinutes { get; set; } = 30;

    // Buffers hidden from the "Save which buffer?" screen -- keyed by the row's
    // Label (e.g. "Replay Buffer", "elgato - Source Record"), not its Key. A
    // row's Key is that filter object's in-memory address for this OBS session
    // (see the plugin's RefreshAll -- there's no stable UUID to use instead),
    // so it changes every OBS restart; the Label is what's actually stable
    // across restarts as long as the source/filter itself isn't renamed.
    public HashSet<string> HiddenBufferLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
