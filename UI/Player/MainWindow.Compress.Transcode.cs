using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Backtrack.Core;

namespace Backtrack;

public partial class MainWindow : Window
{
    private static readonly Regex FfmpegTimeRegex = new(@"time=(\d+):(\d+):(\d+\.?\d*)", RegexOptions.Compiled);
    private static readonly Regex CompressedSuffixRegex = new(@"\s*\(compressed\s+\d+(\.\d+)?\s*mb\)(\s*\(\d+\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string CleanBaseNameForCompression(string nameWithoutExt)
    {
        string cleaned = CompressedSuffixRegex.Replace(nameWithoutExt, "").Trim();
        return string.IsNullOrEmpty(cleaned) ? nameWithoutExt : cleaned;
    }

    internal static string? ResolveFfmpegPath()
    {
        string[] candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ThirdParty", "FFmpeg", "ffmpeg.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
            @"C:\ffmpeg\ffmpeg-9.0.1-essentials_build\bin\ffmpeg.exe",
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
        };

        foreach (string path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string p in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string target = Path.Combine(p, "ffmpeg.exe");
                if (File.Exists(target))
                    return target;
            }
        }

        try
        {
            var psi = new ProcessStartInfo("ffmpeg.exe", "-version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                proc.WaitForExit(1000);
                if (proc.ExitCode == 0)
                    return "ffmpeg.exe";
            }
        }
        catch { }

