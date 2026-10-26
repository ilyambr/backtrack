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
    internal void DefaultAudioTrackSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

        _actionFeedbackHideTimer?.Stop();
        _actionFeedbackHideTimer?.Start();
    }

    private void LoadAudioTracks() => LoadTracks();

    private void LoadTracks()
    {
        if (_vlcPlayer is null)
            return;

        // 1. Load Video Tracks
        _isUpdatingVideoTracks = true;
        List<VideoTrackOption> videoOptions = new();
        try
        {
            var videoDescriptions = _vlcPlayer.VideoTrackDescription;
            if (videoDescriptions != null && videoDescriptions.Length > 0)
            {
                var validVideo = videoDescriptions.Where(t => t.Id != -1).ToList();
                for (int i = 0; i < validVideo.Count; i++)
                {
                    videoOptions.Add(new VideoTrackOption(validVideo[i].Id, $"V{i + 1}"));
                }
            }
            else if (_vlcPlayer.Media is not null)
            {
                var mediaVideo = _vlcPlayer.Media.Tracks.Where(t => t.TrackType == LibVlc.TrackType.Video).ToList();
                for (int i = 0; i < mediaVideo.Count; i++)
                {
                    videoOptions.Add(new VideoTrackOption(i + 1, $"V{i + 1}"));
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[player] Error querying video tracks: {ex.Message}");
        }
        finally
        {
            _isUpdatingVideoTracks = false;
        }

        bool hasMultipleVideoTracks = videoOptions.Count > 1;

        // 2. Load Audio Tracks
        _isUpdatingAudioTracks = true;
        List<AudioTrackOption> audioOptions = new();
        try
        {
            var trackDescriptions = _vlcPlayer.AudioTrackDescription;
            if (trackDescriptions != null && trackDescriptions.Length > 0)
            {
                var validTracks = trackDescriptions.Where(t => t.Id != -1).ToList();
                for (int i = 0; i < validTracks.Count; i++)
                {
                    string label = hasMultipleVideoTracks ? $"A{i + 1}" : $"Track {i + 1}";
                    audioOptions.Add(new AudioTrackOption(validTracks[i].Id, label));
                }
            }
            else if (_vlcPlayer.Media is not null)
            {
                var mediaTracks = _vlcPlayer.Media.Tracks.Where(t => t.TrackType == LibVlc.TrackType.Audio).ToList();
                for (int i = 0; i < mediaTracks.Count; i++)
                {
                    string label = hasMultipleVideoTracks ? $"A{i + 1}" : $"Track {i + 1}";
                    audioOptions.Add(new AudioTrackOption(i + 1, label));
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[player] Error querying audio tracks: {ex.Message}");
        }
        finally
        {
            _isUpdatingAudioTracks = false;
        }

        // 3. Configure Video Track Selector UI
        if (hasMultipleVideoTracks)
        {
            VideoTrackCombo.Visibility = Visibility.Visible;
            _isUpdatingVideoTracks = true;
            try
            {
                VideoTrackCombo.ItemsSource = videoOptions;
                VideoTrackCombo.Height = 34;
                VideoTrackCombo.Width = 64;
                VideoTrackCombo.Margin = new Thickness(0, 0, 6, 0);

                int currentVideoId = _vlcPlayer.VideoTrack;
                int selVideoIdx = videoOptions.FindIndex(o => o.Id == currentVideoId);
                if (selVideoIdx < 0) selVideoIdx = 0;
                VideoTrackCombo.SelectedIndex = selVideoIdx;
            }
            finally
            {
                _isUpdatingVideoTracks = false;
            }

            AudioTrackCombo.Height = 34;
            AudioTrackCombo.Width = 64;
            AudioTrackCombo.Margin = new Thickness(0);
        }
        else
        {
            VideoTrackCombo.Visibility = Visibility.Collapsed;
            VideoTrackCombo.ItemsSource = null;

            AudioTrackCombo.Height = 34;
            AudioTrackCombo.Width = 120;
            AudioTrackCombo.Margin = new Thickness(0);
        }

        // 4. Configure Audio Track Selector UI
        if (audioOptions.Count == 0)
        {
            AudioTrackCombo.Visibility = Visibility.Collapsed;
            AudioTrackCombo.ItemsSource = null;
        }
        else
        {
            AudioTrackCombo.Visibility = Visibility.Visible;
            _isUpdatingAudioTracks = true;
            try
            {
                AudioTrackCombo.ItemsSource = audioOptions;

                int currentVlcTrackId = _vlcPlayer.AudioTrack;
                int selectedIdx = audioOptions.FindIndex(o => o.Id == currentVlcTrackId);
                if (selectedIdx < 0)
                {
                    int preferredIndex = _settings.DefaultPlayerAudioTrackIndex - 1;
                    selectedIdx = preferredIndex >= 0 && preferredIndex < audioOptions.Count ? preferredIndex : 0;
                }

                AudioTrackCombo.SelectedIndex = selectedIdx;

                if (selectedIdx > 0 && selectedIdx < audioOptions.Count && _vlcPlayer.AudioTrack != audioOptions[selectedIdx].Id)
                {
                    _vlcPlayer.SetAudioTrack(audioOptions[selectedIdx].Id);
                }
            }
            finally
            {
                _isUpdatingAudioTracks = false;
            }
        }

        TrackSelectorsPanel.Visibility = (VideoTrackCombo.Visibility == Visibility.Visible || AudioTrackCombo.Visibility == Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    internal void VideoTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingVideoTracks || _vlcPlayer is null || VideoTrackCombo.SelectedItem is not VideoTrackOption opt)
            return;

        if (_vlcPlayer.VideoTrack == opt.Id)
            return;

        _vlcPlayer.SetVideoTrack(opt.Id);

        if (_vlcPlayer.Media is not null)
        {
            var videoTracks = _vlcPlayer.Media.Tracks.Where(t => t.TrackType == LibVlc.TrackType.Video).ToList();
            int idx = VideoTrackCombo.SelectedIndex;
            if (idx >= 0 && idx < videoTracks.Count)
            {
                var vData = videoTracks[idx].Data.Video;
                if (vData.Width > 0 && vData.Height > 0)
                    StatResolution.Text = $"{vData.Width} x {vData.Height}";
                if (vData.FrameRateDen > 0)
                    StatFps.Text = $"{(double)vData.FrameRateNum / vData.FrameRateDen:0.##} fps";
            }
        }
    }

    internal void AudioTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingAudioTracks || _vlcPlayer is null || AudioTrackCombo.SelectedItem is not AudioTrackOption opt)
            return;

        if (_vlcPlayer.AudioTrack == opt.Id)
            return;

        _vlcPlayer.SetAudioTrack(opt.Id);
    }

    private void TogglePlayerMute()
    {
        if (_vlcPlayer is null)
            return;
        _isMuted = !_isMuted;
        _vlcPlayer.Volume = _isMuted ? 0 : (int)PlayerVolumeSlider.Value;
        _vlcPlayer.Mute = _isMuted;
        UpdateVolumeIcon();
        ShowPlayerActionFeedback(_isMuted ? PlayerFeedbackIcon.Mute : PlayerFeedbackIcon.Volume,
            _isMuted ? "Muted" : $"{(int)PlayerVolumeSlider.Value}%");
    }

    internal void PlayerVolumeButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayerMute();
    }

    internal void PlayerVolumeArea_MouseEnter(object sender, MouseEventArgs e)
    {
        _volumePopupCloseDebounce?.Stop();
        PlayerVolumePopup.IsOpen = true;
    }

    internal void PlayerVolumeArea_MouseLeave(object sender, MouseEventArgs e)
    {
        _volumePopupCloseDebounce?.Stop();
        _volumePopupCloseDebounce?.Start();
    }

    internal void PlayerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vlcPlayer is null)
            return;
        if (!_isMuted)
        {
            _vlcPlayer.Volume = (int)e.NewValue;
        }
        if (e.NewValue > 0 && _isMuted)
        {
            _isMuted = false;
            _vlcPlayer.Mute = false;
            _vlcPlayer.Volume = (int)e.NewValue;
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
}
