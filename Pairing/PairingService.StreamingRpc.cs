using System;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace Backtrack.Pairing;

public sealed partial class PairingService
{
    private async Task HandlePutClipAsync(JsonElement request, NetworkStream stream)
    {
        if (!IsAuthorizedClient(request))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." }));
            return;
        }

        string relPath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        long size = request.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long sz) ? sz : -1;
        bool overwrite = request.TryGetProperty("overwrite", out JsonElement o) && o.GetBoolean();

        if (string.IsNullOrEmpty(relPath) || size < 0)
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { error = "Invalid upload parameters." }));
            return;
        }

        if (!TryResolveGalleryPath(relPath, out string fullPath, out string? resolveError))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { error = resolveError ?? "Invalid path." }));
            return;
        }

        string targetPath = fullPath;
        if (!overwrite && File.Exists(targetPath))
        {
            string dir = Path.GetDirectoryName(targetPath)!;
            string nameNoExt = Path.GetFileNameWithoutExtension(targetPath);
            string ext = Path.GetExtension(targetPath);
            int idx = 2;
            while (File.Exists(targetPath))
            {
                targetPath = Path.Combine(dir, $"{nameNoExt} ({idx}){ext}");
                idx++;
            }
        }

        string tempPath = targetPath + ".uploading";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
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
                }
            }

            if (received != size)
            {
                File.Delete(tempPath);
                await WriteLineAsync(stream, JsonSerializer.Serialize(new { error = "Transfer interrupted." }));
                return;
            }

            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);

            string finalRel = Path.GetRelativePath(_settings.ClipsFolder, targetPath).Replace('\\', '/');
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = true, path = finalRel }));
        }
        catch (Exception ex)
        {
            try { File.Delete(tempPath); } catch { }
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    private async Task HandleGetClipAsync(JsonElement request, NetworkStream stream)
    {
        if (!IsAuthorizedClient(request))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." }));
            return;
        }

        string relPath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        long offset = request.TryGetProperty("offset", out JsonElement o) && o.TryGetInt64(out long off) ? Math.Max(0, off) : 0;

        if (!TryResolveGalleryPath(relPath, out string fullPath, out string? resolveError))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { error = resolveError ?? "Invalid path." }));
            return;
        }

        if (!File.Exists(fullPath))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { error = "File not found." }));
            return;
        }

        await StreamFileResponseAsync(stream, fullPath, offset);
    }

    private async Task HandleGetThumbnailAsync(JsonElement request, NetworkStream stream)
    {
        if (!IsAuthorizedClient(request))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." }));
            return;
        }

        string relPath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        if (!TryResolveGalleryPath(relPath, out string fullPath, out string? resolveError))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { error = resolveError ?? "Invalid path." }));
            return;
        }

        if (!File.Exists(fullPath))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { error = "File not found." }));
            return;
        }

        string thumbPath = Path.Combine(Path.GetDirectoryName(fullPath)!, ".thumbnails", Path.GetFileNameWithoutExtension(fullPath) + ".jpg");
        if (File.Exists(thumbPath))
        {
            await StreamFileResponseAsync(stream, thumbPath);
            return;
        }

        await StreamFileResponseAsync(stream, fullPath);
    }

    private static async Task StreamFileResponseAsync(NetworkStream stream, string localPath, long offset = 0)
    {
        try
        {
            using FileStream fileStream = File.OpenRead(localPath);
            if (offset > 0 && offset < fileStream.Length)
                fileStream.Seek(offset, SeekOrigin.Begin);
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = true, size = fileStream.Length - fileStream.Position }));
            await fileStream.CopyToAsync(stream);
            await stream.FlushAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"File stream response failed mid-transfer: {ex.Message}");
        }
    }
}
