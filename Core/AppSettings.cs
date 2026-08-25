using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backtrack;

/// <summary>
/// Theme is now a free-form string Id (a theme file's name, see
/// ThemeManager), not the old fixed AppTheme enum -- an open-ended,
/// drop-a-file set of themes can't be represented by a fixed enum. An
/// existing settings.json from before this change stored the OLD enum's
/// raw numeric ordinal instead of a string, which a plain string property
/// would fail to deserialize entirely -- and AppSettings.Load's own
/// catch-all around the WHOLE file would then silently reset EVERY other
/// setting (hotkey, clips folder, OBS pairing, ...) back to defaults too,
/// not just the theme. This converter accepts either shape: a JSON number
/// is mapped through the old enum's own historical append order
/// (Dark/Light/Yami/Amoled/YamiAcri -- never reordered, per that enum's
/// own comment while it still existed) to the matching theme Id string; a
/// JSON string (the new, ongoing format) passes through unchanged.
/// </summary>
public sealed class ThemeIdConverter : JsonConverter<string>
{
    private static readonly string[] LegacyEnumOrder = { "Dark", "Light", "Yami", "Amoled", "YamiAcri" };

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            int ordinal = reader.GetInt32();
            return ordinal >= 0 && ordinal < LegacyEnumOrder.Length ? LegacyEnumOrder[ordinal] : "Dark";
        }
        return reader.GetString() ?? "Dark";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

public sealed class AppSettings
{
    public bool LaunchWithWindows { get; set; }

    // Where clips live -- can be a local folder or a UNC network path
    // (\\STREAM-PC\Clips) when OBS runs on a different machine than this overlay.
    public string ClipsFolder { get; set; } = DefaultClipsFolder;

