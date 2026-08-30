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
using Backtrack.Obs;
using Backtrack.Pairing;
using Microsoft.Win32;

namespace Backtrack;

public partial class MainWindow : Window
{
    private async Task LoadBufferVisibilityUi()
    {
        if (_settings.ObsIsRemote)
            return;

        BufferVisibilityPanel.Children.Clear();

        if (!_obs.IsConnected)
        {
            AddInfoLine(BufferVisibilityPanel, "Not connected to OBS.");
            return;
        }

        List<ReplayRow> rows;
        try
        {
            rows = await _obs.ListReplayRowsAsync();
        }
        catch (Exception ex)
        {
            AddInfoLine(BufferVisibilityPanel, $"Could not reach the Replay Slider bridge: {ex.Message}");
            return;
        }

        if (rows.Count == 0)
        {
            AddInfoLine(BufferVisibilityPanel, "No replay buffers found.");
            return;
        }

        foreach (ReplayRow row in rows)
            BufferVisibilityPanel.Children.Add(BuildBufferVisibilityRow(row));
    }

    private Border BuildBufferVisibilityRow(ReplayRow row)
    {
        string label = row.Label;
        var toggle = new ToggleButton { Style = (Style)FindResource("AppToggle"), VerticalAlignment = VerticalAlignment.Center };
        toggle.IsChecked = !_settings.HiddenBufferLabels.Contains(label);

        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition());
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = DisplayLabel(label), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Text0"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(toggle, 1);
        topGrid.Children.Add(name);
        topGrid.Children.Add(toggle);
        EnableDoubleTapRename(name, label);

        var folderLabel = new TextBlock
        {
            Text = DescribeRowDestDir(row.DestDir),
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var folderButton = BuildFolderIconButton(async (_, _) => await PickBufferDestFolderAsync(row.Key, folderLabel));

        var bottomGrid = new Grid
        {
            Margin = new Thickness(0, 8, 0, 0),

            Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed,
        };
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition());
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(folderLabel, 0);
        Grid.SetColumn(folderButton, 1);
        bottomGrid.Children.Add(folderLabel);
        bottomGrid.Children.Add(folderButton);

        toggle.Click += (_, _) =>
        {
            if (toggle.IsChecked == true)
                _settings.HiddenBufferLabels.Remove(label);
            else
                _settings.HiddenBufferLabels.Add(label);
            _settings.Save();
            bottomGrid.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        };

        var container = new StackPanel();
        container.Children.Add(topGrid);
        container.Children.Add(bottomGrid);

