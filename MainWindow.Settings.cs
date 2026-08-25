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

    private void ShowAppStartedToast()
    {
        string hotkey = FormatHotkeyText();
        _toastOverlay.ShowAppStarted(hotkey);
    }


    private string FormatHotkeyText()
    {
        var parts = new List<string>();
        if ((_settings.HotkeyModifiers & 0x2) != 0) parts.Add("Ctrl");
        if ((_settings.HotkeyModifiers & 0x1) != 0) parts.Add("Alt");
        if ((_settings.HotkeyModifiers & 0x4) != 0) parts.Add("Shift");
        if ((_settings.HotkeyModifiers & 0x8) != 0) parts.Add("Win");

        string keyStr;
        if (_settings.HotkeyVirtualKey >= 'A' && _settings.HotkeyVirtualKey <= 'Z')
            keyStr = ((char)_settings.HotkeyVirtualKey).ToString();
        else if (_settings.HotkeyVirtualKey >= 0x30 && _settings.HotkeyVirtualKey <= 0x39)
            keyStr = ((char)_settings.HotkeyVirtualKey).ToString();
        else
            keyStr = System.Windows.Input.KeyInterop.KeyFromVirtualKey(_settings.HotkeyVirtualKey).ToString();

        parts.Add(keyStr);
        return string.Join("+", parts);
    }


    

        private async Task InitializeRamDiskAsync()
    {
        if (!_settings.RamDiskEnabled)
            return;

        (bool ok, string? error) = await Task.Run(EnsureRamDiskReady);
        RefreshRamDiskStatusText();

        if (!ok)
        {
            
            
            
            Debug.WriteLine($"RAM disk setup failed: {error}");
            MessageBox.Show(this, $"Couldn't set up the RAM disk: {error}", "Backtrack");
            return;
        }

        
        
        
        if (!_settings.RamDiskInstructionShown)
        {
            _settings.RamDiskInstructionShown = true;
            _settings.Save();
            MessageBox.Show(this,
                $"RAM disk mounted at {_settings.RamDiskDriveLetter}:\\.\n\n" +
                "One-time step: in OBS, go to Settings > Output > Replay Buffer and set its output path to that drive letter. " +
                "OBS doesn't expose a way for Backtrack to do this part for you automatically.",
                "Backtrack");
        }

        if (_obs.IsConnected)
            _ = PushRamDiskDestDirAsync();
    }


    private (bool Success, string? Error) EnsureRamDiskReady()
    {
        if (!RamDisk.IsDriverInstalled())
        {
            (bool installed, string? installError) = RamDisk.InstallDriverElevated();
            if (!installed)
                return (false, installError);
        }

        (bool ok, string? error) = RamDisk.Mount(_settings.RamDiskDriveLetter, _settings.RamDiskSizeMb);
        AppLog.Write(ok
            ? $"RAM disk mounted at {_settings.RamDiskDriveLetter}: ({_settings.RamDiskSizeMb} MB)"
            : $"RAM disk mount failed: {error}");
        return (ok, error);
    }


        private async Task PushRamDiskDestDirAsync()
    {
        try
        {
            await _obs.SetReplayDestDirAsync(_settings.ClipsFolder);
        }
        catch
        {
            
            
            
        }
    }


    private void RefreshRamDiskStatusText()
    {
        if (!_settings.RamDiskEnabled)
        {
            RamDiskStatusText.Text = "Off";
        }
        else if (!RamDisk.IsDriverInstalled())
        {
            RamDiskStatusText.Text = "Enabled -- driver not installed yet (installs on next apply, needs one admin prompt)";
        }
        else if (RamDisk.IsMounted(_settings.RamDiskDriveLetter))
        {
            RamDiskStatusText.Text = $"Mounted at {_settings.RamDiskDriveLetter}:\\ ({_settings.RamDiskSizeMb} MB)";
        }
        else
        {
            RamDiskStatusText.Text = "Enabled, but not currently mounted";
        }
    }


    private async void RamDiskToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = RamDiskToggle.IsChecked == true;
        await ApplyRamDiskConfigAsync(enabled, _settings.RamDiskDriveLetter, _settings.RamDiskSizeMb);
    }


        private void RefreshStorageLimitStatusText()
    {
        if (!_settings.StorageLimitEnabled)
        {
            StorageLimitStatusText.Text = "Off - no limit";
            return;
        }
        double usedGb = GetClipsFolderUsageBytes() / (double)BytesPerGb;
        StorageLimitStatusText.Text = $"{usedGb:0.0} GB used of {_settings.StorageLimitGb:0.#} GB limit";
    }


    private void StorageLimitToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.StorageLimitEnabled = StorageLimitToggle.IsChecked == true;
        _settings.Save();
        StorageLimitFields.Visibility = _settings.StorageLimitEnabled ? Visibility.Visible : Visibility.Collapsed;
        RefreshStorageLimitStatusText();
    }


        private async Task<(bool Success, string? Error)> ApplyRamDiskConfigAsync(bool enabled, char driveLetter, int sizeMb)
    {
        char oldDrive = _settings.RamDiskDriveLetter;
        bool driveOrSizeChanged = oldDrive != driveLetter || sizeMb != _settings.RamDiskSizeMb;

        
        
        
        if ((!enabled || driveOrSizeChanged) && RamDisk.IsMounted(oldDrive))
        {
            await Task.Run(() => RamDisk.Unmount(oldDrive));
            AppLog.Write($"RAM disk unmounted ({oldDrive}:)");
        }

        _settings.RamDiskEnabled = enabled;
        _settings.RamDiskDriveLetter = driveLetter;
        _settings.RamDiskSizeMb = sizeMb;
        _settings.Save();

        Dispatcher.Invoke(() =>
        {
            RamDiskToggle.IsChecked = enabled;
            RamDiskFields.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            RamDiskDriveBox.Text = driveLetter.ToString();
            RamDiskSizeBox.Text = sizeMb.ToString();
        });

        (bool ok, string? error) = enabled ? await Task.Run(EnsureRamDiskReady) : (true, null);

        Dispatcher.Invoke(() =>
        {
            RefreshRamDiskStatusText();
            RefreshBufferDurationUi();
        });

        if (enabled && !ok)
        {
            Debug.WriteLine($"RAM disk setup failed: {error}");
            Dispatcher.Invoke(() => MessageBox.Show(this, $"Couldn't set up the RAM disk: {error}", "Backtrack"));
            return (false, error);
        }

        if (enabled && ok)
        {
            if (!_settings.RamDiskInstructionShown)
            {
                _settings.RamDiskInstructionShown = true;
                _settings.Save();
                Dispatcher.Invoke(() => MessageBox.Show(this,
                    $"RAM disk mounted at {driveLetter}:\\.\n\n" +
                    "One-time step: in OBS, go to Settings > Output > Replay Buffer and set its output path to that drive letter. " +
                    "OBS doesn't expose a way for Backtrack to do this part for you automatically.",
                    "Backtrack"));
            }

            if (_obs.IsConnected)
                _ = PushRamDiskDestDirAsync();
        }

        if (!enabled)
        {
            
            
            
            
            _ = RevertRamDiskDestDirsAsync(oldDrive);

            if (_settings.RamDiskInstructionShown)
            {
                Dispatcher.Invoke(() => MessageBox.Show(this,
                    "RAM disk turned off. Backtrack switched the plugin's clip destination back to your Clips folder.\n\n" +
                    $"One last step in OBS: go to Settings > Output > Replay Buffer and change the output path from {oldDrive}:\\ to a real folder (like your Clips folder). " +
                    "Backtrack can't change this setting automatically, so replay saves won't work until you do.",
                    "Backtrack"));
            }
        }

        return (true, null);
    }


        private void ClearSettingsCacheButton_Click(object sender, RoutedEventArgs e)
    {
        string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Backtrack", "thumbnails");

        ShowConfirmDialog(
            "Reset Backtrack to its default settings and clear cached thumbnails? This resets your hotkey, theme, clips folder, OBS connection, and other settings. Your clips won't be deleted. Backtrack will restart afterward.",
            "Reset",
            confirmed =>
            {
                if (!confirmed) return;

                if (Directory.Exists(cacheDir))
                {
                    foreach (string f in Directory.EnumerateFiles(cacheDir))
                    {
                        
                        
                        
                        try { File.Delete(f); } catch {  }
                    }
                }

                AppSettings.ClearSavedFile();

                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    try { Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true }); }
                    catch {  }
                }
                Application.Current.Shutdown();
            });
    }


        private void RefreshBufferDurationUi()
    {
        
        
        
        if (_settings is null)
            return;

        int minutes = (int)BufferDurationSlider.Value;
        BufferDurationValueText.Text = $"{minutes:00}:00";

        if (!_settings.RamDiskEnabled)
        {
            BufferDurationWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        ReplayBufferSizing.Estimate? estimate = ReplayBufferSizing.TryEstimate(minutes);
        if (estimate is null || estimate.Value.SuggestedSizeMb <= _settings.RamDiskSizeMb)
        {
            BufferDurationWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        BufferDurationWarningText.Text =
            $"⚠ A full flush at {minutes} min is estimated at ~{estimate.Value.SuggestedSizeMb} MB (~{estimate.Value.AssumedBitrateKbps} kbps), " +
            $"more than your {_settings.RamDiskSizeMb} MB RAM disk. Saves at this length risk failing outright -- shorten this or grow the RAM disk first.";
        BufferDurationWarningText.Visibility = Visibility.Visible;
    }


    private void RegisterHotkeyFromSettings()
    {
        try
        {
            _hotkey = new GlobalHotkey(this, (GlobalHotkey.Modifiers)_settings.HotkeyModifiers, (uint)_settings.HotkeyVirtualKey, id: OpenOverlayHotkeyId);
            _hotkey.Pressed += () => Dispatcher.Invoke(ToggleVisible);
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Hotkey registration failed: {ex.Message}");
        }

        if (_settings.CancelRecordHotkeyVirtualKey != 0)
        {
            try
            {
                _cancelRecordHotkey = new GlobalHotkey(this, (GlobalHotkey.Modifiers)_settings.CancelRecordHotkeyModifiers, (uint)_settings.CancelRecordHotkeyVirtualKey, id: CancelRecordHotkeyId);
                _cancelRecordHotkey.Pressed += () => Dispatcher.Invoke(async () =>
                {
                    await CancelActiveRecordingsAsync();
                    await RefreshStatusAsync();
                });
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"Cancel record hotkey registration failed: {ex.Message}");
            }
        }

        if (_settings.BookmarkHotkeyVirtualKey != 0)
        {
            try
            {
                _bookmarkHotkey = new GlobalHotkey(this, (GlobalHotkey.Modifiers)_settings.BookmarkHotkeyModifiers, (uint)_settings.BookmarkHotkeyVirtualKey, id: BookmarkHotkeyId);
                _bookmarkHotkey.Pressed += () => Dispatcher.Invoke(OnBookmarkHotkeyPressed);
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"Bookmark hotkey registration failed: {ex.Message}");
            }
        }
    }


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


        private bool TryBlockForStorageLimit(out string? blockedMessage)
    {
        blockedMessage = null;
        if (!_settings.StorageLimitEnabled)
            return false;

        long usedBytes = GetClipsFolderUsageBytes();
        long limitBytes = (long)(_settings.StorageLimitGb * BytesPerGb);
        if (usedBytes < limitBytes)
            return false;

        double usedGb = usedBytes / (double)BytesPerGb;
        blockedMessage = $"Your clips folder is at {usedGb:0.0} GB, at or over your {_settings.StorageLimitGb:0.#} GB storage limit. " +
            "Free up space, delete some clips, or raise the limit in Settings before recording or saving more.";
        return true;
    }


    private Button BuildRecordRowButton(string label, int status, Func<Task> start, Func<Task> stop, Func<Task>? cancel = null, string? hotkey = null)
    {
        bool recording = status == RecordStatusRecording;

        (string brushKey, string stateLabel) = status switch
        {
            RecordStatusRecording => ("Rec", "Recording"),
            RecordStatusError => ("RecDark", "Error"),
            RecordStatusInactive => ("Text2", "Inactive"),
            RecordStatusNoSignal => ("Text2", "No Signal"),
            _ => ("Text2", "Stopped"),
        };

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = (Brush)FindResource(brushKey),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };

        var name = new TextBlock { Text = label, FontWeight = FontWeights.Bold, FontSize = 12.5, Foreground = (Brush)FindResource("Text0") };
        var stateText = new TextBlock
        {
            Text = stateLabel,
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var statePanel = new StackPanel { Orientation = Orientation.Horizontal };
        statePanel.Children.Add(dot);
        statePanel.Children.Add(stateText);

        
        
        
        
        
        
        
        
        
        UIElement rightContent = statePanel;
        if (hotkey != null)
        {
            var hotkeyText = new TextBlock
            {
                Text = string.IsNullOrEmpty(hotkey) ? "(unbound)" : hotkey,
                FontSize = 10,
                Foreground = (Brush)FindResource("Text2"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            var rightPanel = new StackPanel { Orientation = Orientation.Horizontal };
            rightPanel.Children.Add(hotkeyText);
            rightPanel.Children.Add(statePanel);
            rightContent = rightPanel;
        }

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 0);
        Grid.SetColumn(rightContent, 1);
        content.Children.Add(name);
        content.Children.Add(rightContent);

        string styleKey = recording ? "BufRowButton" : "BufRowButtonNoHover";
        var button = new Button { Style = (Style)FindResource(styleKey), Content = content };

        if (cancel is not null)
        {
            button.MouseRightButtonUp += (s, e) =>
            {
                if (stateText.Text != "Recording")
                    return;

                e.Handled = true;
                var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
                var cancelItem = new MenuItem
                {
                    Header = "Cancel recording",
                    Style = (Style)FindResource("DarkMenuItem"),
                    Foreground = (Brush)FindResource("Rec")
                };
                cancelItem.Click += async (_, _) =>
                {
                    button.IsEnabled = false;
                    try
                    {
                        await cancel();
                        dot.Fill = (Brush)FindResource("Text2");
                        stateText.Text = "Stopped";
                        button.Style = (Style)FindResource("BufRowButton");
                        await Task.Delay(1000);
                        await LoadRecordRowsAsync();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"Couldn't cancel recording: {ex.Message}", "Backtrack");
                        button.IsEnabled = true;
                    }
                };
                contextMenu.Items.Add(cancelItem);
                contextMenu.PlacementTarget = button;
                contextMenu.Placement = PlacementMode.MousePoint;
                contextMenu.IsOpen = true;
            };
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (status == RecordStatusInactive || status == RecordStatusNoSignal)
        {
            button.IsEnabled = false;
            return button;
        }

        button.Click += async (_, _) =>
        {
            
            
            
            if (!recording && TryBlockForStorageLimit(out string? blockMessage))
            {
                MessageBox.Show(this, blockMessage, "Backtrack");
                return;
            }
            button.IsEnabled = false;
            try
            {
                
                
                
                
                await (recording ? stop() : start());

                
                
                
                
                
                
                
                
                
                
                
                
                
                dot.Fill = (Brush)FindResource(recording ? "Text2" : "Rec");
                stateText.Text = recording ? "Stopped" : "Recording";
                button.Style = (Style)FindResource(recording ? "BufRowButtonNoHover" : "BufRowButton");

                
                
                
                
                
                
                
                
                
                
                
                
                
                await Task.Delay(TimeSpan.FromSeconds(2));
                await LoadRecordRowsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't {(recording ? "stop" : "start")} recording: {ex.Message}", "Backtrack");
                button.IsEnabled = true;
            }
        };
        return button;
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


        private void RefreshRamDiskRemoteGating()
    {
        bool remote = _settings.ObsIsRemote;
        LocalRamDiskSection.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
        RemoteRamDiskSection.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;

        if (remote)
            _ = LoadRemoteRamDiskUi();
    }


    private async Task LoadRemoteRamDiskUi()
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            RemoteRamDiskStatusText.Text = "Not paired with a transmitter PC yet -- pair with it first (below, in OBS section).";
            RemoteRamDiskFields.Visibility = Visibility.Collapsed;
            return;
        }

        RemoteRamDiskStatusText.Text = $"Loading from {_settings.PairedPeerName}...";
        RemoteRamDiskFields.Visibility = Visibility.Collapsed;

        RamDiskSnapshot? snapshot = await _pairing.GetRemoteRamDiskSettingsAsync();
        if (snapshot is null)
        {
            RemoteRamDiskStatusText.Text = $"Couldn't reach {_settings.PairedPeerName}'s Backtrack -- make sure it's running and re-open Settings to retry.";
            return;
        }

        RemoteRamDiskStatusText.Text = snapshot.Enabled
            ? (snapshot.Mounted
                ? $"Mounted at {snapshot.DriveLetter}:\\ ({snapshot.SizeMb} MB) on {_settings.PairedPeerName}"
                : $"Enabled on {_settings.PairedPeerName}, but not currently mounted")
            : $"Off on {_settings.PairedPeerName}";
        RemoteRamDiskFields.Visibility = Visibility.Visible;
        RemoteRamDiskToggle.IsChecked = snapshot.Enabled;
        RemoteRamDiskDriveBox.Text = snapshot.DriveLetter.ToString();
        RemoteRamDiskSizeBox.Text = snapshot.SizeMb.ToString();
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


    

    private static string FormatHotkey(GlobalHotkey.Modifiers modifiers, uint virtualKey)
    {
        if (virtualKey == 0)
            return "(unbound)";
        var parts = new List<string>();
        if (modifiers.HasFlag(GlobalHotkey.Modifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(GlobalHotkey.Modifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(GlobalHotkey.Modifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(GlobalHotkey.Modifiers.Win)) parts.Add("Win");

        string keyStr = virtualKey switch
        {
            186 => ";",
            187 => "=",
            188 => ",",
            189 => "-",
            190 => ".",
            191 => "/",
            192 => "`",
            219 => "[",
            220 => "\\",
            221 => "]",
            222 => "'",
            _ => (virtualKey >= 'A' && virtualKey <= 'Z') || (virtualKey >= '0' && virtualKey <= '9')
                ? ((char)virtualKey).ToString()
                : KeyInterop.KeyFromVirtualKey((int)virtualKey).ToString()
        };

        parts.Add(keyStr);
        return string.Join("+", parts);
    }


    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey || _capturingCancelRecordHotkey)
            return;

        _capturingHotkey = true;
        HotkeyCaptureButton.Content = "Press a key combo...";
        PreviewKeyDown += HotkeyCapture_PreviewKeyDown;
    }


    private void HotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            e.Handled = true;
            EndHotkeyCapture(cancelled: true);
            return;
        }

        e.Handled = true;

        GlobalHotkey.Modifiers modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= GlobalHotkey.Modifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= GlobalHotkey.Modifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= GlobalHotkey.Modifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= GlobalHotkey.Modifiers.Win;

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        try
        {
            if (_hotkey is null)
            {
                _hotkey = new GlobalHotkey(this, modifiers, virtualKey, id: OpenOverlayHotkeyId);
                _hotkey.Pressed += () => Dispatcher.Invoke(ToggleVisible);
            }
            else
            {
                _hotkey.Rebind(modifiers, virtualKey);
            }

            _settings.HotkeyModifiers = (int)modifiers;
            _settings.HotkeyVirtualKey = (int)virtualKey;
            _settings.Save();
            HotkeyCaptureButton.Content = FormatHotkey(modifiers, virtualKey);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Backtrack");
            HotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.HotkeyModifiers, (uint)_settings.HotkeyVirtualKey);
        }

        EndHotkeyCapture(cancelled: false);
    }


    private void EndHotkeyCapture(bool cancelled)
    {
        PreviewKeyDown -= HotkeyCapture_PreviewKeyDown;
        _capturingHotkey = false;
        if (cancelled)
            HotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.HotkeyModifiers, (uint)_settings.HotkeyVirtualKey);
    }


    private void CancelRecordHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingCancelRecordHotkey || _capturingHotkey)
            return;

        _capturingCancelRecordHotkey = true;
        CancelRecordHotkeyCaptureButton.Content = "Press a key combo (Esc to clear)...";
        PreviewKeyDown += CancelRecordHotkeyCapture_PreviewKeyDown;
    }


    private void CancelRecordHotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            e.Handled = true;
            _cancelRecordHotkey?.Dispose();
            _cancelRecordHotkey = null;
            _settings.CancelRecordHotkeyModifiers = 0;
            _settings.CancelRecordHotkeyVirtualKey = 0;
            _settings.Save();
            CancelRecordHotkeyCaptureButton.Content = "(unbound)";
            EndCancelRecordHotkeyCapture(cancelled: false);
            return;
        }

        e.Handled = true;

        GlobalHotkey.Modifiers modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= GlobalHotkey.Modifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= GlobalHotkey.Modifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= GlobalHotkey.Modifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= GlobalHotkey.Modifiers.Win;

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        try
        {
            if (_cancelRecordHotkey is null)
            {
                _cancelRecordHotkey = new GlobalHotkey(this, modifiers, virtualKey, id: CancelRecordHotkeyId);
                _cancelRecordHotkey.Pressed += () => Dispatcher.Invoke(async () =>
                {
                    await CancelActiveRecordingsAsync();
                    await RefreshStatusAsync();
                });
            }
            else
            {
                _cancelRecordHotkey.Rebind(modifiers, virtualKey);
            }

            _settings.CancelRecordHotkeyModifiers = (int)modifiers;
            _settings.CancelRecordHotkeyVirtualKey = (int)virtualKey;
            _settings.Save();
            CancelRecordHotkeyCaptureButton.Content = FormatHotkey(modifiers, virtualKey);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Backtrack");
            CancelRecordHotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.CancelRecordHotkeyModifiers, (uint)_settings.CancelRecordHotkeyVirtualKey);
        }

        EndCancelRecordHotkeyCapture(cancelled: false);
    }


    private void EndCancelRecordHotkeyCapture(bool cancelled)
    {
        PreviewKeyDown -= CancelRecordHotkeyCapture_PreviewKeyDown;
        _capturingCancelRecordHotkey = false;
        if (cancelled)
            CancelRecordHotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.CancelRecordHotkeyModifiers, (uint)_settings.CancelRecordHotkeyVirtualKey);
    }

    private void BookmarkHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingBookmarkHotkey || _capturingHotkey || _capturingCancelRecordHotkey)
            return;

        _capturingBookmarkHotkey = true;
        BookmarkHotkeyCaptureButton.Content = "Press a key combo (Esc to clear)...";
        PreviewKeyDown += BookmarkHotkeyCapture_PreviewKeyDown;
    }

    private void BookmarkHotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            e.Handled = true;
            _bookmarkHotkey?.Dispose();
            _bookmarkHotkey = null;
            _settings.BookmarkHotkeyModifiers = 0;
            _settings.BookmarkHotkeyVirtualKey = 0;
            _settings.Save();
            BookmarkHotkeyCaptureButton.Content = "(unbound)";
            EndBookmarkHotkeyCapture(cancelled: false);
            return;
        }

        e.Handled = true;

        GlobalHotkey.Modifiers modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= GlobalHotkey.Modifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= GlobalHotkey.Modifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= GlobalHotkey.Modifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= GlobalHotkey.Modifiers.Win;

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        try
        {
            if (_bookmarkHotkey is null)
            {
                _bookmarkHotkey = new GlobalHotkey(this, modifiers, virtualKey, id: BookmarkHotkeyId);
                _bookmarkHotkey.Pressed += () => Dispatcher.Invoke(OnBookmarkHotkeyPressed);
            }
            else
            {
                _bookmarkHotkey.Rebind(modifiers, virtualKey);
            }

            _settings.BookmarkHotkeyModifiers = (int)modifiers;
            _settings.BookmarkHotkeyVirtualKey = (int)virtualKey;
            _settings.Save();
            BookmarkHotkeyCaptureButton.Content = FormatHotkey(modifiers, virtualKey);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Backtrack");
            BookmarkHotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.BookmarkHotkeyModifiers, (uint)_settings.BookmarkHotkeyVirtualKey);
        }

        EndBookmarkHotkeyCapture(cancelled: false);
    }

    private void EndBookmarkHotkeyCapture(bool cancelled)
    {
        PreviewKeyDown -= BookmarkHotkeyCapture_PreviewKeyDown;
        _capturingBookmarkHotkey = false;
        if (cancelled)
            BookmarkHotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.BookmarkHotkeyModifiers, (uint)_settings.BookmarkHotkeyVirtualKey);
    }

}
