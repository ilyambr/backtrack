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

    internal void UninstallReplaySliderButton_Click(object sender, RoutedEventArgs e)
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
                        await ExecuteSaveReplayRowCoreAsync(rowKey, match?.Label);
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

                var orphanCancelledKeys = _cancelledRecordRows.Where(k => !activeKeys.Contains(k)).ToList();
                foreach (string key in orphanCancelledKeys)
                {
                    _cancelledRecordRows.Remove(key);
                    string label = _recordRowInfoByKey.TryGetValue(key, out var cInfo) ? DisplayLabel(cInfo.Label) : "";
                    string? dur = stoppedDurations.TryGetValue(key, out var d) ? d : null;
                    _toastOverlay.ShowRecordingCancelled(string.IsNullOrEmpty(label) ? null : label, dur);
                    _recordRowInfoByKey.Remove(key);
                }

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
}
