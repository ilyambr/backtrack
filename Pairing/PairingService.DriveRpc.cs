using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Backtrack.Core;

namespace Backtrack.Pairing;

public sealed partial class PairingService
{
    private async Task<string> HandleDriveListFoldersAsync(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." });

        if (!GoogleDriveService.Instance.HasStoredCredentials())
            return JsonSerializer.Serialize(new { success = false, notLoggedIn = true, error = "Main PC is not logged into Google Drive." });

        string? folderId = request.TryGetProperty("folderId", out JsonElement f) ? f.GetString() : null;
        if (folderId == "root")
            folderId = null;

        try
        {
            var folders = await GoogleDriveService.Instance.ListFoldersAsync(folderId);
            string? email = await GoogleDriveService.Instance.GetCurrentUserEmailAsync();

            var folderList = new List<object>();
            foreach (var item in folders)
            {
                folderList.Add(new { id = item.Id, name = item.Name });
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                userEmail = email,
                folders = folderList
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    private async Task<string> HandleDriveCreateFolderAsync(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." });

        if (!GoogleDriveService.Instance.HasStoredCredentials())
            return JsonSerializer.Serialize(new { success = false, notLoggedIn = true, error = "Main PC is not logged into Google Drive." });

        string name = request.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
        string? parentId = request.TryGetProperty("parentId", out JsonElement p) ? p.GetString() : null;
        if (parentId == "root")
            parentId = null;

        if (string.IsNullOrWhiteSpace(name))
            return JsonSerializer.Serialize(new { success = false, error = "Folder name is required." });

        try
        {
            var created = await GoogleDriveService.Instance.CreateFolderAsync(name, parentId);
            if (created != null)
            {
                return JsonSerializer.Serialize(new { success = true, id = created.Id, name = created.Name });
            }
            return JsonSerializer.Serialize(new { success = false, error = "Failed to create folder." });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    private async Task HandleDriveUploadClipAsync(JsonElement request, NetworkStream stream)
    {
        if (!IsAuthorizedClient(request))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." }));
            return;
        }

        if (!GoogleDriveService.Instance.HasStoredCredentials())
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, notLoggedIn = true, error = "Main PC is not logged into Google Drive." }));
            return;
        }

        string relPath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        string? folderId = request.TryGetProperty("folderId", out JsonElement f) ? f.GetString() : null;
        if (folderId == "root")
            folderId = null;

        if (!TryResolveGalleryPath(relPath, out string fullPath, out string? resolveError))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = resolveError ?? "Invalid path." }));
            return;
        }

        if (!File.Exists(fullPath))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = "File not found on host PC." }));
            return;
        }

        string clipName = Path.GetFileName(fullPath);
        AppLog.Write($"[Pairing] Remote-commanded Drive upload starting: {clipName}");

        var progress = new Progress<double>(pVal =>
        {
            try
            {
                _ = WriteLineAsync(stream, JsonSerializer.Serialize(new { progress = pVal, target = relPath }));
            }
            catch { }
        });

        try
        {
            var result = await GoogleDriveService.Instance.UploadClipAsync(fullPath, folderId, progress);
            if (result.Success)
            {
                AppLog.Write($"[Pairing] Remote-commanded Drive upload completed: {clipName}");
                await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = true, webViewLink = result.WebViewLink }));
            }
            else
            {
                AppLog.Write($"[Pairing] Remote-commanded Drive upload failed: {clipName} - {result.ErrorMessage}");
                await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = result.ErrorMessage ?? "Upload failed." }));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Pairing] Remote-commanded Drive upload error: {ex.Message}");
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = ex.Message }));
        }

    }

    public async Task<(bool Success, string? Error, string? UserEmail, List<DriveFolderItem>? Folders, bool NotLoggedIn)> RemoteDriveListFoldersAsync(string? folderId)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.", null, null, false);

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(TimeSpan.FromSeconds(15));
            var fields = new Dictionary<string, object?>
            {
                ["type"] = "drive_list_folders",
                ["secret"] = _settings.PairedPeerSecret,
                ["folderId"] = folderId
            };
            await WriteLineAsync(client.GetStream(), JsonSerializer.Serialize(fields)).WaitAsync(TimeSpan.FromSeconds(15));
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(TimeSpan.FromSeconds(15));
            if (responseLine is null)
                return (false, "No response from paired PC.", null, null, false);

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            bool notLoggedIn = doc.RootElement.TryGetProperty("notLoggedIn", out JsonElement nl) && nl.GetBoolean();
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            string? error = doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : null;
            string? email = doc.RootElement.TryGetProperty("userEmail", out JsonElement em) ? em.GetString() : null;

            var list = new List<DriveFolderItem>();
            if (doc.RootElement.TryGetProperty("folders", out JsonElement fArray) && fArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in fArray.EnumerateArray())
                {
                    string id = el.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? "" : "";
                    string name = el.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? "" : "";
                    list.Add(new DriveFolderItem(id, name, null));
                }
            }

            return (success, error, email, list, notLoggedIn);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, null, false);
        }
    }

    public async Task<(bool Success, string? Error, DriveFolderItem? CreatedFolder)> RemoteDriveCreateFolderAsync(string name, string? parentId)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.", null);

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(TimeSpan.FromSeconds(15));
            var fields = new Dictionary<string, object?>
            {
                ["type"] = "drive_create_folder",
                ["secret"] = _settings.PairedPeerSecret,
                ["name"] = name,
                ["parentId"] = parentId
            };
            await WriteLineAsync(client.GetStream(), JsonSerializer.Serialize(fields)).WaitAsync(TimeSpan.FromSeconds(15));
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(TimeSpan.FromSeconds(15));
            if (responseLine is null)
                return (false, "No response from paired PC.", null);

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            string? error = doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : null;
            if (success)
            {
                string id = doc.RootElement.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? "" : "";
                string folderName = doc.RootElement.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? "" : "";
                return (true, null, new DriveFolderItem(id, folderName, null));
            }
            return (false, error ?? "Failed to create folder on paired PC.", null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    public async Task<(bool Success, string? Error, string? WebViewLink)> RemoteDriveUploadClipAsync(string relativePath, string? folderId, IProgress<double>? progress = null)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.", null);

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(TimeSpan.FromSeconds(15));
            var fields = new Dictionary<string, object?>
            {
                ["type"] = "drive_upload_clip",
                ["secret"] = _settings.PairedPeerSecret,
                ["path"] = relativePath,
                ["folderId"] = folderId
            };
            await WriteLineAsync(client.GetStream(), JsonSerializer.Serialize(fields)).WaitAsync(TimeSpan.FromSeconds(15));

            while (true)
            {
                string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(TimeSpan.FromMinutes(10));
                if (responseLine is null)
                    return (false, "No response from paired PC.", null);

                using JsonDocument doc = JsonDocument.Parse(responseLine);
                if (doc.RootElement.TryGetProperty("progress", out JsonElement progEl))
                {
                    double progVal = progEl.GetDouble();
                    progress?.Report(progVal);
                    continue;
                }

                bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
                string? error = doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : null;
                string? link = doc.RootElement.TryGetProperty("webViewLink", out JsonElement lk) ? lk.GetString() : null;
                return (success, success ? null : (error ?? "Upload request failed."), link);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }
}