        return new Border { Style = (Style)FindResource("SettingsRow"), Child = container };
    }

    private async Task PickBufferDestFolderAsync(string rowKey, TextBlock folderLabel)
    {
        try
        {
            Directory.CreateDirectory(_settings.ClipsFolder);
            var dialog = new OpenFolderDialog { InitialDirectory = _settings.ClipsFolder };
            if (dialog.ShowDialog(this) != true)
                return;

            if (!IsWithinClipsFolder(dialog.FolderName, out string relative))
            {
                MessageBox.Show(this, "Pick a folder inside your clips folder -- Gallery only browses within that tree.", "Backtrack");
                return;
            }

            string absolutePath = relative.Length == 0 ? _settings.ClipsFolder : Path.Combine(_settings.ClipsFolder, relative);
            Directory.CreateDirectory(absolutePath);

            await _obs.SetReplayRowDestDirAsync(rowKey, absolutePath);
            folderLabel.Text = relative.Length == 0 ? "Main clips folder" : relative;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't set that folder: {ex.Message}", "Backtrack");
        }
    }

    private Button BuildRowButton(ReplayRow row)
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = (Brush)FindResource(row.Status switch { 1 => "Green", 2 => "Rec", _ => "Text2" }),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };

        var name = new TextBlock { Text = DisplayLabel(row.Label), FontWeight = FontWeights.Bold, FontSize = 12.5, Foreground = (Brush)FindResource("Text0") };
        var hotkey = new TextBlock
        {
            Text = string.IsNullOrEmpty(row.Hotkey) ? "(unbound)" : row.Hotkey,
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var hkPanel = new StackPanel { Orientation = Orientation.Horizontal };
        hkPanel.Children.Add(dot);
        hkPanel.Children.Add(hotkey);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 0);
        Grid.SetColumn(hkPanel, 1);
        content.Children.Add(name);
        content.Children.Add(hkPanel);

        string styleKey = row.Status == 1 ? "BufRowButton" : "BufRowButtonNoHover";
        var button = new Button { Style = (Style)FindResource(styleKey), Content = content, Tag = row.Key };

        if (row.Status == 0)
        {
            button.IsEnabled = false;
            return button;
        }

        button.Click += async (_, _) =>
        {
            if (TryBlockForStorageLimit(out string? blockMessage))
            {
                MessageBox.Show(this, blockMessage, "Backtrack");
                return;
            }
            button.IsEnabled = false;
            try
            {
                int preferredSeconds = _settings.PreferredClipLengthSeconds > 0 ? _settings.PreferredClipLengthSeconds : 60;
                bool isShortenedBackToBack = false;

                if (_lastReplaySaveUtc.TryGetValue(row.Key, out DateTime lastSave))
                {
                    double elapsed = (DateTime.UtcNow - lastSave).TotalSeconds;
                    if (elapsed > 1 && elapsed < preferredSeconds)
                    {
                        int effectiveSeconds = (int)Math.Ceiling(elapsed);
                        AppLog.Write($"[Replay] Smart deduplication for {row.Label}: clipping {effectiveSeconds}s since last save");
                        try { await _obs.SetReplayRowLengthAsync(row.Key, effectiveSeconds); isShortenedBackToBack = true; } catch { }
                    }
                    else if (preferredSeconds > 0)
                    {
                        try { await _obs.SetReplayRowLengthAsync(row.Key, preferredSeconds); } catch { }
                    }
                }
                else if (preferredSeconds > 0)
                {
                    try { await _obs.SetReplayRowLengthAsync(row.Key, preferredSeconds); } catch { }
                }

                _lastReplaySaveUtc[row.Key] = DateTime.UtcNow;
                await _obs.SaveReplayRowAsync(row.Key);

                if (isShortenedBackToBack && preferredSeconds > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1500);
                        try { await _obs.SetReplayRowLengthAsync(row.Key, preferredSeconds); } catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Save failed: {ex.Message}", "Backtrack");
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private Border BuildSharedClipLengthControl(List<ReplayRow> rows)
    {
        int maxSeconds = Math.Max(MinClipSeconds, _settings.ReplayBufferMinutes * 60);
        int initial = _settings.PreferredClipLengthSeconds > 0
            ? _settings.PreferredClipLengthSeconds
            : (rows.Count > 0 && rows[0].LengthSeconds > 0 ? rows[0].LengthSeconds : 60);
        initial = Math.Clamp(initial, MinClipSeconds, maxSeconds);

        var label = new TextBlock { Text = "Clip length", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Text1"), VerticalAlignment = VerticalAlignment.Center };

        var slider = new Slider { Style = (Style)FindResource("RowLengthSlider"), Value = SecondsToSliderPos(initial, maxSeconds), Margin = new Thickness(10, 0, 10, 0), IsMoveToPointEnabled = false };
        var lengthText = new TextBlock
        {
            Text = FormatDuration(initial * 1000L),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("Accent"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 34,
            TextAlignment = TextAlignment.Right,
        };
        slider.ValueChanged += (_, e) => lengthText.Text = FormatDuration(SliderPosToSeconds(e.NewValue, maxSeconds) * 1000L);

        slider.PreviewMouseLeftButtonDown += (_, e) =>
        {
            slider.CaptureMouse();
            SetSliderValueFromMouse(slider, e.GetPosition(slider));
            e.Handled = true;
        };
        slider.PreviewMouseMove += (_, e) =>
        {
            if (slider.IsMouseCaptured)
                SetSliderValueFromMouse(slider, e.GetPosition(slider));
        };
        slider.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            slider.ReleaseMouseCapture();

            int seconds = SliderPosToSeconds(slider.Value, maxSeconds);
            if (_settings.PreferredClipLengthSeconds != seconds)
            {
                _lastReplaySaveUtc.Clear();
                _streamDeckServer?.ClearLastSaveTimestamps();
            }

            _settings.PreferredClipLengthSeconds = seconds;
            _settings.Save();
            _ = _streamDeckServer?.BroadcastStateSnapshotAsync();

            foreach (ReplayRow row in _lastReplayRows)
            {
                try
                {
                    await _obs.SetReplayRowLengthAsync(row.Key, seconds);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not set clip length: {ex.Message}\n\n(Needs the set-row-length bridge update in obs-replay-slider.)", "Backtrack");
                    break;
                }
            }
            _ = _streamDeckServer?.BroadcastStateSnapshotAsync();
        };

        var row2 = new Grid { Margin = new Thickness(2, 12, 2, 0) };
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row2.ColumnDefinitions.Add(new ColumnDefinition());
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(label, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(lengthText, 2);
        row2.Children.Add(label);
        row2.Children.Add(slider);
        row2.Children.Add(lengthText);

        return new Border { BorderBrush = (Brush)FindResource("Hairline"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0), Margin = new Thickness(0, 6, 0, 0), Child = row2 };
    }

    private async Task<Border> BuildMainRecordFolderRowAsync()
    {
        string? currentFolder = await _obs.GetMainRecordDirectoryAsync();

        var name = new TextBlock { Text = "Full Scene", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Text0"), VerticalAlignment = VerticalAlignment.Center };

        var folderLabel = new TextBlock
        {
            Text = DescribeRecordRowDestDir(currentFolder),
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var folderButton = BuildFolderIconButton(async (_, _) => await PickMainRecordFolderAsync(folderLabel));

        var bottomGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition());
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(folderLabel, 0);
        Grid.SetColumn(folderButton, 1);
        bottomGrid.Children.Add(folderLabel);
        bottomGrid.Children.Add(folderButton);

        var container = new StackPanel();
        container.Children.Add(name);
        container.Children.Add(bottomGrid);

        return new Border { Style = (Style)FindResource("SettingsRow"), Child = container };
    }

    private async Task<Border> BuildRecordFolderRowAsync(RecordRow row)
    {
        string label = row.Label;
        string? currentFolder = await _obs.GetRecordRowDestinationFolderAsync(row.SourceName, row.FilterName);

        var toggle = new ToggleButton { Style = (Style)FindResource("AppToggle"), VerticalAlignment = VerticalAlignment.Center };
        toggle.IsChecked = !_settings.HiddenBufferLabels.Contains(label);

        var name = new TextBlock { Text = DisplayLabel(label), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Text0"), VerticalAlignment = VerticalAlignment.Center };

        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition());
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 0);
        Grid.SetColumn(toggle, 1);
        topGrid.Children.Add(name);
        topGrid.Children.Add(toggle);
        EnableDoubleTapRename(name, label);

        var folderLabel = new TextBlock
        {
            Text = DescribeRecordRowDestDir(currentFolder),
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var folderButton = BuildFolderIconButton(async (_, _) => await PickRecordRowFolderAsync(row.SourceName, row.FilterName, folderLabel));

        var bottomGrid = new Grid
        {
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed,
        };
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition());
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(folderLabel, 0);
        Grid.SetColumn(folderButton, 1);
        bottomGrid.Children.Add(folderLabel);
        bottomGrid.Children.Add(folderButton);

        toggle.Click += (_, _) =>
        {
            if (toggle.IsChecked == true)
                _settings.HiddenBufferLabels.Remove(label);
            else
                _settings.HiddenBufferLabels.Add(label);
            _settings.Save();
            bottomGrid.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        };

        var container = new StackPanel();
        container.Children.Add(topGrid);
        container.Children.Add(bottomGrid);

        return new Border { Style = (Style)FindResource("SettingsRow"), Child = container };
    }

    private void SetLocalRowNameOverride(string originalLabel, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, originalLabel, StringComparison.Ordinal))
            _settings.LocalRowNameOverrides.Remove(originalLabel);
        else
            _settings.LocalRowNameOverrides[originalLabel] = newName;
        _settings.Save();
    }
}
