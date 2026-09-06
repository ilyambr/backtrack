using System;
using System.Collections.Concurrent;
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
    public async Task<string?> GetRemoteNewestClipPathAsync()
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return null;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            string request = JsonSerializer.Serialize(new { type = "newest_clip", secret = _settings.PairedPeerSecret });
            await WriteLineAsync(client.GetStream(), request);
            string? responseLine = await ReadLineAsync(client.GetStream());
            if (responseLine is null)
                return null;

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            if (doc.RootElement.TryGetProperty("error", out _))
                return null;

            return doc.RootElement.TryGetProperty("path", out JsonElement p) ? p.GetString() : null;
        }
        catch { return null; }
    }

    public async Task<RemoteGalleryListing?> ListRemoteGalleryAsync(string relativePath)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return null;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            string request = JsonSerializer.Serialize(new { type = "list_gallery", secret = _settings.PairedPeerSecret, path = relativePath });
            await WriteLineAsync(client.GetStream(), request);
            string? responseLine = await ReadLineAsync(client.GetStream());
            if (responseLine is null)
                return null;

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            if (doc.RootElement.TryGetProperty("error", out _))
                return null;

            List<string> folders = doc.RootElement.GetProperty("folders").EnumerateArray()
                .Select(e => e.GetString() ?? "").ToList();
            List<RemoteGalleryFile> files = doc.RootElement.GetProperty("files").EnumerateArray()
                .Select(e => new RemoteGalleryFile(
                    e.GetProperty("name").GetString() ?? "",
                    e.GetProperty("size").GetInt64(),
                    e.GetProperty("modified").GetDateTime(),
                    e.TryGetProperty("isDeduplicated", out JsonElement dedupEl) && dedupEl.GetBoolean(),
                    e.TryGetProperty("hasDeduplicatedChildren", out JsonElement hasChEl) && hasChEl.GetBoolean(),
                    e.TryGetProperty("originFileName", out JsonElement ofEl) ? ofEl.GetString() : null,
                    e.TryGetProperty("originPath", out JsonElement opEl) ? opEl.GetString() : null))
                .ToList();

            bool settingsChanged = false;

            if (doc.RootElement.TryGetProperty("markers", out JsonElement markersEl) && markersEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in markersEl.EnumerateObject())
                {
                    var mList = new List<double>();
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement mVal in prop.Value.EnumerateArray())
                        {
                            if (mVal.TryGetDouble(out double d))
                                mList.Add(d);
                        }
                    }
                    if (mList.Count > 0)
                    {
                        mList.Sort();
                        _settings.ClipMarkers[prop.Name] = mList;
                        string folderRel = string.IsNullOrEmpty(relativePath) ? prop.Name : $"{relativePath.TrimEnd('/', '\\')}/{prop.Name}";
                        _settings.ClipMarkers[folderRel] = mList;
                        settingsChanged = true;
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("starred", out JsonElement starredEl) && starredEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement sVal in starredEl.EnumerateArray())
                {
                    string? sName = sVal.GetString();
                    if (!string.IsNullOrEmpty(sName))
                    {
                        if (_settings.StarredClips.Add(sName))
                            settingsChanged = true;
                        string folderRel = string.IsNullOrEmpty(relativePath) ? sName : $"{relativePath.TrimEnd('/', '\\')}/{sName}";
                        if (_settings.StarredClips.Add(folderRel))
                            settingsChanged = true;
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("dedupRecords", out JsonElement dedupRecordsEl) && dedupRecordsEl.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    var recs = JsonSerializer.Deserialize<Dictionary<string, DeduplicationEntry>>(dedupRecordsEl.GetRawText());
                    if (recs != null)
                    {
                        DeduplicationService.Instance.ImportRemoteRecords(recs);
                    }
                }
                catch { }
            }

            if (settingsChanged)
            {
                _settings.Save();
            }

            RemoteStorageInfo? storageInfo = null;
            if (doc.RootElement.TryGetProperty("storage", out JsonElement stEl) && stEl.ValueKind == JsonValueKind.Object)
            {
                bool limitEnabled = stEl.TryGetProperty("storageLimitEnabled", out JsonElement le) && le.GetBoolean();
                double limitGb = stEl.TryGetProperty("storageLimitGb", out JsonElement lg) ? lg.GetDouble() : 0;
                long clipsBytes = stEl.TryGetProperty("clipsFolderBytes", out JsonElement cb) && cb.TryGetInt64(out long b) ? b : 0;
                long driveTotal = stEl.TryGetProperty("driveTotalBytes", out JsonElement dt) && dt.TryGetInt64(out long tr) ? tr : 0;
                long driveFree = stEl.TryGetProperty("driveFreeBytes", out JsonElement df) && df.TryGetInt64(out long fr) ? fr : 0;
                storageInfo = new RemoteStorageInfo(limitEnabled, limitGb, clipsBytes, driveTotal, driveFree);
            }

            return new RemoteGalleryListing(folders, files, storageInfo);
        }
        catch { return null; }
    }

    public Task<(bool Success, string? Error)> DownloadRemoteClipAsync(string relativePath, string destPath, IProgress<double>? progress = null) =>
        DownloadStreamedFileAsync("get_clip", relativePath, destPath, progress);

    public Task<(bool Success, string? Error)> DownloadRemoteThumbnailAsync(string relativePath, string destPath) =>
        DownloadStreamedFileAsync("get_thumbnail", relativePath, destPath);

    public Task<(bool Success, string? Error)> DeleteRemoteClipAsync(string relativePath) =>
        SendClipMutationRequestAsync("delete_clip", new Dictionary<string, object?> { ["path"] = relativePath });

    public async Task<(bool Success, string? Error, string? NewPath)> RenameRemoteClipAsync(string relativePath, string newName)
    {
        (bool success, string? error, string? path) = await SendClipMutationRequestWithPathAsync("rename_clip",
            new Dictionary<string, object?> { ["path"] = relativePath, ["newName"] = newName });
        return (success, error, path);
    }

    public Task<(bool Success, string? Error)> MoveRemoteClipsAsync(IEnumerable<string> relativePaths, string destinationRelativeFolder) =>
        SendClipMutationRequestAsync("move_clip", new Dictionary<string, object?>
        {
            ["paths"] = relativePaths.ToArray(),
            ["destination"] = destinationRelativeFolder
        });

    private async Task<(bool Success, string? Error)> SendClipMutationRequestAsync(string type, Dictionary<string, object?> fields)
    {
        (bool success, string? error, _) = await SendClipMutationRequestWithPathAsync(type, fields);
        return (success, error);
    }

    private async Task<(bool Success, string? Error, string? Path)> SendClipMutationRequestWithPathAsync(string type, Dictionary<string, object?> fields)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.", null);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(MutationRequestTimeout);
            fields["type"] = type;
            fields["secret"] = _settings.PairedPeerSecret;
            await WriteLineAsync(client.GetStream(), JsonSerializer.Serialize(fields)).WaitAsync(MutationRequestTimeout);
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(MutationRequestTimeout);
            if (responseLine is null)
                return (false, "No response from the transmitter PC.", null);

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            string? error = doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : null;
            string? path = doc.RootElement.TryGetProperty("path", out JsonElement pt) ? pt.GetString() : null;
            return (success, success ? null : (error ?? "Operation failed."), path);
        }
        catch (TimeoutException)
        {
            return (false, $"{_settings.PairedPeerName ?? "The paired PC"} didn't respond in time.", null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    public async Task<(bool Success, string? Error, string? Path)> UploadRemoteClipAsync(
        string relativePath, string localSourcePath, bool overwrite, IProgress<double>? progress = null)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.", null);

        if (!File.Exists(localSourcePath))
            return (false, "Local source file not found.", null);

        try
        {
            var info = new FileInfo(localSourcePath);
            long total = info.Length;

            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(MutationRequestTimeout);
            NetworkStream stream = client.GetStream();

            var header = new Dictionary<string, object?>
            {
                ["type"] = "put_clip",
                ["secret"] = _settings.PairedPeerSecret,
                ["path"] = relativePath,
                ["size"] = total,
                ["overwrite"] = overwrite,
            };
            string request = JsonSerializer.Serialize(header);
            await WriteLineAsync(stream, request);

            await using var file = File.OpenRead(localSourcePath);
            var buffer = new byte[81920];
            long sent = 0;
            while (sent < total)
            {
                int toRead = (int)Math.Min(buffer.Length, total - sent);
                int read = await file.ReadAsync(buffer.AsMemory(0, toRead));
                if (read == 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read));
                sent += read;
                progress?.Report(total > 0 ? (double)sent / total : 1.0);
            }
            await stream.FlushAsync();

            string? responseLine = await ReadLineAsync(stream).WaitAsync(TimeSpan.FromMinutes(2));
            if (responseLine is null)
                return (false, "No response from the transmitter PC.", null);

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            string? error = doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : null;
            string? finalPath = doc.RootElement.TryGetProperty("path", out JsonElement pt) ? pt.GetString() : null;
            return (success, success ? null : (error ?? "Upload failed."), finalPath);
        }
        catch (TimeoutException)
        {
            return (false, $"{_settings.PairedPeerName ?? "The paired PC"} didn't respond in time.", null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    private readonly ConcurrentDictionary<string, Lazy<Task<(bool Success, string? Error)>>> _activeDownloads = new();

    private Task<(bool Success, string? Error)> DownloadStreamedFileAsync(string requestType, string relativePath, string destPath, IProgress<double>? progress = null)
    {
        Lazy<Task<(bool Success, string? Error)>> lazy = _activeDownloads.GetOrAdd(destPath,
            _ => new Lazy<Task<(bool Success, string? Error)>>(
                () => DownloadStreamedFileCoreAsync(requestType, relativePath, destPath, progress)));
        Task<(bool Success, string? Error)> task = lazy.Value;
        _ = task.ContinueWith(completed => _activeDownloads.TryRemove(destPath, out _), TaskScheduler.Default);
        return task;
    }

    private async Task<(bool Success, string? Error)> DownloadStreamedFileCoreAsync(string requestType, string relativePath, string destPath, IProgress<double>? progress = null)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.");

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            string request = JsonSerializer.Serialize(new { type = requestType, secret = _settings.PairedPeerSecret, path = relativePath });
            NetworkStream stream = client.GetStream();
            await WriteLineAsync(stream, request);

            string? headerLine = await ReadLineAsync(stream);
            if (headerLine is null)
                return (false, "No response from the transmitter PC.");

            using JsonDocument doc = JsonDocument.Parse(headerLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            if (!success)
                return (false, doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : "Download failed.");

            long size = doc.RootElement.GetProperty("size").GetInt64();
            string tempPath = destPath + ".partial";
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            long received = 0;
            var buffer = new byte[81920];
            await using (var file = File.Create(tempPath))
            {
                while (received < size)
                {
                    int toRead = (int)Math.Min(buffer.Length, size - received);
                    int read = await stream.ReadAsync(buffer.AsMemory(0, toRead));
                    if (read == 0) break;
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    progress?.Report(size > 0 ? (double)received / size : 1.0);
                }
            }

            if (received != size)
            {
                File.Delete(tempPath);
                return (false, "Connection dropped before the whole file arrived.");
            }

            File.Delete(destPath);
            File.Move(tempPath, destPath);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, List<double>? Markers, bool IsStarred)> GetRemoteClipMetadataAsync(string relativePath)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, null, false);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(TimeSpan.FromSeconds(3));
            string request = JsonSerializer.Serialize(new
            {
                type = "get_clip_metadata",
                secret = _settings.PairedPeerSecret,
                path = relativePath
            });
            await WriteLineAsync(client.GetStream(), request).WaitAsync(TimeSpan.FromSeconds(3));
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(TimeSpan.FromSeconds(3));
            if (responseLine is null)
                return (false, null, false);

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            if (!success)
                return (false, null, false);

            var markers = new List<double>();
            if (doc.RootElement.TryGetProperty("markers", out JsonElement mArray) && mArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in mArray.EnumerateArray())
                {
                    if (el.TryGetDouble(out double val))
                        markers.Add(val);
                }
            }

            bool isStarred = doc.RootElement.TryGetProperty("isStarred", out JsonElement st) && st.GetBoolean();
            return (true, markers, isStarred);
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Pairing] GetRemoteClipMetadataAsync failed: {ex.Message}");
            return (false, null, false);
        }
    }
}
