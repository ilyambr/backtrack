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

        private void ShowMainWindowAndOpenInPlayer(FileInfo file)
    {
        if (DateTime.UtcNow - _lastQuickOpenUtc < TimeSpan.FromMilliseconds(400))
            return;
        _lastQuickOpenUtc = DateTime.UtcNow;

        _scrim.ArmDismissCooldown(400);

        if (!IsVisible)
            ToggleVisible();

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        Activate();

        OpenInPlayer(file);
        
        
        _playerBackTarget = Screen.Idle;
    }


    private void RevealInExplorerAndClose(string filePath)
    {
        RevealInExplorer(filePath);
        StopPlayerPlayback();
        CloseOverlay(preserveScreen: true);
    }


        private void HandlePlayerKeyboardShortcut(KeyEventArgs e)
    {
        if (PlayerPanel.Visibility != Visibility.Visible || _isPlayerRenaming || _vlcPlayer is null)
            return;
        if (Keyboard.FocusedElement is TextBox)
            return;

        long currentMs = _vlcPlayer.Time;
        long lengthMs = _vlcPlayer.Length;
        long shortSeekMs = Math.Clamp((long)(lengthMs * 0.05), 1000, 15000);
        long longSeekMs = Math.Clamp((long)(lengthMs * 0.10), 2000, 30000);

        switch (e.Key)
        {
            case Key.Space:
            case Key.K:
                bool wasPlaying = _vlcPlayer.IsPlaying;
                PlayPauseButton_Click(this, e);
                ShowPlayerActionFeedback(wasPlaying ? PlayerFeedbackIcon.Pause : PlayerFeedbackIcon.Play);
                break;
            case Key.Left:
                CommitSeek(Math.Max(0, currentMs - shortSeekMs));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekBack, $"-{shortSeekMs / 1000.0:0.#}s");
                break;
            case Key.Right:
                CommitSeek(Math.Min(lengthMs, currentMs + shortSeekMs));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, $"+{shortSeekMs / 1000.0:0.#}s");
                break;
            case Key.J:
                CommitSeek(Math.Max(0, currentMs - longSeekMs));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekBack, $"-{longSeekMs / 1000.0:0.#}s");
                break;
            case Key.L:
                CommitSeek(Math.Min(lengthMs, currentMs + longSeekMs));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, $"+{longSeekMs / 1000.0:0.#}s");
                break;
            case Key.Home:
                CommitSeek(0);
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekBack, "Start");
                break;
            case Key.End:
                CommitSeek(Math.Max(0, lengthMs - 1));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, "End");
                break;
            case Key.M:
                TogglePlayerMute();
                break;
            case Key.Up:
                
                
                
                
                
                
                PlayerVolumeSlider.Value = Math.Min(100, PlayerVolumeSlider.Value + 5);
                ShowPlayerActionFeedback(PlayerFeedbackIcon.Volume, $"{(int)PlayerVolumeSlider.Value}%");
                break;
            case Key.Down:
                PlayerVolumeSlider.Value = Math.Max(0, PlayerVolumeSlider.Value - 5);
                ShowPlayerActionFeedback(PlayerFeedbackIcon.Volume, $"{(int)PlayerVolumeSlider.Value}%");
                break;
            case Key.F:
                ToggleFullscreen_Click(this, e);
                break;
            case Key.OemOpenBrackets:
                JumpToPreviousMarker();
                break;
            case Key.OemCloseBrackets:
                JumpToNextMarker();
                break;
            case Key.B:
                AddPlayerBookmark();
                break;
            case Key.S:
                PlayerStarButton_Click(this, e);
                break;
            case Key.C:
                PlayerCompress_Click(this, e);
                break;
            case Key.D0 or Key.NumPad0: CommitSeek(0); break;
            case Key.D1 or Key.NumPad1: CommitSeek(lengthMs * 1 / 10); break;
            case Key.D2 or Key.NumPad2: CommitSeek(lengthMs * 2 / 10); break;
            case Key.D3 or Key.NumPad3: CommitSeek(lengthMs * 3 / 10); break;
            case Key.D4 or Key.NumPad4: CommitSeek(lengthMs * 4 / 10); break;
            case Key.D5 or Key.NumPad5: CommitSeek(lengthMs * 5 / 10); break;
            case Key.D6 or Key.NumPad6: CommitSeek(lengthMs * 6 / 10); break;
            case Key.D7 or Key.NumPad7: CommitSeek(lengthMs * 7 / 10); break;
            case Key.D8 or Key.NumPad8: CommitSeek(lengthMs * 8 / 10); break;
            case Key.D9 or Key.NumPad9: CommitSeek(lengthMs * 9 / 10); break;
            default:
                return; 
        }

        e.Handled = true;
    }


    private void ApplyBigScreenSize()
    {
        
        
        
        
        double videoColumnWidth = Width - 2;
        double contentHeight = Math.Max(videoColumnWidth * 9.0 / 16.0, 320);

        
        
        
        PlayerVideoHost.Height = contentHeight;
        GalleryScrollHost.MaxHeight = contentHeight;
        Top = TargetScreenBounds.Y + BigTop;
    }


    private void DefaultAudioTrackSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DefaultAudioTrackSelector.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out int index))
            return;
        _settings.DefaultPlayerAudioTrackIndex = index;
        _settings.Save();
    }


        private void ShowPlayerActionFeedback(PlayerFeedbackIcon icon, string? text = null)
    {
        PlayerActionFeedbackIcon.Data = Geometry.Parse(icon switch
        {
            PlayerFeedbackIcon.Play => FeedbackPlayIcon,
            PlayerFeedbackIcon.Pause => FeedbackPauseIcon,
            PlayerFeedbackIcon.SeekForward => FeedbackSeekForwardIcon,
            PlayerFeedbackIcon.SeekBack => FeedbackSeekBackIcon,
            PlayerFeedbackIcon.Mute => VolumeOffIcon,
            _ => VolumeUpIcon,
        });
        PlayerActionFeedbackText.Text = text ?? string.Empty;
        PlayerActionFeedbackText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

        double videoWidth = PlayerVideoView.ActualWidth;
        double videoHeight = PlayerVideoView.ActualHeight;
        PlayerActionFeedbackPopup.HorizontalOffset = (videoWidth - PlayerActionFeedbackBorder.Width) / 2;
        PlayerActionFeedbackPopup.VerticalOffset = (videoHeight - PlayerActionFeedbackBorder.Height) / 2;

        PlayerActionFeedbackBorder.BeginAnimation(OpacityProperty, null); 
        PlayerActionFeedbackBorder.Opacity = 1;
        PlayerActionFeedbackPopup.IsOpen = false;
        Dispatcher.BeginInvoke(new Action(() => PlayerActionFeedbackPopup.IsOpen = true), DispatcherPriority.Loaded);

        _actionFeedbackHideTimer.Stop();
        _actionFeedbackHideTimer.Start();
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
        PlayerFullscreenTransportPopup.IsOpen = false;
        Dispatcher.BeginInvoke(new Action(() => PlayerFullscreenTransportPopup.IsOpen = true), DispatcherPriority.Loaded);
    }


        private async Task<string?> EnsureThumbnailCachedAsync(FileInfo file)
    {
        if (!file.Exists || file.Length == 0)
            return null;

        string cachePath = GetThumbnailCachePath(file);
        
        
        
        
        
        bool durationCached = File.Exists(GetDurationCachePath(file));
        if (File.Exists(cachePath) && durationCached)
            return cachePath;

        if (_libVlc is null)
            return null;

        await ThumbnailGenerationLock.WaitAsync();
        try
        {
            if (!File.Exists(cachePath) || !File.Exists(GetDurationCachePath(file)))
                await GenerateThumbnailAsync(file, cachePath);
        }
        finally
        {
            ThumbnailGenerationLock.Release();
        }

        return File.Exists(cachePath) ? cachePath : null;
    }


        private async Task PrewarmGalleryThumbnailsAsync()
    {
        if (_libVlc is null || !Directory.Exists(_settings.ClipsFolder))
            return;

        List<FileInfo> files;
        try
        {
            files = Directory.EnumerateFiles(_settings.ClipsFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists && f.Length > 0)
                .OrderByDescending(f => f.LastWriteTime) 
                .ToList();
        }
        catch
        {
            return;
        }

        foreach (FileInfo file in files)
            await EnsureThumbnailCachedAsync(file);
    }


    private async Task GenerateThumbnailAsync(FileInfo file, string cachePath)
    {
        
        
        
        
        
        
        
        await Task.Run(() =>
        {
            try
            {
                using var media = new LibVlc.Media(_libVlc!, file.FullName, LibVlc.FromType.FromPath);
                media.AddOption(":no-audio");
                using var player = new LibVlc.MediaPlayer(media) { Hwnd = _thumbnailSinkHwnd, Mute = true };
                using var playingSignal = new ManualResetEventSlim(false);

                player.Playing += (_, _) => playingSignal.Set();
                player.EncounteredError += (_, _) => playingSignal.Set();

                player.Play();
                if (!playingSignal.Wait(TimeSpan.FromSeconds(5)))
                {
                    player.Stop();
                    return;
                }

                
                
                try { File.WriteAllText(Path.ChangeExtension(cachePath, ".duration"), player.Length.ToString()); }
                catch {  }

                long seekTarget = Math.Min(2000, Math.Max(player.Length / 4, 0));
                if (seekTarget > 0)
                    player.Time = seekTarget;
                Thread.Sleep(200);

                player.TakeSnapshot(0, cachePath, 480, 0);
                for (int i = 0; i < 20 && !File.Exists(cachePath); i++)
                    Thread.Sleep(100);

                player.Stop();
            }
            catch
            {
                
                try
                {
                    string durPath = Path.ChangeExtension(cachePath, ".duration");
                    if (!File.Exists(cachePath) && File.Exists(durPath))
                        File.Delete(durPath);
                }
                catch { }
            }
        });
    }


        private void LoadAudioTracks()
    {
        if (_vlcPlayer?.Media is null)
            return;

        var tracks = _vlcPlayer.Media.Tracks.Where(t => t.TrackType == LibVlc.TrackType.Audio).ToList();
        if (tracks.Count == 0)
        {
            AudioTrackCombo.Visibility = Visibility.Collapsed;
            return;
        }

        var options = tracks.Select((t, i) => new AudioTrackOption(t.Id, string.IsNullOrEmpty(t.Description) ? $"Track {i + 1}" : t.Description)).ToList();

        AudioTrackCombo.Visibility = Visibility.Visible;
        AudioTrackCombo.ItemsSource = options;
        
        
        
        
        
        int preferredIndex = _settings.DefaultPlayerAudioTrackIndex - 1;
        AudioTrackCombo.SelectedIndex = preferredIndex >= 0 && preferredIndex < options.Count ? preferredIndex : 0;

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        int desiredId = options[AudioTrackCombo.SelectedIndex].Id;
        Dispatcher.BeginInvoke(() =>
        {
            if (_vlcPlayer is null)
                return;
            _vlcPlayer.SetAudioTrack(-1);
            _vlcPlayer.SetAudioTrack(desiredId);
        }, DispatcherPriority.Background);
    }


    private void AudioTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vlcPlayer is null || AudioTrackCombo.SelectedItem is not AudioTrackOption opt)
            return;

        _vlcPlayer.SetAudioTrack(opt.Id);
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


    private void TogglePlayerMute()
    {
        if (_vlcPlayer is null)
            return;
        _isMuted = !_isMuted;
        _vlcPlayer.Mute = _isMuted;
        UpdateVolumeIcon();
        ShowPlayerActionFeedback(_isMuted ? PlayerFeedbackIcon.Mute : PlayerFeedbackIcon.Volume,
            _isMuted ? "Muted" : $"{(int)PlayerVolumeSlider.Value}%");
    }


    private void PlayerVolumeButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayerMute();
    }


    
    
    
    
    
    
    
    private void PlayerVolumeArea_MouseEnter(object sender, MouseEventArgs e)
    {
        _volumePopupCloseDebounce.Stop();
        PlayerVolumePopup.IsOpen = true;
    }


    private void PlayerVolumeArea_MouseLeave(object sender, MouseEventArgs e)
    {
        _volumePopupCloseDebounce.Stop();
        _volumePopupCloseDebounce.Start();
    }


    private void PlayerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vlcPlayer is null)
            return;
        _vlcPlayer.Volume = (int)e.NewValue;
        
        
        
        if (e.NewValue > 0 && _isMuted)
        {
            _isMuted = false;
            _vlcPlayer.Mute = false;
        }
        UpdateVolumeIcon();
    }


    private void UpdateVolumeIcon()
    {
        if (_vlcPlayer is null)
            return;
        bool showMuted = _isMuted || PlayerVolumeSlider.Value <= 0;
        PlayerVolumeIcon.Data = Geometry.Parse(showMuted ? VolumeOffIcon : VolumeUpIcon);
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
        _seekTimer.Start(); 
    }


    private void PlayerSeekTrack_MouseEnter(object sender, MouseEventArgs e)
    {
        _isHoveringSeekTrack = true;
        PlayerSeekBg.Height = 8;
        PlayerSeekFill.Height = 8;
        PlayerSeekBuffer.Height = 8;
        PlayerSeekThumb.Visibility = Visibility.Visible;
    }


    private void PlayerSeekTrack_MouseLeave(object sender, MouseEventArgs e)
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


    private void PlayerSeekTrack_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isScrubbing = true;
        PlayerSeekTrack.CaptureMouse();
        ProcessSeekInput(e.GetPosition(PlayerSeekTrack));
    }


    private void PlayerSeekTrack_MouseMove(object sender, MouseEventArgs e)
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


    private void PlayerSeekTrack_MouseUp(object sender, MouseButtonEventArgs e)
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
            _seekDebounceTimer.Stop();
            if (_vlcPlayer.IsSeekable)
            {
                CommitSeek(_targetSeekMs);
            }
        }
        else
        {
            _seekDebounceTimer.Stop();
            _seekDebounceTimer.Start();
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


        private void DetachPlayerVideo()
    {
        
        
        
        _freezeFrameTimer.Stop();
        PlayerFreezeFramePopup.IsOpen = false;
        PlayerMenuPopup.IsOpen = false;

        
        
        
        
        
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

        _seekTimer.Stop();
        
        
        
        
        
        
        
        
        
        
        
        
        
        PlayerVideoView.MediaPlayer = null;
    }


        private void StopPlayerPlayback()
    {
        DetachPlayerVideo();
        DisposeVlcPlayerSync();
    }


    private void DisposeVlcPlayerSync()
    {
        if (_vlcPlayer is not null)
        {
            _vlcPlayer.Stop();
            _vlcPlayer.Dispose();
            _vlcPlayer = null;
        }
    }


        private void DisposeVlcPlayerAsync()
    {
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


    private void RenderPlayerMarkers()
    {
        PlayerMarkerCanvas.Children.Clear();
        TrimMarkerCanvas.Children.Clear();

        string? key = GetCurrentClipKey();
        if (key is null || !_settings.ClipMarkers.TryGetValue(key, out var markers) || markers.Count == 0)
            return;

        long durationMs = _vlcPlayer?.Length > 0 ? _vlcPlayer.Length : (_currentPlayerFile is not null ? (TryGetCachedDurationMs(_currentPlayerFile) ?? 1) : 1);
        if (durationMs <= 0) return;
        double durationSec = durationMs / 1000.0;

        double playerTrackWidth = PlayerSeekTrack.ActualWidth > 0 ? PlayerSeekTrack.ActualWidth : 760;
        double trimTrackWidth = TrimTimelineTrack.ActualWidth > 0 ? TrimTimelineTrack.ActualWidth : 760;

        foreach (double markerSec in markers)
        {
            if (markerSec > durationSec) continue;
            double ratio = markerSec / durationSec;

            var pin = new Border
            {
                Width = 6,
                Height = 12,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
                ToolTip = $"Bookmark: {TimeSpan.FromSeconds(markerSec):mm\\:ss}",
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(ratio * playerTrackWidth - 3, 0, 0, 0),
            };
            pin.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                CommitSeek((long)(markerSec * 1000.0));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, TimeSpan.FromSeconds(markerSec).ToString(@"mm\:ss"));
            };
            PlayerMarkerCanvas.Children.Add(pin);

            var trimPin = new Border
            {
                Width = 4,
                Height = 24,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
                ToolTip = $"Bookmark: {TimeSpan.FromSeconds(markerSec):mm\\:ss}",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(ratio * trimTrackWidth - 2, 0, 0, 0),
            };
            TrimMarkerCanvas.Children.Add(trimPin);
        }
    }

    private void JumpToPreviousMarker()
    {
        string? key = GetCurrentClipKey();
        if (key is null || !_settings.ClipMarkers.TryGetValue(key, out var markers) || markers.Count == 0 || _vlcPlayer is null)
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
        string? key = GetCurrentClipKey();
        if (key is null || !_settings.ClipMarkers.TryGetValue(key, out var markers) || markers.Count == 0 || _vlcPlayer is null)
            return;

        double currentSec = _vlcPlayer.Time / 1000.0;
        double? next = markers.Where(m => m > currentSec + 0.5).OrderBy(m => m).Cast<double?>().FirstOrDefault();
        if (next.HasValue)
        {
            CommitSeek((long)(next.Value * 1000.0));
            ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, TimeSpan.FromSeconds(next.Value).ToString(@"mm\:ss"));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayManager.Dispose();
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

}
