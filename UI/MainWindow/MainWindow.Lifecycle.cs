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

        _pairing.OnClipMarkersSyncedFromRemote = (fullPath, clipKey, markers) =>
        {
            if (File.Exists(fullPath))
            {
                _ = EmbedChapterMarkersIntoVideoFileAsync(fullPath, markers);
            }

            Dispatcher.BeginInvoke(() =>
            {
                string? currentKey = GetCurrentClipKey();
                string currentFileName = _currentPlayerFile?.Name ?? Path.GetFileName(currentKey ?? "");
                string targetFileName = Path.GetFileName(fullPath);

                if (string.Equals(currentFileName, targetFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_currentPlayerFile?.FullName, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    _lastRenderedMarkerDurationMs = -1;
                    RenderPlayerMarkers();
                    if (BookmarkPopup?.IsOpen == true)
                    {
                        PopulateBookmarkList();
                    }
                }
            });
        };

        _pairing.OnStarredSyncedFromRemote = (fullPath, clipKey, isStarred) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                string? currentKey = GetCurrentClipKey();
                string currentFileName = _currentPlayerFile?.Name ?? Path.GetFileName(currentKey ?? "");
                string targetFileName = Path.GetFileName(fullPath);

                if (string.Equals(currentFileName, targetFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_currentPlayerFile?.FullName, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    UpdatePlayerStarUi();
                }
            });
        };

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
