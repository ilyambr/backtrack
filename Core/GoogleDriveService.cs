using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using DriveData = Google.Apis.Drive.v3.Data;
using IoFile = System.IO.File;

namespace Backtrack.Core;

public sealed record DriveFolderItem(string Id, string Name, string? ParentId);

public sealed record DriveUploadResult(bool Success, string? FileId, string? WebViewLink, string? ErrorMessage);

public sealed class GoogleDriveService
{
    private static readonly Lazy<GoogleDriveService> _instance = new(() => new GoogleDriveService());
    public static GoogleDriveService Instance => _instance.Value;

    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Backtrack", "GoogleDrive");

    private static readonly string TokenFolder = Path.Combine(AppDataFolder, "Tokens");
    private static readonly string ConfigPath = Path.Combine(AppDataFolder, "client_secrets.json");

    private static readonly string[] Scopes =
    {
        DriveService.Scope.DriveFile,
        DriveService.Scope.DriveMetadataReadonly
    };

    private DriveService? _driveService;
    private UserCredential? _credential;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private CancellationTokenSource? _activeAuthCts;

    public bool IsAuthenticated => _driveService != null;
    public string? LastAuthError { get; private set; }

    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email))
            return "";

        int atIndex = email.IndexOf('@');
        if (atIndex <= 0)
            return new string('*', Math.Max(email.Length, 6));

        string username = email.Substring(0, atIndex);
        string domain = email.Substring(atIndex); // includes '@'

        string maskedUser = new string('*', Math.Max(username.Length, 4));
        return maskedUser + domain;
    }

    public bool HasStoredCredentials()
    {
        if (_driveService != null)
            return true;

        try
        {
            return Directory.Exists(TokenFolder) && Directory.EnumerateFiles(TokenFolder).Any();
        }
        catch
        {
            return false;
        }
    }

    public static string ConfigFilePath => ConfigPath;

    public void CancelAuthentication()
    {
        try
        {
            _activeAuthCts?.Cancel();
        }
        catch { }
    }

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (_driveService != null)
            return true;

        // Cancel any previous hung or in-flight authentication
        CancelAuthentication();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeAuthCts = cts;

        try
        {
            await _authLock.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (_driveService != null)
                return true;

            LastAuthError = null;
            Directory.CreateDirectory(AppDataFolder);
            Directory.CreateDirectory(TokenFolder);

            ClientSecrets secrets = LoadClientSecrets();

            var dataStore = new DpapiFileDataStore(TokenFolder);

            const string closeHtml = @"<html>
<head><title>Backtrack</title><script>window.onload = function() { window.open('', '_self', ''); window.close(); };</script></head>
<body style='background:#131518;color:#F5F6F8;font-family:sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;'>
<div style='text-align:center;'>
<h2 style='margin-bottom:8px;'>Connected to Google Drive!</h2>
<p style='color:#9EABB8;font-size:14px;'>You can return to Backtrack. This tab can be closed.</p>
</div>
</body></html>";

            var receiver = new Google.Apis.Auth.OAuth2.LocalServerCodeReceiver(closeHtml);

            _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                Scopes,
                "user",
                cts.Token,
                dataStore,
                receiver);

            _driveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = _credential,
                ApplicationName = "Backtrack"
            });

            return true;
        }
        catch (OperationCanceledException)
        {
            LastAuthError = "Sign in was cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            LastAuthError = ex.Message;
            AppLog.Write($"[GoogleDrive] Authentication failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (_activeAuthCts == cts)
                _activeAuthCts = null;
            _authLock.Release();
        }
    }

    public async Task SignOutAsync()
    {
        CancelAuthentication();
        await _authLock.WaitAsync();
        try
        {
            _driveService = null;
            _credential = null;
            if (Directory.Exists(TokenFolder))
            {
                Directory.Delete(TokenFolder, true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[GoogleDrive] SignOut error: {ex.Message}");
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task<string?> GetCurrentUserEmailAsync(CancellationToken cancellationToken = default)
    {
        if (!await AuthenticateAsync(cancellationToken))
            return null;

        try
        {
            var req = _driveService!.About.Get();
            req.Fields = "user(displayName,emailAddress)";
            var about = await req.ExecuteAsync(cancellationToken);
            return about.User?.EmailAddress ?? about.User?.DisplayName;
        }
        catch (Exception ex)
        {
            AppLog.Write($"[GoogleDrive] Failed to get user info: {ex.Message}");
            return null;
        }
    }

    public async Task<List<DriveFolderItem>> ListFoldersAsync(string? parentId = null, CancellationToken cancellationToken = default)
    {
        if (!await AuthenticateAsync(cancellationToken))
            throw new InvalidOperationException(LastAuthError ?? "Authentication with Google Drive failed.");

        try
        {
            var targetParent = string.IsNullOrEmpty(parentId) ? "root" : parentId;
            var listReq = _driveService!.Files.List();
            listReq.Q = $"'{targetParent}' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
            listReq.Fields = "nextPageToken, files(id, name, parents)";
            listReq.OrderBy = "folder, name";
            listReq.PageSize = 100;

            var result = new List<DriveFolderItem>();
            string? pageToken = null;

            do
            {
                listReq.PageToken = pageToken;
                var fileList = await listReq.ExecuteAsync(cancellationToken);
                if (fileList.Files != null)
                {
                    foreach (var f in fileList.Files)
                    {
                        string? pId = f.Parents?.FirstOrDefault();
                        result.Add(new DriveFolderItem(f.Id, f.Name, pId));
                    }
                }
                pageToken = fileList.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            return result;
        }
        catch (Exception ex)
        {
            AppLog.Write($"[GoogleDrive] ListFoldersAsync failed: {ex.Message}");
            throw;
        }
    }

    public async Task<DriveFolderItem?> CreateFolderAsync(string folderName, string? parentId = null, CancellationToken cancellationToken = default)
    {
        if (!await AuthenticateAsync(cancellationToken))
            return null;

        try
        {
            var targetParent = string.IsNullOrEmpty(parentId) ? "root" : parentId;
            var folderMetadata = new DriveData.File
            {
                Name = folderName.Trim(),
                MimeType = "application/vnd.google-apps.folder",
                Parents = new List<string> { targetParent }
            };

            var req = _driveService!.Files.Create(folderMetadata);
            req.Fields = "id, name, parents";
            var created = await req.ExecuteAsync(cancellationToken);
            return new DriveFolderItem(created.Id, created.Name, created.Parents?.FirstOrDefault());
        }
        catch (Exception ex)
        {
            AppLog.Write($"[GoogleDrive] CreateFolderAsync failed: {ex.Message}");
            throw;
        }
    }

    public async Task<DriveUploadResult> UploadClipAsync(
        string localFilePath,
        string? targetFolderId,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IoFile.Exists(localFilePath))
            return new DriveUploadResult(false, null, null, "Local file not found.");

        if (!await AuthenticateAsync(cancellationToken))
            return new DriveUploadResult(false, null, null, "Google Drive authentication failed.");

        try
        {
            string fileName = Path.GetFileName(localFilePath);
            string contentType = GetContentType(localFilePath);

            var fileMetadata = new DriveData.File
            {
                Name = fileName,
                Parents = !string.IsNullOrEmpty(targetFolderId) ? new List<string> { targetFolderId } : new List<string> { "root" }
            };

            await using var stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var uploadReq = _driveService!.Files.Create(fileMetadata, stream, contentType);
            uploadReq.Fields = "id, name, webViewLink, webContentLink";
            uploadReq.ChunkSize = ResumableUpload.MinimumChunkSize * 16; // 4MB chunks

            if (progress != null)
            {
                uploadReq.ProgressChanged += uploadProgress =>
                {
                    switch (uploadProgress.Status)
                    {
                        case UploadStatus.Uploading:
                            if (stream.Length > 0)
                            {
                                double pct = (double)uploadProgress.BytesSent / stream.Length * 100.0;
                                progress.Report(Math.Min(100.0, Math.Max(0.0, pct)));
                            }
                            break;
                        case UploadStatus.Completed:
                            progress.Report(100.0);
                            break;
                    }
                };
            }

            var uploadProgressResult = await uploadReq.UploadAsync(cancellationToken);

            if (uploadProgressResult.Status == UploadStatus.Completed)
            {
                var uploadedFile = uploadReq.ResponseBody;
                return new DriveUploadResult(true, uploadedFile?.Id, uploadedFile?.WebViewLink, null);
            }
            else
            {
                string errMsg = uploadProgressResult.Exception?.Message ?? "Upload did not complete successfully.";
                return new DriveUploadResult(false, null, null, errMsg);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[GoogleDrive] UploadClipAsync failed: {ex.Message}");
            return new DriveUploadResult(false, null, null, ex.Message);
        }
    }

    private static string GetContentType(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            _ => "application/octet-stream"
        };
    }

    private static ClientSecrets LoadClientSecrets()
    {
        if (IoFile.Exists(ConfigPath))
        {
            try
            {
                string json = IoFile.ReadAllText(ConfigPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("installed", out var installed) || root.TryGetProperty("web", out installed))
                {
                    string? clientId = installed.GetProperty("client_id").GetString();
                    string? clientSecret = installed.GetProperty("client_secret").GetString();
                    if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
                    {
                        return new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };
                    }
                }
                else if (root.TryGetProperty("client_id", out var idProp) && root.TryGetProperty("client_secret", out var secProp))
                {
                    return new ClientSecrets { ClientId = idProp.GetString(), ClientSecret = secProp.GetString() };
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"[GoogleDrive] Error reading custom client secrets: {ex.Message}");
            }
        }

        // Default embedded desktop OAuth client credentials (obfuscated bytes)
        // Allows users to sign in without manual configuration.
        // Users can still override by dropping client_secrets.json into %AppData%\Backtrack\GoogleDrive\
        return new ClientSecrets
        {
            ClientId = Unmask("ZGxlbWlrZWhoaWtlcG0rZTc7bTsrLD86LTcwZCw7PjswO2hqOiwyKDYxbihlczwtLS5zOjIyOjE4KC44Lz4yMyk4MylzPjIw"),
            ClientSecret = Unmask("GhIeDg0FcC5sPGkHBwgVChwFbC48aw8UbDAfNwkMLXA4H2k=")
        };
    }

    private static string Unmask(string encoded)
    {
        const byte Mask = 0x5D;
        byte[] bytes = Convert.FromBase64String(encoded);
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] ^= Mask;
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
