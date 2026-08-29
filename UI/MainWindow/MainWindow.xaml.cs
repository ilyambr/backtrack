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
using Backtrack.StreamDeck;
using Backtrack.Streaming;
using Backtrack.Updates;
using Microsoft.Win32;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }
    private enum Screen { Idle, SaveReplay, StartRecord, Gallery, Player, Settings }

    private const double CompactWidth = 460;

    private const double WideWidth = 680;

    private const double CompactTop = 76;

    private const double BigTop = 76;

    private const string ScheduledTaskName = "BacktrackAutostart";

    private Rect TargetScreenBounds => DisplayMonitors.ResolveBoundsDiu(_settings.DisplayDeviceName);

    private ObsService _obs = null!;

    private bool _serverEnabledAtStartup;

    private bool _lastKnownAnyRowActive;

    private bool _lastKnownAnyRowError;

    private int _lastKnownActiveRecordRowCount;

    private bool _refreshStatusRunning;

    private readonly Dictionary<string, DateTime> _recordRowActiveSinceUtc = new();

    private readonly Dictionary<string, DateTime> _lastReplaySaveUtc = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, (string Label, string SourceName, string FilterName)> _recordRowInfoByKey = new();

    private bool _recordRowPollSeeded;

    private DispatcherTimer? _pollTimer;

    private DispatcherTimer? _micTimer;

    private DispatcherTimer? _remoteSyncTimer;

    private bool _remoteSyncRunning;

    private readonly StatusOverlay _statusOverlay;

    private readonly ToastOverlay _toastOverlay;

    private readonly UpdatePromptOverlay _updatePrompt = new();

    private DispatcherTimer? _obsStatsTimer = new() { Interval = TimeSpan.FromSeconds(2) };

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

    private PairingService _pairing = null!;

    private RemoteClipStreamServer _remoteStreamServer = null!;

    private StreamDeckIpcServer? _streamDeckServer;

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

    private SystemTrayManager? _trayManager;

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
        _pendingBookmarkUtcTimes.RemoveAll(t => (now - t).TotalMinutes > 5);
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
    private bool _isUpdatingAudioTracks;

    private Task? _pendingVlcDisposeTask;
    private LibVlc.Media? _currentPlayerMedia;

    private FileInfo? _currentPlayerFile;

    private (string RelativePath, string DeviceId)? _currentPlayerRemoteOrigin;

    private DispatcherTimer? _seekTimer;

    private DispatcherTimer? _seekDebounceTimer;

    private DispatcherTimer? _galleryFilterDebounceTimer;

    private DispatcherTimer? _freezeFrameTimer;

    private DispatcherTimer? _volumePopupCloseDebounce;

    private DispatcherTimer? _actionFeedbackHideTimer;

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
    private RemoteStorageInfo? _lastRemoteStorageInfo;

    private bool _remotePcWasConnected;

    private readonly HashSet<string> _selectedClipPaths = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<(FileInfo File, Border Circle, Border Thumb)> _galleryCardSelection = new();

    public MainWindow(StatusOverlay statusOverlay, ToastOverlay toastOverlay, ScrimOverlay scrim, DisclaimerOverlay disclaimer, LogoOverlay logo, StreamingStatusOverlay streamingStatus, PairingRequestOverlay pairingRequestOverlay, RecentClipsOverlay recentClipsOverlay)
    {
        Instance = this;
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

        SizeChanged += (_, _) => { UpdateStreamingBoxVisibility(); RepositionPlayerPopups(); };
        LocationChanged += (_, _) => { UpdateStreamingBoxVisibility(); RepositionPlayerPopups(); };

        _pairing = new PairingService(_settings);
        _remoteStreamServer = new RemoteClipStreamServer(_pairing);
        WirePairingAndStreaming();

        _scrim.Dismissed += () => Dispatcher.BeginInvoke(() => CloseOverlay());
        _scrim.DragHovered += (screenPt) => CheckDragExitThreshold(screenPt);
        ShellDragHelper.EnableDropPreview(this, this);
        KeyDown += MainWindow_KeyDown;

        string url;
        string? password;
        (url, password, _serverEnabledAtStartup) = ResolveObsConnection();
        _obs = new ObsService(url, password);
        WireObsEvents();
        WireVlcAndPairingDelegates();
        SetupTimersAndWindow();
        SetupTrayAndVlc();

        _streamDeckServer = new StreamDeckIpcServer(_obs, _settings,
            () => Dispatcher.BeginInvoke(ToggleVisible),
            () => Dispatcher.BeginInvoke(OnBookmarkHotkeyPressed),
            key => _recordRowActiveSinceUtc.TryGetValue(key, out var dt) ? dt : null);
        _streamDeckServer.Start();

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

}
