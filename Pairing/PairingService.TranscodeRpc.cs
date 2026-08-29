using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Backtrack.Core;
using Backtrack.Interop;

namespace Backtrack.Pairing;

public sealed partial class PairingService
{
    public Func<RamDiskSnapshot?>? GetRamDiskSnapshot { get; set; }
    public Func<bool, char, int, Task<(bool Success, string? Error)>>? ApplyRamDiskSnapshot { get; set; }

    private string HandleGetRamDiskSettings(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." });

        RamDiskSnapshot? snapshot = GetRamDiskSnapshot?.Invoke();
        if (snapshot is null)
            return JsonSerializer.Serialize(new { error = "RAM disk control isn't wired up on this instance." });

        return JsonSerializer.Serialize(snapshot);
    }

    private async Task<string> HandleSetRamDiskSettingsAsync(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." });

        if (ApplyRamDiskSnapshot is null)
            return JsonSerializer.Serialize(new { success = false, error = "RAM disk control isn't wired up on this instance." });

        bool enabled = request.TryGetProperty("enabled", out JsonElement e) && e.GetBoolean();
        string driveText = request.TryGetProperty("driveLetter", out JsonElement d) ? d.GetString() ?? "R" : "R";
        char driveLetter = driveText.Length > 0 ? char.ToUpperInvariant(driveText[0]) : 'R';
        int sizeMb = request.TryGetProperty("sizeMb", out JsonElement s) ? s.GetInt32() : 2048;

        (bool success, string? error) = await ApplyRamDiskSnapshot(enabled, driveLetter, sizeMb);
        return JsonSerializer.Serialize(new { success, error });
    }

    public Func<string, double, double, bool, Task<(bool Success, string? Error, string? NewFileName, long Size)>>? TrimClipForRemote { get; set; }
    public Func<string, double, Task<(bool Success, string? Error, string? NewFileName, long Size)>>? CompressClipForRemote { get; set; }

    private async Task<string> HandleTrimClipAsync(JsonElement request)
    {
        string relativePath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        double startSeconds = request.TryGetProperty("startSeconds", out JsonElement s) ? s.GetDouble() : -1;
        double endSeconds = request.TryGetProperty("endSeconds", out JsonElement en) ? en.GetDouble() : -1;
        bool replaceOriginal = request.TryGetProperty("replaceOriginal", out JsonElement r) && r.GetBoolean();
        AppLog.Write($"[trim_clip] request received: path='{relativePath}' start={startSeconds:0.###}s end={endSeconds:0.###}s replace={replaceOriginal}");

        if (!IsAuthorizedClient(request))
        {
            AppLog.Write("[trim_clip] rejected: not authorized (bad/missing pairing secret)");
            return JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." });
        }

        if (!TryResolveGalleryPath(relativePath, out string fullPath, out string? pathError) ||
            !GalleryFormats.VideoExtensions.Contains(Path.GetExtension(fullPath).ToLowerInvariant()))
        {
            AppLog.Write($"[trim_clip] rejected: bad path -- {pathError ?? "not a clip file"} (resolved to '{fullPath}')");
            return JsonSerializer.Serialize(new { success = false, error = pathError ?? "Not a clip file." });
        }
        if (startSeconds < 0 || endSeconds <= startSeconds)
        {
            AppLog.Write($"[trim_clip] rejected: invalid range (start={startSeconds:0.###}s end={endSeconds:0.###}s)");
            return JsonSerializer.Serialize(new { success = false, error = "Invalid trim range." });
        }
        if (!File.Exists(fullPath))
        {
            AppLog.Write($"[trim_clip] rejected: '{fullPath}' doesn't exist on this PC");
            return JsonSerializer.Serialize(new { success = false, error = "That clip doesn't exist on this PC anymore." });
        }
        if (TrimClipForRemote is null)
        {
            AppLog.Write("[trim_clip] rejected: TrimClipForRemote delegate is null -- this PC's own player/LibVLC isn't ready");
            return JsonSerializer.Serialize(new { success = false, error = "This PC's Backtrack can't trim clips right now (player not ready)." });
        }

        AppLog.Write($"[trim_clip] resolved to '{fullPath}' -- starting export");
        (bool success, string? error, string? newFileName, long fileSize) = await TrimClipForRemote(fullPath, startSeconds, endSeconds, replaceOriginal);
        if (!success || newFileName is null)
        {
            AppLog.Write($"[trim_clip] TrimClipForRemote FAILED: {error ?? "(no error message)"}");
            return JsonSerializer.Serialize(new { success = false, error = error ?? "Trim failed." });
        }

        string newRelativePath = WithNewFileName(relativePath, newFileName);
        AppLog.Write($"[trim_clip] success -- new relative path '{newRelativePath}', size {fileSize} bytes");
        return JsonSerializer.Serialize(new { success = true, path = newRelativePath, size = fileSize });
    }

