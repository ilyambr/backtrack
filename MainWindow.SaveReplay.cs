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

        private async Task RevertRamDiskDestDirsAsync(char driveLetter)
    {
        if (!_obs.IsConnected)
            return;

        string ramDiskPrefix = $"{char.ToUpperInvariant(driveLetter)}:";
        try
        {
            await _obs.SetReplayDestDirAsync(_settings.ClipsFolder);

            foreach (ReplayRow row in await _obs.ListReplayRowsAsync())
            {
                if (row.DestDir.StartsWith(ramDiskPrefix, StringComparison.OrdinalIgnoreCase))
                    await _obs.SetReplayRowDestDirAsync(row.Key, _settings.ClipsFolder);
            }

            await _obs.RevertSourceRecordFilterPathsAsync(driveLetter, _settings.ClipsFolder);
        }
        catch
        {
            
            
        }
    }


    private void UninstallReplaySliderButton_Click(object sender, RoutedEventArgs e)
    {
        ShowConfirmDialog(
            "Uninstall Replay Slider? OBS will be closed first if it's running.",
            "Uninstall",
            async confirmed =>
            {
                if (!confirmed) return;
                UninstallReplaySliderButton.IsEnabled = false;
                (bool success, string? error) = await _updates.UninstallReplaySliderAsync();
                UninstallReplaySliderButton.IsEnabled = true;
                if (!success)
                    MessageBox.Show(this, error ?? "Couldn't uninstall Replay Slider.", "Backtrack");
            });
    }


        private async Task PrefetchRowLabelsAsync()
    {
        if (!_obs.IsConnected)
            return;
        try
        {
            foreach (ReplayRow row in await _obs.ListReplayRowsAsync())
                _rowLabels[row.Key] = row.Label;

            await RefreshRemoteRowHotkeysAsync();
        }
        catch
        {
            
        }
    }


        private async Task RefreshRemoteRowHotkeysAsync()
    {
        foreach (var hk in _remoteRowHotkeys)
        {
            try { hk.Dispose(); } catch { }
        }
        _remoteRowHotkeys.Clear();

        if (!_settings.ObsIsRemote || !_obs.IsConnected)
            return;

        try
        {
            var replayRows = await _obs.ListReplayRowsAsync();
            var recordRows = await _obs.ListRecordRowsAsync();

            var hotkeyActions = new Dictionary<(GlobalHotkey.Modifiers, uint), List<Func<Task>>>();

            foreach (var row in replayRows)
            {
                if (TryParseHotkeyString(row.Hotkey, out var mods, out var vk))
                {
                    if (!hotkeyActions.TryGetValue((mods, vk), out var list))
                    {
                        list = new List<Func<Task>>();
                        hotkeyActions[(mods, vk)] = list;
                    }
                    string rowKey = row.Key;
                    list.Add(async () =>
                    {
                        var freshRows = await _obs.ListReplayRowsAsync();
                        var match = freshRows.FirstOrDefault(r => r.Key == rowKey);
                        if (match is not null && match.Status == 0)
                        {
                            AppLog.Write($"[hotkey] Inactive replay buffer '{rowKey}' ignored");
                            return;
                        }
                        if (TryBlockForStorageLimit(out string? blockMessage))
                        {
                            _toastOverlay.ShowStorageLimitWarning(blockMessage);
                            return;
                        }
                        await _obs.SaveReplayRowAsync(rowKey);
                    });
                }
            }

            foreach (var row in recordRows)
            {
                if (TryParseHotkeyString(row.Hotkey, out var mods, out var vk))
                {
                    if (!hotkeyActions.TryGetValue((mods, vk), out var list))
                    {
                        list = new List<Func<Task>>();
                        hotkeyActions[(mods, vk)] = list;
                    }
                    string rowKey = row.Key;
                    list.Add(async () =>
                    {
                        try
                        {
                            var freshRows = await _obs.ListRecordRowsAsync();
                            var match = freshRows.FirstOrDefault(r => r.Key == rowKey);
                            if (match is not null)
                            {
                                if (match.Status == RecordStatusRecording)
                                {
                                    await _obs.StopRecordRowAsync(rowKey);
                                }
                                else if (match.Status == RecordStatusStopped)
                                {
                                    if (TryBlockForStorageLimit(out string? blockMessage))
                                    {
                                        _toastOverlay.ShowStorageLimitWarning(blockMessage);
                                        return;
                                    }
                                    await _obs.StartRecordRowAsync(rowKey);
                                }
                            }
                            await RefreshStatusAsync();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Write($"Remote record hotkey error for {rowKey}: {ex.Message}");
                        }
                    });
                }
            }

            int hotkeyId = 0x9200;
            foreach (var (combo, actions) in hotkeyActions)
            {
                try
                {
                    var hk = new GlobalHotkey(this, combo.Item1, combo.Item2, id: hotkeyId++);
                    hk.Pressed += () => Dispatcher.Invoke(async () =>
                    {
                        foreach (var action in actions)
                        {
                            try { await action(); } catch (Exception ex) { AppLog.Write($"Remote hotkey action error: {ex.Message}"); }
                        }
                    });
                    _remoteRowHotkeys.Add(hk);
                    AppLog.Write($"Registered remote OBS hotkey: {combo.Item1}+{combo.Item2}");
                }
                catch (Exception ex)
                {
                    AppLog.Write($"Could not register remote OBS hotkey for {combo.Item1}+{combo.Item2}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"RefreshRemoteRowHotkeysAsync failed: {ex.Message}");
        }
    }


    

    private FrameworkElement PanelFor(Screen screen) => screen switch
    {
        Screen.Idle => IdlePanel,
        Screen.SaveReplay => SaveReplayPanel,
        Screen.StartRecord => StartRecordPanel,
        Screen.Gallery => GalleryPanel,
        Screen.Player => PlayerPanel,
        Screen.Settings => SettingsPanel,
        _ => IdlePanel,
    };


    private async Task RefreshStatusAsync()
    {
        _trayManager?.UpdateStatus(_obs.IsConnected, _statusOverlay.IsVisible);

        
        
        
        
        
        
        bool encoderOverloadedNow = DateTime.UtcNow - _lastEncoderOverloadEventUtc < TimeSpan.FromSeconds(4);
        if (encoderOverloadedNow != _encoderOverloadedShown)
        {
            _encoderOverloadedShown = encoderOverloadedNow;
            _statusOverlay.SetEncoderOverloaded(encoderOverloadedNow);
        }

        if (!_obs.IsConnected)
        {
            ConnDot.Fill = (Brush)FindResource("Rec");
            ConnStatusText.Text = "OBS Disconnected";

            
            
            
            
            
            
            
            
            
            
            
            
            
            
            bool serverEnabledNow = _serverEnabledAtStartup;
            if (!_settings.ObsIsRemote)
            {
                (bool enabledNow, bool autoFixed) = await Task.Run(() =>
                {
                    (bool enabled, string? _) = ObsConfigReader.ReadLocalConfig();

                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    if (!enabled && Process.GetProcessesByName("obs64").Length == 0 && ObsConfigReader.TryEnableServer())
                        return (true, true);
                    return (enabled, false);
                });
                serverEnabledNow = enabledNow;
                if (autoFixed)
                    AppLog.Write("ObsConfigReader.TryEnableServer: OBS's WebSocket server was off and OBS wasn't running -- enabled it for the next launch.");
            }

            
            
            
            
            
            
            _serverEnabledAtStartup = serverEnabledNow;

            ConnStatusText.ToolTip = !serverEnabledNow
                ? "OBS's WebSocket server is disabled -- enable it in OBS: Tools > WebSocket Server Settings, or just restart OBS (Backtrack will turn it on for you the moment OBS closes)"
                : _obs.LastError is null ? "Not connected to OBS" : $"OBS: {_obs.LastError}";
            RecordLabel.Text = "Start Recording";
            RecordStatusText.Text = "--:--";
            SetRecordIcon(active: false);
            ReplayStatus.Text = "Off";
            ReplayStatus.Foreground = (Brush)FindResource("Text2");
            SaveReplayIcon.Foreground = (Brush)FindResource("Text0");
            _statusOverlay.SetRecording(false);
            _statusOverlay.SetReplayOnline(false);
            _statusOverlay.SetMicStatus(MicStatus.Hidden);
            _statusOverlay.SetStreaming(false);
            _statusOverlay.SetVirtualCamActive(false);
            _statusOverlay.SetObsDisconnected(true);
            _isStreaming = false;
            UpdateStreamingBoxVisibility();
            return;
        }

        ConnDot.Fill = (Brush)FindResource("Green");
        ConnStatusText.Text = "OBS Connected";
        ConnStatusText.ToolTip = "Connected to OBS";
        _statusOverlay.SetObsDisconnected(false);

        try
        {
            
            
            
            
            
            
            Task<RecordStatus> recStatusTask = _obs.GetRecordStatusAsync();
            Task<List<RecordRow>> recordRowsTask = _obs.ListRecordRowsAsync();
            Task<bool> replayBufferActiveTask = _obs.GetReplayBufferActiveAsync();
            Task<List<ReplayRow>> replayRowsTask = _obs.ListReplayRowsAsync();
            Task<bool> streamActiveTask = _obs.GetStreamActiveAsync();
            Task<bool> virtualCamActiveTask = _obs.GetVirtualCamActiveAsync();

            RecordStatus recStatus = await recStatusTask;
            
            
            
            
            
            
            
            
            
            int activeRecordRowCount;
            try
            {
                List<RecordRow> recordRows = await recordRowsTask;

                
                
                
                
                
                
                
                var activeKeys = new HashSet<string>();
                var newlyStartedKeys = new List<string>();
                foreach (RecordRow row in recordRows)
                {
                    if (row.Status != RecordStatusRecording)
                        continue;
                    activeKeys.Add(row.Key);
                    _recordRowInfoByKey[row.Key] = (row.Label, row.SourceName, row.FilterName);
                    if (_recordRowActiveSinceUtc.TryAdd(row.Key, DateTime.UtcNow))
                        newlyStartedKeys.Add(row.Key);
                }
                List<string> newlyStoppedKeys = _recordRowActiveSinceUtc.Keys.Where(k => !activeKeys.Contains(k)).ToList();
                var stoppedDurations = new Dictionary<string, string>();
                foreach (string staleKey in newlyStoppedKeys)
                {
                    if (_recordRowActiveSinceUtc.Remove(staleKey, out DateTime since))
                    {
                        long durMs = (long)(DateTime.UtcNow - since).TotalMilliseconds;
                        stoppedDurations[staleKey] = FormatDuration(durMs);
                    }
                }

                
                
                
                
                
                
                
                
                
                
                
                
                if (_recordRowPollSeeded)
                {
                    foreach (string key in newlyStartedKeys)
                        _toastOverlay.ShowRecording(started: true, resolvedPath: null);
                    foreach (string key in newlyStoppedKeys)
                    {
                        string? dur = stoppedDurations.TryGetValue(key, out var d) ? d : null;
                        string? path = _recordRowInfoByKey.TryGetValue(key, out var info) && !string.IsNullOrEmpty(info.SourceName) && !string.IsNullOrEmpty(info.FilterName)
                            ? await _obs.GetRecordRowDestinationFolderAsync(info.SourceName, info.FilterName)
                            : null;

                        if (_cancelledRecordRows.Remove(key))
                        {
                            string label = _recordRowInfoByKey.TryGetValue(key, out var cInfo) ? DisplayLabel(cInfo.Label) : "";
                            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                            {
                                try
                                {
                                    var dir = new DirectoryInfo(path);
                                    var latestFile = dir.GetFiles("*.*")
                                        .Where(f => f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                                                    f.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                                                    f.Extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                                                    f.Extension.Equals(".ts", StringComparison.OrdinalIgnoreCase))
                                        .OrderByDescending(f => f.LastWriteTimeUtc)
                                        .FirstOrDefault();
                                    if (latestFile != null && (DateTime.UtcNow - latestFile.LastWriteTimeUtc).TotalSeconds < 30)
                                    {
                                        _ = DeleteOrRecycleCancelledFileAsync(latestFile.FullName);
                                    }
                                }
                                catch { }
                            }
                            _toastOverlay.ShowRecordingCancelled(string.IsNullOrEmpty(label) ? null : label, dur);
                            _recordRowInfoByKey.Remove(key);
                            continue;
                        }

                        _toastOverlay.ShowRecording(started: false, resolvedPath: path);
                        _recordRowInfoByKey.Remove(key);
                    }
                }
                _recordRowPollSeeded = true;

                activeRecordRowCount = activeKeys.Count;
                _lastKnownActiveRecordRowCount = activeRecordRowCount;
            }
            catch
            {
                activeRecordRowCount = _lastKnownActiveRecordRowCount;
            }
            bool anyRecordRowActive = activeRecordRowCount > 0;
            bool recordingAnything = recStatus.Active || anyRecordRowActive;

            
            
            
            
            
            
            bool singleActiveTarget = (recStatus.Active && activeRecordRowCount == 0) || (!recStatus.Active && activeRecordRowCount == 1);
            RecordLabel.Text = singleActiveTarget ? "Stop Recording" : "Start Recording";
            SetRecordIcon(recordingAnything);

            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            
            long? bestDurationMs = null;
            bool bestIsMainPaused = false;
            if (recStatus.Active)
            {
                bestDurationMs = recStatus.DurationMs;
                bestIsMainPaused = recStatus.Paused;
            }
            DateTime nowUtc = DateTime.UtcNow;
            foreach (DateTime since in _recordRowActiveSinceUtc.Values)
            {
                long rowMs = (long)(nowUtc - since).TotalMilliseconds;
                if (bestDurationMs is null || rowMs > bestDurationMs)
                {
                    bestDurationMs = rowMs;
                    bestIsMainPaused = false; 
                }
            }
            RecordStatusText.Text = bestDurationMs is long ms
                ? (bestIsMainPaused ? $"{FormatDuration(ms)} (Paused)" : FormatDuration(ms))
                : "--:--";
            _statusOverlay.SetRecording(recordingAnything);

            
            
            
            
            
            
            
            
            
            
            
            
            bool replayBufferActive = await replayBufferActiveTask;
            bool anyRowActive;
            bool anyRowError;
            try
            {
                List<ReplayRow> rows = await replayRowsTask;
                anyRowActive = rows.Any(r => r.Status == 1);
                anyRowError = rows.Any(r => r.Status == 2);
                _lastKnownAnyRowActive = anyRowActive;
                _lastKnownAnyRowError = anyRowError;
            }
            catch
            {
                
                
                
                
                anyRowActive = _lastKnownAnyRowActive;
                anyRowError = _lastKnownAnyRowError;
            }
            bool replayActive = replayBufferActive || anyRowActive;
            bool showError = anyRowError && !replayActive;

            string replayStateColor = replayActive ? "Green" : showError ? "Rec" : "Text2";
            ReplayStatus.Text = replayActive ? "On" : showError ? "Error" : "Off";
            ReplayStatus.Foreground = (Brush)FindResource(replayStateColor);
            SaveReplayIcon.Foreground = (Brush)FindResource(replayActive ? "Green" : showError ? "Rec" : "Text0");
            _statusOverlay.SetReplayOnline(replayActive);

            
            
            
            
            try
            {
                _isStreaming = await streamActiveTask;
                _statusOverlay.SetStreaming(_isStreaming);
                UpdateStreamingBoxVisibility();
            }
            catch
            {
                
            }

            
            
            
            try
            {
                _statusOverlay.SetVirtualCamActive(await virtualCamActiveTask);
            }
            catch
            {
                
            }
        }
        catch
        {
            
            
        }
    }


    private void SaveReplayTile_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(Screen.SaveReplay);
        _ = LoadReplayRowsAsync();
    }


    

    private async Task LoadReplayRowsAsync()
    {
        BufRowsPanel.Children.Clear();

        if (!_obs.IsConnected)
        {
            AddInfoLine(BufRowsPanel, !_serverEnabledAtStartup
                ? "OBS's WebSocket server is disabled -- enable it in OBS: Tools > WebSocket Server Settings."
                : "Not connected to OBS.");
            return;
        }

        List<ReplayRow> rows;
        try
        {
            rows = await _obs.ListReplayRowsAsync();
        }
        catch (Exception ex)
        {
            AddInfoLine(BufRowsPanel, $"Could not reach the Replay Slider bridge: {ex.Message}");
            AddInfoLine(BufRowsPanel, "Needs the patched obs-replay-slider build (see vendor/obs-replay-slider).");
            return;
        }

        if (rows.Count == 0)
        {
            AddInfoLine(BufRowsPanel, "No replay buffers found.");
            return;
        }

        foreach (ReplayRow row in rows)
            _rowLabels[row.Key] = row.Label;

        
        
        
        
        List<ReplayRow> visibleRows = rows.Where(r => !_settings.HiddenBufferLabels.Contains(r.Label)).ToList();
        _lastReplayRows = visibleRows;

        if (visibleRows.Count == 0)
        {
            AddInfoLine(BufRowsPanel, "All buffers are hidden -- unhide one in Settings > Buffers.");
            return;
        }

        
        foreach (ReplayRow row in visibleRows.OrderBy(r => r.Status == 1 ? 0 : 1))
            BufRowsPanel.Children.Add(BuildRowButton(row));

        BufRowsPanel.Children.Add(BuildSharedClipLengthControl(visibleRows));
    }


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
                await _obs.SaveReplayRowAsync(row.Key);
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
        int initial = Math.Min(rows.Count > 0 ? rows[0].LengthSeconds : 60, maxSeconds);

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

}
