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
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;
using Backtrack.Updates;

namespace Backtrack;

public partial class MainWindow : Window
{
        private void ToggleStatusOverlay()
    {
        _settings.ShowStatusIndicator = !_statusOverlay.IsVisible;
        _settings.Save();

        if (_statusOverlay.IsVisible)
        {
            _statusOverlay.Hide();
        }
        else
        {
            _statusOverlay.Show();
            WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(_statusOverlay).Handle);
        }

        if (SettingsPanel.Visibility == Visibility.Visible)
            ShowStatusIndicatorToggle.IsChecked = _settings.ShowStatusIndicator;
    }


    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(Screen.Settings);
        
        
        
        
        
        
        
        
        
        
        SettingsScrollHost.ScrollToTop();
        LoadSettingsUi();
        _ = LoadBufferVisibilityUi();
        _ = LoadRecordFolderUi();
        RefreshRamDiskRemoteGating();
        RefreshPluginStatusRemoteGating();
    }


    private void LoadDisplaySelector()
    {
        List<DisplayInfo> displays = DisplayMonitors.GetAll();
        
        
        
        var options = displays.Select((d, i) => new DisplayOption(
            d.DeviceName,
            $"{d.FriendlyName ?? $"Display {i + 1}"}{(d.IsPrimary ? " (Primary)" : "")} - {(int)d.BoundsDiu.Width}x{(int)d.BoundsDiu.Height}")).ToList();

        
        
        
        DisplaySelector.SelectionChanged -= DisplaySelector_SelectionChanged;
        DisplaySelector.ItemsSource = options;
        DisplaySelector.SelectedValue = string.IsNullOrEmpty(_settings.DisplayDeviceName)
            ? options.FirstOrDefault(o => displays.First(d => d.DeviceName == o.DeviceName).IsPrimary)?.DeviceName
            : _settings.DisplayDeviceName;
        if (DisplaySelector.SelectedItem is null && options.Count > 0)
            DisplaySelector.SelectedIndex = 0;
        DisplaySelector.SelectionChanged += DisplaySelector_SelectionChanged;
    }


        private void BuildThemeSwatches()
    {
        ThemeSwatchesPanel.Children.Clear();
        ThemeSwatchLabelsPanel.Children.Clear();
        _themeSwatches.Clear();

        foreach (ThemeInfo theme in ThemeManager.DiscoverThemes())
        {
            Brush panelBg = (Brush)theme.Dictionary["PanelBg"];
            Brush accent = (Brush)theme.Dictionary["Accent"];
            Brush text0 = (Brush)theme.Dictionary["Text0"];
            Brush text2 = (Brush)theme.Dictionary["Text2"];

            var dotRow = new StackPanel { Orientation = Orientation.Horizontal };
            dotRow.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Fill = accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });
            dotRow.Children.Add(new System.Windows.Shapes.Rectangle { Height = 6, Width = 46, Fill = text0, RadiusX = 3, RadiusY = 3 });

            var content = new StackPanel { Margin = new Thickness(10) };
            content.Children.Add(dotRow);
            content.Children.Add(new System.Windows.Shapes.Rectangle { Height = 5, Width = 70, Fill = text2, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 12, 0, 0) });
            content.Children.Add(new System.Windows.Shapes.Rectangle { Height = 5, Width = 50, Fill = text2, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 6, 0, 0) });

            var swatch = new Border
            {
                Width = 122, Height = 78, CornerRadius = new CornerRadius(6),
                Background = panelBg, BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 12, 0), Child = content,
            };
            string themeId = theme.Id;
            swatch.MouseLeftButtonUp += (_, _) => ApplyTheme(themeId);
            ThemeSwatchesPanel.Children.Add(swatch);
            _themeSwatches[themeId] = swatch;

            ThemeSwatchLabelsPanel.Children.Add(new TextBlock
            {
                Text = theme.DisplayName, Width = 134, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Text2"),
            });
        }
    }


    private void ApplyTheme(string themeId)
    {
        ThemeManager.Apply(themeId);
        _settings.Theme = themeId;
        _settings.Save();
        RefreshThemeSwatchSelection();
        UpdateGalleryStorageBar();
    }


    
    
    
    
    private void RefreshThemeSwatchSelection()
    {
        var selected = new SolidColorBrush(Color.FromRgb(0x3E, 0xCF, 0x8E));
        foreach ((string themeId, Border swatch) in _themeSwatches.Select(kv => (kv.Key, kv.Value)))
            swatch.BorderBrush = string.Equals(ThemeManager.Current, themeId, StringComparison.OrdinalIgnoreCase) ? selected : Brushes.Transparent;
    }


    private void ThemeSwatchesScroll_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _themeSwatchesDragStart = e.GetPosition(ThemeSwatchesScroll);
        _themeSwatchesDragStartOffset = ThemeSwatchesScroll.HorizontalOffset;
        _themeSwatchesDragged = false;
        
        
        
        ThemeSwatchesScroll.CaptureMouse();
    }


    private void ThemeSwatchesScroll_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_themeSwatchesDragStart is not Point start || e.LeftButton != MouseButtonState.Pressed)
            return;

        double deltaX = e.GetPosition(ThemeSwatchesScroll).X - start.X;
        if (!_themeSwatchesDragged && Math.Abs(deltaX) < ThemeSwatchesDragThreshold)
            return;

        _themeSwatchesDragged = true;
        ThemeSwatchesScroll.ScrollToHorizontalOffset(_themeSwatchesDragStartOffset - deltaX);
    }


    private void ThemeSwatchesScroll_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_themeSwatchesDragStart is null)
            return;
        ThemeSwatchesScroll.ReleaseMouseCapture();
        _themeSwatchesDragStart = null;
        
        
        
        
        
        
        if (_themeSwatchesDragged)
            e.Handled = true;
    }


    private void ThemeSwatchesScroll_PreviewMouseLeave(object sender, MouseEventArgs e)
    {
        
        
        
        
        if (_themeSwatchesDragStart is null)
            return;
        ThemeSwatchesScroll.ReleaseMouseCapture();
        _themeSwatchesDragStart = null;
    }


    private void SettingsScrollHost_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _settingsAutoscrollActive)
            return;
        
        
        e.Handled = true;

        _settingsAutoscrollStartY = e.GetPosition(SettingsScrollHost).Y;
        _settingsAutoscrollActive = true;
        
        
        
        
        
        
        
        
        SettingsScrollHost.CaptureMouse();
        SettingsScrollHost.Cursor = Cursors.SizeAll;
        CompositionTarget.Rendering += SettingsAutoscroll_Tick;
    }


        private void SettingsScrollHost_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        e.Handled = true;
        StopSettingsAutoscroll();
    }


    private void SettingsAutoscroll_Tick(object? sender, EventArgs e)
    {
        double dy = Mouse.GetPosition(SettingsScrollHost).Y - _settingsAutoscrollStartY;
        if (Math.Abs(dy) < AutoscrollDeadZone)
            return;
        SettingsScrollHost.ScrollToVerticalOffset(SettingsScrollHost.VerticalOffset + dy * AutoscrollSensitivity);
    }


    private void StopSettingsAutoscroll()
    {
        if (!_settingsAutoscrollActive)
            return;
        _settingsAutoscrollActive = false;
        CompositionTarget.Rendering -= SettingsAutoscroll_Tick;
        SettingsScrollHost.ReleaseMouseCapture();
        
        
        
        SettingsScrollHost.Cursor = null;
    }


        private void OpenThemesFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ThemeManager.ThemesFolder); 
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ThemeManager.ThemesFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the themes folder: {ex.Message}", "Backtrack");
        }
    }

    private void LoadSettingsUi()
    {
        
        
        
        
        BuildThemeSwatches();
        RefreshThemeSwatchSelection();
        EnableAnimationsToggle.IsChecked = _settings.EnableAnimations;

        DiagnosticLogToggle.IsChecked = _settings.DiagnosticLogEnabled;
        OpenDiagnosticLogButton.Visibility = _settings.DiagnosticLogEnabled ? Visibility.Visible : Visibility.Collapsed;

        
        
        
        
        
        if (!_settings.DeveloperModeAutoSuggested)
        {
            _settings.DeveloperModeAutoSuggested = true;
            _settings.Save();
            if (UpdateService.IsRunningFromDevLocation)
            {
                SetDeveloperModeEnabled(true);
                DeveloperModeLockedNoteText.Visibility = Visibility.Visible;
            }
        }
        DeveloperModeToggle.IsChecked = _settings.DeveloperModeEnabled;

        DisableHardwareAccelToggle.IsChecked = _settings.DisableHardwareAcceleration;

        ShowRecentClipsToggle.IsChecked = _settings.ShowRecentClipsOverlay;
        LaunchWithWindowsToggle.IsChecked = _settings.LaunchWithWindows;
        ClipsFolderText.Text = _settings.ClipsFolder;
        BufferDurationSlider.Value = _settings.ReplayBufferMinutes;
        RefreshBufferDurationUi();

        BuffersSection.Visibility = _settings.ObsIsRemote ? Visibility.Collapsed : Visibility.Visible;
        RecordingsSection.Visibility = _settings.ObsIsRemote ? Visibility.Collapsed : Visibility.Visible;

        ObsRemoteToggle.IsChecked = _settings.ObsIsRemote;
        ObsRemoteFields.Visibility = _settings.ObsIsRemote ? Visibility.Visible : Visibility.Collapsed;
        ObsHostBox.Text = _settings.ObsHost;
        ObsPortBox.Text = _settings.ObsPort.ToString();
        ObsPasswordBox.Password = _settings.ObsRemotePassword;

        ShowDisclaimerToggle.IsChecked = _settings.ShowDisclaimer;
        DisableAudioCuesToggle.IsChecked = _settings.DisableAudioCues;
        if (AudioCueVolumeRow != null && AudioCueVolumeSlider != null && AudioCueVolumeText != null)
        {
            AudioCueVolumeRow.Opacity = _settings.DisableAudioCues ? 0.5 : 1.0;
            AudioCueVolumeSlider.ValueChanged -= AudioCueVolumeSlider_ValueChanged;
            AudioCueVolumeSlider.Value = Math.Clamp(_settings.AudioCueVolume, 0, 100);
            AudioCueVolumeSlider.IsEnabled = !_settings.DisableAudioCues;
            AudioCueVolumeSlider.ValueChanged += AudioCueVolumeSlider_ValueChanged;
            AudioCueVolumeText.Text = $"{Math.Clamp(_settings.AudioCueVolume, 0, 100)}%";
        }
        ShowStatusIndicatorToggle.IsChecked = _settings.ShowStatusIndicator;
        
        
        
        DefaultAudioTrackSelector.SelectionChanged -= DefaultAudioTrackSelector_SelectionChanged;
        DefaultAudioTrackSelector.SelectedIndex = Math.Clamp(_settings.DefaultPlayerAudioTrackIndex, 0, 6);
        DefaultAudioTrackSelector.SelectionChanged += DefaultAudioTrackSelector_SelectionChanged;

        
        
        
        
        StatusIndicatorOrientationSelector.SelectionChanged -= StatusIndicatorOrientationSelector_SelectionChanged;
        StatusIndicatorOrientationSelector.SelectedIndex = _settings.StatusIndicatorOrientation == StatusIndicatorOrientation.Vertical ? 1 : 0;
        StatusIndicatorOrientationSelector.SelectionChanged += StatusIndicatorOrientationSelector_SelectionChanged;

        StatusIndicatorLocationSelector.SelectionChanged -= StatusIndicatorLocationSelector_SelectionChanged;
        StatusIndicatorLocationSelector.SelectedIndex = (int)_settings.StatusIndicatorLocation;
        StatusIndicatorLocationSelector.SelectionChanged += StatusIndicatorLocationSelector_SelectionChanged;

        UpdateStatusIndicatorPreview();

        
        
        
        
        
        
        
        
        
        
        if (_settings.DeveloperModeEnabled && !_settings.DisableBacktrackAutoUpdate)
        {
            _settings.DisableBacktrackAutoUpdate = true;
            _settings.Save();
        }
        DisableBacktrackAutoUpdateToggle.IsChecked = _settings.DisableBacktrackAutoUpdate;
        
        
        
        
        DisableBacktrackAutoUpdateToggle.IsEnabled = !_settings.DeveloperModeEnabled;

        
        
        
        if (_settings.ObsIsRemote && !_settings.DisablePluginAutoUpdate)
        {
            _settings.DisablePluginAutoUpdate = true;
            _settings.Save();
        }
        DisablePluginAutoUpdateToggle.IsChecked = _settings.ObsIsRemote || _settings.DisablePluginAutoUpdate;
        DisablePluginAutoUpdateToggle.IsEnabled = !_settings.ObsIsRemote;
        HotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.HotkeyModifiers, (uint)_settings.HotkeyVirtualKey);
        CancelRecordHotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.CancelRecordHotkeyModifiers, (uint)_settings.CancelRecordHotkeyVirtualKey);
        BookmarkHotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.BookmarkHotkeyModifiers, (uint)_settings.BookmarkHotkeyVirtualKey);

        LoadDisplaySelector();

        ShareClipsToggle.IsChecked = _settings.ShareClipsEnabled;
        RefreshShareClipsUi();
        RefreshPairingStatusUi();
        RenderDiscoveredDevices();

        RamDiskToggle.IsChecked = _settings.RamDiskEnabled;
        RamDiskFields.Visibility = _settings.RamDiskEnabled ? Visibility.Visible : Visibility.Collapsed;
        RamDiskDriveBox.Text = _settings.RamDiskDriveLetter.ToString();
        RamDiskSizeBox.Text = _settings.RamDiskSizeMb.ToString();
        RefreshRamDiskStatusText();

        StorageLimitToggle.IsChecked = _settings.StorageLimitEnabled;
        StorageLimitFields.Visibility = _settings.StorageLimitEnabled ? Visibility.Visible : Visibility.Collapsed;
        StorageLimitGbBox.Text = _settings.StorageLimitGb.ToString("0.#");
        RefreshStorageLimitStatusText();

        AutoDeleteOldClipsToggle.IsChecked = _settings.AutoDeleteOldClipsEnabled;
        AutoDeleteOldClipsFields.Visibility = _settings.AutoDeleteOldClipsEnabled ? Visibility.Visible : Visibility.Collapsed;
        AutoDeleteOldClipsDaysBox.Text = _settings.AutoDeleteOldClipsAfterDays.ToString();

        OverlayLogToggle.IsChecked = _settings.OverlayLogEnabled;
        OverlayLogModeFields.Visibility = _settings.OverlayLogEnabled ? Visibility.Visible : Visibility.Collapsed;
        
        
        
        
        OverlayLogModeSelector.SelectionChanged -= OverlayLogModeSelector_SelectionChanged;
        OverlayLogModeSelector.SelectedIndex = _settings.OverlayLogMode == "Backtrack" ? 1 : 0;
        OverlayLogModeSelector.SelectionChanged += OverlayLogModeSelector_SelectionChanged;

        
        
        
        
        
        
        
    }

    private sealed record DisplayOption(string DeviceName, string Name);

}
