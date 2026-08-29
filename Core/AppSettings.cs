using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backtrack;

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

    public string ClipsFolder { get; set; } = DefaultClipsFolder;

    private static string DefaultClipsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Backtrack");

    public string LocalCopyFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Backtrack (copied)");

    public bool ObsIsRemote { get; set; }
    public string ObsHost { get; set; } = "127.0.0.1";
    public int ObsPort { get; set; } = 4455;
    public string ObsRemotePassword { get; set; } = "";

    public int HotkeyModifiers { get; set; } = 0x1 | 0x2;
    public int HotkeyVirtualKey { get; set; } = 'G';

    public int CancelRecordHotkeyModifiers { get; set; } = 0;
    public int CancelRecordHotkeyVirtualKey { get; set; } = 0;

    public bool ShowDisclaimer { get; set; } = true;

    public bool ShowStatusIndicator { get; set; } = true;

    public StatusIndicatorOrientation StatusIndicatorOrientation { get; set; } = StatusIndicatorOrientation.Horizontal;
    public StatusIndicatorLocation StatusIndicatorLocation { get; set; } = StatusIndicatorLocation.TopRight;

    public bool DisableBacktrackAutoUpdate { get; set; }
    public bool DisablePluginAutoUpdate { get; set; }

    public bool DisableAudioCues { get; set; }

    public int AudioCueVolume { get; set; } = 100;

    [JsonConverter(typeof(ThemeIdConverter))]
    public string Theme { get; set; } = "Dark";

    public bool EnableAnimations { get; set; } = false;

    public bool DiagnosticLogEnabled { get; set; } = false;

    public bool DeveloperModeEnabled { get; set; } = false;

    public bool DeveloperModeAutoSuggested { get; set; } = false;

    public bool DisableHardwareAcceleration { get; set; } = false;

    public bool ShowRecentClipsOverlay { get; set; } = false;
    public double? RecentClipsOverlayX { get; set; }
    public double? RecentClipsOverlayY { get; set; }

    public string? DisplayDeviceName { get; set; }

    public string DeviceId { get; set; } = Guid.NewGuid().ToString();

    public bool ShareClipsEnabled { get; set; }

    public string? PairedPeerDeviceId { get; set; }
    public string? PairedPeerName { get; set; }
    public string? PairedPeerHost { get; set; }
    public int PairedPeerPort { get; set; }
    public string? PairedPeerSecret { get; set; }

    public string? AuthorizedClientDeviceId { get; set; }
    public string? AuthorizedClientName { get; set; }
    public string? AuthorizedClientSecret { get; set; }

    public bool FirewallRulesAttempted { get; set; }

    public bool RamDiskEnabled { get; set; }
    public char RamDiskDriveLetter { get; set; } = 'R';
    public int RamDiskSizeMb { get; set; } = 2048;

    public bool RamDiskInstructionShown { get; set; }

    public DateTimeOffset? LastAppliedBacktrackReleaseAt { get; set; }
    public DateTimeOffset? LastAppliedReplaySliderReleaseAt { get; set; }
    public DateTimeOffset? LastAppliedSourceRecordReleaseAt { get; set; }
    public string? LastAppliedBacktrackDigest { get; set; }
    public string? LastAppliedReplaySliderDigest { get; set; }
    public string? LastAppliedSourceRecordDigest { get; set; }

    public bool StorageLimitEnabled { get; set; }
    public double StorageLimitGb { get; set; } = 10;
    public bool AutoDeleteOldClipsEnabled { get; set; }
    public int AutoDeleteOldClipsAfterDays { get; set; } = 30;

    public bool OverlayLogEnabled { get; set; } = true;
    public string OverlayLogMode { get; set; } = "Obs";

    public int ReplayBufferMinutes { get; set; } = 30;
    public int PreferredClipLengthSeconds { get; set; } = 60;

    public HashSet<string> HiddenBufferLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> LocalRowNameOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int DefaultPlayerAudioTrackIndex { get; set; }

    public HashSet<string> StarredClips { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<double>> ClipMarkers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int BookmarkHotkeyModifiers { get; set; } = 0x2 | 0x4;
    public int BookmarkHotkeyVirtualKey { get; set; } = 'B';

    public string GallerySortMode { get; set; } = "DateDesc";
    public bool GalleryStarredOnly { get; set; } = false;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Backtrack", "settings.json");

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
        }
        return new AppSettings();
    }

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

    public static void ClearSavedFile()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* best effort -- e.g. file briefly locked; caller still restarts either way */ }
    }
}
