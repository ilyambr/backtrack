using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Pairing;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
    internal void PlayerSeekTrack_MouseEnter(object sender, MouseEventArgs e)
    {
        _isHoveringSeekTrack = true;
        PlayerSeekBg.Height = 8;
        PlayerSeekFill.Height = 8;
        PlayerSeekBuffer.Height = 8;
        PlayerSeekThumb.Visibility = Visibility.Visible;
    }

    internal void PlayerSeekTrack_MouseLeave(object sender, MouseEventArgs e)
    {
        _isHoveringSeekTrack = false;
        if (!_isScrubbing)
        {
            PlayerSeekBg.Height = 4;
            PlayerSeekFill.Height = 4;
            PlayerSeekBuffer.Height = 4;
            PlayerSeekThumb.Visibility = Visibility.Collapsed;
            SeekTooltipPopup.IsOpen = false;
        }
    }

    internal void PlayerSeekTrack_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isScrubbing = true;
        PlayerSeekTrack.CaptureMouse();
        ProcessSeekInput(e.GetPosition(PlayerSeekTrack));
    }

    internal void PlayerSeekTrack_MouseMove(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(PlayerSeekTrack);
        double trackWidth = PlayerSeekTrack.ActualWidth;
        if (trackWidth <= 0 || _vlcPlayer == null) return;

        double ratio = Math.Clamp(pos.X / trackWidth, 0.0, 1.0);
        long durationMs = Math.Max(1, _vlcPlayer.Length);
        long hoverMs = (long)(ratio * durationMs);

        SeekTooltipText.Text = FormatDuration(hoverMs);
        SeekTooltipPopup.HorizontalOffset = pos.X - 15;
        SeekTooltipPopup.VerticalOffset = -30;
        SeekTooltipPopup.IsOpen = true;

        if (_isScrubbing)
        {
            ProcessSeekInput(pos);
        }
    }

    internal void PlayerSeekTrack_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isScrubbing)
        {
            _isScrubbing = false;
            PlayerSeekTrack.ReleaseMouseCapture();

            if (!_isHoveringSeekTrack)
            {
                PlayerSeekBg.Height = 4;
                PlayerSeekFill.Height = 4;
                PlayerSeekBuffer.Height = 4;
                PlayerSeekThumb.Visibility = Visibility.Collapsed;
                SeekTooltipPopup.IsOpen = false;
            }

            ProcessSeekInput(e.GetPosition(PlayerSeekTrack), immediate: true);
        }
    }

    private void ProcessSeekInput(Point mousePos, bool immediate = false)
    {
        if (_vlcPlayer == null) return;
        double trackWidth = PlayerSeekTrack.ActualWidth;
        if (trackWidth <= 0) return;

        double ratio = Math.Clamp(mousePos.X / trackWidth, 0.0, 1.0);
        long durationMs = Math.Max(1, _vlcPlayer.Length);
        _targetSeekMs = (long)(ratio * durationMs);

        PlayerSeekFill.Width = ratio * trackWidth;
        PlayerSeekThumb.Margin = new Thickness(ratio * trackWidth - 7, 0, 0, 0);
        PlayerCurrentTime.Text = FormatDuration(_targetSeekMs);

        if (immediate)
        {
            _seekDebounceTimer?.Stop();
            if (_vlcPlayer.IsSeekable)
            {
                CommitSeek(_targetSeekMs);
            }
        }
        else
        {
            _seekDebounceTimer?.Stop();
            _seekDebounceTimer?.Start();
        }
    }

    private void CommitSeek(long ms)
    {
        if (_vlcPlayer is null)
            return;

        if (_playerHasEnded)
        {
            RestartEndedPlayback(ms);
            return;
        }

        if (!_vlcPlayer.IsSeekable)
            return;

        _vlcPlayer.Time = ms;
    }

    private bool TryGetMarkersForCurrentClip(out List<double> markers)
    {
        markers = new List<double>();
        string? key = GetCurrentClipKey();
        if (string.IsNullOrEmpty(key))
            return false;

        if (_settings.ClipMarkers.TryGetValue(key, out var m1) && m1.Count > 0)
        {
            markers = m1;
            return true;
        }

        string fileName = Path.GetFileName(key);
        if (_settings.ClipMarkers.TryGetValue(fileName, out var m2) && m2.Count > 0)
        {
            markers = m2;
            return true;
        }

        if (_currentPlayerFile is not null)
        {
            if (_settings.ClipMarkers.TryGetValue(_currentPlayerFile.Name, out var m3) && m3.Count > 0)
            {
                markers = m3;
                return true;
            }
            if (_settings.ClipMarkers.TryGetValue(_currentPlayerFile.FullName, out var m4) && m4.Count > 0)
            {
                markers = m4;
                return true;
            }
        }

        if (_currentPlayerRemoteOrigin is not null)
        {
            string rel = _currentPlayerRemoteOrigin.Value.RelativePath;
            if (_settings.ClipMarkers.TryGetValue(rel, out var m5) && m5.Count > 0)
            {
                markers = m5;
                return true;
            }
            string relName = Path.GetFileName(rel);
            if (_settings.ClipMarkers.TryGetValue(relName, out var m6) && m6.Count > 0)
            {
                markers = m6;
                return true;
            }
        }

        return false;
    }

    private void RenderPlayerMarkers()
    {
        PlayerMarkerCanvas.Children.Clear();
        TrimMarkerCanvas.Children.Clear();

        if (!TryGetMarkersForCurrentClip(out var markers) || markers.Count == 0)
            return;

        long durationMs = _vlcPlayer?.Length > 0 ? _vlcPlayer.Length : (_currentPlayerFile is not null ? (TryGetCachedDurationMs(_currentPlayerFile) ?? 0) : 0);
        if (durationMs <= 0) return;
        double durationSec = durationMs / 1000.0;

        double playerTrackWidth = PlayerSeekTrack.ActualWidth > 0 ? PlayerSeekTrack.ActualWidth : 760;
        double trimTrackWidth = TrimTimelineTrack.ActualWidth > 0 ? TrimTimelineTrack.ActualWidth : 760;

        foreach (double markerSec in markers)
        {
            if (markerSec > durationSec + 1.0) continue;
            double ratio = Math.Clamp(markerSec / durationSec, 0.0, 1.0);

            var pin = new Border
            {
                Width = 6,
                Height = 12,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
                ToolTip = $"Bookmark: {TimeSpan.FromSeconds(markerSec):mm\\:ss}",
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            pin.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                CommitSeek((long)(markerSec * 1000.0));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, TimeSpan.FromSeconds(markerSec).ToString(@"mm\:ss"));
            };
            double left = Math.Clamp(ratio * playerTrackWidth - 3, 0, Math.Max(0, playerTrackWidth - 6));
            Canvas.SetLeft(pin, left);
            Canvas.SetTop(pin, 6);
            PlayerMarkerCanvas.Children.Add(pin);

            var trimPin = new Border
            {
                Width = 2,
                Height = 34,
                CornerRadius = new CornerRadius(1),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
                ToolTip = $"Bookmark: {TimeSpan.FromSeconds(markerSec):mm\\:ss}",
                VerticalAlignment = VerticalAlignment.Center,
            };
            double trimLeft = Math.Clamp(ratio * trimTrackWidth - 1, 0, Math.Max(0, trimTrackWidth - 2));
            Canvas.SetLeft(trimPin, trimLeft);
            Canvas.SetTop(trimPin, 0);
            TrimMarkerCanvas.Children.Add(trimPin);
        }
    }

    private void JumpToPreviousMarker()
    {
        if (!TryGetMarkersForCurrentClip(out var markers) || markers.Count == 0 || _vlcPlayer is null)
            return;

        double currentSec = _vlcPlayer.Time / 1000.0;
        double? prev = markers.Where(m => m < currentSec - 0.5).OrderByDescending(m => m).Cast<double?>().FirstOrDefault();
        if (prev.HasValue)
        {
            CommitSeek((long)(prev.Value * 1000.0));
            ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekBack, TimeSpan.FromSeconds(prev.Value).ToString(@"mm\:ss"));
        }
    }

    private void JumpToNextMarker()
    {
        if (!TryGetMarkersForCurrentClip(out var markers) || markers.Count == 0 || _vlcPlayer is null)
            return;

        double currentSec = _vlcPlayer.Time / 1000.0;
        double? next = markers.Where(m => m > currentSec + 0.5).OrderBy(m => m).Cast<double?>().FirstOrDefault();
        if (next.HasValue)
        {
            CommitSeek((long)(next.Value * 1000.0));
            ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, TimeSpan.FromSeconds(next.Value).ToString(@"mm\:ss"));
        }
    }

    private void UpdatePlayerSeekUi()
    {
        if (_vlcPlayer is null || _isScrubbing)
            return;

        long lengthMs = _vlcPlayer.Length;
        long timeMs = _vlcPlayer.Time;

        if (lengthMs <= 0)
            return;

        if (_previewLooping && _trimStart is not null && _trimEnd is not null && timeMs >= _trimEnd.Value.TotalMilliseconds)
            CommitSeek((long)_trimStart.Value.TotalMilliseconds);

        PlayerCurrentTime.Text = FormatDuration(Math.Max(timeMs, 0));
        PlayerDurationText.Text = FormatDuration(Math.Max(lengthMs, 0));

        double ratio = Math.Clamp((double)timeMs / lengthMs, 0.0, 1.0);
        double trackWidth = PlayerSeekTrack.ActualWidth;

        if (trackWidth > 0)
        {
            PlayerSeekFill.Width = ratio * trackWidth;
            PlayerSeekThumb.Margin = new Thickness(ratio * trackWidth - 7, 0, 0, 0);

            if (lengthMs > 0 && (_lastRenderedMarkerDurationMs != lengthMs || Math.Abs(_lastRenderedTrackWidth - trackWidth) > 2.0))
            {
                _lastRenderedMarkerDurationMs = lengthMs;
                _lastRenderedTrackWidth = trackWidth;
                RenderPlayerMarkers();
            }
        }

        if (TrimPanel.Visibility == Visibility.Visible)
            UpdateTrimTimelineUi();
    }
}
