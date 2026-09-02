using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Pairing;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
    internal void TrimCancel_Click(object sender, RoutedEventArgs e)
    {
        _trimStart = null;
        _trimEnd = null;
        TrimStartText.Text = "0:00";
        TrimEndText.Text = "0:00";
        TrimStatusText.Text = "";
        TrimPanel.Visibility = Visibility.Collapsed;
        PlayerTransportRow.Visibility = Visibility.Visible;
        MoveTransportControlsForTrim(intoTrimRow: false);
        StopPreviewLoop();
    }

    internal void TrimStartHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _trimDragMode = TrimDragMode.Start;
        TrimTimelineTrack.CaptureMouse();

        e.Handled = true;
    }

    internal void TrimEndHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _trimDragMode = TrimDragMode.End;
        TrimTimelineTrack.CaptureMouse();
        e.Handled = true;
    }

    internal void TrimTimelineTrack_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _trimDragMode = TrimDragMode.Seek;
        _isScrubbing = true;
        TrimTimelineTrack.CaptureMouse();
        ProcessTrimTimelineInput(e.GetPosition(TrimTimelineTrack));
    }

    internal void TrimTimelineTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (_trimDragMode == TrimDragMode.None)
            return;
        ProcessTrimTimelineInput(e.GetPosition(TrimTimelineTrack));
    }

    internal void TrimTimelineTrack_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_trimDragMode == TrimDragMode.None)
            return;

        bool wasSeek = _trimDragMode == TrimDragMode.Seek;
        TrimTimelineTrack.ReleaseMouseCapture();
        TrimHandleTooltipPopup.IsOpen = false;
        if (wasSeek)
        {
            ProcessTrimTimelineInput(e.GetPosition(TrimTimelineTrack), immediate: true);
            _isScrubbing = false;
        }
        _trimDragMode = TrimDragMode.None;
    }

    internal void TrimTimelineTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        BuildTrimRuler();
        UpdateTrimTimelineUi();
    }

    private void ProcessTrimTimelineInput(Point pos, bool immediate = false)
    {
        if (_vlcPlayer is null)
            return;
        double trackWidth = TrimTimelineTrack.ActualWidth;
        if (trackWidth <= 0)
            return;

        long lengthMs = Math.Max(1, _vlcPlayer.Length);
        double ratio = Math.Clamp(pos.X / trackWidth, 0.0, 1.0);
        long ms = (long)(ratio * lengthMs);

        switch (_trimDragMode)
        {
            case TrimDragMode.Start:
                {
                    long endMs = (long)(_trimEnd ?? TimeSpan.FromMilliseconds(lengthMs)).TotalMilliseconds;
                    ms = Math.Clamp(ms, 0, Math.Max(0, endMs - 1));
                    _trimStart = TimeSpan.FromMilliseconds(ms);
                    ShowTrimHandleTooltip(pos.X, ms);
                    break;
                }
            case TrimDragMode.End:
                {
                    long startMs = (long)(_trimStart ?? TimeSpan.Zero).TotalMilliseconds;
                    ms = Math.Clamp(ms, Math.Min(lengthMs, startMs + 1), lengthMs);
                    _trimEnd = TimeSpan.FromMilliseconds(ms);
                    ShowTrimHandleTooltip(pos.X, ms);
                    break;
                }
            case TrimDragMode.Seek:
                _targetSeekMs = ms;
                PlayerCurrentTime.Text = FormatDuration(ms);
                if (immediate)
                {
                    _seekDebounceTimer?.Stop();
                    if (_vlcPlayer.IsSeekable)
                        CommitSeek(ms);
                    else
                        _seekDebounceTimer?.Start();
                }
                else
                {
                    _seekDebounceTimer?.Stop();
                    _seekDebounceTimer?.Start();
                }
                break;
        }

        UpdateTrimTimelineUi();
    }

    private void ShowTrimHandleTooltip(double x, long ms)
    {
        TrimHandleTooltipText.Text = FormatDuration(ms);
        TrimHandleTooltipPopup.HorizontalOffset = x - 15;
        TrimHandleTooltipPopup.VerticalOffset = -30;
        TrimHandleTooltipPopup.IsOpen = true;
    }

    private void UpdateTrimTimelineUi()
    {
        if (_vlcPlayer is null)
            return;
        double trackWidth = TrimTimelineTrack.ActualWidth;
        if (trackWidth <= 0)
            return;

        long lengthMs = Math.Max(1, _vlcPlayer.Length);
        long startMs = (long)(_trimStart ?? TimeSpan.Zero).TotalMilliseconds;
        long endMs = (long)(_trimEnd ?? TimeSpan.FromMilliseconds(lengthMs)).TotalMilliseconds;

        double startX = Math.Clamp((double)startMs / lengthMs, 0, 1) * trackWidth;
        double endX = Math.Clamp((double)endMs / lengthMs, 0, 1) * trackWidth;

        TrimSelectedRange.Margin = new Thickness(startX, 0, 0, 0);
        TrimSelectedRange.Width = Math.Max(0, endX - startX);

        const double handleWidth = 10;
        double maxHandleX = Math.Max(0, trackWidth - handleWidth);
        TrimStartHandle.Margin = new Thickness(Math.Clamp(startX - handleWidth / 2, 0, maxHandleX), 0, 0, 0);
        TrimEndHandle.Margin = new Thickness(Math.Clamp(endX - handleWidth / 2, 0, maxHandleX), 0, 0, 0);

        const double playheadWidth = 2;
        double playRatio = Math.Clamp((double)_vlcPlayer.Time / lengthMs, 0, 1);
        double playheadX = Math.Clamp(playRatio * trackWidth - playheadWidth / 2, 0, Math.Max(0, trackWidth - playheadWidth));
        TrimPlayhead.Margin = new Thickness(playheadX, 0, 0, 0);

        TrimStartText.Text = FormatDuration(startMs);
        TrimEndText.Text = FormatDuration(endMs);
    }

    private void BuildTrimRuler()
    {
        TrimRulerCanvas.Children.Clear();
        if (_vlcPlayer is null)
            return;
        double trackWidth = TrimTimelineTrack.ActualWidth;
        if (trackWidth <= 0)
            return;
        long lengthMs = Math.Max(1, _vlcPlayer.Length);

        const int tickCount = 6;
        for (int i = 0; i < tickCount; i++)
        {
            double ratio = i / (double)(tickCount - 1);
            double x = ratio * trackWidth;
            long ms = (long)(ratio * lengthMs);

            var tick = new Border { Width = 1, Height = 5, Background = (Brush)FindResource("Hairline") };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, 0);
            TrimRulerCanvas.Children.Add(tick);

            var label = new TextBlock { Text = FormatDuration(ms), FontSize = 9.5, Foreground = (Brush)FindResource("Text2") };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            double labelX = i == 0 ? x : i == tickCount - 1 ? x - label.DesiredSize.Width : x - label.DesiredSize.Width / 2;
            Canvas.SetLeft(label, labelX);
            Canvas.SetTop(label, 6);
            TrimRulerCanvas.Children.Add(label);
        }
    }
    private async Task CaptureAndShowPlayerFreezeFrameAsync(FileInfo? file = null)
    {
        try
        {
            string snapPath = Path.Combine(Path.GetTempPath(), $"bt_trim_snap_{Guid.NewGuid():N}.png");
            bool snapOk = false;
            if (_vlcPlayer is not null)
            {
                try
                {
                    if (_vlcPlayer.TakeSnapshot(0, snapPath, 480, 0))
                    {
                        for (int i = 0; i < 15; i++)
                        {
                            if (File.Exists(snapPath) && new FileInfo(snapPath).Length > 0)
                            {
                                snapOk = true;
                                break;
                            }
                            await Task.Delay(20);
                        }
                    }
                }
                catch { }
            }

            if (snapOk && File.Exists(snapPath))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(snapPath);
                    bmp.EndInit();
                    bmp.Freeze();
                    PlayerFreezeFrame.Source = bmp;
                    try { File.Delete(snapPath); } catch { }
                }
                catch { }
            }
            else if (file != null && File.Exists(file.FullName))
            {
                await LoadThumbnailAsync(file, PlayerFreezeFrame);
            }
            else if (_currentPlayerRemoteOrigin is (string relPath, _))
            {
                string? thumbPath = GetRemoteThumbnailCachePath(relPath, DateTime.UtcNow, _remoteStreamTotalBytes);
                if (thumbPath != null && File.Exists(thumbPath))
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(thumbPath);
                    bmp.EndInit();
                    bmp.Freeze();
                    PlayerFreezeFrame.Source = bmp;
                }
            }

            PlayerFreezeFrame.Effect = null;
            if (PlayerFreezeFrameDimmer != null)
                PlayerFreezeFrameDimmer.Visibility = Visibility.Collapsed;

            PlayerFreezeFramePopup.IsOpen = true;
            _freezeFrameTimer?.Stop();
            ReopenPlayerOverlayPopup();
        }
        catch (Exception ex)
        {
            AppLog.WriteError("[Trim] CaptureAndShowPlayerFreezeFrameAsync failed", ex);
        }
    }
}
