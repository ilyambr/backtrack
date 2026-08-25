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
    private enum Screen { Idle, SaveReplay, StartRecord, Gallery, Player, Settings }


    private const double CompactWidth = 460;

    private const double WideWidth = 680;


    
    
    
    private const double CompactTop = 76;


    
    
    
    
    
    
    private const double BigTop = 90;


    private const string ScheduledTaskName = "BacktrackAutostart";


        private Rect TargetScreenBounds => DisplayMonitors.ResolveBoundsDiu(_settings.DisplayDeviceName);


    private readonly ObsService _obs;

    private bool _serverEnabledAtStartup;


    
    
    
    
    
    
    private bool _lastKnownAnyRowActive;

    private bool _lastKnownAnyRowError;

    private int _lastKnownActiveRecordRowCount;

    private bool _refreshStatusRunning;

    
    
    
    
    
    
    
    
    
    
    
    
    private readonly Dictionary<string, DateTime> _recordRowActiveSinceUtc = new();


    
    
    
    
    
    private readonly Dictionary<string, (string Label, string SourceName, string FilterName)> _recordRowInfoByKey = new();


    
    
    
    
    
    private bool _recordRowPollSeeded;


    private readonly DispatcherTimer _pollTimer;

    private readonly DispatcherTimer _micTimer;

    private readonly DispatcherTimer _remoteSyncTimer;

    private bool _remoteSyncRunning;

    private readonly StatusOverlay _statusOverlay;

    private readonly ToastOverlay _toastOverlay;

    private readonly UpdatePromptOverlay _updatePrompt = new();

    private readonly DispatcherTimer _obsStatsTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private long? _lastRenderTotalFrames, _lastRenderSkippedFrames, _lastOutputTotalFrames, _lastOutputSkippedFrames;

    private DateTime? _obsLogClearAtUtc;

    private readonly ScrimOverlay _scrim;

    private readonly DisclaimerOverlay _disclaimer;


    
    
    
    
    
    
    private string? _pendingUpdateName;

    private Action? _pendingUpdateInstall;

    private readonly LogoOverlay _logo;

    private readonly StreamingStatusOverlay _streamingStatus;

    private readonly PairingRequestOverlay _pairingRequestOverlay;

    private readonly AppSettings _settings;

    private readonly UpdateService _updates = new();


    
    
    
    
    
    
    private bool _manualUpdateReady;

    
    
    
    
    private bool _isStreaming;

    
    
    private DateTime _lastEncoderOverloadToastUtc = DateTime.MinValue;

    
    
    
    
    
    
    
    
    
    
    
    private DateTime _lastEncoderOverloadEventUtc = DateTime.MinValue;

    private bool _encoderOverloadedShown;

    private readonly PairingService _pairing;

    private readonly RemoteClipStreamServer _remoteStreamServer;

    private readonly Dictionary<string, string> _rowLabels = new();

    private List<ReplayRow> _lastReplayRows = new();

    private const int OpenOverlayHotkeyId = 0x9001;

    private const int CancelRecordHotkeyId = 0x9002;

    private const int BookmarkHotkeyId = 0x9003;

    private GlobalHotkey? _hotkey;

    private GlobalHotkey? _cancelRecordHotkey;

    private GlobalHotkey? _bookmarkHotkey;

    private readonly List<GlobalHotkey> _remoteRowHotkeys = new();

    private bool _capturingCancelRecordHotkey;

    private bool _capturingBookmarkHotkey;

    private readonly List<double> _activeRecordingMarkers = new();

    private readonly List<DateTime> _pendingBookmarkUtcTimes = new();

    private bool _cancellingMainRecording;

    private string? _cancellingMainRecordingDuration;

    private readonly HashSet<string> _cancelledRecordRows = new();

    private Screen _lastScreen = Screen.Idle;

    
    
    
    
    
    
    private Screen _playerBackTarget = Screen.Gallery;

    private readonly SystemTrayManager _trayManager;


    private bool _isRenamingCard;

    private bool _isPlayerRenaming;

    
    
    
    
    
    
    
    
    private Action? _cancelPlayerRename;

    private async void OnBookmarkHotkeyPressed()
    {
        if (PlayerPanel.Visibility == Visibility.Visible && (_currentPlayerFile is not null || _currentPlayerRemoteOrigin is not null))
        {
            AddPlayerBookmark();
            return;
        }

        double? activeRecordSec = null;
        try
        {
            var recStatus = await _obs.GetRecordStatusAsync();
            if (recStatus.Active && recStatus.DurationMs > 0)
            {
                activeRecordSec = recStatus.DurationMs / 1000.0;
            }
        }
        catch { }

        if (activeRecordSec is null && _recordRowActiveSinceUtc.Count > 0)
        {
            DateTime earliest = _recordRowActiveSinceUtc.Values.Min();
            activeRecordSec = Math.Max(0, (DateTime.UtcNow - earliest).TotalSeconds);
        }

        if (activeRecordSec is not null)
        {
            double sec = activeRecordSec.Value;
            _activeRecordingMarkers.Add(sec);
            TimeSpan ts = TimeSpan.FromSeconds(sec);
            _toastOverlay.ShowBookmarkAdded($"At {ts:mm\\:ss}");
            return;
        }

        DateTime now = DateTime.UtcNow;
        _pendingBookmarkUtcTimes.Add(now);
        _toastOverlay.ShowBookmarkAdded("Bookmark set for replay");
    }

    private bool _isTrimming;

    private readonly HashSet<string> _pendingDeletePaths = new(StringComparer.OrdinalIgnoreCase);

    
    
    private readonly HashSet<string> _pendingRemoteDeletePaths = new(StringComparer.Ordinal);


    

    private LibVlc.LibVLC? _libVlc;

    private LibVlc.MediaPlayer? _vlcPlayer;

    
    
    
    
    
    
    private bool _playerHasEnded;

    private bool _isMuted;

    private Task? _pendingVlcDisposeTask;

    private FileInfo? _currentPlayerFile;

    
    
    
    
    
    
    
    
    private (string RelativePath, string DeviceId)? _currentPlayerRemoteOrigin;

    private readonly DispatcherTimer _seekTimer;

    private readonly DispatcherTimer _seekDebounceTimer;

    private readonly DispatcherTimer _galleryFilterDebounceTimer;

    private readonly DispatcherTimer _freezeFrameTimer;

    private readonly DispatcherTimer _volumePopupCloseDebounce;

    private readonly DispatcherTimer _actionFeedbackHideTimer;

    private bool _isScrubbing = false;

    private bool _isHoveringSeekTrack = false;

    private long _targetSeekMs = 0;

    private IntPtr _thumbnailSinkHwnd;


    
    private bool _capturingHotkey;


    
    private TimeSpan? _trimStart;

    private TimeSpan? _trimEnd;

    private bool _previewLooping;

    private enum TrimDragMode { None, Start, End, Seek }

    private TrimDragMode _trimDragMode = TrimDragMode.None;


    
    
    
    private static readonly float[] PlaybackSpeeds = { 0.5f, 1f, 1.5f, 2f };

    private int _playbackSpeedIndex = 1; 


    

    
    
    
    private string? _currentGalleryFolder;


    
    
    
    
    private bool _galleryIsRemote;

    
    private string? _currentRemoteGalleryFolder;

    
    
    
    
    
    
    
    private bool _remotePcWasConnected;


    private readonly HashSet<string> _selectedClipPaths = new(StringComparer.OrdinalIgnoreCase);

    
    
    
    private readonly List<(FileInfo File, Border Circle, Border Thumb)> _galleryCardSelection = new();


    public MainWindow(StatusOverlay statusOverlay, ToastOverlay toastOverlay, ScrimOverlay scrim, DisclaimerOverlay disclaimer, LogoOverlay logo, StreamingStatusOverlay streamingStatus, PairingRequestOverlay pairingRequestOverlay, RecentClipsOverlay recentClipsOverlay)
    {
        InitializeComponent();
        
        
        
        
        
        BuildThemeSwatches();
        _statusOverlay = statusOverlay;
        _pairingRequestOverlay = pairingRequestOverlay;
        _recentClipsOverlay = recentClipsOverlay;
        _toastOverlay = toastOverlay;
        _scrim = scrim;
        _disclaimer = disclaimer;
        _logo = logo;
        _streamingStatus = streamingStatus;
        _settings = AppSettings.Load();
        
        
        
        AppLog.FileLoggingEnabled = _settings.DiagnosticLogEnabled;
        AppLog.DeveloperModeEnabled = _settings.DeveloperModeEnabled;
        UpdateService.DeveloperModeEnabled = _settings.DeveloperModeEnabled;

        
        
        
        
        SizeChanged += (_, _) => UpdateStreamingBoxVisibility();
        LocationChanged += (_, _) => UpdateStreamingBoxVisibility();

        _pairing = new PairingService(_settings);
        _remoteStreamServer = new RemoteClipStreamServer(_pairing);
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

        
        
        
        
        
        
        
        
        
        _scrim.Dismissed += () => Dispatcher.BeginInvoke(() => CloseOverlay());
        KeyDown += MainWindow_KeyDown;

        string url;
        string? password;
        (url, password, _serverEnabledAtStartup) = ResolveObsConnection();
        _obs = new ObsService(url, password);

        
        
        
        _obs.RecordingStateChanged += (active, path) => Dispatcher.BeginInvoke(async () =>
        {
            if (_cancellingMainRecording)
            {
                if (!active)
                {
                    _cancellingMainRecording = false;
                    string? dur = _cancellingMainRecordingDuration;
                    _cancellingMainRecordingDuration = null;
                    if (path is not null)
                    {
                        await DeleteOrRecycleCancelledFileAsync(path);
                    }
                    _toastOverlay.ShowRecordingCancelled("Full Scene", dur);
                    AppLog.Write($"Main recording cancelled and recycled: '{path}'");
                }
                return;
            }

            _toastOverlay.ShowRecording(active, path);
            if (active)
                AppLog.Write("Recording started");
            else if (path is not null)
            {
                if (_activeRecordingMarkers.Count > 0)
                {
                    string clipKey = Path.GetFileName(path);
                    _settings.ClipMarkers[clipKey] = new List<double>(_activeRecordingMarkers);
                    _activeRecordingMarkers.Clear();
                    _settings.Save();
                }
                AppLog.Write($"Recording saved to '{path}'");
                ShowObsModeMessage($"Recording saved to '{path}'");
                RefreshRecentClipsOverlay();
            }
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
                    _settings.ClipMarkers[clipKey] = new List<double>(_activeRecordingMarkers);
                    _activeRecordingMarkers.Clear();
                    _settings.Save();
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
                        markers.Sort();
                        _settings.ClipMarkers[clipKey] = markers;
                        _settings.Save();
                    }
                }
            }

            _toastOverlay.CompleteProcessingClip(key, label, path);
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

        
        
        
        
        _pairing.GetRamDiskSnapshot = () => new RamDiskSnapshot(
            _settings.RamDiskEnabled, _settings.RamDiskDriveLetter, _settings.RamDiskSizeMb,
            RamDisk.IsMounted(_settings.RamDiskDriveLetter));
        _pairing.ApplyRamDiskSnapshot = ApplyRamDiskConfigAsync;

        
        
        
        _pairing.EnsureThumbnailCachedForRemote = async fullPath => await EnsureThumbnailCachedAsync(new FileInfo(fullPath));
        
        
        _pairing.GetCachedDurationMsForRemote = fullPath => TryGetCachedDurationMs(new FileInfo(fullPath));
        
        
        
        _pairing.TrimClipForRemote = TrimClipForRemoteAsync;
        _pairing.CompressClipForRemote = CompressClipForRemoteHostAsync;

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
            
            
            
            
            
            
            _libVlc = new LibVlc.LibVLC("--no-video-title-show", "--avcodec-hw=none");
            AudioCues.Initialize();

            
            
            
            
            
            
            var thumbnailSink = new Window { Width = 2, Height = 2, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            _thumbnailSinkHwnd = new WindowInteropHelper(thumbnailSink).EnsureHandle();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LibVLC init failed: {ex.Message}");
        }

        _obs.Start();
        _pollTimer.Start();
        _micTimer.Start();
        _remoteSyncTimer.Start();
        
        
        
        
        
        
        
        if (!string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            _remoteSyncRunning = true;
            _ = SyncRemoteClipsAsync().ContinueWith(_ => _remoteSyncRunning = false, TaskScheduler.FromCurrentSynchronizationContext());
        }
        _ = RefreshStatusAsync();
        ShowScreen(Screen.Idle);
        SyncGalleryToolbarUi();
        _ = RefreshGalleryCountAsync();
        _ = PrefetchRowLabelsAsync();
        
        
        _ = PrewarmGalleryThumbnailsAsync();
        
        
        _ = InitializeRamDiskAsync();

        
        
        
        
        if (_settings.LaunchWithWindows)
        {
            try { CreateOrUpdateStartupTask(); }
            catch (Exception ex) { Debug.WriteLine($"Startup task self-heal failed: {ex.Message}"); }
        }

        
        
        
        
        
        
        
        
        
        
        
        
        ShowAppStartedToast();
        if (!UpdateService.IsDevBuild)
            _ = CheckForUpdatesAsync();

        RestartAutoDeleteOldClipsTimer();
        InitializeOverlayLog();
        InitializeRecentClipsOverlay();
    }


    private void PositionInBottomRightCorner()
    {
        const double margin = 20;
        Rect bounds = TargetScreenBounds;
        double width = _recentClipsOverlay.ActualWidth > 0 ? _recentClipsOverlay.ActualWidth : 260;
        double height = _recentClipsOverlay.ActualHeight > 0 ? _recentClipsOverlay.ActualHeight : 100;
        _recentClipsOverlay.Left = bounds.X + bounds.Width - width - margin;
        _recentClipsOverlay.Top = bounds.Y + bounds.Height - height - margin;
    }


    private static string FormatFileSize(long bytes)
    {
        const double mb = 1024.0 * 1024.0;
        double sizeMb = bytes / mb;
        return sizeMb >= 1000 ? $"{sizeMb / 1024.0:0.#} GB" : $"{sizeMb:0.#} MB";
    }


    private DateTime _lastQuickOpenUtc = DateTime.MinValue;


        private async Task<(bool Available, string InstalledVersion)> CheckSelfAvailabilityAsync()
    {
        Version installed = UpdateService.CurrentAppVersion;
        try
        {
            ReleaseInfo? release = await _updates.GetLatestReleaseAsync("ilyambr", "backtrack",
                name => name.Contains("win", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (release?.DownloadUrl is null)
                return (false, installed.ToString(3));

            bool versionBumped = UpdateService.IsNewer(release.Version, installed);
            
            
            
            
            bool available = ShouldApplyUpdate(release, versionBumped, installedFileMissing: false,
                () => _settings.LastAppliedBacktrackReleaseAt, v => _settings.LastAppliedBacktrackReleaseAt = v,
                () => _settings.LastAppliedBacktrackDigest, v => _settings.LastAppliedBacktrackDigest = v);
            return (available, installed.ToString(3));
        }
        catch
        {
            return (false, installed.ToString(3));
        }
    }


        private bool ShouldApplyUpdate(ReleaseInfo release, bool versionBumped, bool installedFileMissing,
        Func<DateTimeOffset?> getLastApplied, Action<DateTimeOffset?> setLastApplied,
        Func<string?> getLastDigest, Action<string?> setLastDigest)
    {
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (installedFileMissing)
            return true;

        DateTimeOffset? lastApplied = getLastApplied();
        string? lastDigest = getLastDigest();

        if (lastApplied is null && lastDigest is null && !versionBumped)
        {
            setLastApplied(release.PublishedAt);
            setLastDigest(release.Digest);
            _settings.Save();
            return false;
        }

        bool digestKnownBothSides = release.Digest is not null && lastDigest is not null;
        bool digestChanged = digestKnownBothSides && !string.Equals(release.Digest, lastDigest, StringComparison.OrdinalIgnoreCase);

        
        
        
        
        
        
        
        
        
        
        
        
        if (digestKnownBothSides && !digestChanged)
            return false;

        bool republishedByTimestamp = !digestKnownBothSides && release.PublishedAt is not null && lastApplied is not null && release.PublishedAt > lastApplied;

        return versionBumped || digestChanged || republishedByTimestamp;
    }


    private void RecordUpdateApplied(ReleaseInfo release, Action<DateTimeOffset?> setLastApplied, Action<string?> setLastDigest)
    {
        setLastApplied(release.PublishedAt ?? DateTimeOffset.UtcNow);
        setLastDigest(release.Digest);
        _settings.Save();
    }


    
    
    
    
    
    
    
    private bool _obsReopenPendingFromPluginUpdates;


    private void AutoDeleteOldClipsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoDeleteOldClipsEnabled = AutoDeleteOldClipsToggle.IsChecked == true;
        _settings.Save();
        AutoDeleteOldClipsFields.Visibility = _settings.AutoDeleteOldClipsEnabled ? Visibility.Visible : Visibility.Collapsed;
        RestartAutoDeleteOldClipsTimer();
    }


        private void ExperimentalHeader_Click(object sender, MouseButtonEventArgs e)
    {
        bool expand = ExperimentalContent.Visibility != Visibility.Visible;
        ExperimentalContent.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        ExperimentalHeaderText.Text = expand ? "▾ EXPERIMENTAL" : "▸ EXPERIMENTAL";
    }


        private void DestructiveHeader_Click(object sender, MouseButtonEventArgs e)
    {
        bool expand = DestructiveContent.Visibility != Visibility.Visible;
        DestructiveContent.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        DestructiveHeaderText.Text = expand ? "▾ MAINTENANCE" : "▸ MAINTENANCE";
    }


    private void UninstallBacktrackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowConfirmDialog(
            "Uninstall Backtrack? This removes the app, its Start Menu shortcut, and its registry entry. Your clips aren't touched.",
            "Uninstall",
            confirmed =>
            {
                if (!confirmed) return;
                (bool success, string? error) = Backtrack.Interop.SelfUninstall.BeginUninstall();
                if (!success)
                {
                    MessageBox.Show(this, error ?? "Couldn't start the uninstall.", "Backtrack");
                    return;
                }
                
                
                
                
                Application.Current.Shutdown();
            });
    }


    private void UninstallSourceRecordButton_Click(object sender, RoutedEventArgs e)
    {
        ShowConfirmDialog(
            "Uninstall Source Record? OBS will be closed first if it's running.",
            "Uninstall",
            async confirmed =>
            {
                if (!confirmed) return;
                UninstallSourceRecordButton.IsEnabled = false;
                (bool success, string? error) = await _updates.UninstallSourceRecordAsync();
                UninstallSourceRecordButton.IsEnabled = true;
                if (!success)
                    MessageBox.Show(this, error ?? "Couldn't uninstall Source Record.", "Backtrack");
            });
    }


    private void BufferDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RefreshBufferDurationUi();


    private async void ApplyBufferDuration_Click(object sender, RoutedEventArgs e)
    {
        int minutes = (int)BufferDurationSlider.Value;
        _settings.ReplayBufferMinutes = minutes;
        _settings.Save();

        try
        {
            await _obs.SetReplayBufferDurationAsync(minutes * 60);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't reach the Replay Slider bridge: {ex.Message}", "Backtrack");
        }
    }


        private ConfirmDialog? _activeConfirmDialog;


    private void ShowConfirmDialog(string message, string confirmButtonText, Action<bool> callback)
    {
        _activeConfirmDialog?.Close();
        _activeConfirmDialog = ConfirmDialog.ShowNonModal(this, message, confirmButtonText, confirmed =>
        {
            _activeConfirmDialog = null;
            callback(confirmed);
        });
    }


        
    
    
    
    private static void FadeWindowIn(Window window, double durationMs = 180)
    {
        window.Opacity = 0;
        var cacheTarget = window.Content as FrameworkElement;
        if (cacheTarget != null)
            cacheTarget.CacheMode = new BitmapCache();

        window.Show();
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        if (cacheTarget != null)
            fade.Completed += (_, _) => cacheTarget.CacheMode = null;
        window.BeginAnimation(OpacityProperty, fade);
    }


        private static void FadeWindowOut(Window window, double durationMs = 150, Action? onCompleted = null, bool useCache = true)
    {
        FrameworkElement? cacheTarget = useCache ? window.Content as FrameworkElement : null;
        if (cacheTarget != null)
            cacheTarget.CacheMode = new BitmapCache();

        var fade = new DoubleAnimation(window.Opacity, 0, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            window.Hide();
            window.Opacity = 1;
            if (cacheTarget != null)
                cacheTarget.CacheMode = null;
            onCompleted?.Invoke();
        };
        window.BeginAnimation(OpacityProperty, fade);
    }


    private void CloseOverlay(bool preserveScreen = false)
    {
        
        if (_capturingHotkey)
            EndHotkeyCapture(cancelled: true);
        if (_capturingCancelRecordHotkey)
            EndCancelRecordHotkeyCapture(cancelled: true);
        if (_capturingBookmarkHotkey)
            EndBookmarkHotkeyCapture(cancelled: true);

        
        
        
        StopSettingsAutoscroll();

        if (!_settings.EnableAnimations)
        {
            
            
            
            
            
            if (preserveScreen)
            {
                
                
                
                
                
                
            }
            else if (!IsCriticalOperationActive())
            {
                _lastScreen = Screen.Idle;
                ShowScreen(Screen.Idle, skipEntranceAnimation: true);
            }
            else
            {
                ShowScreen(_lastScreen, skipEntranceAnimation: true);
            }

            Hide();
            _scrim.Hide();
        }
        else
        {
            
            
            
            
            
            
            
            
            
            
            
            
            
            if (preserveScreen)
            {
                FadeWindowOut(this, durationMs: 80, useCache: PlayerPanel.Visibility != Visibility.Visible);
            }
            else
            {
                if (!IsCriticalOperationActive())
                    _lastScreen = Screen.Idle;
                Screen targetScreen = _lastScreen;
                FadeWindowOut(this, durationMs: 80, onCompleted: () => ShowScreen(targetScreen, skipEntranceAnimation: true), useCache: PlayerPanel.Visibility != Visibility.Visible);
            }

            
            
            
            
            FadeWindowOut(_scrim);
        }

        _disclaimer.Hide();
        _logo.Hide();
        _streamingStatus.Hide();
        _recentClipsOverlay.Hide();
        _toastOverlay.UpdatePosition(false);
        _updatePrompt.HidePrompt();
        RefreshOverlayLogVisibilityAndMode();

        
        
        
        _statusOverlay.IsHudOpen = false;
        _statusOverlay.Reposition();
    }


    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible)
            return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_activeConfirmDialog != null && _activeConfirmDialog.IsLoaded)
            {
                _activeConfirmDialog.Close();
                _activeConfirmDialog = null;
            }
            else if (TrimPanel.Visibility == Visibility.Visible)
            {
                
                
                
                TrimCancel_Click(sender, e);
            }
            else if (_isPlayerFullscreen)
            {
                
                
                ExitPlayerFullscreen();
            }
            else if (_selectedClipPaths.Count > 0)
            {
                _selectedClipPaths.Clear();
                RefreshGallerySelectionUi();
            }
            else
            {
                CloseOverlay();
            }
            return;
        }

        HandlePlayerKeyboardShortcut(e);
    }


        public void MarkFirewallRulesAttempted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _settings.FirewallRulesAttempted = true;
            _settings.Save();
        });
    }


    private void RefreshUpdatePromptVisibility()
    {
        if (IsVisible && _pendingUpdateName is not null && _pendingUpdateInstall is not null)
            _updatePrompt.ShowPrompt(_pendingUpdateName, _pendingUpdateInstall);
        else
            _updatePrompt.HidePrompt();
    }


        private static void PrepareAnimatePanelIn(FrameworkElement panel, bool useCache)
    {
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (useCache)
            panel.CacheMode = new BitmapCache();
        panel.RenderTransform = new ScaleTransform(0.96, 0.96);
        panel.RenderTransformOrigin = new Point(0.5, 0.5);
        panel.Opacity = 0;
    }


    private static void StartAnimatePanelIn(FrameworkElement panel)
    {
        
        
        
        
        
        var duration = TimeSpan.FromMilliseconds(320);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = (ScaleTransform)panel.RenderTransform;

        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => panel.CacheMode = null;

        panel.BeginAnimation(OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
    }


    private void ShowScreen(Screen screen, bool skipEntranceAnimation = false)
    {
        _scrim.ArmDismissCooldown(400);

        
        
        
        
        
        
        StopSettingsAutoscroll();

        FrameworkElement newPanel = PanelFor(screen);
        bool switchingPanel = newPanel.Visibility != Visibility.Visible;
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        bool animateEntrance = switchingPanel && screen != Screen.Player && !skipEntranceAnimation && _settings.EnableAnimations;

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        IdlePanel.Visibility = Visibility.Collapsed;
        SaveReplayPanel.Visibility = Visibility.Collapsed;
        StartRecordPanel.Visibility = Visibility.Collapsed;
        GalleryPanel.Visibility = Visibility.Collapsed;
        PlayerPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (screen != Screen.Player)
        {
            PlayerVideoView.Visibility = Visibility.Collapsed;
            DetachPlayerVideo();
        }
        
        
        
        
        
        
        
        
        TopRightButtons.Visibility = Visibility.Collapsed;

        bool big = screen is Screen.Gallery or Screen.Player;
        Width = screen == Screen.Settings ? WideWidth : big ? BigWidth() : CompactWidth;
        Rect targetBounds = TargetScreenBounds;
        Left = targetBounds.X + (targetBounds.Width - Width) / 2;

        if (screen == Screen.Settings)
        {
            double maxScrollHeight = Math.Max(targetBounds.Height - 260, 450);
            SettingsScrollHost.MaxHeight = maxScrollHeight;
            Top = targetBounds.Y + Math.Max((targetBounds.Height - (maxScrollHeight + 80)) / 2, 85);
        }
        else if (big)
        {
            ApplyBigScreenSize();
        }
        else
        {
            Top = targetBounds.Y + CompactTop;
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        if (animateEntrance)
            PrepareAnimatePanelIn(newPanel, useCache: screen is Screen.Idle or Screen.SaveReplay or Screen.StartRecord or Screen.Settings);
        else if (switchingPanel)
        {
            
            
            
            
            newPanel.Opacity = 1;
            newPanel.RenderTransform = null;
            newPanel.CacheMode = null;
        }

        newPanel.Visibility = Visibility.Visible;
        
        
        
        
        
        
        
        
        
        
        
        if (!skipEntranceAnimation)
            UpdateLayout();
        
        
        
        
        
        
        
        
        
        Dispatcher.BeginInvoke(new Action(UpdateStreamingBoxVisibility), DispatcherPriority.Loaded);

        
        
        
        
        
        
        
        
        
        if (screen == Screen.Idle)
            _ = RefreshGalleryCountAsync();

        if (animateEntrance)
            StartAnimatePanelIn(newPanel);

        
        
        TopRightButtons.Visibility = screen == Screen.Idle ? Visibility.Visible : Visibility.Collapsed;

        
        
        
        
        
        
        
        
        
        
        if (screen != Screen.Player)
        {
            PlayerOverlayPopup.IsOpen = false;
            PlayerMenuPopup.IsOpen = false;
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        if (screen != Screen.Player)
            DisposeVlcPlayerAsync();

        if (screen is Screen.Idle or Screen.SaveReplay or Screen.StartRecord or Screen.Gallery or Screen.Settings)
            _lastScreen = screen;

        
        
        
        
        UpdateRecentClipsOverlayVisibility(screen);

        IntPtr toastHwnd = new WindowInteropHelper(_toastOverlay).Handle;
        if (toastHwnd != IntPtr.Zero)
        {
            WindowZOrder.BringToFrontWithoutActivating(toastHwnd);
        }
    }


        private double BigWidth() => Math.Min(TargetScreenBounds.Width * 0.78, 2000);


    private void BackToIdle_Click(object sender, MouseButtonEventArgs e) => ShowScreen(Screen.Idle);


    private void ShowStatusIndicatorToggle_Click(object sender, RoutedEventArgs e)
    {
        ToggleStatusOverlay();
        _trayManager?.UpdateStatus(_obs.IsConnected, _statusOverlay.IsVisible);
    }


    private void StatusIndicatorOrientationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.StatusIndicatorOrientation = StatusIndicatorOrientationSelector.SelectedItem is ComboBoxItem { Tag: "Vertical" }
            ? StatusIndicatorOrientation.Vertical
            : StatusIndicatorOrientation.Horizontal;
        _settings.Save();
        _statusOverlay.Reposition();
        UpdateStatusIndicatorPreview();
    }


    private void StatusIndicatorLocationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        
        
        
        
        _settings.StatusIndicatorLocation = (StatusIndicatorLocation)StatusIndicatorLocationSelector.SelectedIndex;
        _settings.Save();
        _statusOverlay.Reposition();
        UpdateStatusIndicatorPreview();
    }


        private void UpdateStatusIndicatorPreview()
    {
        bool horizontal = _settings.StatusIndicatorOrientation == StatusIndicatorOrientation.Horizontal;
        bool isLeft = _settings.StatusIndicatorLocation is StatusIndicatorLocation.TopLeft or StatusIndicatorLocation.BottomLeft;
        bool isTop = _settings.StatusIndicatorLocation is StatusIndicatorLocation.TopLeft or StatusIndicatorLocation.TopRight;

        StatusIndicatorPreviewPanel.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        StatusIndicatorPreviewPanel.HorizontalAlignment = isLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        StatusIndicatorPreviewPanel.VerticalAlignment = isTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        
        
        Thickness gap = horizontal ? new Thickness(5, 0, 0, 0) : new Thickness(0, 5, 0, 0);
        for (int i = 0; i < StatusIndicatorPreviewPanel.Children.Count; i++)
        {
            if (StatusIndicatorPreviewPanel.Children[i] is FrameworkElement badge)
                badge.Margin = i == 0 ? default : gap;
        }
    }


        private void StatusIndicatorPreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
            return;
        double targetHeight = e.NewSize.Width * 9.0 / 16.0;
        
        
        
        
        if (double.IsNaN(StatusIndicatorPreviewBorder.Height) || Math.Abs(StatusIndicatorPreviewBorder.Height - targetHeight) > 0.5)
            StatusIndicatorPreviewBorder.Height = targetHeight;
    }


        private void SetRecordIcon(bool active)
    {
        RecordDot.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        RecordSquare.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }


        private void UpdateStreamingBoxVisibility()
    {
        if (_isStreaming && IsVisible && IdlePanel.Visibility == Visibility.Visible)
        {
            _streamingStatus.Reposition(new Rect(Left, Top, Width, ActualHeight));
            _streamingStatus.Show();
        }
        else
        {
            _streamingStatus.Hide();
        }
    }


        private static string FormatDuration(long ms)
    {
        int totalSeconds = (int)(ms / 1000);
        int h = totalSeconds / 3600;
        int m = totalSeconds / 60 % 60;
        int s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }


    
    
    
    
    
    private const string FullscreenEnterIcon = "M7,14H5v5h5v-2H7V14zM5,10h2V7h3V5H5V10zM17,17h-3v2h5v-5h-2V17zM14,5v2h3v3h2V5H14z";

    private const string FullscreenExitIcon = "M5,16h3v3h2v-5H5V16zM8,8H5v2h5V5H8V8zM14,19h2v-3h3v-2h-5V19zM16,5h-2v5h5V8h-3V5z";


    
    
    private const string VolumeUpIcon = "M3,9v6h4l5,5V4L7,9H3z M16.5,12c0,-1.77 -1.02,-3.29 -2.5,-4.03v8.05c1.48,-0.73 2.5,-2.26 2.5,-4.02z M14,3.23v2.06c2.89,0.86 5,3.54 5,6.71s-2.11,5.85 -5,6.71v2.06c4.01,-0.91 7,-4.49 7,-8.77s-2.99,-7.86 -7,-8.77z";

    private const string VolumeOffIcon = "M16.5,12c0,-1.77 -1.02,-3.29 -2.5,-4.03v2.21l2.45,2.45c0.03,-0.2 0.05,-0.41 0.05,-0.63z M19,12c0,0.94 -0.2,1.82 -0.54,2.64l1.51,1.51C20.63,14.91 21,13.5 21,12c0,-4.28 -2.99,-7.86 -7,-8.77v2.06c2.89,0.86 5,3.54 5,6.71z M4.27,3L3,4.27L7.73,9H3v6h4l5,5v-6.73l4.25,4.25c-0.67,0.52 -1.42,0.93 -2.25,1.18v2.06c1.38,-0.31 2.63,-0.95 3.69,-1.81L19.73,21L21,19.73L4.27,3z M12,4L9.91,6.09L12,8.18V4z";

    private const string FeedbackPlayIcon = "M8,5v14l11,-7z";

    private const string FeedbackPauseIcon = "M6,19h4V5H6V19z M14,5v14h4V5H14z";

    private const string FeedbackSeekForwardIcon = "M4,18l8.5,-6L4,6v12z M13,6v12l8.5,-6L13,6z";

    private const string FeedbackSeekBackIcon = "M11,18V6l-8.5,6L11,18z M20,18V6l-8.5,6L20,18z";


    private enum PlayerFeedbackIcon { Play, Pause, SeekForward, SeekBack, Volume, Mute }


    private bool _isPlayerFullscreen;

    private double _preFullscreenWidth;

    private double _preFullscreenLeft;

    private string DescribeRowDestDir(string destDir)
    {
        if (string.IsNullOrEmpty(destDir))
            return "Not set -- clips stay wherever this buffer writes them";
        return IsWithinClipsFolder(destDir, out string relative)
            ? (relative.Length == 0 ? "Main clips folder" : relative)
            : destDir; 
    }


    

    
    
    
    
    private const int RecordStatusInactive = 0;  

    private const int RecordStatusStopped = 1;   

    private const int RecordStatusRecording = 2;

    private const int RecordStatusError = 3;     

    
    
    
    
    
    
    private const int RecordStatusNoSignal = 4;


    private const long BytesPerGb = 1024L * 1024L * 1024L;


        private long GetClipsFolderUsageBytes()
    {
        if (string.IsNullOrWhiteSpace(_settings.ClipsFolder) || !Directory.Exists(_settings.ClipsFolder))
            return 0;
        try
        {
            return Directory.EnumerateFiles(_settings.ClipsFolder, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !_pendingDeletePaths.Contains(Path.GetFullPath(f)))
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0; 
        }
    }


    private DispatcherTimer? _autoDeleteOldClipsTimer;


        private void RestartAutoDeleteOldClipsTimer()
    {
        _autoDeleteOldClipsTimer?.Stop();
        _autoDeleteOldClipsTimer = null;

        if (!_settings.AutoDeleteOldClipsEnabled)
            return;

        RunAutoDeleteOldClips(); 
        _autoDeleteOldClipsTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _autoDeleteOldClipsTimer.Tick += (_, _) => RunAutoDeleteOldClips();
        _autoDeleteOldClipsTimer.Start();
    }


        private const int MinClipSeconds = 15;


        private static int SliderPosToSeconds(double pos, int maxSeconds)
    {
        double t = pos / 1000.0;
        return (int)Math.Round(MinClipSeconds + (maxSeconds - MinClipSeconds) * t * t);
    }


    private static double SecondsToSliderPos(int seconds, int maxSeconds)
    {
        double t = Math.Sqrt(Math.Clamp((seconds - MinClipSeconds) / (double)(maxSeconds - MinClipSeconds), 0, 1));
        return t * 1000.0;
    }


        private static void SetSliderValueFromMouse(Slider slider, Point mousePos)
    {
        double width = slider.ActualWidth;
        if (width <= 0)
            return;
        double ratio = Math.Clamp(mousePos.X / width, 0.0, 1.0);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }


    private void AddInfoLine(Panel container, string text)
    {
        container.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)FindResource("Text2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 4, 2, 4),
        });
    }


    

    private static readonly string[] VideoExtensions = GalleryFormats.VideoExtensions;


    private int CountClips()
    {
        try
        {
            return Directory.Exists(_settings.ClipsFolder)
                ? Directory.EnumerateFiles(_settings.ClipsFolder, "*", SearchOption.AllDirectories)
                    .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new FileInfo(f))
                    .Count(f => f.Exists && f.Length > 0 && TryGetCachedDurationMs(f) is not < 2000)
                : 0;
        }
        catch
        {
            return 0;
        }
    }


        private string? GetNewestClipPath()
    {
        try
        {
            string? newest = Directory.Exists(_settings.ClipsFolder)
                ? Directory.EnumerateFiles(_settings.ClipsFolder, "*", SearchOption.AllDirectories)
                    .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new FileInfo(f))
                    .Where(f => f.Exists && f.Length > 0 && TryGetCachedDurationMs(f) is not < 2000)
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault()
                    ?.FullName
                : null;
            
            
            
            
            
            
            return newest is null ? null : Path.GetFullPath(newest);
        }
        catch
        {
            return null;
        }
    }


    
    
    
    
    
    
    
    
    
    
    
    
    private long _clipOpenToken;


        private string? _currentStreamToken;

    private long _remoteStreamTotalBytes;


        private string GetRemoteClipCachePath(string relativePath, string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Backtrack", "RemoteCache", _settings.PairedPeerDeviceId ?? "",
        Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "",
        fileName);


        private StackPanel WithNewestDot(TextBlock title, string tooltip)
    {
        Thickness titleMargin = title.Margin;
        title.Margin = new Thickness(4, 0, 0, 0);

        
        
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = (Brush)FindResource("NewestClip"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = titleMargin };
        row.Children.Add(dot);
        row.Children.Add(title);
        return row;
    }


    private static readonly SemaphoreSlim ThumbnailGenerationLock = new(1, 1);


    private static long? TryGetCachedDurationMs(FileInfo file)
    {
        try
        {
            string path = GetDurationCachePath(file);
            return File.Exists(path) && long.TryParse(File.ReadAllText(path), out long ms) ? ms : null;
        }
        catch
        {
            return null;
        }
    }


    private static bool IsNetworkPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);


    private async Task CopyToThisPcAsync(FileInfo file, Button triggerButton)
    {
        triggerButton.IsEnabled = false;
        string originalText = (string)triggerButton.Content;
        triggerButton.Content = "Copying...";
        try
        {
            Directory.CreateDirectory(_settings.LocalCopyFolder);
            string dest = Path.Combine(_settings.LocalCopyFolder, file.Name);
            await Task.Run(() => File.Copy(file.FullName, dest, overwrite: true));
            triggerButton.Content = "Copied";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't copy that clip: {ex.Message}", "Backtrack");
            triggerButton.Content = originalText;
            triggerButton.IsEnabled = true;
        }
    }


    

        private static string ResolveLocalClipPath(FileInfo file) => file.FullName;


    private sealed record AudioTrackOption(int Id, string Name);


    

    
    
    
    private const int PlayPauseButtonHomeColumn = 0;

    private const int AudioTrackComboHomeColumn = 4;

    private const int PlayerSpeedButtonHomeColumn = 5;

    private const int PlayerVolumeButtonHomeColumn = 6;

    private const int PlayerFullscreenButtonHomeColumn = 7;


    
    
    
    
    
    
    
    
    private const double PlayPauseButtonNormalSize = 42;

    private const double PlayPauseButtonTrimSize = 28;


    private void StopPreviewLoop()
    {
        _previewLooping = false;
        PreviewLoopIcon.Visibility = Visibility.Visible;
        PreviewStopIcon.Visibility = Visibility.Collapsed;
    }


    private static async Task CopyWithRetryAsync(string sourcePath, string destPath, bool overwrite, int attempts = 5)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(sourcePath, destPath, overwrite);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }


    private sealed record DisplayOption(string DeviceName, string Name);


    private void RefreshRemotePluginStatusText()
    {
        RemotePluginStatusText.Text = string.IsNullOrEmpty(_settings.PairedPeerSecret)
            ? "Not paired with a transmitter PC yet -- pair with it first (below, in OBS section)."
            : $"Paired with {_settings.PairedPeerName}. Click \"Check & update\" to check its plugin versions.";
    }


    private readonly Dictionary<string, Border> _themeSwatches = new(StringComparer.OrdinalIgnoreCase);


    

    
    
    
    private Point? _themeSwatchesDragStart;

    private double _themeSwatchesDragStartOffset;

    
    
    
    
    
    
    private const double ThemeSwatchesDragThreshold = 4;

    private bool _themeSwatchesDragged;


    

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private bool _settingsAutoscrollActive;

    private double _settingsAutoscrollStartY;


    
    
    
    
    private const double AutoscrollSensitivity = 0.06;

    private const double AutoscrollDeadZone = 4;


    private void ShowDisclaimerToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowDisclaimer = ShowDisclaimerToggle.IsChecked == true;
        _settings.Save();
        if (!_settings.ShowDisclaimer)
            _disclaimer.Hide();
        else if (IsVisible)
            _disclaimer.Show();
    }


    private void DisableBacktrackAutoUpdateToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.DisableBacktrackAutoUpdate = DisableBacktrackAutoUpdateToggle.IsChecked == true;
        _settings.Save();
    }


    private void DisablePluginAutoUpdateToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.DisablePluginAutoUpdate = DisablePluginAutoUpdateToggle.IsChecked == true;
        _settings.Save();
    }


    private void EnableAnimationsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.EnableAnimations = EnableAnimationsToggle.IsChecked == true;
        _settings.Save();
    }


    private void DiagnosticLogToggle_Click(object sender, RoutedEventArgs e) => SetDiagnosticLogEnabled(DiagnosticLogToggle.IsChecked == true);


    private void SetDiagnosticLogEnabled(bool enabled)
    {
        _settings.DiagnosticLogEnabled = enabled;
        _settings.Save();
        AppLog.FileLoggingEnabled = enabled;
        DiagnosticLogToggle.IsChecked = enabled;
        OpenDiagnosticLogButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled)
            AppLog.Write("Diagnostic log file enabled");
    }


    private void DeveloperModeToggle_Click(object sender, RoutedEventArgs e) => SetDeveloperModeEnabled(DeveloperModeToggle.IsChecked == true);


    private void SetDeveloperModeEnabled(bool enabled)
    {
        _settings.DeveloperModeEnabled = enabled;
        _settings.Save();
        AppLog.DeveloperModeEnabled = enabled;
        UpdateService.DeveloperModeEnabled = enabled;
        DeveloperModeToggle.IsChecked = enabled;

        
        
        
        
        
        
        
        
        if (enabled && !_settings.DiagnosticLogEnabled)
            SetDiagnosticLogEnabled(true);

        
        
        
        
        
        
        
        
        _settings.DisableBacktrackAutoUpdate = enabled;
        _settings.Save();
        DisableBacktrackAutoUpdateToggle.IsChecked = enabled;
        DisableBacktrackAutoUpdateToggle.IsEnabled = !enabled;
    }


    private void DisableHardwareAccelToggle_Click(object sender, RoutedEventArgs e)
    {
        
        
        
        _settings.DisableHardwareAcceleration = DisableHardwareAccelToggle.IsChecked == true;
        _settings.Save();
        MessageBox.Show(this, "This takes effect the next time Backtrack starts.", "Backtrack");
    }


    private void OpenDiagnosticLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(AppLog.LogFilePath))
            {
                MessageBox.Show(this, "Nothing's been logged to the file yet.", "Backtrack");
                return;
            }
            Process.Start(new ProcessStartInfo(AppLog.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the log file: {ex.Message}", "Backtrack");
        }
    }


    
    
    
    
    private void LaunchWithWindowsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = LaunchWithWindowsToggle.IsChecked == true;
        try
        {
            if (enabled)
                CreateOrUpdateStartupTask();
            else
                DeleteStartupTask();

            _settings.LaunchWithWindows = enabled;
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't update the startup task: {ex.Message}", "Backtrack");
            LaunchWithWindowsToggle.IsChecked = !enabled;
        }
    }


    
    
    
    
    
    
    
    private static string SchtasksPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");


    private void QuitApp_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

}
