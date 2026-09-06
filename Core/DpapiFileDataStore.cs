using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace Backtrack.Core;

/// <summary>
/// A secure, DPAPI-backed implementation of Google APIs IDataStore.
/// Encrypts stored credentials using Windows DPAPI (CurrentUser scope)
/// with atomic write/replace operations to prevent file corruption.
/// Automatically migrates existing plaintext tokens and scrubs old files.
/// </summary>
public sealed class DpapiFileDataStore : IDataStore
{
    private readonly string _folderPath;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Backtrack.GoogleDrive.OAuth.TokenStore.Entropy.v1");

    public DpapiFileDataStore(string folderPath)
    {
        _folderPath = folderPath;
        Directory.CreateDirectory(_folderPath);
    }

    public Task StoreAsync<T>(string key, T value)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key MUST have a value", nameof(key));

        string serialized = NewtonsoftJsonSerializer.Instance.Serialize(value);
        byte[] plainBytes = Encoding.UTF8.GetBytes(serialized);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);

        string targetFile = GetFilePath(key);
        string tempFile = Path.Combine(_folderPath, $"{key}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(tempFile, encryptedBytes);
            File.Move(tempFile, targetFile, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            throw;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key MUST have a value", nameof(key));

        string filePath = GetFilePath(key);
        if (File.Exists(filePath))
        {
            try { File.Delete(filePath); } catch { }
        }

        return Task.CompletedTask;
    }

    public Task<T?> GetAsync<T>(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key MUST have a value", nameof(key));

        string filePath = GetFilePath(key);
        if (!File.Exists(filePath))
            return Task.FromResult<T?>(default);

        try
        {
            byte[] fileBytes = File.ReadAllBytes(filePath);

            // Attempt DPAPI unprotect
            byte[] plainBytes;
            try
            {
                plainBytes = ProtectedData.Unprotect(fileBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            catch
            {
                // If unprotect fails, check if this is a legacy unencrypted token file
                string rawText = Encoding.UTF8.GetString(fileBytes);
                if (rawText.TrimStart().StartsWith("{"))
                {
                    // Validate deserialization as plaintext legacy format
                    T? migratedValue = NewtonsoftJsonSerializer.Instance.Deserialize<T>(rawText);
                    if (migratedValue != null)
                    {
                        // Migrate immediately to DPAPI-encrypted storage and overwrite plaintext file
                        _ = StoreAsync(key, migratedValue);
                        return Task.FromResult<T?>(migratedValue);
                    }
                }
                return Task.FromResult<T?>(default);
            }

            string json = Encoding.UTF8.GetString(plainBytes);
            T? result = NewtonsoftJsonSerializer.Instance.Deserialize<T>(json);
            return Task.FromResult<T?>(result);
        }
        catch
        {
            // Do not log token contents or error details that might leak sensitive info
            return Task.FromResult<T?>(default);
        }
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(_folderPath))
        {
            foreach (var file in Directory.GetFiles(_folderPath))
            {
                try { File.Delete(file); } catch { }
            }
        }
        return Task.CompletedTask;
    }

    private string GetFilePath(string key)
    {
        // Safe alphanumeric filename matching Google FileDataStore conventions
        return Path.Combine(_folderPath, key);
    }
}
