using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;
using Backtrack.Streaming;
using Backtrack.Updates;
using Microsoft.Win32;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
    private static string FormatFileSize(long bytes)
    {
        const double mb = 1024.0 * 1024.0;
        double sizeMb = bytes / mb;
        return sizeMb >= 1000 ? $"{sizeMb / 1024.0:0.#} GB" : $"{sizeMb:0.#} MB";
    }

    private DateTime _lastQuickOpenUtc = DateTime.MinValue;

    private static string FormatDuration(long ms)
    {
        int totalSeconds = (int)(ms / 1000);
        int h = totalSeconds / 3600;
        int m = totalSeconds / 60 % 60;
        int s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }

    private const string FullscreenEnterIcon = "M7,14H5v5h5v-2H7V14zM5,10h2V7h3V5H5V10zM17,17h-3v2h5v-5h-2V17zM14,5v2h3v3h2V5H14z";

    private const string FullscreenExitIcon = "M5,16h3v3h2v-5H5V16zM8,8H5v2h5V5H8V8zM14,19h2v-3h3v-2h-5V19zM16,5h-2v5h5V8h-3V5z";

    private const string VolumeUpIcon = "M3,9v6h4l5,5V4L7,9H3z M16.5,12c0,-1.77 -1.02,-3.29 -2.5,-4.03v8.05c1.48,-0.73 2.5,-2.26 2.5,-4.02z M14,3.23v2.06c2.89,0.86 5,3.54 5,6.71s-2.11,5.85 -5,6.71v2.06c4.01,-0.91 7,-4.49 7,-8.77s-2.99,-7.86 -7,-8.77z";

    private const string VolumeOffIcon = "M16.5,12c0,-1.77 -1.02,-3.29 -2.5,-4.03v2.21l2.45,2.45c0.03,-0.2 0.05,-0.41 0.05,-0.63z M19,12c0,0.94 -0.2,1.82 -0.54,2.64l1.51,1.51C20.63,14.91 21,13.5 21,12c0,-4.28 -2.99,-7.86 -7,-8.77v2.06c2.89,0.86 5,3.54 5,6.71z M4.27,3L3,4.27L7.73,9H3v6h4l5,5v-6.73l4.25,4.25c-0.67,0.52 -1.42,0.93 -2.25,1.18v2.06c1.38,-0.31 2.63,-0.95 3.69,-1.81L19.73,21L21,19.73L4.27,3z M12,4L9.91,6.09L12,8.18V4z";

    private const string FeedbackPlayIcon = "M8,5v14l11,-7z";

    private const string FeedbackPauseIcon = "M6,19h4V5H6V19z M14,5v14h4V5H14z";

    private const string FeedbackSeekForwardIcon = "M4,18l8.5,-6L4,6v12z M13,6v12l8.5,-6L13,6z";

    private const string FeedbackSeekBackIcon = "M11,18V6l-8.5,6L11,18z M20,18V6l-8.5,6L20,18z";

    private enum PlayerFeedbackIcon { Play, Pause, SeekForward, SeekBack, Volume, Mute }

    private bool _isPlayerFullscreen;

    private double _preFullscreenWidth;

    private double _preFullscreenLeft;

    private string DescribeRowDestDir(string destDir)
    {
        if (string.IsNullOrEmpty(destDir))
            return "Not set -- clips stay wherever this buffer writes them";
        return IsWithinClipsFolder(destDir, out string relative)
            ? (relative.Length == 0 ? "Main clips folder" : relative)
            : destDir;
    }

    private const int RecordStatusInactive = 0;

    private const int RecordStatusStopped = 1;

    private const int RecordStatusRecording = 2;

    private const int RecordStatusError = 3;

    private const int RecordStatusNoSignal = 4;

    private const long BytesPerGb = 1024L * 1024L * 1024L;

    private long GetClipsFolderUsageBytes()
    {
        if (string.IsNullOrWhiteSpace(_settings.ClipsFolder) || !Directory.Exists(_settings.ClipsFolder))
            return 0;
        try
        {
            return Directory.EnumerateFiles(_settings.ClipsFolder, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !_pendingDeletePaths.Contains(Path.GetFullPath(f)))
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    private DispatcherTimer? _autoDeleteOldClipsTimer;

    private void RestartAutoDeleteOldClipsTimer()
    {
        _autoDeleteOldClipsTimer?.Stop();
        _autoDeleteOldClipsTimer = null;

        if (!_settings.AutoDeleteOldClipsEnabled)
            return;

        RunAutoDeleteOldClips();
        _autoDeleteOldClipsTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _autoDeleteOldClipsTimer.Tick += (_, _) => RunAutoDeleteOldClips();
        _autoDeleteOldClipsTimer.Start();
    }

    private const int MinClipSeconds = 15;

    private static int SliderPosToSeconds(double pos, int maxSeconds)
    {
        double t = pos / 1000.0;
        return (int)Math.Round(MinClipSeconds + (maxSeconds - MinClipSeconds) * t * t);
    }

    private static double SecondsToSliderPos(int seconds, int maxSeconds)
    {
        double t = Math.Sqrt(Math.Clamp((seconds - MinClipSeconds) / (double)(maxSeconds - MinClipSeconds), 0, 1));
        return t * 1000.0;
    }

    private static void SetSliderValueFromMouse(Slider slider, Point mousePos)
    {
        double width = slider.ActualWidth;
        if (width <= 0)
            return;
        double ratio = Math.Clamp(mousePos.X / width, 0.0, 1.0);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }

    private void AddInfoLine(Panel container, string text)
    {
        container.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)FindResource("Text2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 4, 2, 4),
        });
    }

    private static readonly string[] VideoExtensions = GalleryFormats.VideoExtensions;

    private int CountClips()
    {
        try
        {
            return Directory.Exists(_settings.ClipsFolder)
                ? Directory.EnumerateFiles(_settings.ClipsFolder, "*", SearchOption.AllDirectories)
                    .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new FileInfo(f))
                    .Count(f => f.Exists && f.Length > 0 && TryGetCachedDurationMs(f) is not < 2000)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private string? GetNewestClipPath()
    {
        try
        {
            string? newest = Directory.Exists(_settings.ClipsFolder)
                ? Directory.EnumerateFiles(_settings.ClipsFolder, "*", SearchOption.AllDirectories)
                    .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new FileInfo(f))
                    .Where(f => f.Exists && f.Length > 0 && TryGetCachedDurationMs(f) is not < 2000)
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault()
                    ?.FullName
                : null;

            return newest is null ? null : Path.GetFullPath(newest);
        }
        catch
        {
            return null;
        }
    }

    private long _clipOpenToken;

    private string? _currentStreamToken;

    private long _remoteStreamTotalBytes;

    private string GetRemoteClipCachePath(string relativePath, string fileName) => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Backtrack", "RemoteCache", _settings.PairedPeerDeviceId ?? "",
    Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "",
    fileName);

    private StackPanel WithNewestDot(TextBlock title, string tooltip)
    {
        Thickness titleMargin = title.Margin;
        title.Margin = new Thickness(4, 0, 0, 0);

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = (Brush)FindResource("NewestClip"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = titleMargin, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(dot);
        row.Children.Add(title);
        return row;
    }

    private UIElement MakeLinkIcon()
    {
        var geo1 = Geometry.Parse("M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71");
        var geo2 = Geometry.Parse("M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71");
        var combined = new GeometryGroup();
        combined.Children.Add(geo1);
        combined.Children.Add(geo2);

        var path = new System.Windows.Shapes.Path
        {
            Data = combined,
            Stroke = (Brush)FindResource("Text2"),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Linked clip (deduplicated)",
            Margin = new Thickness(0, 0, 4, 0),
        };
        return path;
    }

    private StackPanel WithDeduplicationDot(StackPanel? existingRow, TextBlock title)
    {
        UIElement linkIcon = MakeLinkIcon();

        if (existingRow is not null)
        {
            linkIcon = MakeLinkIcon();
            ((System.Windows.Shapes.Path)linkIcon).Margin = new Thickness(6, 0, 4, 0);
            existingRow.Children.Insert(existingRow.Children.Count - 1, linkIcon);
            return existingRow;
        }

        Thickness titleMargin = title.Margin;
        title.Margin = new Thickness(0, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = titleMargin };
        row.Children.Add(linkIcon);
        row.Children.Add(title);
        return row;
    }

    private static readonly SemaphoreSlim ThumbnailGenerationLock = new(1, 1);

    private static long? TryGetCachedDurationMs(FileInfo file)
    {
        try
        {
            string path = GetDurationCachePath(file);
            return File.Exists(path) && long.TryParse(File.ReadAllText(path), out long ms) ? ms : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNetworkPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    private async Task CopyToThisPcAsync(FileInfo file, Button triggerButton)
    {
        triggerButton.IsEnabled = false;
        string originalText = (string)triggerButton.Content;
        triggerButton.Content = "Copying...";
        try
        {
            Directory.CreateDirectory(_settings.LocalCopyFolder);
            string dest = Path.Combine(_settings.LocalCopyFolder, file.Name);
            await Task.Run(() => File.Copy(file.FullName, dest, overwrite: true));
            triggerButton.Content = "Copied";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't copy that clip: {ex.Message}", "Backtrack");
            triggerButton.Content = originalText;
            triggerButton.IsEnabled = true;
        }
    }

    private static string ResolveLocalClipPath(FileInfo file) => file.FullName;

    private bool IsCriticalOperationActive()
    {
        bool isRenaming = _isRenamingCard || _isPlayerRenaming;
        bool isTrimming = (TrimPanel != null && TrimPanel.Visibility == Visibility.Visible) || _trimStart.HasValue || _trimEnd.HasValue || _isTrimming;
        bool isSelectingClips = _selectedClipPaths.Count > 0;
        bool isDialogActive = _activeConfirmDialog != null && _activeConfirmDialog.IsLoaded;

        return isRenaming || isTrimming || isSelectingClips || isDialogActive;
    }

    private static async Task CopyWithRetryAsync(string sourcePath, string destPath, bool overwrite, int attempts = 5)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(sourcePath, destPath, overwrite);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

}
