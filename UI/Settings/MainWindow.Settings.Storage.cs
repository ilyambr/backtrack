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

namespace Backtrack;

public partial class MainWindow : Window
{
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

    internal void StorageLimitToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.StorageLimitEnabled = StorageLimitToggle.IsChecked == true;
        _settings.Save();
        StorageLimitFields.Visibility = _settings.StorageLimitEnabled ? Visibility.Visible : Visibility.Collapsed;
        RefreshStorageLimitStatusText();
    }

    internal void ClearSettingsCacheButton_Click(object sender, RoutedEventArgs e)
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

                        try { File.Delete(f); } catch { }
                    }
                }

                AppSettings.ClearSavedFile();

                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    try { Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true }); }
                    catch { }
                }
                Application.Current.Shutdown();
            });
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

    internal void ApplyStorageLimit_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(StorageLimitGbBox.Text.Trim(), out double gb) || gb <= 0)
        {
            MessageBox.Show(this, "Storage limit must be a number of gigabytes greater than 0.", "Backtrack");
            return;
        }
        _settings.StorageLimitGb = gb;
        _settings.Save();
        RefreshStorageLimitStatusText();
    }

    internal void ApplyAutoDeleteOldClips_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AutoDeleteOldClipsDaysBox.Text.Trim(), out int days) || days <= 0)
        {
            MessageBox.Show(this, "Age must be a whole number of days greater than 0.", "Backtrack");
            return;
        }
        _settings.AutoDeleteOldClipsAfterDays = days;
        _settings.Save();
        RestartAutoDeleteOldClipsTimer();
    }
}