        return null;
    }

    private async Task CompressLocalClipAsync(string sourcePath, double targetMb, bool replaceOriginal)
    {
        if (!File.Exists(sourcePath))
        {
            _toastOverlay.ShowCompressFailed("Original clip no longer exists.");
            return;
        }

        string dir = Path.GetDirectoryName(sourcePath) ?? _settings.ClipsFolder;
        string nameWithoutExt = CleanBaseNameForCompression(Path.GetFileNameWithoutExtension(sourcePath));
        string ext = Path.GetExtension(sourcePath);
        string destPath = Path.Combine(dir, $"{nameWithoutExt} (compressed {targetMb:0.#}MB){ext}");

        int dup = 2;
        while (File.Exists(destPath))
        {
            destPath = Path.Combine(dir, $"{nameWithoutExt} (compressed {targetMb:0.#}MB) ({dup}){ext}");
            dup++;
        }

        long durationMs = TryGetCachedDurationMs(new FileInfo(sourcePath)) ?? 30000;
        double durationSec = Math.Max(1.0, durationMs / 1000.0);

        double totalBits = targetMb * 8.0 * 1024.0 * 1024.0;
        double audioBitrate = 128000.0;
        int videoBitrateKbps = Math.Max(64, (int)((totalBits / durationSec - audioBitrate) / 1000.0));

        Action<double> onProgress = prog =>
        {
            Dispatcher.Invoke(() => SetClipCompressing(sourcePath, prog));
        };

        bool success = await Task.Run(() => RunFfmpegOrLibvlcCompress(sourcePath, destPath, videoBitrateKbps, durationSec, onProgress));

        if (success && File.Exists(destPath))
        {
            if (replaceOriginal)
            {
                try
                {
                    File.Copy(destPath, sourcePath, overwrite: true);
                    File.Delete(destPath);

                    string thumbCache = GetThumbnailCachePath(new FileInfo(sourcePath));
                    if (File.Exists(thumbCache)) try { File.Delete(thumbCache); } catch { }
                    string durCache = GetDurationCachePath(new FileInfo(sourcePath));
                    if (File.Exists(durCache)) try { File.Delete(durCache); } catch { }
                }
                catch (Exception ex)
                {
                    AppLog.Write($"Failed to replace original file with compressed file: {ex.Message}");
                    _toastOverlay.ShowCompressFailed(ex.Message);
                    ClearClipCompressing(sourcePath);
                    return;
                }

                _toastOverlay.ShowCompressSaved(Path.GetFileName(sourcePath));
            }
            else
            {
                _toastOverlay.ShowCompressSaved(Path.GetFileName(destPath));
            }
        }
        else
        {
            _toastOverlay.ShowCompressFailed("Compression failed.");
        }

        ClearClipCompressing(sourcePath);

        Dispatcher.Invoke(() =>
        {
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
            RefreshRecentClipsOverlay();
        });
    }

    private async Task CompressRemoteStreamingClipAsync(string relativePath, double targetMb, bool replaceOriginal)
    {
        SetClipCompressing(relativePath, 0.0);

        var progress = new Progress<double>(pct =>
        {
            Dispatcher.Invoke(() => SetClipCompressing(relativePath, pct));
        });

        (bool success, string? error, string? newPath, long size) = await _pairing.CompressRemoteClipAsync(relativePath, targetMb, progress);
        if (success && newPath is not null)
        {
            _toastOverlay.ShowCompressSaved(Path.GetFileName(newPath));
        }
        else
        {
            _toastOverlay.ShowCompressFailed(error ?? "Remote compression failed.");
        }

        ClearClipCompressing(relativePath);

        Dispatcher.Invoke(() =>
        {
            if (GalleryPanel.Visibility == Visibility.Visible)
                _ = LoadRemoteGalleryAsync();
            RefreshRecentClipsOverlay();
        });
    }

    public async Task<(bool Success, string? Error, string? NewFileName, long FileSize)> CompressClipForRemoteHostAsync(string fullPath, double targetMb, Action<double>? onProgress)
    {
        if (!File.Exists(fullPath))
            return (false, "Clip file not found.", null, 0);

        string dir = Path.GetDirectoryName(fullPath) ?? _settings.ClipsFolder;
        string nameWithoutExt = CleanBaseNameForCompression(Path.GetFileNameWithoutExtension(fullPath));
        string ext = Path.GetExtension(fullPath);
        string destPath = Path.Combine(dir, $"{nameWithoutExt} (compressed {targetMb:0.#}MB){ext}");

        int dup = 2;
        while (File.Exists(destPath))
        {
            destPath = Path.Combine(dir, $"{nameWithoutExt} (compressed {targetMb:0.#}MB) ({dup}){ext}");
            dup++;
        }

        long durationMs = TryGetCachedDurationMs(new FileInfo(fullPath)) ?? 30000;
        double durationSec = Math.Max(1.0, durationMs / 1000.0);

        double totalBits = targetMb * 8.0 * 1024.0 * 1024.0;
        double audioBitrate = 128000.0;
        int videoBitrateKbps = Math.Max(64, (int)((totalBits / durationSec - audioBitrate) / 1000.0));

        bool success = await Task.Run(() => RunFfmpegOrLibvlcCompress(fullPath, destPath, videoBitrateKbps, durationSec, onProgress));
        if (success && File.Exists(destPath))
        {
            var info = new FileInfo(destPath);
            return (true, null, info.Name, info.Length);
        }

        return (false, "Compression transcode failed.", null, 0);
    }

    private static string? _cachedH264EncoderArgs;

    private static string GetFastestVideoEncoderArgs(string ffmpegPath, int videoBitrateKbps)
    {
        if (_cachedH264EncoderArgs != null)
            return _cachedH264EncoderArgs.Replace("{BITRATE}", videoBitrateKbps.ToString());

        string testFile = Path.Combine(Path.GetTempPath(), $"backtrack_enc_test_{Guid.NewGuid():N}.mp4");
        string selectedEncoder = "-c:v libx264 -preset veryfast -b:v {BITRATE}k -maxrate {BITRATE}k -bufsize " + (videoBitrateKbps * 2) + "k";

        string[] hardwareEncoders = new[]
        {
            "-c:v h264_nvenc -preset p2 -b:v {BITRATE}k -maxrate {BITRATE}k -bufsize " + (videoBitrateKbps * 2) + "k",
            "-c:v h264_amf -quality speed -b:v {BITRATE}k -maxrate {BITRATE}k -bufsize " + (videoBitrateKbps * 2) + "k",
            "-c:v h264_qsv -preset veryfast -b:v {BITRATE}k -maxrate {BITRATE}k -bufsize " + (videoBitrateKbps * 2) + "k",
        };

        foreach (var enc in hardwareEncoders)
        {
            try
            {
                string concreteArgs = enc.Replace("{BITRATE}", "500");
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -f lavfi -i color=c=black:s=64x64:d=0.1 {concreteArgs} -f mp4 \"{testFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(1500);
                    if (proc.ExitCode == 0)
                    {
                        selectedEncoder = enc;
                        break;
                    }
                }
            }
            catch { }
            finally
            {
                try { if (File.Exists(testFile)) File.Delete(testFile); } catch { }
            }
        }

        _cachedH264EncoderArgs = selectedEncoder;
        return _cachedH264EncoderArgs.Replace("{BITRATE}", videoBitrateKbps.ToString());
    }

    private bool RunFfmpegOrLibvlcCompress(string sourcePath, string destPath, int videoBitrateKbps, double durationSec, Action<double>? onProgress)
    {
        string? ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null)
        {
            return false;
        }

        try
        {
            string videoArgs = GetFastestVideoEncoderArgs(ffmpegPath, videoBitrateKbps);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -i \"{sourcePath}\" {videoArgs} -c:a aac -b:a 128k -movflags +faststart \"{destPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                var match = FfmpegTimeRegex.Match(e.Data);
                if (match.Success && durationSec > 0)
                {
                    double h = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    double m = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    double s = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    double currentSec = h * 3600 + m * 60 + s;
                    double progress = Math.Clamp(currentSec / durationSec, 0.0, 1.0);
                    onProgress?.Invoke(progress);
                }
            };

            proc.Start();
            proc.BeginErrorReadLine();
            proc.WaitForExit();

            return proc.ExitCode == 0 && File.Exists(destPath) && new FileInfo(destPath).Length > 0;
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Compress] Error during transcode: {ex.Message}");
            return false;
        }
    }
}
