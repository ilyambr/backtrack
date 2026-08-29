using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Backtrack.Core;
using Backtrack.Pairing;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
    private static readonly Regex FfmpegTimeRegex = new(@"time=(\d+):(\d+):(\d+\.?\d*)", RegexOptions.Compiled);
    private static readonly Regex CompressedSuffixRegex = new(@"\s*\(compressed\s+\d+(\.\d+)?\s*mb\)(\s*\(\d+\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Dictionary<string, double> _activeCompressingClips = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string, double>? ClipCompressionProgressChanged;
    public event Action<string>? ClipCompressionCompleted;

    private static string CleanBaseNameForCompression(string nameWithoutExt)
    {
        string cleaned = CompressedSuffixRegex.Replace(nameWithoutExt, "").Trim();
        return string.IsNullOrEmpty(cleaned) ? nameWithoutExt : cleaned;
    }

    private double _selectedCompressMb = 10.0;
    private bool _isCustomCompressSelected = false;

    private void SetClipCompressing(string clipPath, double progress)
    {
        _activeCompressingClips[clipPath] = progress;
        string fileName = Path.GetFileName(clipPath);
        if (!string.IsNullOrEmpty(fileName))
            _activeCompressingClips[fileName] = progress;

        Dispatcher.BeginInvoke(() => ClipCompressionProgressChanged?.Invoke(clipPath, progress));
    }

    private void ClearClipCompressing(string clipPath)
    {
        _activeCompressingClips.Remove(clipPath);
        string fileName = Path.GetFileName(clipPath);
        if (!string.IsNullOrEmpty(fileName))
            _activeCompressingClips.Remove(fileName);

        Dispatcher.BeginInvoke(() => ClipCompressionCompleted?.Invoke(clipPath));
    }
    private static string? ResolveFfmpegPath()
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

    internal void PlayerCompress_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;
        CompressStatusText.Text = string.Empty;
        UpdateCompressPresetButtonsUi();
        CompressPopup.IsOpen = true;
        RepositionPlayerPopups();
    }

    internal void CloseCompressPopup_Click(object sender, RoutedEventArgs e)
    {
        CompressPopup.IsOpen = false;
    }

    private void UpdateCompressPresetButtonsUi()
    {
        var accentBrush = (Brush)FindResource("Accent");
        var regularBrush = (Brush)FindResource("RowBg");
        var text0 = (Brush)FindResource("Text0");
        var textBg = (Brush)FindResource("PanelBg");

        Button[] buttons = new[] { CompressPreset10, CompressPreset25, CompressPreset50, CompressPreset100, CompressPreset250 };
        foreach (var btn in buttons)
        {
            if (btn is null) continue;
            bool isSel = !_isCustomCompressSelected && btn.Tag is string tag && double.TryParse(tag, out double mb) && Math.Abs(mb - _selectedCompressMb) < 0.1;
            btn.Background = isSel ? accentBrush : regularBrush;
            btn.Foreground = isSel ? textBg : text0;
        }

        if (CompressCustomButton is not null)
        {
            CompressCustomButton.Background = _isCustomCompressSelected ? accentBrush : regularBrush;
            CompressCustomButton.Foreground = _isCustomCompressSelected ? textBg : text0;
        }
    }

    internal void CompressPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, out double targetMb))
        {
            _selectedCompressMb = targetMb;
            _isCustomCompressSelected = false;
            CompressCustomRow.Visibility = Visibility.Collapsed;
            UpdateCompressPresetButtonsUi();
        }
    }

    internal void CompressCustomButton_Click(object sender, RoutedEventArgs e)
    {
        _isCustomCompressSelected = true;
        CompressCustomRow.Visibility = Visibility.Visible;
        UpdateCompressPresetButtonsUi();
        CompressCustomMbBox.Focus();
        CompressCustomMbBox.SelectAll();
    }

    internal void CompressReplace_Click(object sender, RoutedEventArgs e) =>
        RunCompressAction(replaceOriginal: true);

    internal void CompressSaveNew_Click(object sender, RoutedEventArgs e) =>
        RunCompressAction(replaceOriginal: false);

    private void RunCompressAction(bool replaceOriginal)
    {
        double targetMb = _selectedCompressMb;
        if (_isCustomCompressSelected)
        {
            if (!double.TryParse(CompressCustomMbBox.Text.Trim(), out targetMb) || targetMb <= 0)
            {
                CompressStatusText.Text = "Please enter a valid size in MB.";
                return;
            }
        }

        CompressPopup.IsOpen = false;

        FileInfo? localFile = _currentPlayerFile;
        (string RelativePath, string DeviceId)? remoteOrigin = _currentPlayerRemoteOrigin;
        Screen backTarget = _playerBackTarget;

        if (localFile is not null)
        {
            SetClipCompressing(localFile.FullName, 0.0);
        }
        else if (remoteOrigin is not null)
        {
            SetClipCompressing(remoteOrigin.Value.RelativePath, 0.0);
        }

        StopPlayerPlayback();
        if (backTarget == Screen.Gallery)
        {
            ShowScreen(Screen.Gallery);
            LoadGallery();
        }
        else
        {
            ShowScreen(Screen.Idle);
            RefreshRecentClipsOverlay();
            _recentClipsOverlay.Show();
        }

        if (localFile is not null)
        {
            _ = CompressLocalClipAsync(localFile.FullName, targetMb, replaceOriginal);
        }
        else if (remoteOrigin is not null)
        {
            _ = CompressRemoteStreamingClipAsync(remoteOrigin.Value.RelativePath, targetMb, replaceOriginal);
        }
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

        string destPath;
        if (replaceOriginal)
        {
            destPath = Path.Combine(Path.GetTempPath(), $"backtrack_compress_{Guid.NewGuid():N}{ext}");
        }
        else
        {
            destPath = Path.Combine(dir, $"{nameWithoutExt} (compressed {targetMb:0.#}MB){ext}");
            int dup = 2;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(dir, $"{nameWithoutExt} (compressed {targetMb:0.#}MB) ({dup}){ext}");
                dup++;
            }
        }

        SetClipCompressing(sourcePath, 0.0);

        long durationMs = TryGetCachedDurationMs(new FileInfo(sourcePath)) ?? (_vlcPlayer?.Length > 0 ? _vlcPlayer.Length : 30000);
        double durationSec = Math.Max(1.0, durationMs / 1000.0);

        double totalBits = targetMb * 8.0 * 1024.0 * 1024.0;
        double audioBitrate = 128000.0;
        int videoBitrateKbps = Math.Max(64, (int)((totalBits / durationSec - audioBitrate) / 1000.0));

        bool success = await Task.Run(() => RunFfmpegOrLibvlcCompress(sourcePath, destPath, videoBitrateKbps, durationSec, p => SetClipCompressing(sourcePath, p)));

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

        (bool success, string? error, string? newPath, long size) = await _pairing.CompressRemoteClipAsync(relativePath, targetMb);
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

    public async Task<(bool Success, string? Error, string? NewFileName, long FileSize)> CompressClipForRemoteHostAsync(string fullPath, double targetMb)
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

        bool success = await Task.Run(() => RunFfmpegOrLibvlcCompress(fullPath, destPath, videoBitrateKbps, durationSec, null));
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
        if (_cachedH264EncoderArgs is not null)
        {
            return string.Format(_cachedH264EncoderArgs, videoBitrateKbps, videoBitrateKbps * 2);
        }

        (string enc, string argsTemplate)[] candidateEncoders = new[]
        {
            ("h264_nvenc", "-c:v h264_nvenc -preset p1 -tune ull -b:v {0}k -maxrate {0}k -bufsize {1}k"),
            ("h264_amf", "-c:v h264_amf -quality speed -b:v {0}k -maxrate {0}k -bufsize {1}k"),
            ("h264_qsv", "-c:v h264_qsv -preset veryfast -b:v {0}k -maxrate {0}k -bufsize {1}k"),
            ("h264_mf", "-c:v h264_mf -b:v {0}k"),
        };

        foreach (var (enc, argsTemplate) in candidateEncoders)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-f lavfi -i color=size=256x256:rate=30:duration=0.1 -c:v {enc} -f null -",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    proc.WaitForExit(1000);
                    if (proc.ExitCode == 0)
                    {
                        AppLog.Write($"Hardware video encoder active: {enc}");
                        _cachedH264EncoderArgs = argsTemplate;
                        return string.Format(argsTemplate, videoBitrateKbps, videoBitrateKbps * 2);
                    }
                }
            }
            catch { }
        }

        _cachedH264EncoderArgs = "-c:v libx264 -preset ultrafast -threads 0 -b:v {0}k -maxrate {0}k -bufsize {1}k";
        AppLog.Write("Hardware video encoder not available, using libx264 ultrafast.");
        return string.Format(_cachedH264EncoderArgs, videoBitrateKbps, videoBitrateKbps * 2);
    }

    private bool RunFfmpegOrLibvlcCompress(string sourcePath, string destPath, int videoBitrateKbps, double durationSec, Action<double>? onProgress)
    {
        string? ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null)
        {
            AppLog.Write("FFmpeg not found for compression.");
            return false;
        }

        string videoArgs = GetFastestVideoEncoderArgs(ffmpegPath, videoBitrateKbps);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -hwaccel auto -i \"{sourcePath}\" -map 0:v:0 -map 0:a? {videoArgs} -c:a copy -movflags +faststart \"{destPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var proc = new Process { StartInfo = psi };
            var stderrOutput = new System.Text.StringBuilder();

            proc.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    stderrOutput.AppendLine(args.Data);

                    if (onProgress is not null && durationSec > 0)
                    {
                        var match = FfmpegTimeRegex.Match(args.Data);
                        if (match.Success)
                        {
                            if (double.TryParse(match.Groups[1].Value, out double h) &&
                                double.TryParse(match.Groups[2].Value, out double m) &&
                                double.TryParse(match.Groups[3].Value, out double s))
                            {
                                double currentSec = h * 3600.0 + m * 60.0 + s;
                                double progress = Math.Clamp(currentSec / durationSec, 0.0, 0.99);
                                onProgress(progress);
                            }
                        }
                    }
                }
            };

            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            bool exited = proc.WaitForExit(300000);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                AppLog.Write("FFmpeg compression timed out after 5 minutes.");
                if (File.Exists(destPath))
                {
                    try { File.Delete(destPath); } catch { }
                }
                return false;
            }

            if (proc.ExitCode == 0 && File.Exists(destPath))
            {
                var fi = new FileInfo(destPath);
                if (fi.Length > 10000)
                {
                    AppLog.Write($"FFmpeg compression succeeded: {destPath} ({fi.Length / 1024 / 1024} MB)");
                    return true;
                }
            }

            AppLog.Write($"FFmpeg compression failed with exit code {proc.ExitCode}. Stderr: {stderrOutput}");
            if (File.Exists(destPath))
            {
                try { File.Delete(destPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"FFmpeg compression exception: {ex.Message}");
            if (File.Exists(destPath))
            {
                try { File.Delete(destPath); } catch { }
            }
        }

        return false;
    }
}