    private static string DefaultClipsFolder => Path.Combine(
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

    // Global hotkey to cancel in-progress recording and discard the recorded file.
    public int CancelRecordHotkeyModifiers { get; set; } = 0;
    public int CancelRecordHotkeyVirtualKey { get; set; } = 0;

    public bool ShowDisclaimer { get; set; } = true;

    // Mirrors the tray icon's own "Hide Status Overlay"/"Show Status Overlay"
    // menu item -- either toggling this in Settings or from the tray flips
    // the same underlying window and this same setting, so both stay in
    // sync and the choice persists across restarts (the tray toggle alone
    // used to be runtime-only, resetting to visible on every launch).
    public bool ShowStatusIndicator { get; set; } = true;

    // Defaults match the status indicator's original hardcoded shape/position
    // (horizontal strip, top-right) so upgrading doesn't move it. See the
    // enums' own comments (StatusOverlay.xaml.cs) for what each value means.
    public StatusIndicatorOrientation StatusIndicatorOrientation { get; set; } = StatusIndicatorOrientation.Horizontal;
    public StatusIndicatorLocation StatusIndicatorLocation { get; set; } = StatusIndicatorLocation.TopRight;

    // Both off by default (auto-update stays on unless explicitly turned off).
    // Only gate the automatic startup check in CheckForUpdatesAsync -- the
    // "Check now" button always still works regardless of either, same as
    // Windows Update's own "notify but don't auto-install" pattern still
    // lets you manually check/install; these two are about NOT wanting it
    // to happen unattended, not about losing the ability to trigger it.
    public bool DisableBacktrackAutoUpdate { get; set; }
    public bool DisablePluginAutoUpdate { get; set; }

    // Audio chimes played when recordings or clips are saved (opt-out, enabled by default).
    public bool DisableAudioCues { get; set; }

    // Volume for audio chimes (0 to 100, default 100).
    public int AudioCueVolume { get; set; } = 100;

    // See ThemeIdConverter's own comment for why this needs a custom
    // converter: an existing settings.json's stored value predates this
    // being a string at all.
    [JsonConverter(typeof(ThemeIdConverter))]
    public string Theme { get; set; } = "Dark";

    // Off by default -- see MainWindow's ShowScreen/ToggleVisible/CloseOverlay
    // for the AllowsTransparency="True" experiment these gate. That change
    // enabled a genuine screen-transition fade/scale and a real window-level
    // open/close fade, but on a layered window ANY animation means a full
    // software re-composite of the whole window on every frame, which reads
    // as smooth on some setups and janky/frame-dropped on others depending
    // on hardware, drivers, and what's on screen underneath (a game with its
    // own heavy GPU load in particular). Instant show/hide is the safe,
    // guaranteed-smooth-because-there's-nothing-to-drop-frames-on default;
    // this is the escape hatch for turning the animated version back on.
    public bool EnableAnimations { get; set; } = false;

    // Off by default -- see AppLog's own comment on why this exists (a real
    // debugging session repeatedly needed hand-added temporary file logging
    // because AppLog's ring buffer is in-memory only, gone on restart).
    public bool DiagnosticLogEnabled { get; set; } = false;

    // Makes logged/shown errors include the full exception (stack trace and
    // all) instead of just its Message -- only meaningfully useful alongside
    // DiagnosticLogEnabled above, since that's where the extra detail
    // actually ends up persisted anywhere. Also the sole authority for
    // UpdateService.IsDevBuild (see its own comment) -- this is now the MAIN
    // way to declare a dev build, not an add-on to a path guess.
    public bool DeveloperModeEnabled { get; set; } = false;

    // Set true the one time MainWindow.LoadSettingsUi auto-suggests
    // DeveloperModeEnabled based on UpdateService.IsRunningFromDevLocation,
    // so that suggestion only ever happens once -- after that, the toggle is
    // fully user-controlled, including turning it back off in a location
    // that would still trip the auto-suggestion.
    public bool DeveloperModeAutoSuggested { get; set; } = false;

    // RenderOptions.ProcessRenderMode -- applied once at startup (App.xaml.cs),
    // before any window is created, since WPF's rendering pipeline isn't
    // something that can be flipped cleanly mid-session. A troubleshooting
    // escape hatch for actual visual corruption/glitches in Backtrack's own
    // overlay on unusual GPU/driver combos, same category of issue this
    // repo's CLAUDE.md already documents extensively for the layered-window
    // rendering path -- not a capture/recording setting (OBS owns that
    // entirely; Backtrack has no capture pipeline of its own).
    public bool DisableHardwareAcceleration { get; set; } = false;

    // Off by default -- a floating draggable window is a bigger ask on
    // someone's screen than anything else this app adds unprompted. Null
    // X/Y means "never been positioned yet"; RecentClipsOverlay.Show picks a
    // sensible default (bottom-right of the active display) the first time,
    // then MainWindow persists wherever it actually gets dragged to via
    // RecentClipsOverlay.PositionChanged.
    public bool ShowRecentClipsOverlay { get; set; } = false;
    public double? RecentClipsOverlayX { get; set; }
    public double? RecentClipsOverlayY { get; set; }

    // Which monitor the overlay and all its auxiliary windows appear on --
    // Win32's own per-monitor device name (e.g. "\\.\DISPLAY1"), not an index,
    // since indices can silently renumber when a monitor is plugged/unplugged
    // but a still-connected monitor's device name doesn't change. Null/empty
    // means "whichever one Windows currently calls primary."
    public string? DisplayDeviceName { get; set; }

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

    // Attempted exactly once, ever -- see Interop/FirewallRules.cs. Set after the
    // first try regardless of outcome (elevation approved, denied, or the netsh
    // calls themselves failed), not just on success, so a user who dismisses the
    // one UAC prompt doesn't get re-prompted on every subsequent launch.
    public bool FirewallRulesAttempted { get; set; }

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

    // Sha256 digest (preferred, when GitHub provides one -- see ReleaseInfo.Digest)
    // and updated_at timestamp (fallback for the rare asset without a digest) of
    // the release ASSET, not just its version tag, that Backtrack last actually
    // applied for itself and each companion plugin -- lets UpdateService catch a
    // same-version-tag re-upload (this project's own release workflow sometimes
    // reuses the version number for a small fix) that plain version-number
    // comparison would otherwise miss entirely. Both null until the first check
    // after this tracking was added; see
    // CheckAndApplyPluginUpdateAsync/CheckAndApplySelfUpdateAsync for how that
    // gets seeded without forcing an unnecessary reinstall.
    public DateTimeOffset? LastAppliedBacktrackReleaseAt { get; set; }
    public DateTimeOffset? LastAppliedReplaySliderReleaseAt { get; set; }
    public DateTimeOffset? LastAppliedSourceRecordReleaseAt { get; set; }
    public string? LastAppliedBacktrackDigest { get; set; }
    public string? LastAppliedReplaySliderDigest { get; set; }
    public string? LastAppliedSourceRecordDigest { get; set; }

    // Settings > Clips > Storage limit / Auto-delete old clips. StorageLimitEnabled
    // false means no limit at all, regardless of StorageLimitGb's stored value --
    // a hard "stop letting you make new clips" gate once ClipsFolder's total size
    // reaches the limit, not an auto-cleanup (that's the separate setting below).
    // Neither one deletes anything by itself; see TryBlockForStorageLimit /
    // RunAutoDeleteOldClips in MainWindow.xaml.cs.
    public bool StorageLimitEnabled { get; set; }
    public double StorageLimitGb { get; set; } = 10;
    public bool AutoDeleteOldClipsEnabled { get; set; }
    public int AutoDeleteOldClipsAfterDays { get; set; } = 30;

    // Bottom-right corner log window (see OverlayLogWindow). "Obs" mirrors
    // OBS's own status-bar-style warnings (encoding overload, saves) one line
    // at a time; "Backtrack" shows a scrollable window into AppLog instead.
    public bool OverlayLogEnabled { get; set; } = true;
    public string OverlayLogMode { get; set; } = "Obs";

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

    // Local-only display name override for a buffer/recording-source row,
    // double-tapped in Settings -- keyed by the row's real OBS-reported Label
    // (same stability reasoning as HiddenBufferLabels just above: Key isn't
    // stable across an OBS restart, Label is). Purely cosmetic on Backtrack's
    // end; every OBS-facing call still uses the real Label/SourceName/
    // FilterName throughout, never this override. A buffer and a recording
    // source backed by the SAME Source Record filter share the exact same
    // Label string (both obs-replay-slider docks derive it from one shared
    // FilterRowLabel helper), so keying by Label naturally links renaming
    // across both lists instead of needing separate tracking for each.
    public Dictionary<string, string> LocalRowNameOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Settings > Player > "Default audio track". 0 = automatic (whichever
    // track LibVLC/the clip lists first, the old unconditional behavior).
    // 1-6 = OBS's own fixed Track 1-6 output numbering -- a clip's audio
    // tracks don't come back from LibVLC pre-labeled with which OBS track
    // number they actually are, only in whatever order the file lists them
    // (usually the same order, but not something to rely on), so this is
    // matched positionally (Nth audio track in the file = Track N) the same
    // way LoadAudioTracks' own "Track {i+1}" fallback naming already
    // assumes. Exists because which OBS track actually carries real audio
    // depends entirely on this user's own Advanced Audio Properties routing
    // (e.g. desktop audio on Track 2, mic on Track 1) -- defaulting to
    // "whichever came first" can default to a genuinely silent track for a
    public int DefaultPlayerAudioTrackIndex { get; set; }

    public HashSet<string> StarredClips { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<double>> ClipMarkers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int BookmarkHotkeyModifiers { get; set; } = 0x2 | 0x4; // Control | Shift
    public int BookmarkHotkeyVirtualKey { get; set; } = 'B';

    public string GallerySortMode { get; set; } = "DateDesc";
    public bool GalleryStarredOnly { get; set; } = false;

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
                {
                    loaded.ClipsFolder = ResolveClipsFolderForThisMachine(loaded.ClipsFolder);
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings file -- fall back to defaults rather than crash.
        }
        return new AppSettings();
    }

    // settings.json lives under %AppData%, so it doesn't normally follow anyone
    // between machines -- but it's a plain file, and people do sometimes copy it
    // over by hand to carry pairing/hotkey/etc config to a second PC quickly.
    // If it carries ClipsFolder along too, the copied value bakes in the
    // ORIGINAL machine's Windows username (MyVideos resolves through the
    // account name), which is very likely wrong on the new machine -- e.g.
    // "...\Administrator\Videos\Backtrack" landing on a PC whose actual account
    // isn't named Administrator. Only kicks in when the loaded path (a) doesn't
    // exist on this machine and (b) matches our own generated default's shape
    // for a different user, so a deliberately chosen custom folder -- or a
    // legitimate UNC path to a stream PC's share, see the property doc above --
    // is never touched.
    private static string ResolveClipsFolderForThisMachine(string loadedClipsFolder)
    {
        if (string.IsNullOrWhiteSpace(loadedClipsFolder) || Directory.Exists(loadedClipsFolder))
            return loadedClipsFolder;

        string suffix = Path.Combine("Videos", "Backtrack");
        return loadedClipsFolder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? DefaultClipsFolder
            : loadedClipsFolder;
    }

    public void Save()
    {
        string dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }

    /// <summary>
    /// Settings > Destructive > Clear settings cache. Deletes settings.json
    /// itself (not just an in-memory reset) so the NEXT Load() -- after the
    /// caller restarts the app, since a live reset would mean manually
    /// re-syncing dozens of already-bound Settings UI controls by hand -- has
    /// nothing to read and falls through to a plain `new AppSettings()`,
    /// same as a genuinely first-ever run.
    /// </summary>
    public static void ClearSavedFile()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* best effort -- e.g. file briefly locked; caller still restarts either way */ }
    }
}
