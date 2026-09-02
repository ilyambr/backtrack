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

    private string? ComputeObsOverloadWarning(ObsStats stats)
    {
        const double ThresholdPct = 1.0;
        string? result = null;

        if (_lastRenderTotalFrames is long lastRenderTotal && _lastRenderSkippedFrames is long lastRenderSkipped)
        {
            long totalDelta = stats.RenderTotalFrames - lastRenderTotal;
            long skippedDelta = stats.RenderSkippedFrames - lastRenderSkipped;
            if (totalDelta > 0)
            {
                double pct = 100.0 * skippedDelta / totalDelta;
                if (pct > ThresholdPct)
                    result = $"Rendering lag ({pct:0.#}% frames skipped)";
            }
        }

        if (_lastOutputTotalFrames is long lastOutTotal && _lastOutputSkippedFrames is long lastOutSkipped)
        {
            long totalDelta = stats.OutputTotalFrames - lastOutTotal;
            long skippedDelta = stats.OutputSkippedFrames - lastOutSkipped;
            if (totalDelta > 0)
            {
                double pct = 100.0 * skippedDelta / totalDelta;
                if (pct > ThresholdPct)
                    result = $"Encoding overloaded ({pct:0.#}% frames skipped)";
            }
        }

        _lastRenderTotalFrames = stats.RenderTotalFrames;
        _lastRenderSkippedFrames = stats.RenderSkippedFrames;
        _lastOutputTotalFrames = stats.OutputTotalFrames;
        _lastOutputSkippedFrames = stats.OutputSkippedFrames;
        return result;
    }

    private async Task<(bool Available, string InstalledVersion)> CheckPluginAvailabilityAsync(string repo, string dllFileName, Func<string, bool> assetPredicate,
    Func<DateTimeOffset?> getLastApplied, Action<DateTimeOffset?> setLastApplied, Func<string?> getLastDigest, Action<string?> setLastDigest)
    {
        if (!_updates.IsObsInstalled)
            return (false, "OBS not installed");

        Version installed = _updates.GetInstalledPluginVersion(dllFileName);
        try
        {
            ReleaseInfo? release = await _updates.GetLatestReleaseAsync("ilyambr", repo, assetPredicate);
            if (release?.DownloadUrl is null)
                return (false, installed.ToString(3));

            bool versionBumped = UpdateService.IsNewer(release.Version, installed);
            return (ShouldApplyUpdate(release, versionBumped, installed == UpdateService.MissingPluginVersion, getLastApplied, setLastApplied, getLastDigest, setLastDigest), installed.ToString(3));
        }
        catch
        {
            return (false, installed.ToString(3));
        }
    }

    private (string Url, string? Password, bool ServerEnabledAtStartup) ResolveObsConnection()
    {
        if (_settings.ObsIsRemote)
            return ($"ws://{_settings.ObsHost}:{_settings.ObsPort}", _settings.ObsRemotePassword, true);

        (bool enabled, string? password) = ObsConfigReader.ReadLocalConfig();
        return ("ws://127.0.0.1:4455", password, enabled);
    }

    private async Task CancelActiveRecordingsAsync()
    {
        if (!_obs.IsConnected)
            return;

        try
        {
            RecordStatus mainStatus = await _obs.GetRecordStatusAsync();
            if (mainStatus.Active)
            {
                _cancellingMainRecording = true;
                _cancellingMainRecordingDuration = FormatDuration(mainStatus.DurationMs);
                await _obs.StopMainRecordAsync();
            }

            List<RecordRow> activeRows = (await _obs.ListRecordRowsAsync()).Where(r => r.Status == RecordStatusRecording).ToList();
            foreach (RecordRow row in activeRows)
            {
                _cancelledRecordRows.Add(row.Key);
                _recordRowInfoByKey[row.Key] = (row.Label, row.SourceName, row.FilterName);
                await _obs.CancelRecordRowAsync(row.Key);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"CancelActiveRecordings failed: {ex.Message}");
        }
    }

    internal void RecordTile_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RecordLabel.Text != "Stop Recording" && _lastKnownActiveRecordRowCount == 0)
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
            await CancelActiveRecordingsAsync();
            await RefreshStatusAsync();
        };
        contextMenu.Items.Add(cancelItem);
        contextMenu.PlacementTarget = RecordTile;
        contextMenu.Placement = PlacementMode.MousePoint;
        contextMenu.IsOpen = true;
    }

    private async Task LoadRecordFolderUi()
    {
        if (_settings.ObsIsRemote)
            return;

        RecordFolderPanel.Children.Clear();

        if (!_obs.IsConnected)
        {
            AddInfoLine(RecordFolderPanel, "Not connected to OBS.");
            return;
        }

        RecordFolderPanel.Children.Add(await BuildMainRecordFolderRowAsync());

        List<RecordRow> rows;
        try
        {
            rows = await _obs.ListRecordRowsAsync();
        }
        catch (Exception ex)
        {
            AddInfoLine(RecordFolderPanel, $"Could not reach the Replay Slider bridge: {ex.Message}");
            return;
        }

        if (rows.Count == 0)
        {
            AddInfoLine(RecordFolderPanel, "No Source Record filters found.");
            return;
        }

        foreach (RecordRow row in rows)
        {
            if (!string.IsNullOrEmpty(row.SourceName) && !string.IsNullOrEmpty(row.FilterName))
            {
                RecordFolderPanel.Children.Add(await BuildRecordFolderRowAsync(row));
            }
        }
    }

    private async Task PickMainRecordFolderAsync(TextBlock folderLabel)
    {
        try
        {
            string initialDir = await _obs.GetMainRecordDirectoryAsync() ?? _settings.ClipsFolder;
            var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(initialDir) ? initialDir : _settings.ClipsFolder };
            if (dialog.ShowDialog(this) != true)
                return;

            string selectedFolder = dialog.FolderName;
            await _obs.SetMainRecordDirectoryAsync(selectedFolder);
            folderLabel.Text = DescribeRecordRowDestDir(selectedFolder);
            AppLog.Write($"Set OBS's main recording path to '{selectedFolder}'");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't update recording folder: {ex.Message}", "Backtrack");
        }
    }

    private string DescribeRecordRowDestDir(string? destDir)
    {
        if (string.IsNullOrEmpty(destDir))
            return "Not set -- recordings stay wherever this filter writes them";

        return IsWithinClipsFolder(destDir, out string relative)
            ? (relative.Length == 0 ? "Not set -- recordings stay wherever this filter writes them" : relative)
            : destDir;
    }

    private async Task PickRecordRowFolderAsync(string sourceName, string filterName, TextBlock folderLabel)
    {
        try
        {
            Directory.CreateDirectory(_settings.ClipsFolder);
            var dialog = new OpenFolderDialog { InitialDirectory = _settings.ClipsFolder };
            if (dialog.ShowDialog(this) != true)
                return;

            string selectedFolder = dialog.FolderName;
            await _obs.SetRecordRowDestinationFolderAsync(sourceName, filterName, selectedFolder);
            folderLabel.Text = DescribeRecordRowDestDir(selectedFolder);
            AppLog.Write($"Set recording folder for '{sourceName} - {filterName}' to '{selectedFolder}'");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't update recording folder: {ex.Message}", "Backtrack");
        }
    }

    private string DisplayLabel(string originalLabel) =>
    _settings.LocalRowNameOverrides.TryGetValue(originalLabel, out string? custom) ? custom : originalLabel;

    private void EnableDoubleTapRename(TextBlock nameBlock, string originalLabel)
    {
        nameBlock.Cursor = Cursors.IBeam;
        nameBlock.ToolTip = "Double-click to rename (local to this PC only)";
        nameBlock.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2 || nameBlock.Parent is not Panel parent)
                return;
            e.Handled = true;

            int index = parent.Children.IndexOf(nameBlock);
            if (index < 0)
                return;

            var box = new TextBox
            {
                Text = DisplayLabel(originalLabel),
                FontSize = nameBlock.FontSize,
                FontWeight = nameBlock.FontWeight,
                Background = (Brush)FindResource("RowBg"),
                Foreground = (Brush)FindResource("Text0"),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (parent is Grid grid)
                Grid.SetColumn(box, Grid.GetColumn(nameBlock));

            bool finished = false;
            void Finish(bool commit)
            {
                if (finished)
                    return;
                finished = true;
                if (commit)
                    SetLocalRowNameOverride(originalLabel, box.Text);
                _ = LoadBufferVisibilityUi();
                _ = LoadRecordFolderUi();
            }

            parent.Children.RemoveAt(index);
            parent.Children.Insert(index, box);
            box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            box.LostFocus += (_, _) => Finish(commit: true);
            box.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter) { ke.Handled = true; Finish(commit: true); }
                else if (ke.Key == Key.Escape) { ke.Handled = true; Finish(commit: false); }
            };
        };
    }

    private Button BuildFolderIconButton(RoutedEventHandler onClick)
    {
        var iconPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.89 2 1.99 2H20c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z"),
            Fill = (Brush)FindResource("Text1"),
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var button = new Button
        {
            Content = iconPath,
            Style = (Style)FindResource("BareIconButton"),
            Padding = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Choose destination folder"
        };

        if (_settings.ObsIsRemote)
        {
            button.IsEnabled = false;
            button.ToolTip = "OBS is on a different PC -- destination folders can't be browsed from here.";
        }

        button.MouseEnter += (_, _) => iconPath.Fill = (Brush)FindResource("Text0");
        button.MouseLeave += (_, _) => iconPath.Fill = (Brush)FindResource("Text1");

        button.Click += onClick;
        return button;
    }

    internal void ObsRemoteToggle_Click(object sender, RoutedEventArgs e)
    {
        ObsRemoteFields.Visibility = ObsRemoteToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshPluginStatusRemoteGating()
    {
        bool remote = _settings.ObsIsRemote;
        LocalPluginStatusRows.IsEnabled = !remote;
        PluginStatusRemoteNotice.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        RemotePluginSection.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;

        if (remote)
        {
            if (!_settings.DisablePluginAutoUpdate)
            {
                _settings.DisablePluginAutoUpdate = true;
                _settings.Save();
            }
            DisablePluginAutoUpdateToggle.IsChecked = true;
            DisablePluginAutoUpdateToggle.IsEnabled = false;
            RefreshRemotePluginStatusText();
        }
        else
        {
            DisablePluginAutoUpdateToggle.IsChecked = _settings.DisablePluginAutoUpdate;
            DisablePluginAutoUpdateToggle.IsEnabled = true;
        }
    }
}