    private async Task<string> HandleCompressClipAsync(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
        {
            AppLog.Write("[compress_clip] rejected: client is not authorized");
            return JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." });
        }

        string relativePath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        double targetMb = request.TryGetProperty("targetMb", out JsonElement mb) ? mb.GetDouble() : 25.0;

        AppLog.Write($"[compress_clip] incoming: relativePath='{relativePath}' targetMb={targetMb}");

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            AppLog.Write("[compress_clip] rejected: missing or empty path");
            return JsonSerializer.Serialize(new { success = false, error = "Missing clip path." });
        }

        if (!TryResolveGalleryPath(relativePath, out string fullPath, out string? pathError) ||
            !GalleryFormats.VideoExtensions.Contains(Path.GetExtension(fullPath).ToLowerInvariant()))
        {
            AppLog.Write($"[compress_clip] rejected: bad path -- {pathError ?? "not a clip file"} (resolved to '{fullPath}')");
            return JsonSerializer.Serialize(new { success = false, error = pathError ?? "Not a clip file." });
        }

        if (!File.Exists(fullPath))
        {
            AppLog.Write($"[compress_clip] rejected: '{fullPath}' doesn't exist on this PC");
            return JsonSerializer.Serialize(new { success = false, error = "That clip doesn't exist on this PC anymore." });
        }

        if (CompressClipForRemote is null)
        {
            AppLog.Write("[compress_clip] rejected: CompressClipForRemote delegate is null");
            return JsonSerializer.Serialize(new { success = false, error = "This PC's Backtrack can't compress clips right now." });
        }

        AppLog.Write($"[compress_clip] resolved to '{fullPath}' -- starting compression");
        (bool success, string? error, string? newFileName, long fileSize) = await CompressClipForRemote(fullPath, targetMb);
        if (!success || newFileName is null)
        {
            AppLog.Write($"[compress_clip] CompressClipForRemote FAILED: {error ?? "(no error message)"}");
            return JsonSerializer.Serialize(new { success = false, error = error ?? "Compression failed." });
        }

        string newRelativePath = WithNewFileName(relativePath, newFileName);
        AppLog.Write($"[compress_clip] success -- new relative path '{newRelativePath}', size {fileSize} bytes");
        return JsonSerializer.Serialize(new { success = true, path = newRelativePath, size = fileSize });
    }

    public Func<Task<PluginVersionsSnapshot>>? CheckAndApplyPluginUpdatesRemotely { get; set; }

    private async Task<string> HandleCheckPluginUpdatesAsync(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." });

        if (CheckAndApplyPluginUpdatesRemotely is null)
            return JsonSerializer.Serialize(new { error = "Plugin update control isn't wired up on this instance." });

        PluginVersionsSnapshot snapshot = await CheckAndApplyPluginUpdatesRemotely();
        return JsonSerializer.Serialize(snapshot);
    }
}
