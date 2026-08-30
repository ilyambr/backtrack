using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Backtrack.Core;

namespace Backtrack;

public partial class MainWindow : Window
{
    private void ToggleStarClip(string clipKey)
    {
        if (string.IsNullOrWhiteSpace(clipKey)) return;
        string fileName = Path.GetFileName(clipKey);
        bool isStarred = _settings.StarredClips.Contains(clipKey) || _settings.StarredClips.Contains(fileName);
        bool newStarred = !isStarred;

        if (newStarred)
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
        if (_pairing is not null)
        {
            _ = _pairing.SendSyncStarredAsync(clipKey, newStarred);
        }
    }

    private void SaveClipMarkers(string clipKey, List<double> markers, bool syncToRemote = true)
    {
        if (string.IsNullOrWhiteSpace(clipKey)) return;
        string fileName = Path.GetFileName(clipKey);
        markers.Sort();
        _settings.ClipMarkers[clipKey] = markers;
        if (!string.Equals(fileName, clipKey, StringComparison.OrdinalIgnoreCase))
        {
            _settings.ClipMarkers[fileName] = markers;
        }
        _settings.Save();

        if (syncToRemote && _pairing is not null)
        {
            _ = _pairing.SendSyncClipMarkersAsync(clipKey, markers);
        }

        string? fullPath = null;
        if (File.Exists(clipKey))
        {
            fullPath = clipKey;
        }
        else if (!string.IsNullOrEmpty(_settings.ClipsFolder))
        {
            string candidate = Path.Combine(_settings.ClipsFolder, fileName);
            if (File.Exists(candidate))
                fullPath = candidate;
        }

        if (fullPath != null)
        {
            _ = EmbedChapterMarkersIntoVideoFileAsync(fullPath, markers);
        }
    }

    private async Task EmbedChapterMarkersIntoVideoFileAsync(string filePath, List<double> markers)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext != ".mp4" && ext != ".mkv" && ext != ".mov")
            return;

        string? ffmpegPath = ResolveFfmpegPath();
        if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            return;

        try
        {
            await Task.Run(async () =>
            {
                double durationSec = 0;
                var fi = new FileInfo(filePath);
                long? cachedMs = TryGetCachedDurationMs(fi);
                if (cachedMs.HasValue && cachedMs.Value > 0)
                {
                    durationSec = cachedMs.Value / 1000.0;
                }
                else
                {
                    durationSec = markers.Count > 0 ? markers.Max() + 10.0 : 60.0;
                }

                var sb = new StringBuilder();
                sb.AppendLine(";FFMETADATA1");

                var sorted = markers.Where(m => m >= 0).OrderBy(m => m).ToList();
                if (sorted.Count > 0)
                {
                    int bookmarkIdx = 1;
                    if (sorted[0] > 0.5)
                    {
                        sb.AppendLine("[CHAPTER]");
                        sb.AppendLine("TIMEBASE=1/1000");
                        sb.AppendLine("START=0");
                        sb.AppendLine($"END={(long)(sorted[0] * 1000)}");
                        sb.AppendLine("title=Start");
                    }

                    for (int i = 0; i < sorted.Count; i++)
                    {
                        double cur = sorted[i];
                        double next = (i + 1 < sorted.Count) ? sorted[i + 1] : Math.Max(cur + 1.0, durationSec);
                        sb.AppendLine("[CHAPTER]");
                        sb.AppendLine("TIMEBASE=1/1000");
                        sb.AppendLine($"START={(long)(cur * 1000)}");
                        sb.AppendLine($"END={(long)(next * 1000)}");
                        sb.AppendLine($"title=Bookmark {bookmarkIdx++}");
                    }
                }

                string metaPath = Path.Combine(Path.GetTempPath(), $"backtrack_meta_{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(metaPath, sb.ToString(), Encoding.UTF8);

                string dir = Path.GetDirectoryName(filePath)!;
                string tempOut = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(filePath)}_meta_tmp_{Guid.NewGuid():N}{ext}");

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = $"-y -i \"{filePath}\" -i \"{metaPath}\" -map_metadata 1 -codec copy \"{tempOut}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync();
                        if (proc.ExitCode == 0 && File.Exists(tempOut) && new FileInfo(tempOut).Length > 1024)
                        {
                            for (int attempt = 0; attempt < 5; attempt++)
                            {
                                try
                                {
                                    File.Move(tempOut, filePath, overwrite: true);
                                    AppLog.Write($"[Bookmarks] Embedded {sorted.Count} chapter marker(s) into {Path.GetFileName(filePath)}");
                                    break;
                                }
                                catch (IOException)
                                {
                                    await Task.Delay(200);
                                }
                            }
                        }
                        else
                        {
                            string err = await proc.StandardError.ReadToEndAsync();
                            AppLog.Write($"[Bookmarks] FFmpeg metadata injection failed: {err}");
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { }
                    try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { }
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Bookmarks] EmbedChapterMarkersAsync error: {ex.Message}");
        }
    }
}
