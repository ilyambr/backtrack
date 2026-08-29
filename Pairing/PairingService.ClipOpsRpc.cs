using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Backtrack.Core;
using Backtrack.Interop;

namespace Backtrack.Pairing;

public sealed partial class PairingService
{
    private string HandleDeleteClip(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." });

        string relPath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        if (!TryResolveGalleryPath(relPath, out string fullPath, out string? resolveError))
            return JsonSerializer.Serialize(new { error = resolveError ?? "Invalid path." });

        if (!File.Exists(fullPath))
            return JsonSerializer.Serialize(new { error = "File not found." });

        try
        {
            RecycleBin.Delete(fullPath);
            return JsonSerializer.Serialize(new { success = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private string HandleRenameClip(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." });

        string relPath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        string newName = request.TryGetProperty("newName", out JsonElement n) ? n.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(newName))
            return JsonSerializer.Serialize(new { error = "Invalid new name." });

        if (!TryResolveGalleryPath(relPath, out string fullPath, out string? resolveError))
            return JsonSerializer.Serialize(new { error = resolveError ?? "Invalid path." });

        if (!File.Exists(fullPath))
            return JsonSerializer.Serialize(new { error = "File not found." });

        try
        {
            string ext = Path.GetExtension(fullPath);
            string sanitizedNewName = Path.GetFileNameWithoutExtension(newName).Trim();
            if (string.IsNullOrEmpty(sanitizedNewName))
                return JsonSerializer.Serialize(new { error = "Invalid new name." });

            string dir = Path.GetDirectoryName(fullPath)!;
            string newFileName = sanitizedNewName + ext;
            string destPath = Path.Combine(dir, newFileName);

            if (string.Equals(fullPath, destPath, StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(new { success = true, path = relPath });

            if (File.Exists(destPath))
                return JsonSerializer.Serialize(new { error = "A file with that name already exists." });

            File.Move(fullPath, destPath);

            string newRelPath = WithNewFileName(relPath, newFileName);
            return JsonSerializer.Serialize(new { success = true, path = newRelPath });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private string HandleMoveClip(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." });

        string destRel = request.TryGetProperty("destination", out JsonElement d) ? d.GetString() ?? "" : "";
        if (!TryResolveGalleryPath(destRel, out string destDir, out string? resolveError))
            return JsonSerializer.Serialize(new { error = resolveError ?? "Invalid destination path." });

        if (!Directory.Exists(destDir))
            return JsonSerializer.Serialize(new { error = "Destination folder not found." });

        List<string> paths = new();
        if (request.TryGetProperty("paths", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in pEl.EnumerateArray())
            {
                string? s = item.GetString();
                if (!string.IsNullOrEmpty(s)) paths.Add(s);
            }
        }
        else if (request.TryGetProperty("path", out JsonElement singleP))
        {
            string? s = singleP.GetString();
            if (!string.IsNullOrEmpty(s)) paths.Add(s);
        }

        if (paths.Count == 0)
            return JsonSerializer.Serialize(new { error = "No paths provided to move." });

        int movedCount = 0;
        List<string> errors = new();

        foreach (string relPath in paths)
        {
            if (!TryResolveGalleryPath(relPath, out string srcPath, out string? srcErr))
            {
                errors.Add($"{relPath}: {srcErr ?? "Invalid path"}");
                continue;
            }

            bool isFile = File.Exists(srcPath);
            bool isDir = Directory.Exists(srcPath);

            if (!isFile && !isDir)
            {
                errors.Add($"{relPath}: File or folder not found.");
                continue;
            }

            try
            {
                string name = Path.GetFileName(srcPath);
                string targetPath = Path.Combine(destDir, name);

                if (string.Equals(Path.GetFullPath(srcPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (isDir)
                {
                    if (targetPath.StartsWith(srcPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"{name}: Cannot move a folder into itself.");
                        continue;
                    }
                    if (Directory.Exists(targetPath))
                    {
                        errors.Add($"{name}: A folder with that name already exists in destination.");
                        continue;
                    }
                    Directory.Move(srcPath, targetPath);
                    movedCount++;
                }
                else
                {
                    string finalPath = targetPath;
                    int copyIdx = 2;
                    string nameNoExt = Path.GetFileNameWithoutExtension(name);
                    string ext = Path.GetExtension(name);
                    while (File.Exists(finalPath))
                    {
                        finalPath = Path.Combine(destDir, $"{nameNoExt} ({copyIdx}){ext}");
                        copyIdx++;
                    }
                    File.Move(srcPath, finalPath);
                    movedCount++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{relPath}: {ex.Message}");
            }
        }

        if (movedCount == 0 && errors.Count > 0)
            return JsonSerializer.Serialize(new { error = string.Join("; ", errors) });

        return JsonSerializer.Serialize(new { success = true, moved = movedCount });
    }

    private static string WithNewFileName(string relativePath, string newFileName)
    {
        string sanitized = relativePath.Replace('\\', '/');
        int lastSlash = sanitized.LastIndexOf('/');
        return lastSlash >= 0 ? sanitized.Substring(0, lastSlash + 1) + newFileName : newFileName;
    }
}
