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
    private void ApplyBigScreenSize()
    {
        double videoColumnWidth = Width - 2;
        double contentHeight = Math.Max(videoColumnWidth * 9.0 / 16.0, 320);

        Rect screenBounds = TargetScreenBounds;
        double screenH = screenBounds.Height;

        // PlayerVideoHost must stay exactly 16:9 (contentHeight) so video fits edge-to-edge with no black bars
        PlayerVideoHost.Height = contentHeight;

        double maxGalleryHeight = Math.Max(280, screenH - BigTop - 180);
        GalleryScrollHost.MaxHeight = Math.Min(contentHeight * 0.93, maxGalleryHeight);

        Top = screenBounds.Y + BigTop;
    }


    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlayerFullscreen)
            ExitPlayerFullscreen();
        else
            EnterPlayerFullscreen();
    }


    
    
    
    
    
    
    
    
    private void ReopenPlayerFullscreenTransportPopup()
    {
        if (!PlayerFullscreenTransportPopup.IsOpen)
        {
            PlayerFullscreenTransportPopup.IsOpen = true;
        }
        else
        {
            double offset = PlayerFullscreenTransportPopup.HorizontalOffset;
            PlayerFullscreenTransportPopup.HorizontalOffset = offset + 0.0001;
            PlayerFullscreenTransportPopup.HorizontalOffset = offset;
        }
    }


    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vlcPlayer is null)
            return;
        if (_vlcPlayer.IsPlaying)
        {
            _vlcPlayer.Pause();
        }
        else if (_playerHasEnded)
        {
            RestartEndedPlayback(0);
        }
        else
        {
            _vlcPlayer.Play();
        }
    }


        private void RestartEndedPlayback(long resumeAtMs)
    {
        if (_vlcPlayer is null)
            return;

        _playerHasEnded = false;
        _vlcPlayer.Stop();
        _vlcPlayer.Play();
        if (resumeAtMs > 0)
            _vlcPlayer.Time = resumeAtMs;
        _seekTimer?.Start(); 
    }


        private void DetachPlayerVideo()
    {
        
        
        
        _freezeFrameTimer?.Stop();
        PlayerFreezeFramePopup.IsOpen = false;
        PlayerMenuPopup.IsOpen = false;
        CompressPopup.IsOpen = false;
        BookmarkPopup.IsOpen = false;

        
        
        
        
        
        if (_isPlayerFullscreen)
        {
            _isPlayerFullscreen = false;
            RootBorder.BorderThickness = new Thickness(1);
            PlayerFullscreenTransportPopup.IsOpen = false;
            PlayerFullscreenTransportBorder.Child = null;
            PlayerTransportBar.ClearValue(BackgroundProperty);
            PlayerTransportBar.ClearValue(WidthProperty);
            DockPanel.SetDock(PlayerTransportBar, Dock.Bottom);
            PlayerVideoColumnDock.Children.Insert(0, PlayerTransportBar);
            PlayerTitlePill.Margin = new Thickness(0);
            PlayerMenuPill.Margin = new Thickness(0, 0, 20, 0);
            PlayerTitleBarHost.Height = 46;
            _scrim.SetExitButtonVisible(true);
            PlayerFullscreenIcon.Data = Geometry.Parse(FullscreenEnterIcon);
            PlayerFullscreenButton.ToolTip = "Fullscreen";
        }

        _seekTimer?.Stop();
        
        
        
        
        
        
        
        
        
        
        
        
        
        PlayerVideoView.MediaPlayer = null;
    }


        private void StopPlayerPlayback()
    {
        DetachPlayerVideo();
        DisposeVlcPlayerSync();
    }


    private void DisposeVlcPlayerSync()
    {
        _currentPlayerMedia?.Dispose();
        _currentPlayerMedia = null;
        if (_vlcPlayer is not null)
        {
            _vlcPlayer.Stop();
            _vlcPlayer.Dispose();
            _vlcPlayer = null;
        }
    }


        private void DisposeVlcPlayerAsync()
    {
        _currentPlayerMedia?.Dispose();
        _currentPlayerMedia = null;
        if (_vlcPlayer is null)
            return;

        LibVlc.MediaPlayer playerToDispose = _vlcPlayer;
        _vlcPlayer = null;
        
        
        
        _pendingVlcDisposeTask = Task.Run(() =>
        {
            try
            {
                playerToDispose.Stop();
                playerToDispose.Dispose();
            }
            catch
            {
                
            }
        });
    }


    

    private void PlayerSpeedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vlcPlayer is null)
            return;
        _playbackSpeedIndex = (_playbackSpeedIndex + 1) % PlaybackSpeeds.Length;
        float speed = PlaybackSpeeds[_playbackSpeedIndex];
        _vlcPlayer.SetRate(speed);
        PlayerSpeedText.Text = speed == (int)speed ? $"{(int)speed}x" : $"{speed}x";
    }


    private void ResetPlaybackSpeed()
    {
        _playbackSpeedIndex = 1; 
        PlayerSpeedText.Text = "1x";
        
        
        
        
    }


        private void PreviewLoopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vlcPlayer is null)
            return;

        if (_previewLooping)
        {
            StopPreviewLoop();
            return;
        }

        if (_trimStart is null || _trimEnd is null || _trimEnd <= _trimStart)
        {
            MessageBox.Show(this, "Set both a start and an end point first.", "Backtrack");
            return;
        }

        _previewLooping = true;
        
        
        
        
        
        PreviewLoopIcon.Visibility = Visibility.Collapsed;
        PreviewStopIcon.Visibility = Visibility.Visible;
        CommitSeek((long)_trimStart.Value.TotalMilliseconds);
        if (!_vlcPlayer.IsPlaying)
            _vlcPlayer.Play();
    }


    private long _lastRenderedMarkerDurationMs = -1;
    private double _lastRenderedTrackWidth = -1;

    protected override void OnClosed(EventArgs e)
    {
        _trayManager?.Dispose();
        StopPlayerPlayback();
        _libVlc?.Dispose();
        _hotkey?.Dispose();
        _cancelRecordHotkey?.Dispose();
        _bookmarkHotkey?.Dispose();
        foreach (var hk in _remoteRowHotkeys)
        {
            try { hk.Dispose(); } catch { }
        }
        _remoteRowHotkeys.Clear();
        
        
        if (_settings.RamDiskEnabled)
            RamDisk.Unmount(_settings.RamDiskDriveLetter);
        base.OnClosed(e);
    }


        private void PlayerTrim_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;
        bool opening = TrimPanel.Visibility != Visibility.Visible;
        TrimPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        PlayerTransportRow.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        MoveTransportControlsForTrim(opening);
        if (opening)
        {
            long lengthMs = _vlcPlayer?.Length ?? 0;
            _trimStart ??= TimeSpan.Zero;
            _trimEnd ??= TimeSpan.FromMilliseconds(Math.Max(0, lengthMs));
            BuildTrimRuler();
            UpdateTrimTimelineUi();
        }
        else
        {
            StopPreviewLoop();
        }
    }


        private void MoveTransportControlsForTrim(bool intoTrimRow)
    {
        PlayPauseButton.Width = PlayPauseButton.Height = intoTrimRow ? PlayPauseButtonTrimSize : PlayPauseButtonNormalSize;
        
        
        
        
        
        PlayPauseButton.Style = (Style)FindResource(intoTrimRow ? "BareIconButton" : "PlayerTransportButton");
        
        
        
        PlayPauseButton.Margin = intoTrimRow ? new Thickness(0, 0, 10, 0) : default;
        Reparent(PlayPauseButton, intoTrimRow ? TrimActionButtons : PlayerTransportRow, intoTrimRow ? null : PlayPauseButtonHomeColumn, insertAtFront: intoTrimRow);
        Reparent(AudioTrackCombo, intoTrimRow ? TrimTransportExtras : PlayerTransportRow, intoTrimRow ? null : AudioTrackComboHomeColumn);
        Reparent(PlayerSpeedButton, intoTrimRow ? TrimTransportExtras : PlayerTransportRow, intoTrimRow ? null : PlayerSpeedButtonHomeColumn);
        Reparent(PlayerVolumeButton, intoTrimRow ? TrimTransportExtras : PlayerTransportRow, intoTrimRow ? null : PlayerVolumeButtonHomeColumn);
        Reparent(PlayerFullscreenButton, intoTrimRow ? TrimTransportExtras : PlayerTransportRow, intoTrimRow ? null : PlayerFullscreenButtonHomeColumn);

        static void Reparent(FrameworkElement element, Panel newParent, int? gridColumn, bool insertAtFront = false)
        {
            if (element.Parent is Panel oldParent && !ReferenceEquals(oldParent, newParent))
                oldParent.Children.Remove(element);
            if (gridColumn is int col)
                Grid.SetColumn(element, col);
            if (!newParent.Children.Contains(element))
            {
                if (insertAtFront)
                    newParent.Children.Insert(0, element);
                else
                    newParent.Children.Add(element);
            }
        }
    }

    private sealed record AudioTrackOption(int Id, string Name);

    private const int PlayPauseButtonHomeColumn = 0;
    private const int AudioTrackComboHomeColumn = 4;
    private const int PlayerSpeedButtonHomeColumn = 5;
    private const int PlayerVolumeButtonHomeColumn = 6;
    private const int PlayerFullscreenButtonHomeColumn = 7;

    private const double PlayPauseButtonNormalSize = 42;
    private const double PlayPauseButtonTrimSize = 28;

    private void StopPreviewLoop()
    {
        _previewLooping = false;
        PreviewLoopIcon.Visibility = Visibility.Visible;
        PreviewStopIcon.Visibility = Visibility.Collapsed;
    }

}
