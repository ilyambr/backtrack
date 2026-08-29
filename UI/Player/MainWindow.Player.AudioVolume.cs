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

        _actionFeedbackHideTimer?.Stop();
        _actionFeedbackHideTimer?.Start();
    }


        private void LoadAudioTracks()
    {
        if (_vlcPlayer is null)
            return;

        _isUpdatingAudioTracks = true;
        try
        {
            var trackDescriptions = _vlcPlayer.AudioTrackDescription;
            List<AudioTrackOption> options = new();

            if (trackDescriptions != null && trackDescriptions.Length > 0)
            {
                var validTracks = trackDescriptions.Where(t => t.Id != -1).ToList();
                for (int i = 0; i < validTracks.Count; i++)
                {
                    options.Add(new AudioTrackOption(validTracks[i].Id, $"Track {i + 1}"));
                }
            }
            else if (_vlcPlayer.Media is not null)
            {
                var mediaTracks = _vlcPlayer.Media.Tracks.Where(t => t.TrackType == LibVlc.TrackType.Audio).ToList();
                for (int i = 0; i < mediaTracks.Count; i++)
                {
                    options.Add(new AudioTrackOption(i + 1, $"Track {i + 1}"));
                }
            }

            if (options.Count == 0)
            {
                AudioTrackCombo.Visibility = Visibility.Collapsed;
                return;
            }

            // Always display the Audio Track box whenever audio tracks exist
            AudioTrackCombo.Visibility = Visibility.Visible;
            AudioTrackCombo.ItemsSource = options;

            // Match currently playing VLC track, or preferred track from settings
            int currentVlcTrackId = _vlcPlayer.AudioTrack;
            int selectedIdx = options.FindIndex(o => o.Id == currentVlcTrackId);
            if (selectedIdx < 0)
            {
                int preferredIndex = _settings.DefaultPlayerAudioTrackIndex - 1;
                selectedIdx = preferredIndex >= 0 && preferredIndex < options.Count ? preferredIndex : 0;
            }

            AudioTrackCombo.SelectedIndex = selectedIdx;

            // If a non-default audio track is preferred and differs from active track, switch to it
            if (selectedIdx > 0 && selectedIdx < options.Count && _vlcPlayer.AudioTrack != options[selectedIdx].Id)
            {
                _vlcPlayer.SetAudioTrack(options[selectedIdx].Id);
            }
        }
        finally
        {
            _isUpdatingAudioTracks = false;
        }
    }


    private void AudioTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingAudioTracks || _vlcPlayer is null || AudioTrackCombo.SelectedItem is not AudioTrackOption opt)
            return;

        // If VLC is already playing this track, don't restart the audio output decoder
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


    private void PlayerVolumeButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayerMute();
    }


    
    
    
    
    
    
    
    private void PlayerVolumeArea_MouseEnter(object sender, MouseEventArgs e)
    {
        _volumePopupCloseDebounce?.Stop();
        PlayerVolumePopup.IsOpen = true;
    }


    private void PlayerVolumeArea_MouseLeave(object sender, MouseEventArgs e)
    {
        _volumePopupCloseDebounce?.Stop();
        _volumePopupCloseDebounce?.Start();
    }


    private void PlayerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
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
