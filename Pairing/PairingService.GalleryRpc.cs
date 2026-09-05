using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Backtrack.Core;

namespace Backtrack.Pairing;

public sealed partial class PairingService
{
    private bool TryResolveGalleryPath(string relativePath, out string fullPath, out string? error)
    {
        string root = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = string.IsNullOrEmpty(relativePath) ? root : Path.GetFullPath(Path.Combine(root, relativePath));

        if (candidate != root && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            fullPath = "";
            error = "That path is outside the clips folder.";
            return false;
        }

        fullPath = candidate;
        error = null;
        return true;
    }

    public Func<string, Task<string?>>? EnsureThumbnailCachedForRemote { get; set; }
    public Func<string, long?>? GetCachedDurationMsForRemote { get; set; }

    private string HandleNewestClip(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." });

        try
        {
            string root = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root))
                return JsonSerializer.Serialize(new { path = (string?)null });

            string? newest = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => GalleryFormats.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                .Where(f => GetCachedDurationMsForRemote?.Invoke(f.FullName) is not < 2000)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()
                ?.FullName;

            string? relativePath = newest is null ? null
                : Path.GetRelativePath(root, newest).Replace(Path.DirectorySeparatorChar, '/');
            return JsonSerializer.Serialize(new { path = relativePath });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private string HandleListGallery(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." });

        string relativePath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        if (!TryResolveGalleryPath(relativePath, out string fullPath, out string? pathError))
            return JsonSerializer.Serialize(new { error = pathError });

        try
        {
            if (!Directory.Exists(fullPath))
                return JsonSerializer.Serialize(new { error = "That folder doesn't exist on this PC." });

            DeduplicationService.Instance.PruneOrphanedRecords(File.Exists);

            string[] folders = Directory.GetDirectories(fullPath)
                .Select(d => Path.GetFileName(d) ?? "")
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var fileInfos = Directory.EnumerateFiles(fullPath)
                .Where(f => GalleryFormats.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                .Where(f => GetCachedDurationMsForRemote?.Invoke(f.FullName) is not < 2000)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            var fileNamesSet = new HashSet<string>(fileInfos.Select(fi => fi.Name), StringComparer.OrdinalIgnoreCase);

            var files = fileInfos.Select(f =>
            {
                bool isDedup = DeduplicationService.Instance.IsDeduplicated(f.FullName, out var dEntry) &&
                    !string.IsNullOrEmpty(dEntry?.OriginClipFileName) &&
                    (fileNamesSet.Contains(dEntry.OriginClipFileName) || File.Exists(dEntry.OriginClipPath));

                bool hasChildren = !isDedup && DeduplicationService.Instance.GetAllRecords().Values
                    .Any(r => (string.Equals(r.OriginClipPath, f.FullName, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(r.OriginClipFileName, f.Name, StringComparison.OrdinalIgnoreCase)) &&
                              (fileNamesSet.Contains(r.ClipFileName) || File.Exists(r.ClipPath)));

                return new
                {
                    name = f.Name,
                    size = f.Length,
                    modified = f.LastWriteTimeUtc,
                    isDeduplicated = isDedup,
                    hasDeduplicatedChildren = hasChildren,
                    originFileName = isDedup ? dEntry?.OriginClipFileName : null,
                    originPath = isDedup ? dEntry?.OriginClipPath : null
                };
            }).ToArray();

            var markersMap = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            var starredList = new List<string>();

            foreach (var f in files)
            {
                if (_settings.ClipMarkers.TryGetValue(f.name, out var m) && m.Count > 0)
                {
                    markersMap[f.name] = m;
                }
                if (_settings.StarredClips.Contains(f.name))
                {
                    starredList.Add(f.name);
                }
            }

            string rootDrive = Path.GetPathRoot(fullPath) ?? "C:\\";
            var drive = new DriveInfo(rootDrive);
            long driveTotalBytes = drive.IsReady ? drive.TotalSize : 0;
            long driveFreeBytes = drive.IsReady ? drive.AvailableFreeSpace : 0;
            long clipsFolderUsageBytes = 0;
            try
            {
                string rootFolder = _settings.ClipsFolder;
                if (Directory.Exists(rootFolder))
                {
                    clipsFolderUsageBytes = Directory.EnumerateFiles(rootFolder, "*", SearchOption.AllDirectories)
                        .Where(f => GalleryFormats.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .Sum(f => new FileInfo(f).Length);
                }
            }
            catch { }

            var storageInfo = new
            {
                storageLimitEnabled = _settings.StorageLimitEnabled,
                storageLimitGb = _settings.StorageLimitGb,
                clipsFolderBytes = clipsFolderUsageBytes,
                driveTotalBytes = driveTotalBytes,
                driveFreeBytes = driveFreeBytes
            };

            return JsonSerializer.Serialize(new
            {
                folders,
                files,
                markers = markersMap,
                starred = starredList,
                dedupRecords = DeduplicationService.Instance.GetAllRecords(),
                storage = storageInfo
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private string HandlePlayAudioCue(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." });

        string cue = request.TryGetProperty("cue", out JsonElement c) ? c.GetString() ?? "" : "";
        int volume = request.TryGetProperty("volume", out JsonElement v) && v.TryGetInt32(out int vol) ? vol : -1;

        AudioCues.PlayCueByName(cue, volume);
        return JsonSerializer.Serialize(new { success = true });
    }

    private string HandleSyncClipMarkers(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." });

        string clipKey = request.TryGetProperty("clipKey", out JsonElement k) ? k.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(clipKey))
            return JsonSerializer.Serialize(new { success = false, error = "clipKey is required." });

        var markers = new List<double>();
        if (request.TryGetProperty("markers", out JsonElement mArray) && mArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in mArray.EnumerateArray())
            {
                if (el.TryGetDouble(out double val))
                    markers.Add(val);
            }
        }

        if (markers.Count > 0)
        {
            markers.Sort();
            _settings.ClipMarkers[clipKey] = markers;
            string fileName = Path.GetFileName(clipKey);
            if (!string.Equals(fileName, clipKey, StringComparison.OrdinalIgnoreCase))
            {
                _settings.ClipMarkers[fileName] = markers;
            }
        }
        else
        {
            _settings.ClipMarkers.Remove(clipKey);
            _settings.ClipMarkers.Remove(Path.GetFileName(clipKey));
        }

        _settings.Save();
        AppLog.Write($"[Pairing] Synced {markers.Count} markers for clip '{clipKey}' from paired peer");
        return JsonSerializer.Serialize(new { success = true });
    }

    private string HandleSyncStarred(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." });

        string clipKey = request.TryGetProperty("clipKey", out JsonElement k) ? k.GetString() ?? "" : "";
        bool isStarred = request.TryGetProperty("isStarred", out JsonElement s) && s.GetBoolean();
        if (string.IsNullOrWhiteSpace(clipKey))
            return JsonSerializer.Serialize(new { success = false, error = "clipKey is required." });

        string fileName = Path.GetFileName(clipKey);
        if (isStarred)
        {
            _settings.StarredClips.Add(clipKey);
            _settings.StarredClips.Add(fileName);
        }
        else
        {
            _settings.StarredClips.Remove(clipKey);
            _settings.StarredClips.Remove(fileName);
        }

        _settings.Save();
        AppLog.Write($"[Pairing] Synced starred ({isStarred}) for clip '{clipKey}' from paired peer");
        return JsonSerializer.Serialize(new { success = true });
    }

    public async Task<bool> SendPlayAudioCueAsync(string cue, int volume)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return false;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(TimeSpan.FromSeconds(2));
            string request = JsonSerializer.Serialize(new
            {
                type = "play_audio_cue",
                secret = _settings.PairedPeerSecret,
                cue,
                volume
            });
            await WriteLineAsync(client.GetStream(), request).WaitAsync(TimeSpan.FromSeconds(2));
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(TimeSpan.FromSeconds(2));
            if (responseLine != null)
            {
                using JsonDocument doc = JsonDocument.Parse(responseLine);
                return doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Pairing] SendPlayAudioCueAsync failed: {ex.Message}");
        }
        return false;
    }

    public async Task<bool> SendSyncClipMarkersAsync(string clipKey, List<double> markers)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return false;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(TimeSpan.FromSeconds(2));
            string request = JsonSerializer.Serialize(new
            {
                type = "sync_clip_markers",
                secret = _settings.PairedPeerSecret,
                clipKey,
                markers
            });
            await WriteLineAsync(client.GetStream(), request).WaitAsync(TimeSpan.FromSeconds(2));
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(TimeSpan.FromSeconds(2));
            if (responseLine != null)
            {
                using JsonDocument doc = JsonDocument.Parse(responseLine);
                return doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Pairing] SendSyncClipMarkersAsync failed: {ex.Message}");
        }
        return false;
    }

    public async Task<bool> SendSyncStarredAsync(string clipKey, bool isStarred)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return false;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(TimeSpan.FromSeconds(2));
            string request = JsonSerializer.Serialize(new
            {
                type = "sync_starred",
                secret = _settings.PairedPeerSecret,
                clipKey,
                isStarred
            });
            await WriteLineAsync(client.GetStream(), request).WaitAsync(TimeSpan.FromSeconds(2));
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(TimeSpan.FromSeconds(2));
            if (responseLine != null)
            {
                using JsonDocument doc = JsonDocument.Parse(responseLine);
                return doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Pairing] SendSyncStarredAsync failed: {ex.Message}");
        }
        return false;
    }
}
