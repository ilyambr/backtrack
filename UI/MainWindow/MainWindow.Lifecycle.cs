using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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

    private void WirePairingAndStreaming()
    {
        _remoteStreamServer.StreamStarted += (token, totalBytes) => Dispatcher.BeginInvoke(() =>
        {
            if (_currentStreamToken == token && totalBytes > 0)
            {
                _remoteStreamTotalBytes = totalBytes;
                StatSize.Text = $"{totalBytes / 1024.0 / 1024.0:0.#} MB";
                long durMs = _vlcPlayer?.Length ?? 0;
                if (durMs > 0)
                {
                    long kbps = (long)((totalBytes * 8.0) / (durMs / 1000.0) / 1000.0);
                    StatBitrate.Text = $"{kbps:N0} kbps";
                }
            }
        });
        _pairing.PairingRequested += (deviceName, code, requestId) => Dispatcher.BeginInvoke(() =>
        {
            _pairingRequestOverlay.ShowRequest(deviceName, code,
                onAllow: () =>
                {
                    _pairing.ApproveRequest(requestId);

                    RefreshShareClipsUi();
                },
                onDeny: () => _pairing.DenyRequest(requestId));
        });

        _pairing.StartDiscoveryListener();
        if (_settings.ShareClipsEnabled)
        {
            _pairing.StartAnnouncing();
            _pairing.StartPairingServer();
        }
        _pairing.DiscoveredPeersChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (SettingsPanel.Visibility == Visibility.Visible)
                RenderDiscoveredDevices();
        });

    }

    private void WireObsEvents()
    {
        _obs.RecordingStateChanged += (active, path) => Dispatcher.BeginInvoke(async () =>
        {
            if (_cancellingMainRecording)
            {
                if (!active)
                {
                    _cancellingMainRecording = false;
                    string? dur = _cancellingMainRecordingDuration;
                    _cancellingMainRecordingDuration = null;
                    _activeRecordingMarkers.Clear();
                    if (path is not null)
                    {
                        try
                        {
                            await DeleteOrRecycleCancelledFileAsync(path);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Write($"DeleteOrRecycleCancelledFileAsync skipped/failed: {ex.Message}");
                        }
                    }
                    _toastOverlay.ShowRecordingCancelled("Full Scene", dur);
                    AppLog.Write($"Main recording cancelled and recycled: '{path}'");
                }
                return;
            }

            if (active)
                AppLog.Write("Recording started");
            else if (path is not null)
            {
                if (File.Exists(path) && new FileInfo(path).Length < 10240)
                {
                    try { File.Delete(path); } catch { }
                    AppLog.Write($"Aborted empty recording removed: '{path}'");
                    return;
                }

                _toastOverlay.ShowRecording(active, path);
                if (_activeRecordingMarkers.Count > 0)
                {
                    string clipKey = Path.GetFileName(path);
                    var markers = new List<double>(_activeRecordingMarkers);
                    _activeRecordingMarkers.Clear();
                    SaveClipMarkers(clipKey, markers);
                }
                AppLog.Write($"Recording saved to '{path}'");
                ShowObsModeMessage($"Recording saved to '{path}'");
                RefreshRecentClipsOverlay();
                return;
            }

            _toastOverlay.ShowRecording(active, path);
        });
        _obs.StreamingStateChanged += active => Dispatcher.BeginInvoke(() =>
        {

            if (active == _isStreaming)
                return;
            _toastOverlay.ShowStreaming(active);
            AppLog.Write(active ? "Livestream started" : "Livestream ended");
            _isStreaming = active;
            _statusOverlay.SetStreaming(active);
            UpdateStreamingBoxVisibility();
        });

        _obs.VirtualCamStateChanged += active => Dispatcher.BeginInvoke(() =>
        {
            _statusOverlay.SetVirtualCamActive(active);
        });
        _obs.EncoderOverloadDetected += info => Dispatcher.BeginInvoke(() =>
        {

            _lastEncoderOverloadEventUtc = DateTime.UtcNow;

            if (DateTime.UtcNow - _lastEncoderOverloadToastUtc < TimeSpan.FromSeconds(30))
                return;
            _lastEncoderOverloadToastUtc = DateTime.UtcNow;

            var causes = new List<string>();
            if (info.MainStream)
                causes.Add("the stream (encoder or network)");
            if (info.MainRecording)
                causes.Add("the main recording");
            if (info.MainReplayBuffer)
                causes.Add("the main replay buffer");
            if (info.ThisFilter)
                causes.Add($"'{info.Filter}' on '{info.Source}'");
            if (causes.Count == 0)
                return;

            string summary = string.Join(", ", causes);
            _toastOverlay.ShowEncoderOverload(summary);
            AppLog.Write($"Encoder overload detected: {summary}");
        });

        async Task<string> ResolveRowLabelAsync(string key)
        {
            if (!_rowLabels.TryGetValue(key, out string? label))
            {

                await PrefetchRowLabelsAsync();
                _rowLabels.TryGetValue(key, out label);
            }
            label ??= key;
            return DisplayLabel(label);
        }

        _obs.ReplaySaving += key => Dispatcher.BeginInvoke(async () =>
        {
            var replayRows = await _obs.ListReplayRowsAsync();
            var match = replayRows.FirstOrDefault(r => r.Key == key);
            if (match is not null && match.Status == 0)
            {

                return;
            }
            string label = await ResolveRowLabelAsync(key);
            _toastOverlay.ShowProcessingClip(key, label);
        });
        _obs.ReplaySaved += (key, path) => Dispatcher.BeginInvoke(async () =>
        {
            string label = await ResolveRowLabelAsync(key);

            if (!string.IsNullOrEmpty(path))
            {
                string clipKey = Path.GetFileName(path);
                var file = new FileInfo(path);

                if (_activeRecordingMarkers.Count > 0)
                {
                    var markers = new List<double>(_activeRecordingMarkers);
                    _activeRecordingMarkers.Clear();
                    SaveClipMarkers(clipKey, markers);
                }
                else if (_pendingBookmarkUtcTimes.Count > 0)
                {
                    await EnsureThumbnailCachedAsync(file);
                    long? durationMs = TryGetCachedDurationMs(file);
                    double clipDurationSec = durationMs.HasValue && durationMs.Value > 0 ? durationMs.Value / 1000.0 : 60.0;

                    DateTime saveTimeUtc = DateTime.UtcNow;
                    var markers = new List<double>();

                    foreach (DateTime bookmarkUtc in _pendingBookmarkUtcTimes)
                    {
                        double elapsedSinceBookmark = (saveTimeUtc - bookmarkUtc).TotalSeconds;
                        if (elapsedSinceBookmark >= 0 && elapsedSinceBookmark <= (clipDurationSec + 15.0))
                        {
                            double bookmarkPosSec = Math.Max(0, clipDurationSec - elapsedSinceBookmark);
                            markers.Add(bookmarkPosSec);
                        }
                    }

                    _pendingBookmarkUtcTimes.Clear();

                    if (markers.Count > 0)
                    {
                        SaveClipMarkers(clipKey, markers);
                    }
                }
            }

            string? customSub = null;
            var dedupEntry = DeduplicationService.Instance.RegisterSavedClip(key, path);
            if (dedupEntry != null)
            {
                string durStr = FormatDuration(dedupEntry.DurationSeconds * 1000L);
                string folder = !string.IsNullOrEmpty(path) ? (Path.GetDirectoryName(path) ?? path) : _settings.ClipsFolder;
                customSub = $"Saved {durStr} at '{folder}'";
            }

            _toastOverlay.CompleteProcessingClip(key, label, path, customSub);
            AppLog.Write($"{label} saved to '{path}'");
            _ = RefreshGalleryCountAsync();
            RefreshRecentClipsOverlay();
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
        });

        PlayerSeekTrack.SizeChanged += (_, _) => { if (PlayerPanel.Visibility == Visibility.Visible) RenderPlayerMarkers(); };
        TrimTimelineTrack.SizeChanged += (_, _) => { if (PlayerPanel.Visibility == Visibility.Visible) RenderPlayerMarkers(); };
        _obs.StateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            AppLog.Write(_obs.IsConnected ? "Connected to OBS" : "Disconnected from OBS");
            _ = PrefetchRowLabelsAsync();
            if (_settings.RamDiskEnabled && RamDisk.IsMounted(_settings.RamDiskDriveLetter))
                _ = PushRamDiskDestDirAsync();
            else if (!_settings.RamDiskEnabled)
                _ = _obs.RevertSourceRecordFilterPathsAsync(_settings.RamDiskDriveLetter, _settings.ClipsFolder);
        });

    }

    private void WireVlcAndPairingDelegates()
    {
        _pairing.GetRamDiskSnapshot = () => new RamDiskSnapshot(
            _settings.RamDiskEnabled, _settings.RamDiskDriveLetter, _settings.RamDiskSizeMb,
            RamDisk.IsMounted(_settings.RamDiskDriveLetter));
        _pairing.ApplyRamDiskSnapshot = ApplyRamDiskConfigAsync;

        _pairing.EnsureThumbnailCachedForRemote = async fullPath => await EnsureThumbnailCachedAsync(new FileInfo(fullPath));

        _pairing.GetCachedDurationMsForRemote = fullPath => TryGetCachedDurationMs(new FileInfo(fullPath));

        _pairing.TrimClipForRemote = TrimClipForRemoteAsync;
        _pairing.CompressClipForRemote = CompressClipForRemoteHostAsync;
        _pairing.MergeClipsForRemote = MergeClipsForRemoteHostAsync;

        AudioCues.IsRemoteModeActive = () => _settings.ObsIsRemote && !string.IsNullOrEmpty(_settings.PairedPeerSecret);
        AudioCues.RemoteCuePlayer = (cue, vol) => _pairing.SendPlayAudioCueAsync(cue, vol);

        _pairing.CheckAndApplyPluginUpdatesRemotely = async () =>
        {

            PluginVersionInfo replaySlider = await await Dispatcher.InvokeAsync(() =>
                CheckAndApplyPluginUpdateAsync("obs-replay-slider", "Replay Slider", "replay-slider.dll", ReplaySliderStatusDot, ReplaySliderVersionText,
                    name => name.Contains("windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                    () => _settings.LastAppliedReplaySliderReleaseAt, v => _settings.LastAppliedReplaySliderReleaseAt = v,
                    () => _settings.LastAppliedReplaySliderDigest, v => _settings.LastAppliedReplaySliderDigest = v, isManualTrigger: true, deferObsReopen: true));
            PluginVersionInfo sourceRecord = await await Dispatcher.InvokeAsync(() =>
                CheckAndApplyPluginUpdateAsync("obs-source-record", "Source Record", "source-record.dll", SourceRecordStatusDot, SourceRecordVersionText,
                    name => name.Contains("windows-installer", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                    () => _settings.LastAppliedSourceRecordReleaseAt, v => _settings.LastAppliedSourceRecordReleaseAt = v,
                    () => _settings.LastAppliedSourceRecordDigest, v => _settings.LastAppliedSourceRecordDigest = v, isManualTrigger: true, deferObsReopen: true));
            await Dispatcher.InvokeAsync(ReopenObsIfPendingFromPluginUpdates);
            return new PluginVersionsSnapshot(replaySlider, sourceRecord);
        };

    }

    private void SetupTimersAndWindow()
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) =>
        {

            if (_refreshStatusRunning)
                return;
            _refreshStatusRunning = true;
            try
            {
                await RefreshStatusAsync();
            }
            finally
            {
                _refreshStatusRunning = false;
            }
        };

        _micTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _micTimer.Tick += (_, _) => _statusOverlay.SetMicStatus(_obs.GetMicStatus());

        _remoteSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
        _remoteSyncTimer.Tick += async (_, _) =>
        {
            if (_remoteSyncRunning || string.IsNullOrEmpty(_settings.PairedPeerSecret))
                return;
            _remoteSyncRunning = true;
            try
            {
                await SyncRemoteClipsAsync();
            }
            finally
            {
                _remoteSyncRunning = false;
            }
        };

        _seekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _seekTimer.Tick += (_, _) => UpdatePlayerSeekUi();

        _seekDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _seekDebounceTimer.Tick += (_, _) =>
        {
            _seekDebounceTimer.Stop();
            if (_targetSeekMs >= 0)
            {
                CommitSeek(_targetSeekMs);
            }
        };

        _galleryFilterDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _galleryFilterDebounceTimer.Tick += (_, _) =>
        {
            _galleryFilterDebounceTimer.Stop();
            LoadGallery();
        };

        _freezeFrameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _freezeFrameTimer.Tick += (_, _) =>
        {
            _freezeFrameTimer.Stop();
            PlayerFreezeFramePopup.IsOpen = false;
        };

        _volumePopupCloseDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _volumePopupCloseDebounce.Tick += (_, _) =>
        {
            _volumePopupCloseDebounce.Stop();
            PlayerVolumePopup.IsOpen = false;
        };

        _actionFeedbackHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _actionFeedbackHideTimer.Tick += (_, _) =>
        {
            _actionFeedbackHideTimer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            fadeOut.Completed += (_, _) =>
            {
                PlayerActionFeedbackPopup.IsOpen = false;
                PlayerActionFeedbackBorder.Opacity = 1;
            };
            PlayerActionFeedbackBorder.BeginAnimation(OpacityProperty, fadeOut);
        };

        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        Left = TargetScreenBounds.X + (TargetScreenBounds.Width - Width) / 2;
        Top = TargetScreenBounds.Y + CompactTop;
        Acrylic.TryEnableBlurBehind(hwnd, 16, 17, 19, 205);

        ToolWindow.Enable(hwnd);

        RegisterHotkeyFromSettings();

    }

    private void SetupTrayAndVlc()
    {
        _trayManager = new SystemTrayManager(this);
        _trayManager.OnOpenHudRequested += () => Dispatcher.BeginInvoke(ToggleVisible);
        _trayManager.OnOpenSettingsRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible) ToggleVisible();
            ShowScreen(Screen.Settings);

            SettingsScrollHost.ScrollToTop();
        });
        _trayManager.OnOpenClipsFolderRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (Directory.Exists(_settings.ClipsFolder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.ClipsFolder}\"") { UseShellExecute = true });
            }
        });
        _trayManager.OnToggleStatusOverlayRequested += () => Dispatcher.BeginInvoke(() =>
        {
            ToggleStatusOverlay();
            _trayManager.UpdateStatus(_obs.IsConnected, _statusOverlay.IsVisible);
        });
        _trayManager.OnQuitRequested += () => Dispatcher.BeginInvoke(() => Application.Current.Shutdown());

        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(RepositionAllForDisplayChange);

        try
        {
            LibVlc.Core.Initialize();

            _libVlc = new LibVlc.LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-osd", "--avcodec-hw=none");
            AudioCues.Initialize();

            var thumbnailSink = new Window { Width = 2, Height = 2, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            _thumbnailSinkHwnd = new WindowInteropHelper(thumbnailSink).EnsureHandle();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LibVLC init failed: {ex.Message}");
        }

        _obs.Start();
        _pollTimer?.Start();
        _micTimer?.Start();
        _remoteSyncTimer?.Start();

        if (!string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            _remoteSyncRunning = true;
            _ = SyncRemoteClipsAsync().ContinueWith(_ => _remoteSyncRunning = false, TaskScheduler.FromCurrentSynchronizationContext());
        }
        _ = RefreshStatusAsync();

    }

}
