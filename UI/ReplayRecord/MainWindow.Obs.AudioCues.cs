using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Backtrack.Core;

namespace Backtrack;

public partial class MainWindow : Window
{
    internal void DisableAudioCuesToggle_Click(object sender, RoutedEventArgs e)
    {
        bool disabled = DisableAudioCuesToggle.IsChecked == true;
        _settings.DisableAudioCues = disabled;
        if (!disabled)
        {
            _settings.DisableStartAudioCue = false;
            _settings.DisableSaveAudioCue = false;
        }
        _settings.Save();
        SyncAudioCuesUi();
    }

    internal void DisableAudioCuesRow_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowAudioCuesContextMenu(sender as FrameworkElement ?? DisableAudioCuesRow);
    }

    private void ShowAudioCuesContextMenu(FrameworkElement target)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu"), PlacementTarget = target, Placement = PlacementMode.Bottom };

        Brush recBrush = (Brush)FindResource("Rec");
        Brush textBrush = (Brush)FindResource("Text0");

        bool startMuted = _settings.DisableAudioCues || _settings.DisableStartAudioCue;
        var muteStartItem = new MenuItem
        {
            Header = "Mute start cue",
            IsCheckable = true,
            IsChecked = startMuted,
            Foreground = startMuted ? recBrush : textBrush,
            Style = (Style)FindResource("DarkMenuItem")
        };
        muteStartItem.Click += (_, _) =>
        {
            _settings.DisableStartAudioCue = !_settings.DisableStartAudioCue;
            if (!_settings.DisableStartAudioCue)
                _settings.DisableAudioCues = false;
            else if (_settings.DisableSaveAudioCue)
                _settings.DisableAudioCues = true;
            _settings.Save();
            bool isMuted = _settings.DisableAudioCues || _settings.DisableStartAudioCue;
            muteStartItem.IsChecked = isMuted;
            muteStartItem.Foreground = isMuted ? recBrush : textBrush;
            SyncAudioCuesUi();
        };

        bool saveMuted = _settings.DisableAudioCues || _settings.DisableSaveAudioCue;
        var muteSaveItem = new MenuItem
        {
            Header = "Mute save cue",
            IsCheckable = true,
            IsChecked = saveMuted,
            Foreground = saveMuted ? recBrush : textBrush,
            Style = (Style)FindResource("DarkMenuItem")
        };
        muteSaveItem.Click += (_, _) =>
        {
            _settings.DisableSaveAudioCue = !_settings.DisableSaveAudioCue;
            if (!_settings.DisableSaveAudioCue)
                _settings.DisableAudioCues = false;
            else if (_settings.DisableStartAudioCue)
                _settings.DisableAudioCues = true;
            _settings.Save();
            bool isMuted = _settings.DisableAudioCues || _settings.DisableSaveAudioCue;
            muteSaveItem.IsChecked = isMuted;
            muteSaveItem.Foreground = isMuted ? recBrush : textBrush;
            SyncAudioCuesUi();
        };

        var openFolderItem = new MenuItem
        {
            Header = "Open sound file location",
            Style = (Style)FindResource("DarkMenuItem")
        };
        openFolderItem.Click += (_, _) =>
        {
            string dir = AudioCues.AudioDirectory;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        };

        menu.Items.Add(muteStartItem);
        menu.Items.Add(muteSaveItem);
        menu.Items.Add(new Separator { Style = (Style)FindResource("DarkSeparator") });
        menu.Items.Add(openFolderItem);

        menu.IsOpen = true;
    }

    private void SyncAudioCuesUi()
    {
        if (DisableAudioCuesToggle is null) return;

        bool masterDisabled = _settings.DisableAudioCues || (_settings.DisableStartAudioCue && _settings.DisableSaveAudioCue);
        DisableAudioCuesToggle.IsChecked = masterDisabled;

        if (DisableAudioCuesSubtext != null)
        {
            DisableAudioCuesSubtext.Text = "Mutes chimes when recordings or clips are saved.";
        }

        if (AudioCueVolumeRow != null && AudioCueVolumeSlider != null)
        {
            AudioCueVolumeRow.Opacity = masterDisabled ? 0.5 : 1.0;
            AudioCueVolumeSlider.IsEnabled = !masterDisabled;
        }
    }

    private DispatcherTimer? _audioCuePreviewDebounce;

    internal void AudioCueVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSettingsUiLoading || _settings is null || AudioCueVolumeText is null || AudioCueVolumeSlider is null) return;
        int vol = (int)Math.Round(AudioCueVolumeSlider.Value);
        AudioCueVolumeText.Text = $"{vol}%";
        _settings.AudioCueVolume = vol;
        _settings.Save();

        _audioCuePreviewDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _audioCuePreviewDebounce.Stop();
        _audioCuePreviewDebounce.Tick -= AudioCuePreview_Tick;
        _audioCuePreviewDebounce.Tick += AudioCuePreview_Tick;
        _audioCuePreviewDebounce.Start();
    }

    internal void AudioCuePreview_Tick(object? sender, EventArgs e)
    {
        _audioCuePreviewDebounce?.Stop();
        if (!_settings.DisableAudioCues && !_settings.DisableSaveAudioCue && _settings.AudioCueVolume > 0)
        {
            AudioCues.PlayPreview(_settings.AudioCueVolume);
        }
    }
}
