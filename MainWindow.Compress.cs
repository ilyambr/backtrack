using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Backtrack.Core;
using Backtrack.Pairing;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
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

    private void PlayerCompress_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;
        CompressStatusText.Text = string.Empty;
        CompressCustomRow.Visibility = Visibility.Collapsed;
        CompressPopup.IsOpen = true;
    }

    private void CompressCustomButton_Click(object sender, RoutedEventArgs e)
    {
        CompressCustomRow.Visibility = CompressCustomRow.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (CompressCustomRow.Visibility == Visibility.Visible)
        {
            CompressCustomMbBox.Focus();
            CompressCustomMbBox.SelectAll();
        }
    }

    private void CompressPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, out double targetMb))
        {
            CompressPopup.IsOpen = false;
            _ = ExecuteCompressCurrentClipAsync(targetMb);
        }
    }

    private void CompressCustomExecute_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(CompressCustomMbBox.Text.Trim(), out double targetMb) && targetMb > 0)
        {
            CompressPopup.IsOpen = false;
            _ = ExecuteCompressCurrentClipAsync(targetMb);
        }
        else
        {
            CompressStatusText.Text = "Please enter a valid size in MB.";
        }
    }

    private async Task ExecuteCompressCurrentClipAsync(double targetMb)
    {
        if (_currentPlayerFile is not null)
        {
            await CompressLocalClipAsync(_currentPlayerFile.FullName, targetMb);
        }
        else if (_currentPlayerRemoteOrigin is not null)
        {
            await CompressRemoteStreamingClipAsync(_currentPlayerRemoteOrigin.Value.RelativePath, targetMb);
        }
    }

    private async Task CompressLocalClipAsync(string sourcePath, double targetMb)
    {
        if (!File.Exists(sourcePath))
        {
            _toastOverlay.ShowCompressFailed("Original clip no longer exists.");
            return;
        }

        string dir = Path.GetDirectoryName(sourcePath) ?? _settings.ClipsFolder;
        string nameWithoutExt = Path.GetFileNameWithoutExtension(sourcePath);
        string ext = Path.GetExtension(sourcePath);
        string destPath = Path.Combine(dir, $"{nameWithoutExt} (compressed {targetMb:0.#}MB){ext}");

        int dup = 2;
        while (File.Exists(destPath))
        {
            destPath = Path.Combine(dir, $"{nameWithoutExt} (compressed {targetMb:0.#}MB) ({dup}){ext}");
            dup++;
        }

        _toastOverlay.ShowCompressStarted($"Target: {targetMb:0.#} MB");

        long durationMs = TryGetCachedDurationMs(new FileInfo(sourcePath)) ?? (_vlcPlayer?.Length > 0 ? _vlcPlayer.Length : 30000);
        double durationSec = Math.Max(1.0, durationMs / 1000.0);

        double totalBits = targetMb * 8.0 * 1024.0 * 1024.0;
        double audioBitrate = 128000.0;
        int videoBitrateKbps = Math.Max(64, (int)((totalBits / durationSec - audioBitrate) / 1000.0));

        bool success = await Task.Run(() => RunFfmpegOrLibvlcCompress(sourcePath, destPath, videoBitrateKbps));

        if (success && File.Exists(destPath))
        {
            FileInfo destInfo = new FileInfo(destPath);
            double actualMb = destInfo.Length / (1024.0 * 1024.0);
            _toastOverlay.ShowTrimSaved(Path.GetFileName(destPath));
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();

            // Automatically open the compressed clip in player
            OpenInPlayer(destInfo);
        }
        else
        {
            _toastOverlay.ShowCompressFailed("Compression failed.");
        }
    }

    private async Task CompressRemoteStreamingClipAsync(string relativePath, double targetMb)
    {
        _toastOverlay.ShowCompressStarted($"Target: {targetMb:0.#} MB (Remote)");
        (bool success, string? error, string? newPath, long size) = await _pairing.CompressRemoteClipAsync(relativePath, targetMb);
        if (success && newPath is not null)
        {
            _toastOverlay.ShowTrimSaved(Path.GetFileName(newPath));
            if (GalleryPanel.Visibility == Visibility.Visible)
                _ = LoadRemoteGalleryAsync();

            // Automatically open the remote compressed clip
            var remoteFile = new RemoteGalleryFile(Path.GetFileName(newPath), size, DateTime.UtcNow);
            OpenRemoteClipStreaming(newPath, remoteFile);
        }
        else
        {
            _toastOverlay.ShowCompressFailed(error ?? "Remote compression failed.");
        }
    }

    public async Task<(bool Success, string? Error, string? NewFileName, long FileSize)> CompressClipForRemoteHostAsync(string fullPath, double targetMb)
    {
        if (!File.Exists(fullPath))
            return (false, "Clip file not found.", null, 0);

        string dir = Path.GetDirectoryName(fullPath) ?? _settings.ClipsFolder;
        string nameWithoutExt = Path.GetFileNameWithoutExtension(fullPath);
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

        bool success = await Task.Run(() => RunFfmpegOrLibvlcCompress(fullPath, destPath, videoBitrateKbps));
        if (success && File.Exists(destPath))
        {
            var info = new FileInfo(destPath);
            return (true, null, info.Name, info.Length);
        }

        return (false, "Compression transcode failed.", null, 0);
    }

    private bool RunFfmpegOrLibvlcCompress(string sourcePath, string destPath, int videoBitrateKbps)
    {
        string? ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is not null)
        {
            try
            {
                var psi = new ProcessStartInfo(ffmpegPath,
                    $"-y -i \"{sourcePath}\" -c:v libx264 -b:v {videoBitrateKbps}k -maxrate {videoBitrateKbps}k -bufsize {videoBitrateKbps * 2}k -preset fast -c:a aac -b:a 128k -movflags +faststart \"{destPath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    proc.WaitForExit(300000); // 5 min max
                    if (proc.ExitCode == 0 && File.Exists(destPath) && new FileInfo(destPath).Length > 1000)
                    {
                        AppLog.Write($"FFmpeg compression succeeded: {destPath} ({new FileInfo(destPath).Length} bytes)");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"FFmpeg compression exception: {ex.Message}");
            }
        }

        // Fallback: LibVLC transcode with proper synchronization
        try
        {
            if (_libVlc is not null)
            {
                using var media = new LibVlc.Media(_libVlc, new Uri(sourcePath));
                media.AddOption($":sout=#transcode{{vcodec=h264,vb={videoBitrateKbps},acodec=mp4a,ab=128,channels=2,samplerate=44100}}:std{{access=file,mux=mp4,dst={destPath.Replace("\\", "/")}}}");
                media.AddOption(":sout-keep");

                using var exportPlayer = new LibVlc.MediaPlayer(media);
                using var done = new ManualResetEventSlim(false);
                bool encounteredError = false;

                exportPlayer.EndReached += (_, _) => done.Set();
                exportPlayer.EncounteredError += (_, _) =>
                {
                    encounteredError = true;
                    done.Set();
                };

                exportPlayer.Play();
                if (done.Wait(TimeSpan.FromMinutes(10)) && !encounteredError)
                {
                    exportPlayer.Stop();
                    if (File.Exists(destPath) && new FileInfo(destPath).Length > 1000)
                    {
                        AppLog.Write($"LibVLC transcode succeeded: {destPath} ({new FileInfo(destPath).Length} bytes)");
                        return true;
                    }
                }
                exportPlayer.Stop();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"LibVLC transcode fallback exception: {ex.Message}");
        }

        return false;
    }
}
