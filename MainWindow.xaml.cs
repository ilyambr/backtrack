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

    // LogoOverlay sits at a fixed Top=20 with Height=46 (bottom edge at 66), so the
    // compact HUD panel needs to start clear of that, not at the same Top=40 both
    // windows used to share back when the logo was drawn inside MainWindow itself.
    private const double CompactTop = 76;

    // Gallery/Player always open at this same fixed Top, rather than centering
    // against the real ActualHeight (which isn't known until after layout runs,
    // and settles across a couple of frames) -- that meant computing Top
    // reactively, and doing so visibly moved the window a beat after it first
    // appeared, no matter how that recompute was timed/debounced. A fixed value
    // has no timing dependency at all, so there's nothing left to jitter.
    private const double BigTop = 90;

    private const string RunKeyName = "Backtrack";
    private const string ScheduledTaskName = "BacktrackAutostart";

    /// <summary>Re-resolved on every access (cheap: one EnumDisplayMonitors call) so a display change in Settings takes effect on the next reposition without an app restart.</summary>
    private Rect TargetScreenBounds => DisplayMonitors.ResolveBoundsDiu(_settings.DisplayDeviceName);

    private readonly ObsService _obs;
    private bool _serverEnabledAtStartup;

    // Last successful ListReplayRowsAsync read, for RefreshStatusAsync's Save
    // Replay pill only -- see the catch there for why this exists: a bridge
    // call failing on just ONE poll tick (out of one per second) used to reset
    // these to false outright, which could flip the pill to "Off" for a moment
    // even while a buffer row was genuinely still armed and its own row button
    // (rendered from a separate, independently-timed call) kept showing green.
    private bool _lastKnownAnyRowActive;
    private bool _lastKnownAnyRowError;
    private int _lastKnownActiveRecordRowCount;
    private bool _refreshStatusRunning;
    // Per-row (not one shared value): the moment RefreshStatusAsync first
    // notices each individual Source Record row actively recording, keyed by
    // RecordRow.Key, pruned once that row stops. Approximate, not a true
    // start time -- see RefreshStatusAsync's own comment. Needs to be
    // per-row rather than one shared timestamp for two reasons: (1) it used
    // to get wiped to null every single tick the MAIN recording was active,
    // even while a row was ALSO recording that whole time, so the moment
    // main stopped and a still-running row took over the display, the timer
    // read "just started" instead of however long that row had genuinely
    // been going; (2) two different rows starting at two different times
    // need their own separate timestamps to compare, not one shared "since
    // SOMETHING became active" value that can't tell them apart.
    private readonly Dictionary<string, DateTime> _recordRowActiveSinceUtc = new();

    // Cached alongside _recordRowActiveSinceUtc so a toast fired for a row
    // that just STOPPED (see RefreshStatusAsync) can still say its real
    // label and look up a destination folder -- by the time a row drops out
    // of ListRecordRowsAsync's result, its own RecordRow object is already
    // gone from that tick's list.
    private readonly Dictionary<string, (string Label, string SourceName, string FilterName)> _recordRowInfoByKey = new();

    // False until the first RefreshStatusAsync poll completes -- without
    // this, every row already recording when Backtrack starts (or
    // reconnects) would look "newly started" on that first tick and toast
    // for something that's been going for a while already, not something
    // that just happened.
    private bool _recordRowPollSeeded;

    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _micTimer;
    private readonly DispatcherTimer _remoteSyncTimer;
    private bool _remoteSyncRunning;
    private readonly StatusOverlay _statusOverlay;
    private readonly ToastOverlay _toastOverlay;
    private readonly UpdatePromptOverlay _updatePrompt = new();
    private readonly OverlayLogWindow _overlayLog = new();
    private readonly DispatcherTimer _obsStatsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private long? _lastRenderTotalFrames, _lastRenderSkippedFrames, _lastOutputTotalFrames, _lastOutputSkippedFrames;
    private DateTime? _obsLogClearAtUtc;
    private readonly ScrimOverlay _scrim;
    private readonly DisclaimerOverlay _disclaimer;

    // Set when an update was found but deferred because OBS is actively
    // recording/streaming (see ObsService.IsRecordingOrStreamingAsync).
    // Only one tracked at a time -- good enough in practice, since hitting
    // this at all is already the rare case. RefreshUpdatePromptVisibility
    // shows/hides _updatePrompt to match both "is there one pending" and
    // "is the HUD actually open right now".
    private string? _pendingUpdateName;
    private Action? _pendingUpdateInstall;
    private readonly LogoOverlay _logo;
    private readonly StreamingStatusOverlay _streamingStatus;
    private readonly PairingRequestOverlay _pairingRequestOverlay;
    private readonly RecentClipsOverlay _recentClipsOverlay;
    private readonly AppSettings _settings;
    private readonly UpdateService _updates = new();

    // True once a manual "Check now" press has found something available and
    // the button has turned into "Update" -- the actual install only happens
    // on the SECOND press, once the user has explicitly seen there's
    // something to install rather than it just happening silently on the
    // first click. Doesn't affect the automatic hourly check, which still
    // applies updates on its own the moment it's safe to (see CheckForUpdatesAsync).
    private bool _manualUpdateReady;
    // Last known streaming state -- StreamingStatusOverlay needs this plus 2
    // more conditions true before it's actually shown (see
    // UpdateStreamingBoxVisibility), and this is the only one of the 3 not
    // already readable directly off some existing element/property.
    private bool _isStreaming;
    // Cooldown gate for the EncoderOverloadDetected toast -- see that
    // subscription's own comment for why this isn't a plain de-dup.
    private DateTime _lastEncoderOverloadToastUtc = DateTime.MinValue;
    // Separate from the toast cooldown above: the plugin only re-emits
    // EncoderOverloadDetected roughly every ~2s for as long as an overload
    // is actually ongoing, and never fires a distinct "it stopped" event --
    // so RefreshStatusAsync infers "still overloaded right now" purely from
    // how recently this was last touched (see its own check), independent of
    // whatever the toast's own 30s cooldown is doing. Touched by TWO
    // independent detectors, whichever notices first: this event (requires
    // obs-source-record with an active Source Record filter somewhere) and
    // RefreshObsModeLogAsync's own raw-GetStats delta check (needs nothing
    // but stock obs-websocket, so it's the one that still works with zero
    // filters running).
    private DateTime _lastEncoderOverloadEventUtc = DateTime.MinValue;
    private bool _encoderOverloadedShown;
    private readonly PairingService _pairing;
    private readonly RemoteClipStreamServer _remoteStreamServer;
    private readonly Dictionary<string, string> _rowLabels = new();
    private List<ReplayRow> _lastReplayRows = new();
    private GlobalHotkey? _hotkey;
    private Screen _lastScreen = Screen.Idle;
    // Which screen BackToGallery_Click returns to. Defaults to Gallery at the
    // top of every OpenInPlayer call (the overwhelming common case: clicking
    // a card FROM Gallery); ShowMainWindowAndOpenInPlayer overrides it to
    // Idle right after, since a clip opened from the Recent Clips overlay
    // was never reached by navigating through Gallery, so backing out of it
    // shouldn't land there either.
    private Screen _playerBackTarget = Screen.Gallery;
    private readonly SystemTrayManager _trayManager;

    private bool _isRenamingCard;
    private bool _isPlayerRenaming;
    // Set by PlayerRename_Click while its in-place TextBox is up, cleared the
    // moment it closes (committed or reverted) either way. Lets a caller like
    // BackToGallery_Click cancel an in-progress rename explicitly, instead of
    // relying on the TextBox's own LostFocus handler to sort it out on its
    // own -- LostFocus fires as a side effect of clicking Back too, which
    // raced CommitRename (and its own OpenInPlayer refresh) against
    // BackToGallery_Click's ShowScreen(Gallery) over which screen ends up
    // showing.
    private Action? _cancelPlayerRename;
    private bool _isTrimming;
    private readonly HashSet<string> _pendingDeletePaths = new(StringComparer.OrdinalIgnoreCase);
    // Remote counterpart, keyed by relative path (there's no local FileInfo
    // to key a remote card off of) -- see QueueRemoteDeleteWithUndo.
    private readonly HashSet<string> _pendingRemoteDeletePaths = new(StringComparer.Ordinal);

    // --------------------------------------------------------------- LibVLC / Player

    private LibVlc.LibVLC? _libVlc;
    private LibVlc.MediaPlayer? _vlcPlayer;
    // Set on EndReached, cleared once actually resumed. A bare Play()/seek
    // after libvlc reaches end-of-stream is a well-known LibVLC quirk that
    // just silently does nothing -- the demuxer/pipeline needs an explicit
    // Stop()+Play() cycle to become resumable again, not just a Time= write
    // or another Play() call on top of the already-ended one. See
    // PlayPauseButton_Click and CommitSeek, the two ways to resume.
    private bool _playerHasEnded;
    private Task? _pendingVlcDisposeTask;
    private FileInfo? _currentPlayerFile;
    // Set (right after OpenInPlayer) whenever the clip currently open in
    // Player is actually a downloaded copy of a REMOTE clip -- RelativePath
    // as the host PC knows it, DeviceId identifying which paired peer.
    // Cleared at the top of every OpenInPlayer call (a genuinely local clip
    // clears it back out). Checked by PlayerDelete_Click/the Player title
    // rename/RunTrimAsync so those edits get mirrored back to the actual
    // clip on the stream PC, not just applied to this PC's local cached
    // copy of it -- see each of those call sites for the round-trip itself.
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

    // Hotkey capture (Settings)
    private bool _capturingHotkey;

    // Trim
    private TimeSpan? _trimStart;
    private TimeSpan? _trimEnd;
    private bool _previewLooping;
    private enum TrimDragMode { None, Start, End, Seek }
    private TrimDragMode _trimDragMode = TrimDragMode.None;

    // Playback speed -- cycled by PlayerSpeedButton, not a slider; a small
    // fixed set covers the actual use cases here (slow-mo review, skimming a
    // long buffer) without needing arbitrary precision.
    private static readonly float[] PlaybackSpeeds = { 0.5f, 1f, 1.5f, 2f };
    private int _playbackSpeedIndex = 1; // 1f

    // --------------------------------------------------------------- Gallery folders / selection

    // null means "at the clips-folder root" -- kept nullable instead of always holding
    // a path so GalleryTile_Click can reset browsing back to the top with one write,
    // and so GalleryUp_Click has an unambiguous "there's no further up" state.
    private string? _currentGalleryFolder;
    private string GalleryFolder => _currentGalleryFolder ?? _settings.ClipsFolder;

    // Whether Gallery is currently browsing this PC's own ClipsFolder or the
    // paired transmitter PC's, over the pairing connection (see
    // PairingService.ListRemoteGalleryAsync/DownloadRemoteClipAsync). Reset to
    // local every time Gallery is (re)opened from Idle -- see GalleryTile_Click.
    private bool _galleryIsRemote;
    // Relative to the remote PC's own ClipsFolder root; null means "at that root".
    private string? _currentRemoteGalleryFolder;
    // Edge-triggered ("only when it actually changes") -- starts false so
    // the very first failed remote gallery load (before ever successfully
    // reaching that PC this session) doesn't fire a "lost connection" toast
    // for a connection that never actually existed yet; the inline Gallery
    // message already covers that case. Only set true after a real
    // successful listing, only reset (with the toast firing) on the
    // transition FROM that back to a failed one.
    private bool _remotePcWasConnected;

    private readonly HashSet<string> _selectedClipPaths = new(StringComparer.OrdinalIgnoreCase);
    // Rebuilt every LoadGallery() call -- lets mass actions and the selection-circle
    // visuals look up a card's controls by file without threading extra state through
    // BuildClipCard's return value (still just a Border, used everywhere else as one).
    private readonly List<(FileInfo File, Border Circle, Border Thumb)> _galleryCardSelection = new();

    public MainWindow(StatusOverlay statusOverlay, ToastOverlay toastOverlay, ScrimOverlay scrim, DisclaimerOverlay disclaimer, LogoOverlay logo, StreamingStatusOverlay streamingStatus, PairingRequestOverlay pairingRequestOverlay, RecentClipsOverlay recentClipsOverlay)
    {
        InitializeComponent();
        // A safe initial default in case anything reads _themeSwatches before
        // Settings is ever opened -- LoadSettingsUi rebuilds this properly
        // every time Settings actually opens (see its own comment on why
        // that's necessary now: unlike the old fixed 5-theme version, the
        // discovered set of themes can genuinely change while running).
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
        // As early as possible -- before anything else in this constructor
        // has a chance to call AppLog.Write, so nothing from startup itself
        // is silently missed from the file log if it's enabled.
        AppLog.FileLoggingEnabled = _settings.DiagnosticLogEnabled;
        AppLog.DeveloperModeEnabled = _settings.DeveloperModeEnabled;
        UpdateService.DeveloperModeEnabled = _settings.DeveloperModeEnabled;

        // Self-corrects StreamingStatusOverlay's position once this window's
        // real post-switch bounds actually settle (see UpdateStreamingBoxVisibility's
        // own comment) -- also just generally keeps it tracking MainWindow if
        // it ever moves independently of a screen switch.
        SizeChanged += (_, _) => UpdateStreamingBoxVisibility();
        LocationChanged += (_, _) => UpdateStreamingBoxVisibility();

        _pairing = new PairingService(_settings);
        _remoteStreamServer = new RemoteClipStreamServer(_pairing);
        _pairing.PairingRequested += (deviceName, code, requestId) => Dispatcher.BeginInvoke(() =>
        {
            _pairingRequestOverlay.ShowRequest(deviceName, code,
                onAllow: () =>
                {
                    _pairing.ApproveRequest(requestId);
                    // Live-updates AuthorizedDeviceRow if Settings happens to
                    // already be open when this approval lands, instead of
                    // only reflecting the new device on next visit to the
                    // Sharing section.
                    RefreshShareClipsUi();
                },
                onDeny: () => _pairing.DenyRequest(requestId));
        });
        // Always listening for other Backtrack instances announcing themselves, so
        // the Settings screen has a live discovered-devices list ready the moment
        // it's opened rather than only starting to listen once you navigate there.
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

        // CloseOverlay has an optional parameter (preserveScreen) -- a bare
        // "CloseOverlay" method-group reference here used to convert cleanly
        // to a zero-arg delegate back when the method had no parameters, but
        // a default value only applies at an explicit call site, not when
        // the method is captured as a stored delegate like this. The
        // dispatcher later invokes that stored delegate expecting zero args,
        // but the method genuinely takes one at the IL level -- a real
        // TargetParameterCountException crash, not hypothetical. The
        // explicit lambda forces a proper zero-arg closure instead.
        _scrim.Dismissed += () => Dispatcher.BeginInvoke(() => CloseOverlay());
        KeyDown += MainWindow_KeyDown;

        string url;
        string? password;
        (url, password, _serverEnabledAtStartup) = ResolveObsConnection();
        _obs = new ObsService(url, password);

        // Events, not polling -- these fire the instant OBS says so, whether or
        // not the HUD is even open, which is the only way "did it actually save"
        // can be answered truthfully instead of guessed at.
        _obs.RecordingStateChanged += (active, path) => Dispatcher.BeginInvoke(() =>
        {
            _toastOverlay.ShowRecording(active, path);
            if (active)
                AppLog.Write("Recording started");
            else if (path is not null)
            {
                AppLog.Write($"Recording saved to '{path}'");
                ShowObsModeMessage($"Recording saved to '{path}'");
                RefreshRecentClipsOverlay();
            }
        });
        _obs.StreamingStateChanged += active => Dispatcher.BeginInvoke(() =>
        {
            // obs-websocket appears to genuinely emit two separate stop
            // transitions when Twitch Enhanced Broadcasting/multitrack is
            // active (its own underlying RTMP sub-output stopping alongside
            // the regular stream output) -- reported live as "Livestream
            // Ended" toasting twice back to back. Skip re-announcing a state
            // that's already current instead of trying to filter by exact
            // cause.
            if (active == _isStreaming)
                return;
            _toastOverlay.ShowStreaming(active);
            AppLog.Write(active ? "Livestream started" : "Livestream ended");
            _isStreaming = active;
            _statusOverlay.SetStreaming(active);
            UpdateStreamingBoxVisibility();
        });
        // Snappier than waiting for RefreshStatusAsync's own 1s poll (which
        // still covers Backtrack opening or OBS reconnecting while the
        // Virtual Camera is already on -- see its own comment); no toast,
        // just the status indicator, since nothing asked for one here.
        _obs.VirtualCamStateChanged += active => Dispatcher.BeginInvoke(() =>
        {
            _statusOverlay.SetVirtualCamActive(active);
        });
        _obs.EncoderOverloadDetected += info => Dispatcher.BeginInvoke(() =>
        {
            // Always touch this, regardless of the toast's own cooldown
            // below -- RefreshStatusAsync's status-indicator badge needs to
            // know an overload is STILL happening right now every ~2s,
            // independent of how often a toast is allowed to pop up about it.
            _lastEncoderOverloadEventUtc = DateTime.UtcNow;

            // The plugin re-emits this roughly every ~2s for as long as the
            // condition holds, not just once on a transition -- showing a
            // fresh toast every single time would spam the screen for a
            // sustained overload. Cooldown instead of full de-dup, since
            // which specific output is affected can genuinely change
            // between checks and a plain "already showing this" guard
            // wouldn't catch a NEW cause starting right after an old one
            // stopped, mid-cooldown.
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
        // Shared by ReplaySaving and ReplaySaved below -- both need the same
        // real display label for the same row key, and both need the same
        // self-healing retry (see its own comment).
        async Task<string> ResolveRowLabelAsync(string key)
        {
            if (!_rowLabels.TryGetValue(key, out string? label))
            {
                // The row's key is a raw obs_source_t* address formatted as a
                // string, not a real identifier (see obs-replay-slider's
                // AddRow/RebuildRows: "not all OBS versions... have
                // obs_source_get_uuid(), so use the source object's own
                // address instead") -- which is NOT stable across an OBS
                // restart on the transmitter PC, since every source gets a
                // new address. A cache built before the last such restart is
                // silently wrong for every row, not just missing one, which
                // is exactly what showed up as a meaningless raw number in
                // the toast instead of the real filter/source name. Retry
                // once, live, before giving up -- self-heals this toast
                // immediately instead of waiting for Backtrack's own next
                // reconnect to refresh the whole cache.
                await PrefetchRowLabelsAsync();
                _rowLabels.TryGetValue(key, out label);
            }
            label ??= key;
            return DisplayLabel(label); // local rename override, if any -- see its own comment
        }

        // Needs obs-replay-slider 0.2.20+ (ReplaySaving's own doc comment) --
        // an older paired build simply never fires this, and a save just
        // goes straight to ReplaySaved below with no lead-in toast, same as
        // it always used to for every trigger, not just a hotkey one.
        _obs.ReplaySaving += key => Dispatcher.BeginInvoke(async () =>
        {
            string label = await ResolveRowLabelAsync(key);
            _toastOverlay.ShowProcessingClip(key, label);
        });
        _obs.ReplaySaved += (key, path) => Dispatcher.BeginInvoke(async () =>
        {
            string label = await ResolveRowLabelAsync(key);

            // Quick-finishes the processing toast's bar then swaps to the
            // saved toast -- or, if this key never had a processing toast
            // showing (an older paired obs-replay-slider build with no
            // ReplaySaving event -- see its own comment), just shows the
            // saved toast directly.
            _toastOverlay.CompleteProcessingClip(key, label, path);
            AppLog.Write($"{label} saved to '{path}'");
            ShowObsModeMessage($"Replay saved to '{path}'");
            _ = RefreshGalleryCountAsync();
            RefreshRecentClipsOverlay();
        });
        _obs.StateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            AppLog.Write(_obs.IsConnected ? "Connected to OBS" : "Disconnected from OBS");
            _ = PrefetchRowLabelsAsync();
            if (_settings.RamDiskEnabled && RamDisk.IsMounted(_settings.RamDiskDriveLetter))
                _ = PushRamDiskDestDirAsync();
            else if (!_settings.RamDiskEnabled)
                _ = _obs.RevertSourceRecordFilterPathsAsync(_settings.RamDiskDriveLetter, _settings.ClipsFolder);
        });

        // Lets a paired receiver PC's Backtrack read/change RAM disk settings on
        // THIS instance over the network -- only meaningful/reachable when this PC
        // is the one actually running OBS. See PairingService's remote RAM disk
        // control section for the request handling and auth.
        _pairing.GetRamDiskSnapshot = () => new RamDiskSnapshot(
            _settings.RamDiskEnabled, _settings.RamDiskDriveLetter, _settings.RamDiskSizeMb,
            RamDisk.IsMounted(_settings.RamDiskDriveLetter));
        _pairing.ApplyRamDiskSnapshot = ApplyRamDiskConfigAsync;

        // Lets a paired receiver PC's remote Gallery tab reuse this instance's
        // own thumbnail cache instead of PairingService duplicating LibVLC
        // frame-grab logic it has no access to -- see EnsureThumbnailCachedAsync.
        _pairing.EnsureThumbnailCachedForRemote = async fullPath => await EnsureThumbnailCachedAsync(new FileInfo(fullPath));
        // So list_gallery hides obviously-broken/glitched clips the same way
        // the local Gallery does -- see TryGetCachedDurationMs.
        _pairing.GetCachedDurationMsForRemote = fullPath => TryGetCachedDurationMs(new FileInfo(fullPath));
        // Lets a paired receiver PC's trim_clip request run the real trim
        // directly against this PC's own local file -- see TrimClipForRemote's
        // own comment for why nothing needs to cross the network for this.
        _pairing.TrimClipForRemote = TrimClipForRemoteAsync;

        // Same reasoning as RAM disk above -- plugin version checks/updates read
        // and write C:\Program Files\obs-studio on THIS machine (see
        // UpdateService), so a paired receiver PC needs this run here, not on
        // itself. Dispatcher.InvokeAsync because incoming pairing requests are
        // handled on PairingService's own network thread, not the UI thread, and
        // CheckAndApplyPluginUpdateAsync touches UI elements (dot/version text)
        // directly.
        _pairing.CheckAndApplyPluginUpdatesRemotely = async () =>
        {
            // isManualTrigger: true -- a remote request to check-and-apply right
            // now is just as much an explicit "yes, install it" as the local
            // Settings button, not a silent background check.
            // deferObsReopen: true on both -- same reasoning as CheckForUpdatesAsync's own call.
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
            // Re-entrancy guard: RefreshStatusAsync now makes 4 sequential OBS
            // round-trips (2 more than it used to, for the Start Recording
            // aggregation) -- if one tick ever takes longer than the 1s interval
            // to finish (a slow bridge round-trip, OBS's own UI thread briefly
            // busy, etc.), the timer fires again anyway and a second overlapping
            // call starts racing the first, both touching the same UI elements
            // and connection. That pileup is what a sudden "everything feels
            // laggy" (and, downstream, WPF layout passes falling behind enough
            // for the Player overlay Popup to end up looking stale) turned out
            // to be -- skip this tick entirely rather than let them stack.
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

        // 20 min, not tighter -- this walks the paired PC's ENTIRE clips
        // tree every tick (see SyncRemoteClipsAsync), so it needs to be
        // infrequent enough not to be a constant background network/disk
        // drag on a large library, while still catching up reasonably soon
        // after a clip was made while this PC was asleep/closed/unpaired.
        // An on-demand remote Gallery browse or clip open already covers the
        // "I want this specific clip right now" case instantly; this is
        // purely the "make sure nothing's silently missing" background net.
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

        // A local reload (Directory.EnumerateFiles) is cheap enough to not
        // strictly need this, but the remote gallery's own reload is a real
        // network round trip (see LoadRemoteGalleryAsync) -- without a
        // debounce, typing a whole search term would fire one request per
        // keystroke instead of one after you stop typing.
        _galleryFilterDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _galleryFilterDebounceTimer.Tick += (_, _) =>
        {
            _galleryFilterDebounceTimer.Stop();
            LoadGallery();
        };

        // One-shot: started right as playback begins, hides the freeze-frame
        // cover once decode has had time to stabilize past the glitchy start.
        _freezeFrameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _freezeFrameTimer.Tick += (_, _) =>
        {
            _freezeFrameTimer.Stop();
            PlayerFreezeFramePopup.IsOpen = false;
        };

        // Debounced close for the volume slider's hover popup: closing
        // immediately on MouseLeave meant the small visual gap between the
        // button and the popup above it (StaysOpen Popups don't share
        // hit-test area with their PlacementTarget) closed it the instant
        // the cursor crossed that gap on the way from one to the other.
        // Both PlayerVolumeArea_MouseEnter/Leave (wired to both the button
        // and the popup's own content) restart/cancel this.
        _volumePopupCloseDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _volumePopupCloseDebounce.Tick += (_, _) =>
        {
            _volumePopupCloseDebounce.Stop();
            PlayerVolumePopup.IsOpen = false;
        };

        // One-shot: fades PlayerActionFeedbackPopup out and closes it, a
        // beat after ShowPlayerActionFeedback opens it.
        _actionFeedbackHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _actionFeedbackHideTimer.Tick += (_, _) =>
        {
            _actionFeedbackHideTimer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            fadeOut.Completed += (_, _) =>
            {
                PlayerActionFeedbackPopup.IsOpen = false;
                PlayerActionFeedbackBorder.Opacity = 1; // reset for the next time this shows
            };
            PlayerActionFeedbackBorder.BeginAnimation(OpacityProperty, fadeOut);
        };

        // The window needs a real HWND immediately for RegisterHotKey and the
        // acrylic blur, but must never actually appear until the hotkey is
        // pressed -- EnsureHandle() creates it without calling Show().
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        Left = TargetScreenBounds.X + (TargetScreenBounds.Width - Width) / 2;
        Top = TargetScreenBounds.Y + CompactTop;
        Acrylic.TryEnableBlurBehind(hwnd, 16, 17, 19, 205);
        // This is a hotkey-summoned HUD, not an independent app window -- it and
        // every auxiliary overlay window (Status/Toast/Scrim/Disclaimer/Logo) were
        // showing up as five or six separate Alt+Tab entries for one app, since
        // ShowInTaskbar="False" alone doesn't affect Alt+Tab, only the taskbar.
        ToolWindow.Enable(hwnd);

        RegisterHotkeyFromSettings();

        _trayManager = new SystemTrayManager(this);
        _trayManager.OnOpenHudRequested += () => Dispatcher.BeginInvoke(ToggleVisible);
        _trayManager.OnOpenSettingsRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible) ToggleVisible();
            ShowScreen(Screen.Settings);
            // See SettingsButton_Click's own comment on why this resets to
            // the top rather than leaving whatever scroll position the
            // ScrollViewer happened to still be holding from last time.
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

        // Nothing else in this file ever reacted to Windows' resolution
        // actually changing while the app was already running -- every
        // window's Left/Top is computed from TargetScreenBounds/
        // DisplayMonitors at the moment it's shown/moved, but "the moment
        // it's shown" only happens to run again on real navigation (Show
        // Screen) or manual triggers (Settings' own Display dropdown).
        // ToggleVisible's own show path in particular just re-Shows the
        // window as-is, reusing whatever Left/Top was last computed against
        // the OLD resolution -- after a shrink, that can be entirely off the
        // new, smaller desktop, with no way back in except killing the
        // process. Fires on a non-UI thread (SystemEvents' own message-only
        // window), hence the Dispatcher hop.
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(RepositionAllForDisplayChange);

        try
        {
            LibVlc.Core.Initialize();
            // --avcodec-hw=none: disables hardware-accelerated decoding. Without this,
            // the video surface can come up as a blank white swapchain with nothing
            // ever drawn into it on machines/VMs where GPU decode acceleration isn't
            // reliably available -- software decode is slower but actually paints frames.
            // (Tried --avcodec-hw=any to see if it also fixed the ~1s glitchy startup
            // frame -- it didn't, so no reason to carry that regression risk for nothing.)
            _libVlc = new LibVlc.LibVLC("--no-video-title-show", "--avcodec-hw=none");

            // A real HWND for thumbnail-generation MediaPlayers to render into, never
            // shown (EnsureHandle creates the native window without Show() ever being
            // called, same trick MainWindow itself uses for the hotkey's HWND). Without
            // an explicit render target, libvlc creates its own floating window to
            // render into instead -- which is exactly what looked like "VLC opening in
            // the background" for every single clip while generating thumbnails.
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
        // Also run once right away on startup, not just 20 minutes from now
        // -- otherwise a clip made on the transmitter PC while this PC was
        // off wouldn't get caught up until the FIRST tick, not immediately
        // once this PC is back and paired. Goes through the same
        // _remoteSyncRunning guard as the timer itself, in case the first
        // real tick fires before this startup pass (a huge library, a slow
        // network) is even done.
        if (!string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            _remoteSyncRunning = true;
            _ = SyncRemoteClipsAsync().ContinueWith(_ => _remoteSyncRunning = false, TaskScheduler.FromCurrentSynchronizationContext());
        }
        _ = RefreshStatusAsync();
        ShowScreen(Screen.Idle);
        _ = RefreshGalleryCountAsync();
        _ = PrefetchRowLabelsAsync();
        // Starts generating/caching thumbnails immediately at launch, well before
        // the user has any reason to open Gallery -- see PrewarmGalleryThumbnailsAsync.
        _ = PrewarmGalleryThumbnailsAsync();
        // Tied to this app's own lifetime, not OBS's -- mounted here, unmounted in
        // OnClosed. No-ops immediately if the feature isn't enabled in Settings.
        _ = InitializeRamDiskAsync();

        // Self-heals the startup task if Settings says it should exist -- if the
        // app was ever renamed or moved (the exe path baked into the task's /TR
        // argument goes stale), this quietly recreates it instead of leaving
        // Settings showing "on" while the real task is broken or gone.
        if (_settings.LaunchWithWindows)
        {
            try { CreateOrUpdateStartupTask(); }
            catch (Exception ex) { Debug.WriteLine($"Startup task self-heal failed: {ex.Message}"); }
        }

        // Once, at startup -- no more recurring hourly timer. This is the one
        // moment auto-applying an update unattended is actually fine (see
        // CheckForUpdatesAsync's doc comment); every other trigger requires an
        // explicit click, either the bottom-left prompt's Install button or
        // the Settings "Check now" -> "Update" button.
        //
        // Skipped entirely on a dev build (see UpdateService.IsDevBuild): a
        // locally-compiled binary will always look "out of date" by digest
        // regardless of version string, and auto-applying here would silently
        // overwrite whatever's actively being tested with the real release.
        // The manual Settings button still works either way -- this only
        // disables the automatic/unprompted paths, not manual control.
        ShowAppStartedToast();
        if (!UpdateService.IsDevBuild)
            _ = CheckForUpdatesAsync();

        RestartAutoDeleteOldClipsTimer();
        InitializeOverlayLog();
        InitializeRecentClipsOverlay();
    }

    /// <summary>
    /// Just wires position-persistence at startup -- does NOT show the window.
    /// Shown/hidden in lockstep with the HUD itself (ToggleVisible/CloseOverlay),
    /// same as Disclaimer/Logo/StreamingStatus, not an always-on desktop
    /// fixture; MainWindow itself starts hidden until the hotkey is pressed,
    /// same reasoning applies here.
    /// </summary>
    private void InitializeRecentClipsOverlay()
    {
        _recentClipsOverlay.PositionChanged += (x, y) =>
        {
            _settings.RecentClipsOverlayX = x;
            _settings.RecentClipsOverlayY = y;
            _settings.Save();
        };
    }

    private void PositionRecentClipsOverlay()
    {
        if (_settings.RecentClipsOverlayX is double x && _settings.RecentClipsOverlayY is double y)
        {
            _recentClipsOverlay.Left = x;
            _recentClipsOverlay.Top = y;
            return;
        }

        // First time ever shown -- SizeToContent means ActualWidth/Height
        // aren't real until an actual layout pass happens, so a guessed
        // constant here (this used to be a flat 260x140) is wrong the moment
        // real tiles populate it: four 96px thumbnails plus the drag grip is
        // a lot wider than that, which is exactly why this was landing flush
        // against the corner with zero margin, partly off-screen. Positioned
        // once now with a reasonable fallback so it doesn't flash somewhere
        // silly, then corrected for real off the window's own first
        // SizeChanged once its true content size is actually measured.
        PositionInBottomRightCorner();
        void Handler(object? s, SizeChangedEventArgs e)
        {
            _recentClipsOverlay.SizeChanged -= Handler;
            PositionInBottomRightCorner();
        }
        _recentClipsOverlay.SizeChanged += Handler;
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

    /// <summary>
    /// Single choke point for "should the Recent Clips overlay be on screen
    /// right now" -- called from ShowScreen (every in-HUD navigation),
    /// ToggleVisible's show branch (reopening the HUD), and
    /// ShowRecentClipsToggle_Click (the setting flips while already on some
    /// screen). Idle-only on purpose: showing it over Settings/Gallery/
    /// Player/Save Replay/Start Record put a floating "recent clips" box on
    /// top of screens that are already ABOUT clips (Gallery/Player) or
    /// actively mid-flow (Save Replay/Start Record), which read as clutter
    /// rather than the quick-access shortcut it's meant to be.
    /// </summary>
    private void UpdateRecentClipsOverlayVisibility(Screen currentScreen)
    {
        if (!_settings.ShowRecentClipsOverlay || !IsVisible || currentScreen != Screen.Idle)
        {
            _recentClipsOverlay.Hide();
            return;
        }

        // Tiles first, then position -- their fixed 96px-per-tile structural
        // width is already correct synchronously (thumbnails load in async
        // after, but that doesn't change layout width), so positioning after
        // this avoids an extra visible jump on top of the SizeChanged
        // correction PositionRecentClipsOverlay already does for the
        // very-first-time case.
        RefreshRecentClipsOverlay();
        PositionRecentClipsOverlay();
        _recentClipsOverlay.Show();
    }

    /// <summary>
    /// Scans the whole clips folder tree (not just its root) so recordings
    /// saved into a Source Record filter's own custom destination subfolder
    /// (see RECORDINGS in Settings) show up here too, not just buffer saves
    /// landing at the root -- same filename-extension/glitch filtering
    /// LoadGallery already uses, just recursive and capped to the newest 4.
    /// No-ops entirely if the overlay's turned off; called opportunistically
    /// after every save regardless of that, cheaper to check the flag here
    /// than at every call site.
    /// </summary>
    /// <summary>
    /// Paired-as-receiver PCs care about the OTHER PC's clips, not whatever
    /// (usually empty, or just irrelevant old local test clips) happens to
    /// be in this PC's own ClipsFolder -- same "which source matters here"
    /// rule GalleryTile_Click already uses to decide local vs. remote.
    /// Previously this always scanned local disk regardless, which is
    /// exactly why the overlay showed nothing at all on a receiver PC: there
    /// was nothing local to find, and the remote side of this was simply
    /// never built.
    /// </summary>
    private void RefreshRecentClipsOverlay()
    {
        if (!_settings.ShowRecentClipsOverlay)
            return;

        if (!string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            _ = RefreshRecentClipsOverlayRemoteAsync();
            return;
        }

        try
        {
            if (!Directory.Exists(_settings.ClipsFolder))
                return;

            List<FileInfo> recent = Directory.EnumerateFiles(_settings.ClipsFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())
                            && !_pendingDeletePaths.Contains(Path.GetFullPath(f)))
                .Select(f => new FileInfo(f))
                .Where(f => TryGetCachedDurationMs(f) is not < 2000)
                .OrderByDescending(f => f.LastWriteTime)
                .Take(4)
                .ToList();

            _recentClipsOverlay.SetTiles(recent.Select(BuildRecentClipTile));
        }
        catch
        {
            // Best effort -- a floating convenience overlay isn't worth
            // surfacing an error over; it just stays showing whatever it had.
        }
    }

    /// <summary>
    /// Remote counterpart of RefreshRecentClipsOverlay's local scan --
    /// reuses the same whole-tree walk SyncRemoteClipsAsync uses (metadata
    /// only, no downloads here), just takes the 4 most recent instead of
    /// filtering down to what's missing. Best effort, same as the local
    /// path: an unreachable transmitter just leaves the overlay showing
    /// whatever it last had, no error surfaced over a floating convenience
    /// overlay.
    /// </summary>
    private async Task RefreshRecentClipsOverlayRemoteAsync()
    {
        List<(string RelativePath, RemoteGalleryFile File)>? all = await ListAllRemoteClipsAsync();
        if (all is null)
            return;

        List<(string RelativePath, RemoteGalleryFile File)> recent = all
            .Where(t => !_pendingRemoteDeletePaths.Contains(t.RelativePath))
            .OrderByDescending(t => t.File.Modified)
            .Take(4)
            .ToList();

        _recentClipsOverlay.SetTiles(recent.Select(t => BuildRecentRemoteClipTile(t.RelativePath, t.File)));
    }

    private Border BuildRecentClipTile(FileInfo file)
    {
        var thumb = new Border { Background = (Brush)FindResource("ThumbnailBg"), Width = 96, Height = 64, Cursor = Cursors.Hand, ClipToBounds = true };
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };
        thumb.Child = thumbImage;
        thumb.MouseLeftButtonUp += (_, _) => ShowMainWindowAndOpenInPlayer(file);

        var title = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 96,
            Margin = new Thickness(0, 4, 0, 0),
        };

        DateTime modified = file.LastWriteTime;
        string dateText = modified.Date == DateTime.Today ? modified.ToString("h:mm tt") : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock
        {
            Text = $"{dateText} · {FormatFileSize(file.Length)}",
            FontSize = 9.5,
            Foreground = (Brush)FindResource("Text2"),
            Width = 96,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var content = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        content.Children.Add(thumb);
        content.Children.Add(title);
        content.Children.Add(sub);

        var tile = new Border { Child = content };
        _ = LoadThumbnailAndPruneIfGlitchedAsync(file, thumbImage, tile);

        // Same three items, same order, same red Delete as the Gallery card's
        // own context menu (BuildClipCard) -- kept as a separate small build
        // here rather than sharing one helper, since this tile's compact
        // layout has no card Border/selection-circle machinery to hook into
        // the way BuildClipCard's version does.
        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var openFolderItem = new MenuItem { Header = "Open file location", Style = (Style)FindResource("DarkMenuItem") };
        openFolderItem.Click += (_, _) => RevealInExplorerAndClose(file.FullName);
        var copyPathItem = new MenuItem { Header = "Copy path", Style = (Style)FindResource("DarkMenuItem") };
        copyPathItem.Click += (_, _) => Clipboard.SetText(file.FullName);
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem"), Foreground = (Brush)FindResource("Rec") };
        deleteItem.Click += (_, _) =>
        {
            // DeleteClip's Border param is unused internally (confirmed by
            // reading it -- it only drives QueueDeleteWithUndo(file), which
            // finds/removes cards by matching FileInfo, not this reference),
            // so a throwaway one here is fine. No RefreshRecentClipsOverlay()
            // call needed here -- DeleteClip shows an async confirm dialog
            // first, so calling it right here would've fired before the user
            // even answered; QueueDeleteWithUndo itself now refreshes this
            // overlay once the delete is actually queued (see its own
            // comment), which covers every entry point, not just this one.
            DeleteClip(file, new Border());
        };
        contextMenu.Items.Add(openFolderItem);
        contextMenu.Items.Add(copyPathItem);
        contextMenu.Items.Add(deleteItem);
        thumb.ContextMenu = contextMenu;

        return tile;
    }

    /// <summary>
    /// Remote counterpart of BuildRecentClipTile above -- same compact
    /// layout, but built from a RemoteGalleryFile/relative path instead of a
    /// local FileInfo, since a receiver PC's own quick-gallery overlay has
    /// no local file to work from until one actually gets clicked (see
    /// OpenRemoteClipAsync). No "Open file location" in the context menu --
    /// nothing to reveal in Explorer for a clip that isn't necessarily
    /// downloaded yet -- and "Copy path" copies the remote-relative path
    /// (informational) rather than a local one that might not exist.
    /// </summary>
    private Border BuildRecentRemoteClipTile(string relativePath, RemoteGalleryFile file)
    {
        var thumb = new Border { Background = (Brush)FindResource("ThumbnailBg"), Width = 96, Height = 64, Cursor = Cursors.Hand, ClipToBounds = true };
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };
        thumb.Child = thumbImage;
        thumb.MouseLeftButtonUp += (_, _) => OpenRemoteClipStreaming(relativePath, file);

        var title = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 96,
            Margin = new Thickness(0, 4, 0, 0),
        };

        DateTime modified = file.Modified.ToLocalTime();
        string dateText = modified.Date == DateTime.Today ? modified.ToString("h:mm tt") : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock
        {
            Text = $"{dateText} · {FormatFileSize(file.Size)}",
            FontSize = 9.5,
            Foreground = (Brush)FindResource("Text2"),
            Width = 96,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var content = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        content.Children.Add(thumb);
        content.Children.Add(title);
        content.Children.Add(sub);

        var tile = new Border { Child = content };
        _ = LoadRemoteThumbnailAsync(relativePath, file, thumbImage);

        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var copyPathItem = new MenuItem { Header = "Copy path", Style = (Style)FindResource("DarkMenuItem") };
        copyPathItem.Click += (_, _) => Clipboard.SetText(relativePath);
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem"), Foreground = (Brush)FindResource("Rec") };
        deleteItem.Click += (_, _) => DeleteRemoteClip(relativePath, file);
        contextMenu.Items.Add(copyPathItem);
        contextMenu.Items.Add(deleteItem);
        thumb.ContextMenu = contextMenu;

        return tile;
    }

    /// <summary>
    /// RefreshRecentClipsOverlay's own pre-filter can't know a clip is
    /// glitched (sub-2s) until its real duration is actually probed, which
    /// only happens as a side effect of thumbnail generation -- same
    /// "unprobed clip shows optimistically" tradeoff LoadGallery's own
    /// comment describes for Gallery cards. Gallery gets away with leaving
    /// that stale until whatever reloads it next; this floating overlay only
    /// refreshes on a new save or the HUD reopening, so a glitched tile needs
    /// to prune itself right here instead of waiting for some other trigger.
    /// </summary>
    private async Task LoadThumbnailAndPruneIfGlitchedAsync(FileInfo file, Image thumbImage, Border tile)
    {
        await LoadThumbnailAsync(file, thumbImage);
        if (TryGetCachedDurationMs(file) is < 2000 && tile.Parent is Panel parent)
            parent.Children.Remove(tile);
    }

    private static string FormatFileSize(long bytes)
    {
        const double mb = 1024.0 * 1024.0;
        double sizeMb = bytes / mb;
        return sizeMb >= 1000 ? $"{sizeMb / 1024.0:0.#} GB" : $"{sizeMb:0.#} MB";
    }

    /// <summary>The overlay is only ever visible while the HUD itself is (see ToggleVisible/CloseOverlay), so the visibility check here is mostly defensive -- reveals the HUD first if it's somehow hidden anyway, then opens the clip. Same "only show if not already" check App.xaml.cs's own _showEvent handler uses.</summary>
    private void ShowMainWindowAndOpenInPlayer(FileInfo file)
    {
        if (!IsVisible)
            ToggleVisible();

        // Unconditional, not just inside the branch above -- the click that
        // got here fired on _recentClipsOverlay, a genuinely separate
        // top-level Window (ToolWindow.Enable only hides it from Alt+Tab/
        // taskbar, WS_EX_TOOLWINDOW; it doesn't set WS_EX_NOACTIVATE, so
        // that click still gave IT real Win32 activation), not on this
        // window, regardless of whether ToggleVisible above actually ran.
        // Nothing else in this call chain re-activates MainWindow itself --
        // ShowScreen/OpenInPlayer only change visibility/content. Without
        // this, PlayerOverlayPopup and the fullscreen transport popup (both
        // owned by MainWindow) get set up while a DIFFERENT window still
        // holds real activation, which can look fine initially but loses
        // the title/back button on the next real Z-order event (VLC's own
        // native right-click context menu stealing focus), and never shows
        // them correctly in fullscreen at all -- confirmed live: both only
        // happen opening a clip from the quick-gallery overlay, never from
        // the normal Gallery screen, which never involves a second window.
        Activate();

        OpenInPlayer(file);
        // See _playerBackTarget's own comment -- overridden AFTER OpenInPlayer
        // runs, since OpenInPlayer itself resets this to Gallery at its top.
        _playerBackTarget = Screen.Idle;
    }

    // ------------------------------------------------------------ overlay log

    private void InitializeOverlayLog()
    {
        AppLog.Write("Backtrack started");
        AppLog.Changed += () => Dispatcher.BeginInvoke(RefreshBacktrackModeLog);

        _obsStatsTimer.Tick += async (_, _) => await RefreshObsModeLogAsync();
        _obsStatsTimer.Start();
    }

    /// <summary>
    /// Call after OverlayLogEnabled/OverlayLogMode changes in Settings, and
    /// from ToggleVisible/CloseOverlay whenever the HUD itself opens or
    /// closes -- _overlayLog is only ever shown while BOTH the feature is
    /// enabled AND the HUD is currently open, same lifecycle as the update
    /// prompt (see UpdatePromptOverlay), not a persistent always-on badge.
    /// </summary>
    private void RefreshOverlayLogVisibilityAndMode()
    {
        if (!_settings.OverlayLogEnabled || !IsVisible)
        {
            _overlayLog.Hide();
            return;
        }

        bool obsMode = _settings.OverlayLogMode != "Backtrack";
        _overlayLog.Show();
        _overlayLog.SetMode(obsMode);
        if (obsMode)
            _ = RefreshObsModeLogAsync();
        else
            RefreshBacktrackModeLog();
    }

    private void RefreshBacktrackModeLog()
    {
        if (!_settings.OverlayLogEnabled || !IsVisible || _settings.OverlayLogMode != "Backtrack")
            return;

        List<string> lines = AppLog.Snapshot().Select(e => $"[{e.TimestampLocal:HH:mm:ss}] {e.Message}").ToList();
        _overlayLog.SetBacktrackLines(lines);
    }

    /// <summary>Sets the OBS-mode line and arms its auto-clear timer -- for one-off events (recording/replay saved), not the recurring overload check below, which re-asserts itself every poll instead.</summary>
    private void ShowObsModeMessage(string text)
    {
        if (!_settings.OverlayLogEnabled || !IsVisible || _settings.OverlayLogMode == "Backtrack")
            return;
        _overlayLog.SetObsLine(text);
        _obsLogClearAtUtc = DateTime.UtcNow.AddSeconds(5);
    }

    /// <summary>
    /// Polled every 2s (see _obsStatsTimer): the closest available equivalent
    /// to OBS's own status bar, built from the same underlying frame-drop
    /// counters OBS uses internally (see ObsService.GetStatsAsync) since
    /// there's no API for the literal status bar text or its exact timing.
    /// Overload warnings persist for as long as the condition keeps being true
    /// on each poll; one-off save messages (see ShowObsModeMessage) instead
    /// clear themselves after a few seconds via _obsLogClearAtUtc.
    ///
    /// The stats fetch/delta-tracking below now always runs while connected,
    /// regardless of the in-HUD overlay log's own settings -- it used to
    /// bail out immediately whenever OverlayLogEnabled/IsVisible/OverlayLogMode
    /// said not to show the text in the HUD, which also meant the underlying
    /// overload DETECTION silently stopped too (not just its display), the
    /// entire time the HUD was closed -- i.e. almost always, since this is a
    /// hotkey-summoned overlay. That's what fed the always-on status
    /// indicator's own encoder-overload badge dark: this detector, unlike
    /// obs-source-record's own vendor event (a completely separate, older
    /// detection path -- see ObsService.EncoderOverloadDetected), doesn't
    /// depend on any Source Record filter existing at all, so it's the one
    /// that actually caught a main-output overload with none active. Only
    /// the SetObsLine display calls further down still respect those
    /// HUD-only settings.
    /// </summary>
    private async Task RefreshObsModeLogAsync()
    {
        if (!_obs.IsConnected)
        {
            _overlayLog.SetObsLine("");
            return;
        }

        bool showInOverlayLog = _settings.OverlayLogEnabled && IsVisible && _settings.OverlayLogMode != "Backtrack";

        try
        {
            ObsStats stats = await _obs.GetStatsAsync();
            string? warning = ComputeObsOverloadWarning(stats);
            if (warning is not null)
            {
                // Same timestamp the vendor-event path below touches --
                // either signal means "an overload is happening right now"
                // to RefreshStatusAsync's badge check, whichever one catches
                // it first.
                _lastEncoderOverloadEventUtc = DateTime.UtcNow;
            }

            if (!showInOverlayLog)
                return;

            if (warning is not null)
            {
                _overlayLog.SetObsLine(warning);
                _obsLogClearAtUtc = null;
                return;
            }

            if (_obsLogClearAtUtc is DateTime clearAt)
            {
                if (DateTime.UtcNow < clearAt)
                    return; // still showing a recent one-off message -- leave it
                _obsLogClearAtUtc = null;
            }
            _overlayLog.SetObsLine("");
        }
        catch
        {
            // Transient request failure -- leave whatever was last shown rather
            // than flashing blank every 2s until it recovers.
        }
    }

    /// <summary>
    /// Diffs consecutive polls (not a lifetime average, which would dilute to
    /// nothing over a long session) to get a recent skipped-frame rate for
    /// rendering (GPU-side) and output/encoding separately. Encoding wins if
    /// both are simultaneously over threshold, since that's the one that
    /// actually costs you frames in the saved file.
    /// </summary>
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

    /// <summary>
    /// Check-only (no install) for one plugin -- same release fetch and
    /// ShouldApplyUpdate logic CheckAndApplyPluginUpdateAsync uses, just
    /// without ever reaching InstallPluginUpdateAsync. Used by the manual
    /// "Check now" button's first press, which is only supposed to report
    /// what's available, not act on it yet. Reusing ShouldApplyUpdate here
    /// (rather than duplicating its comparison logic) does mean its baseline-
    /// seeding side effect can fire from a check-only pass too -- harmless
    /// and correct either way, since seeding never claims an update is needed.
    /// </summary>
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

    /// <summary>Same idea as CheckPluginAvailabilityAsync, for Backtrack itself.</summary>
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
            // Backtrack checking on itself -- if this is running at all, it's
            // obviously installed; "genuinely missing" only ever applies to
            // a plugin DLL that might not be there, never to the app asking
            // about its own already-running self.
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

    /// <summary>
    /// Checks and applies updates for both companion OBS plugins and for
    /// Backtrack itself -- still deferring to the bottom-left prompt instead
    /// of installing if OBS happens to be actively recording/streaming/replaying
    /// right now (see CheckAndApplyPluginUpdateAsync/CheckAndApplySelfUpdateAsync).
    ///
    /// Plugins are checked BEFORE Backtrack itself, deliberately: applying a
    /// self-update ends in Application.Current.Shutdown() a few lines below,
    /// which would exit the process before ever reaching the plugin checks if
    /// they came after it in this same sequential method -- silently skipping
    /// them whenever Backtrack itself also happened to have an update.
    ///
    /// Auto-applying (rather than always just prompting) is only appropriate
    /// at the one moment this is safe to do completely unattended: app
    /// startup, before anything's necessarily even happened yet this session.
    /// There's no recurring hourly re-check anymore, so that startup call is
    /// the only automatic trigger left -- this same method is also reused by
    /// the manual Check now/Update button's second press, which is an
    /// explicit "yes, install it" click regardless of when it happens.
    ///
    /// Each check is independent and swallows its own failures (no network,
    /// repo has no releases yet, etc.) so one failing never blocks the
    /// others. Updates the Settings SHARING rows directly (dot + version)
    /// regardless of whether that screen is currently visible.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool isManualTrigger = false)
    {
        // _obs.Start() (constructor) kicks off connecting to OBS in the
        // background with nothing awaiting it, and this method is called
        // right after in that same constructor with no synchronization
        // between the two. CheckAndApplyPluginUpdateAsync's own safety check
        // (IsRecordingOrStreamingAsync) treats "not connected to OBS yet" the
        // same as "confirmed nothing is active" -- correct if OBS genuinely
        // isn't running, but wrong if OBS IS running and actively
        // recording/streaming RIGHT NOW and this connection attempt just
        // hasn't finished its handshake yet. In practice the
        // GitHub API round-trip inside CheckAndApplyPluginUpdateAsync usually
        // gives the local OBS handshake enough of a head start to land first
        // anyway, but "usually" isn't a real guarantee -- and the UI visibly
        // shows "Disconnected" for close to a second on a normal launch,
        // proving this race window is real, not theoretical. Give the
        // connection attempt a bounded chance to actually resolve (success or
        // "OBS isn't there") before trusting that check with something as
        // disruptive as closing OBS to install an update.
        var obsConnectDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!_obs.IsConnected && DateTime.UtcNow < obsConnectDeadline)
            await Task.Delay(100);

        // deferObsReopen: true on both -- see CheckAndApplyPluginUpdateAsync's
        // own comment on why relaunching OBS between these two instead of once
        // after both is the likely cause of the second plugin's update
        // looking like it failed right after the first one succeeded.
        await CheckAndApplyPluginUpdateAsync("obs-replay-slider", "Replay Slider", "replay-slider.dll", ReplaySliderStatusDot, ReplaySliderVersionText,
            name => name.Contains("windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
            () => _settings.LastAppliedReplaySliderReleaseAt, v => _settings.LastAppliedReplaySliderReleaseAt = v,
            () => _settings.LastAppliedReplaySliderDigest, v => _settings.LastAppliedReplaySliderDigest = v, isManualTrigger, deferObsReopen: true);
        await CheckAndApplyPluginUpdateAsync("obs-source-record", "Source Record", "source-record.dll", SourceRecordStatusDot, SourceRecordVersionText,
            name => name.Contains("windows-installer", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
            () => _settings.LastAppliedSourceRecordReleaseAt, v => _settings.LastAppliedSourceRecordReleaseAt = v,
            () => _settings.LastAppliedSourceRecordDigest, v => _settings.LastAppliedSourceRecordDigest = v, isManualTrigger, deferObsReopen: true);
        ReopenObsIfPendingFromPluginUpdates();

        // Never on a dev build (see UpdateService.IsDevBuild): a locally-compiled
        // binary's digest never matches the official release's, so this would
        // always think an update is "available" regardless of version string,
        // and applying it ends in Application.Current.Shutdown() a few lines
        // into CheckAndApplySelfUpdateAsync -- which used to kill the dev
        // process out from under whoever just wanted the plugin updates above,
        // even though those had already applied by that point. Plugins remain
        // fully updatable from a dev build; only Backtrack's own self-update is
        // off-limits here.
        if (!UpdateService.IsDevBuild)
            await CheckAndApplySelfUpdateAsync(isManualTrigger);
    }

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

    /// <summary>Green = confirmed current (already was, or just got updated); red = couldn't confirm (check failed); grey = not checked yet this session.</summary>
    private void SetUpdateStatus(System.Windows.Shapes.Ellipse dot, TextBlock versionText, string version, bool? ok)
    {
        dot.Fill = (Brush)FindResource(ok switch { true => "Green", false => "Rec", null => "Text2" });
        versionText.Text = version;
    }

    /// <summary>
    /// A version bump always wins. Beyond that, prefers comparing the release
    /// asset's own content digest (sha256, see ReleaseInfo.Digest) against
    /// whatever was recorded last time -- immune to clock skew and to any
    /// metadata-only touch that isn't an actual content change -- and only
    /// falls back to the asset's updated_at timestamp when a digest isn't
    /// available on both sides (some older assets never got one computed).
    /// Either signal catches a same-version-tag re-upload that plain
    /// version-number comparison alone would miss.
    ///
    /// First time ever checking a given repo (nothing recorded on either
    /// side) and the version already matches latest: seeds the baseline via
    /// the setters and reports "no update needed" rather than reinstalling
    /// something that's already correct.
    /// </summary>
    private bool ShouldApplyUpdate(ReleaseInfo release, bool versionBumped, bool installedFileMissing,
        Func<DateTimeOffset?> getLastApplied, Action<DateTimeOffset?> setLastApplied,
        Func<string?> getLastDigest, Action<string?> setLastDigest)
    {
        // A completely missing plugin (uninstalled, or wiped by something
        // like an OBS reinstall that doesn't preserve third-party plugin
        // files) must always reinstall, full stop -- regardless of what
        // settings.json remembers about a previously-applied digest.
        // settings.json survives independently of the plugin file itself
        // (confirmed live: it survives Backtrack's own uninstall/reinstall
        // by design, and has no way to know when something ELSE, like an
        // OBS reinstall, deletes the plugin out from under it), so a
        // matching stored digest can otherwise mask a genuinely absent
        // plugin as "up to date" forever. The digest-trust shortcut just
        // below exists for a different problem entirely -- an INSTALLED
        // binary's own self-reported version STRING lagging its real
        // content, not the binary being absent -- and was never meant to
        // cover this case; check for absence first, before it gets a
        // chance to.
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

        // A matching digest is authoritative: it proves the exact bytes of
        // this release were already installed, regardless of what the
        // installed binary's OWN self-reported version string claims.
        // versionBumped compares GitHub's tag against that self-reported
        // string (see GetInstalledPluginVersion) -- a plugin whose version-
        // embedding is out of sync with its real content (hit for real in
        // obs-source-record: CMakeLists.txt's hardcoded version lagged
        // buildspec.json for several releases) would otherwise make
        // versionBumped permanently true even once the actual fix is
        // installed, reinstalling the same already-current release every
        // single check forever -- closing/reopening OBS each time for
        // nothing. Trust a matching digest over a version-string mismatch.
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

    /// <summary>
    /// SetPendingUpdate only ever gets called to SET the one tracked deferred
    /// prompt -- nothing was clearing it once the situation resolved (applied
    /// successfully, or a later check found it already current), so a prompt
    /// shown once during a busy moment would just sit there forever afterward,
    /// even once the plugin/app was genuinely up to date. Scoped to the
    /// matching component's name so resolving one deferred update can't
    /// accidentally clear a DIFFERENT one still genuinely pending.
    /// </summary>
    private void ClearPendingUpdateIfMatches(string componentDisplayName)
    {
        if (_pendingUpdateName == componentDisplayName)
            SetPendingUpdate(null, null);
    }

    // Set true by CheckAndApplyPluginUpdateAsync whenever it closes OBS to
    // install a plugin but was told (deferReopen) not to relaunch it itself --
    // ReopenObsIfPendingFromPluginUpdates below is what actually does that,
    // once, after every plugin in a batch has had its turn. See
    // InstallPluginUpdateAsync's own comment for why relaunching per-plugin
    // instead of once per batch was the likely cause of a second plugin's
    // update looking like it failed right after the first one succeeded.
    private bool _obsReopenPendingFromPluginUpdates;

    private async Task<PluginVersionInfo> CheckAndApplyPluginUpdateAsync(string repo, string displayName, string dllFileName, System.Windows.Shapes.Ellipse dot, TextBlock versionText, Func<string, bool> assetPredicate,
        Func<DateTimeOffset?> getLastApplied, Action<DateTimeOffset?> setLastApplied, Func<string?> getLastDigest, Action<string?> setLastDigest, bool isManualTrigger = false, bool deferObsReopen = false)
    {
        // No local OBS install (e.g. a receiver-only PC paired to a transmitter's
        // OBS over the network) -- nothing to check a plugin version against and
        // nothing for a downloaded installer to install into, so skip entirely
        // instead of downloading+running an installer that just errors out.
        if (!_updates.IsObsInstalled)
        {
            SetUpdateStatus(dot, versionText, "OBS not installed", ok: null);
            return new PluginVersionInfo("OBS not installed", null);
        }

        Version installed = _updates.GetInstalledPluginVersion(dllFileName);
        try
        {
            ReleaseInfo? release = await _updates.GetLatestReleaseAsync("ilyambr", repo, assetPredicate);
            if (release?.DownloadUrl is null)
            {
                SetUpdateStatus(dot, versionText, installed.ToString(3), ok: false);
                return new PluginVersionInfo(installed.ToString(3), false);
            }

            bool versionBumped = UpdateService.IsNewer(release.Version, installed);
            if (!ShouldApplyUpdate(release, versionBumped, installed == UpdateService.MissingPluginVersion, getLastApplied, setLastApplied, getLastDigest, setLastDigest))
            {
                SetUpdateStatus(dot, versionText, installed.ToString(3), ok: true);
                ClearPendingUpdateIfMatches(displayName);
                return new PluginVersionInfo(installed.ToString(3), true);
            }

            async Task ApplyAsync()
            {
                // Defense in depth, not the primary gate below -- this is what
                // actually runs the install regardless of which of the 3 paths
                // got here (auto-check, the manual Apply button, or later
                // clicking the deferred bottom-left prompt's Install button,
                // which bypasses the primary gate entirely since it's a
                // captured closure). A livestream starting in between the
                // primary gate passing and this actually running is a tiny
                // window, but a real one -- never let a plugin update land
                // while genuinely live, full stop.
                if (await _obs.GetStreamActiveAsync())
                {
                    MessageBox.Show(this, $"You're currently livestreaming. End your stream before updating {displayName}.", "Backtrack");
                    SetUpdateStatus(dot, versionText, $"{installed.ToString(3)} (blocked -- you're livestreaming)", ok: null);
                    return;
                }
                _toastOverlay.ShowUpdateInProgress(displayName);
                bool obsWasRunning = await _updates.InstallPluginUpdateAsync(release.DownloadUrl, release.Digest, reopenAfterInstall: !deferObsReopen);
                if (deferObsReopen && obsWasRunning)
                    _obsReopenPendingFromPluginUpdates = true;
                RecordUpdateApplied(release, setLastApplied, setLastDigest);
                AppLog.Write($"{displayName} updated to {release.Version}");
                _toastOverlay.ShowUpdateApplied(displayName, release.Version);
                SetUpdateStatus(dot, versionText, release.Version, ok: true);
                ClearPendingUpdateIfMatches(displayName);
            }

            // Used for the bottom-left prompt's deferred Install button
            // specifically (SetPendingUpdate below) -- that's always a
            // standalone, independently-timed click, never part of the same
            // synchronous batch CheckForUpdatesAsync's own final
            // ReopenObsIfPendingFromPluginUpdates call covers, so this plugin
            // needs to reopen OBS for itself right after applying regardless
            // of whatever deferObsReopen this whole method was originally
            // called with.
            async Task ApplyAndReopenAsync()
            {
                await ApplyAsync();
                ReopenObsIfPendingFromPluginUpdates();
            }

            // Plugin updates specifically (not Backtrack's own self-update --
            // see CheckAndApplySelfUpdateAsync, deliberately untouched by this)
            // never apply at all while actually livestreaming, full stop --
            // not deferred-with-a-forceable-Install-button the way the
            // recording/replay case below is, an explicit block instead. An
            // explicit trigger (isManualTrigger -- the Settings Apply button,
            // or a paired PC's remote request) gets told why directly; the
            // silent automatic check just shows the same status text without
            // interrupting with a dialog.
            if (await _obs.GetStreamActiveAsync())
            {
                if (isManualTrigger)
                {
                    MessageBox.Show(this, $"You're currently livestreaming. End your stream before updating {displayName}.", "Backtrack");
                }
                SetUpdateStatus(dot, versionText, $"{installed.ToString(3)} (blocked -- you're livestreaming)", ok: null);
                return new PluginVersionInfo(installed.ToString(3), null);
            }

            // Installing a plugin update means closing OBS out from under
            // whatever it's doing (InstallPluginUpdateAsync's CloseObsIfRunningAsync)
            // -- never worth it mid-recording/replay without asking first
            // either, just not as absolute a rule as streaming above. Deferred
            // to the bottom-left prompt instead, so the user can force it
            // through right now if they'd rather do that (ApplyAsync's own
            // livestream re-check above still applies if streaming started by
            // the time that Install button actually gets clicked).
            if (await _obs.IsRecordingOrStreamingAsync())
            {
                SetUpdateStatus(dot, versionText, $"{installed.ToString(3)} (waiting for OBS)", ok: null);
                SetPendingUpdate(displayName, () => _ = ApplyAndReopenAsync());
                return new PluginVersionInfo(installed.ToString(3), null);
            }

            // Settings > "Disable OBS plugin auto-updates". Same shape as the
            // OBS-busy deferral just above -- SetPendingUpdate still surfaces
            // that an update exists (via the bottom-left prompt) and it's
            // still forceable through right there, this just stops it from
            // applying itself unattended. isManualTrigger (the Settings
            // "Check now" button) always bypasses this, same as it already
            // bypasses the version/digest staleness check above.
            if (!isManualTrigger && _settings.DisablePluginAutoUpdate)
            {
                SetUpdateStatus(dot, versionText, $"{installed.ToString(3)} (update available)", ok: null);
                SetPendingUpdate(displayName, () => _ = ApplyAndReopenAsync());
                return new PluginVersionInfo(installed.ToString(3), null);
            }

            await ApplyAsync();
            return new PluginVersionInfo(release.Version, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check/apply failed for {repo}: {ex.Message}");
            AppLog.WriteError($"Update check/apply failed for {repo}", ex);
            // In case this failed after ShowUpdateInProgress already showed
            // (e.g. InstallPluginUpdateAsync itself threw) -- otherwise that
            // toast would sit there claiming to still be updating forever,
            // never replaced by ShowUpdateApplied since this one never
            // reached it. No-op if it never got that far.
            _toastOverlay.ClearUpdateInProgress(displayName);
            SetUpdateStatus(dot, versionText, installed.ToString(3), ok: false);
            return new PluginVersionInfo(installed.ToString(3), false);
        }
    }

    /// <summary>Call once, after every plugin in a deferObsReopen batch has had its turn (see CheckAndApplyPluginUpdateAsync's deferObsReopen param). No-op if nothing in the batch actually closed OBS.</summary>
    private void ReopenObsIfPendingFromPluginUpdates()
    {
        if (!_obsReopenPendingFromPluginUpdates)
            return;
        _obsReopenPendingFromPluginUpdates = false;
        _updates.RelaunchObsIfInstalled();
    }

    private async Task CheckAndApplySelfUpdateAsync(bool isManualTrigger = false)
    {
        Version installed = UpdateService.CurrentAppVersion;
        try
        {
            // "win", not "windows" -- the automated release script's actual asset
            // name is "Backtrack-v{version}-win-x64.zip", which the old
            // "windows"-substring check never matched, so this always fell
            // through to the red/not-found branch below regardless of whether
            // the installed version actually was current.
            ReleaseInfo? release = await _updates.GetLatestReleaseAsync("ilyambr", "backtrack",
                name => name.Contains("win", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (release?.DownloadUrl is null)
            {
                SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, installed.ToString(3), ok: false);
                return;
            }

            bool versionBumped = UpdateService.IsNewer(release.Version, installed);
            // Same reasoning as CheckSelfAvailabilityAsync's own call --
            // Backtrack checking on itself is never "genuinely missing".
            if (!ShouldApplyUpdate(release, versionBumped, installedFileMissing: false,
                    () => _settings.LastAppliedBacktrackReleaseAt, v => _settings.LastAppliedBacktrackReleaseAt = v,
                    () => _settings.LastAppliedBacktrackDigest, v => _settings.LastAppliedBacktrackDigest = v))
            {
                SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, installed.ToString(3), ok: true);
                ClearPendingUpdateIfMatches("Backtrack");
                return;
            }

            async Task ApplyAsync()
            {
                _toastOverlay.ShowUpdateInProgress("Backtrack");
                RecordUpdateApplied(release, v => _settings.LastAppliedBacktrackReleaseAt = v, v => _settings.LastAppliedBacktrackDigest = v);
                AppLog.Write($"Backtrack updating to {release.Version} (relaunching)");
                await _updates.ApplySelfUpdateAsync(release.DownloadUrl, release.Version, release.Digest);
                SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, release.Version, ok: true);
                // The helper script above is now waiting for this process to exit --
                // shut down cleanly so it can finish the swap and relaunch.
                Application.Current.Shutdown();
            }

            // Applying a self-update shuts Backtrack down mid-swap -- fine on its
            // own, but not something to do while a recording/stream/replay is
            // actively relying on this instance's hotkey/tray/overlay without
            // asking first. Deferred to the bottom-left prompt instead, so the
            // user can force it through right now if they'd rather do that.
            if (await _obs.IsRecordingOrStreamingAsync())
            {
                SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, $"{installed.ToString(3)} (waiting for OBS)", ok: null);
                SetPendingUpdate("Backtrack", () => _ = ApplyAsync());
                return;
            }

            // Settings > "Disable Backtrack auto-updates" -- same shape as the
            // OBS-busy deferral just above; see CheckAndApplyPluginUpdateAsync's
            // own identical gate for the fuller reasoning.
            if (!isManualTrigger && _settings.DisableBacktrackAutoUpdate)
            {
                SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, $"{installed.ToString(3)} (update available)", ok: null);
                SetPendingUpdate("Backtrack", () => _ = ApplyAsync());
                return;
            }

            await ApplyAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Self-update check/apply failed: {ex.Message}");
            AppLog.WriteError("Self-update check/apply failed", ex);
            // Same reasoning as CheckAndApplyPluginUpdateAsync's own catch --
            // only matters if ApplySelfUpdateAsync threw before reaching
            // Shutdown(); a genuine success never gets here at all.
            _toastOverlay.ClearUpdateInProgress("Backtrack");
            SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, installed.ToString(3), ok: false);
        }
    }

    // ------------------------------------------------------------- RAM disk

    /// <summary>
    /// Installs the ImDisk driver if it isn't already present (one UAC prompt,
    /// only ever needed once), then mounts the RAM disk. Safe to call more than
    /// once (e.g. from the Settings toggle mid-session, not just at startup) --
    /// Mount() itself re-mounts cleanly if something's already sitting on that
    /// drive letter. Returns immediately as a no-op if the feature is off.
    /// </summary>
    private async Task InitializeRamDiskAsync()
    {
        if (!_settings.RamDiskEnabled)
            return;

        (bool ok, string? error) = await Task.Run(EnsureRamDiskReady);
        RefreshRamDiskStatusText();

        if (!ok)
        {
            // Was silently swallowed into Debug output only, so a failure here
            // looked indistinguishable from success -- "nothing happened" from the
            // user's side, with no indication anything had even been attempted.
            Debug.WriteLine($"RAM disk setup failed: {error}");
            MessageBox.Show(this, $"Couldn't set up the RAM disk: {error}", "Backtrack");
            return;
        }

        // OBS has no API to set the Replay Buffer's own output path -- this is a
        // one-time nudge to point it at the mounted drive by hand, not something
        // shown on every launch once the user's already done it.
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

    /// <summary>Tells replay-slider's dock to move trimmed clips off the RAM disk onto wherever this app's own clips actually live.</summary>
    private async Task PushRamDiskDestDirAsync()
    {
        try
        {
            await _obs.SetReplayDestDirAsync(_settings.ClipsFolder);
        }
        catch
        {
            // Needs the plugin's set_dest_dir bridge request -- an older plugin
            // build just fails this call harmlessly; the dock's own "Move clips
            // to:" field still works as a one-time manual fallback either way.
        }
    }

    /// <summary>
    /// Undoes PushRamDiskDestDirAsync (and any per-row override that was also
    /// pointed at the RAM disk) when the RAM disk gets turned off, so clips
    /// don't keep getting routed at a drive letter that's about to stop
    /// existing. Only touches rows actually pointed at the RAM disk drive --
    /// a row the user deliberately sent somewhere else on purpose (unrelated
    /// to the RAM disk) is left alone.
    ///
    /// This is the plugin-mediated post-save move step, NOT OBS's own native
    /// Replay Buffer output path (Settings > Output > Replay Buffer) -- OBS
    /// exposes no API for that one (obs-websocket's SetRecordDirectory only
    /// covers the Recording output, not Replay Buffer), so that side still
    /// needs the same one-time manual flip back, same as enabling it did.
    /// </summary>
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
            // Same story as PushRamDiskDestDirAsync -- older plugin builds just
            // fail these calls harmlessly.
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

    private void OverlayLogToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.OverlayLogEnabled = OverlayLogToggle.IsChecked == true;
        _settings.Save();
        OverlayLogModeFields.Visibility = _settings.OverlayLogEnabled ? Visibility.Visible : Visibility.Collapsed;
        RefreshOverlayLogVisibilityAndMode();
    }

    private void OverlayLogModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.OverlayLogMode = OverlayLogModeSelector.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "Obs";
        _settings.Save();
        RefreshOverlayLogVisibilityAndMode();
    }

    /// <summary>Shows current usage against the configured limit, or just "Off" -- same idea as RefreshRamDiskStatusText.</summary>
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

    private void ApplyStorageLimit_Click(object sender, RoutedEventArgs e)
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

    private void AutoDeleteOldClipsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoDeleteOldClipsEnabled = AutoDeleteOldClipsToggle.IsChecked == true;
        _settings.Save();
        AutoDeleteOldClipsFields.Visibility = _settings.AutoDeleteOldClipsEnabled ? Visibility.Visible : Visibility.Collapsed;
        RestartAutoDeleteOldClipsTimer();
    }

    private void ApplyAutoDeleteOldClips_Click(object sender, RoutedEventArgs e)
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

    private async void ApplyRamDiskSettings_Click(object sender, RoutedEventArgs e)
    {
        string driveText = RamDiskDriveBox.Text.Trim().TrimEnd(':');
        if (driveText.Length != 1 || !char.IsLetter(driveText[0]))
        {
            MessageBox.Show(this, "Drive letter must be a single letter, e.g. R.", "Backtrack");
            return;
        }
        if (!int.TryParse(RamDiskSizeBox.Text.Trim(), out int sizeMb) || sizeMb < 256)
        {
            MessageBox.Show(this, "Size must be a number of megabytes, at least 256.", "Backtrack");
            return;
        }

        await ApplyRamDiskConfigAsync(_settings.RamDiskEnabled, char.ToUpperInvariant(driveText[0]), sizeMb);
    }

    /// <summary>
    /// Applies a full RAM disk configuration: unmounts if the drive/size actually
    /// changed (or it's being turned off), saves, re-mounts if enabled, and pushes
    /// the result to OBS. Shared by the two local UI handlers above AND by
    /// PairingService's remote RAM disk control (see the constructor's
    /// _pairing.ApplyRamDiskSnapshot wiring), which invokes this directly from its
    /// own network-handling thread rather than a UI event -- hence the explicit
    /// Dispatcher hops around every UI touch instead of assuming we're already on it.
    /// </summary>
    private async Task<(bool Success, string? Error)> ApplyRamDiskConfigAsync(bool enabled, char driveLetter, int sizeMb)
    {
        char oldDrive = _settings.RamDiskDriveLetter;
        bool driveOrSizeChanged = oldDrive != driveLetter || sizeMb != _settings.RamDiskSizeMb;

        // Off the UI thread -- Mount/Unmount shell out to imdisk.exe and block on
        // it, which used to freeze the whole window if that process took any real
        // time (or hit the redirect-read deadlock; see RunImDisk).
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
            // Points the plugin's shared dest-dir and any per-row override that
            // was pointed at the RAM disk back at ClipsFolder -- see
            // RevertRamDiskDestDirsAsync for why this can't cover OBS's own
            // native Replay Buffer output path too.
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

    /// <summary>Collapsed by default -- clicking the "EXPERIMENTAL" header just flips ExperimentalContent's visibility and the arrow glyph to match.</summary>
    private void ExperimentalHeader_Click(object sender, MouseButtonEventArgs e)
    {
        bool expand = ExperimentalContent.Visibility != Visibility.Visible;
        ExperimentalContent.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        ExperimentalHeaderText.Text = expand ? "▾ EXPERIMENTAL" : "▸ EXPERIMENTAL";
    }

    /// <summary>Same collapsed-by-default header pattern as ExperimentalHeader_Click above.</summary>
    private void DestructiveHeader_Click(object sender, MouseButtonEventArgs e)
    {
        bool expand = DestructiveContent.Visibility != Visibility.Visible;
        DestructiveContent.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        DestructiveHeaderText.Text = expand ? "▾ DESTRUCTIVE" : "▸ DESTRUCTIVE";
    }

    /// <summary>
    /// Resets settings.json AND clears the generated-thumbnail cache next to
    /// it -- "settings cache" here means both things living under
    /// %AppData%/%LocalAppData%\Backtrack, not just the thumbnails. Doesn't
    /// touch clips. Restarts the app immediately after, since a live reset
    /// would otherwise mean manually re-syncing every already-bound Settings
    /// control (hotkey, theme, pairing, OBS connection, ...) by hand instead
    /// of just letting a fresh launch's AppSettings.Load() do it correctly.
    /// </summary>
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
                        // Best-effort per file, not all-or-nothing -- a
                        // thumbnail mid-generation right now can be briefly
                        // locked; that's not a reason to leave the rest.
                        try { File.Delete(f); } catch { /* best effort */ }
                    }
                }

                AppSettings.ClearSavedFile();

                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    try { Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true }); }
                    catch { /* best effort -- worst case the user relaunches manually */ }
                }
                Application.Current.Shutdown();
            });
    }

    private void ClearClipsDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        string clipsFolder = _settings.ClipsFolder;
        if (string.IsNullOrWhiteSpace(clipsFolder) || !Directory.Exists(clipsFolder))
        {
            MessageBox.Show(this, "Your clips folder isn't set or doesn't exist.", "Backtrack");
            return;
        }

        List<string> clipFiles;
        try
        {
            // VideoExtensions (.mp4/.mkv/.flv/.mov -- see GalleryFormats), the
            // exact same list the Gallery itself uses to decide what's a clip
            // vs. anything else that happens to be sitting in this folder. A
            // recursive scan filtered to just that list, not a folder delete,
            // is what keeps this from touching subfolders or non-clip files
            // someone might have stored in their clips folder for their own
            // reasons -- deleting only what Backtrack itself put there.
            clipFiles = Directory.EnumerateFiles(clipsFolder, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't read the clips folder: {ex.Message}", "Backtrack");
            return;
        }

        if (clipFiles.Count == 0)
        {
            MessageBox.Show(this, "No clips found in your clips folder.", "Backtrack");
            return;
        }

        ShowConfirmDialog(
            $"Permanently delete {clipFiles.Count} clip(s) from \"{clipsFolder}\"? " +
            "Only the clip files will be deleted. Folders, other file types, and subfolders will not be affected.",
            "Delete clips",
            confirmed =>
            {
                if (!confirmed) return;
                int failed = 0;
                foreach (string f in clipFiles)
                {
                    try { File.Delete(f); }
                    catch { failed++; }
                }
                LoadGallery();
                if (failed > 0)
                    MessageBox.Show(this, $"{failed} clip(s) couldn't be deleted (in use, or permissions). The rest were removed.", "Backtrack");
            });
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
                // The wrapper process is now waiting for THIS process to exit before it
                // actually deletes anything -- Shutdown, not just closing the window, so
                // that wait doesn't hang around forever if some other window/overlay is
                // still keeping the app alive.
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

    private void SuggestRamDiskSize_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RamDiskTargetMinutesBox.Text.Trim(), out int minutes) || minutes <= 0)
        {
            MessageBox.Show(this, "Enter a number of minutes first.", "Backtrack");
            return;
        }

        ReplayBufferSizing.Estimate? estimate = ReplayBufferSizing.TryEstimate(minutes);
        if (estimate is null)
        {
            MessageBox.Show(this, "Couldn't read OBS's config to estimate this -- enter a size manually.", "Backtrack");
            return;
        }

        RamDiskSizeBox.Text = estimate.Value.SuggestedSizeMb.ToString();
        MessageBox.Show(this,
            $"Suggested {estimate.Value.SuggestedSizeMb} MB for a {minutes}-minute buffer, based on {estimate.Value.Source} (~{estimate.Value.AssumedBitrateKbps} kbps).\n\n" +
            "Click \"Save & apply\" to actually use it.",
            "Backtrack");
    }

    private void BufferDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RefreshBufferDurationUi();

    /// <summary>
    /// Updates the value label and, using the same bitrate-estimate logic as
    /// the RAM disk size suggester, warns if a full flush at this length
    /// likely won't fit the *currently configured* RAM disk size. This is an
    /// estimate based on OBS's main output bitrate config, not each Source
    /// Record filter's own (possibly different) encoder settings -- it can be
    /// off for a filter with an unusually high or low bitrate of its own.
    /// </summary>
    private void RefreshBufferDurationUi()
    {
        // The Slider's Minimum/Maximum being set during InitializeComponent()
        // coerces its Value and fires ValueChanged immediately -- before the
        // constructor body below InitializeComponent() has assigned _settings.
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

    private void RegisterHotkeyFromSettings()
    {
        try
        {
            _hotkey = new GlobalHotkey(this, (GlobalHotkey.Modifiers)_settings.HotkeyModifiers, (uint)_settings.HotkeyVirtualKey);
            _hotkey.Pressed += () => Dispatcher.Invoke(ToggleVisible);
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Hotkey registration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// So a "replay saved" toast can show a real row name even if a save
    /// happens via the game's own hotkey without the Save Replay screen ever
    /// having been opened this session.
    /// </summary>
    private async Task PrefetchRowLabelsAsync()
    {
        if (!_obs.IsConnected)
            return;
        try
        {
            foreach (ReplayRow row in await _obs.ListReplayRowsAsync())
                _rowLabels[row.Key] = row.Label;
        }
        catch
        {
            // Fine -- the toast just falls back to showing the row key instead of its label.
        }
    }

    /// <summary>
    /// Returns (url, password, serverEnabledAtStartup). Local mode reads this
    /// PC's own obs-websocket config so the password never needs typing;
    /// remote mode (OBS on a different PC) has no way to see that machine's
    /// config, so host/port/password all come from Settings instead, and
    /// "serverEnabledAtStartup" is just assumed true since we can't check it
    /// up front.
    /// </summary>
    private (string Url, string? Password, bool ServerEnabledAtStartup) ResolveObsConnection()
    {
        if (_settings.ObsIsRemote)
            return ($"ws://{_settings.ObsHost}:{_settings.ObsPort}", _settings.ObsRemotePassword, true);

        (bool enabled, string? password) = ObsConfigReader.ReadLocalConfig();
        return ("ws://127.0.0.1:4455", password, enabled);
    }

    /// <summary>
    /// The one path that closes the whole HUD, not just MainWindow -- the Scrim is a
    /// full-screen, non-click-through window (that's the point: it blocks clicks to
    /// the game underneath while the HUD is open), so if it's left showing after
    /// MainWindow hides, the user is stuck staring at a dark screen that eats every
    /// click with no way back out. Both the hotkey-close path and the Scrim's own
    /// background-click/X-button dismissal must go through this, not just Hide().
    ///
    /// Also tears down the LibVLC player if the Player screen was open: VideoView's
    /// video surface is a real, separate top-level OS window (not a true WPF child --
    /// that's how it dodges the "airspace" problem), so simply hiding MainWindow does
    /// NOT hide it. Left running, it's an orphaned always-on-top window with nothing
    /// left to route clicks to it -- exactly the "locked out, had to Alt+F4" bug.
    /// </summary>
    private ConfirmDialog? _activeConfirmDialog;

    private bool IsCriticalOperationActive()
    {
        bool isRenaming = _isRenamingCard || _isPlayerRenaming;
        bool isTrimming = (TrimPanel != null && TrimPanel.Visibility == Visibility.Visible) || _trimStart.HasValue || _trimEnd.HasValue || _isTrimming;
        bool isSelectingClips = _selectedClipPaths.Count > 0;
        bool isDialogActive = _activeConfirmDialog != null && _activeConfirmDialog.IsLoaded;

        return isRenaming || isTrimming || isSelectingClips || isDialogActive;
    }

    private void ShowConfirmDialog(string message, string confirmButtonText, Action<bool> callback)
    {
        _activeConfirmDialog?.Close();
        _activeConfirmDialog = ConfirmDialog.ShowNonModal(this, message, confirmButtonText, confirmed =>
        {
            _activeConfirmDialog = null;
            callback(confirmed);
        });
    }

    /// <param name="preserveScreen">
    /// Skip the usual reset-to-Idle (even when nothing critical is in
    /// progress) and leave whatever screen/panel is currently showing
    /// exactly as it is -- used by RevealInExplorerAndClose and
    /// PlayerFolder_Click so reopening the overlay lands back on the same
    /// screen (and, for Gallery, the same subfolder) instead of bouncing to
    /// Idle. The whole point of that action is a quick trip out to Explorer
    /// and back, not "I'm done with the overlay for now." Callers must
    /// already have switched away from Player before passing true -- Player
    /// doesn't survive a hide/show round trip (see PlayerFolder_Click).
    /// </param>
    /// <summary>
    /// Fades a window's Opacity from 0 to 1 then Show()s it -- a plain WPF
    /// DoubleAnimation, not the native AnimateWindow trick tried earlier this
    /// session, which only ever half-worked (didn't reliably blend on every
    /// setup) and got reverted. This works because MainWindow (and Scrim,
    /// already AllowsTransparency="True") is a genuinely layered window now
    /// -- Window.Opacity animation is the actual textbook-supported use case
    /// for that, unlike a non-layered window silently ignoring it.
    /// </summary>
    // See FadeWindowOut's own comment for why useCache exists at all -- Player
    // never needs useCache:false here, since it doesn't survive a hide/show
    // round trip (see PlayerFolder_Click's own comment), so it's never the
    // active panel at the moment a fade-in starts.
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

    /// <summary>
    /// Fades a window's Opacity to 0, then Hide()s it and resets Opacity back
    /// to 1 -- so the NEXT FadeWindowIn call starts from a clean, fully-
    /// opaque state rather than wherever this fade-out happened to leave it.
    /// onCompleted (optional) runs right after that, still only if the fade
    /// actually reached 0 naturally -- see CloseOverlay's own call site for
    /// why that matters (a reopen mid-fade replaces this animation instead
    /// of letting it finish, so onCompleted correctly never runs then).
    ///
    /// useCache mirrors PrepareAnimatePanelIn's own useCache: animating just
    /// Window.Opacity still means re-rendering the FULL window content on
    /// every single frame the layered window pushes -- caching window.Content
    /// once (a BitmapCache on RootBorder, or Scrim's own root Grid) and
    /// alpha-blending that cached bitmap instead is what makes the fade
    /// actually smooth rather than the window just snapping away once
    /// whatever handful of frames DID make it through finish dropping.
    /// Default true; CloseOverlay passes false specifically when closing
    /// from Player, since VLC's native HWND is still attached and playing
    /// throughout this fade (ShowScreen's own screen-swap, which would call
    /// StopPlayerPlayback, is deferred until onCompleted) -- same "airspace"
    /// incompatibility as PrepareAnimatePanelIn's own Player exclusion.
    /// </summary>
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
        // preserveScreen (and IsCriticalOperationActive below) can both skip
        // the ShowScreen call that would otherwise stop this -- covered
        // directly here too so closing the HUD mid-autoscroll can never
        // leave CompositionTarget.Rendering ticking against a hidden window.
        StopSettingsAutoscroll();

        if (!_settings.EnableAnimations)
        {
            // Instant path, matching how this worked before the
            // AllowsTransparency="True" experiment entirely -- no fade, no
            // deferred swap, the panel reset (if any) just happens
            // synchronously up front since there's no animation for it to
            // race against.
            if (preserveScreen)
            {
                // Deliberately don't touch ShowScreen/_lastScreen at all here --
                // Gallery's _currentGalleryFolder and Player's _currentPlayerFile
                // are untouched by anything in this path, so leaving the current
                // panel as-is is sufficient; ToggleVisible's reopen path doesn't
                // call ShowScreen either, so whatever's still active just
                // reappears exactly as it was.
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
            // The actual panel swap (ShowScreen) is deferred into
            // FadeWindowOut's own onCompleted below instead of happening up
            // front -- doing it before the fade even started meant the fade
            // was visibly fading OUT IDLE'S content, not whatever screen the
            // user was actually just looking at (Gallery/Settings/Player):
            // the swap itself is instant (no UpdateLayout to soften it, see
            // ShowScreen's skipEntranceAnimation comment), so it read as
            // "everything disappears but the idle panel stays for a few
            // frames" instead of a clean fade of the real screen. Deferring
            // it to onCompleted means the swap only ever happens once the
            // window is already Hide()'d, so it's genuinely invisible.
            // Whether/what to reset to is still decided NOW (not 150ms from
            // now), just not actually APPLIED yet.
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

            // Tiles panel deliberately faster than the scrim -- the dimmed
            // backdrop lingering a beat longer while the tiles themselves have
            // already snapped away reads as intentional (a graceful undim), not
            // as the tiles being "stuck".
            FadeWindowOut(_scrim);
        }

        _disclaimer.Hide();
        _logo.Hide();
        _streamingStatus.Hide();
        _recentClipsOverlay.Hide();
        _toastOverlay.UpdatePosition(false);
        _updatePrompt.HidePrompt();
        RefreshOverlayLogVisibilityAndMode();

        // See StatusOverlay.IsHudOpen's own comment -- back to normal
        // taskbar-avoiding behavior (or the fullscreen-app check) now that
        // the HUD itself is no longer the reason to ignore the taskbar.
        _statusOverlay.IsHudOpen = false;
        _statusOverlay.Reposition();
    }

    private static void RevealInExplorer(string filePath) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });

    private void RevealInExplorerAndClose(string filePath)
    {
        RevealInExplorer(filePath);
        StopPlayerPlayback();
        CloseOverlay(preserveScreen: true);
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
                // Matches BackToGallery_Click's own identical branch: Escape
                // cancels the trim first, same as fullscreen's own
                // one-press-just-backs-out-of-the-sub-mode precedent below.
                TrimCancel_Click(sender, e);
            }
            else if (_isPlayerFullscreen)
            {
                // Standard fullscreen-video expectation: Escape backs out of
                // fullscreen first, not straight out of the whole overlay.
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

    /// <summary>
    /// YouTube-style playback shortcuts: Space/K play-pause, Left/Right and
    /// J/L seek, Home/End jump to start/end, 0-9 jump to that tenth of the
    /// clip, M mute, F fullscreen. Active only while Player is the visible
    /// screen, and only when nothing is capturing text input -- checked via
    /// both _isPlayerRenaming (the in-place title rename) and a general
    /// focused-TextBox check, since letter keys like J/K/L/M/F would
    /// otherwise get hijacked from someone just typing a new clip name.
    /// Routes every seek through CommitSeek, not a bare _vlcPlayer.Time
    /// write, so a shortcut pressed right after a clip finishes gets the
    /// same Stop()+Play() revival RestartEndedPlayback handles for mouse
    /// seeks -- see CommitSeek's own comment on why that's needed at all.
    ///
    /// Left/Right and J/L step by a FRACTION of the clip's own length, not a
    /// flat 5s/10s -- Backtrack's clips are often well under a minute (this
    /// whole session's own test clips ran 9-30s), where a flat 5s jump can
    /// eat most or all of the clip in one press, making fine seeking
    /// impossible. Clamped at both ends so it doesn't go to 0 on a
    /// near-instant clip or absurd on a long recording.
    /// </summary>
    private void HandlePlayerKeyboardShortcut(KeyEventArgs e)
    {
        if (PlayerPanel.Visibility != Visibility.Visible || _isPlayerRenaming || _vlcPlayer is null)
            return;
        if (Keyboard.FocusedElement is TextBox)
            return;

        long currentMs = _vlcPlayer.Time;
        long lengthMs = _vlcPlayer.Length;
        long shortSeekMs = Math.Clamp((long)(lengthMs * 0.05), 1000, 15000);
        long longSeekMs = Math.Clamp((long)(lengthMs * 0.10), 2000, 30000);

        switch (e.Key)
        {
            case Key.Space:
            case Key.K:
                bool wasPlaying = _vlcPlayer.IsPlaying;
                PlayPauseButton_Click(this, e);
                ShowPlayerActionFeedback(wasPlaying ? PlayerFeedbackIcon.Pause : PlayerFeedbackIcon.Play);
                break;
            case Key.Left:
                CommitSeek(Math.Max(0, currentMs - shortSeekMs));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekBack, $"-{shortSeekMs / 1000.0:0.#}s");
                break;
            case Key.Right:
                CommitSeek(Math.Min(lengthMs, currentMs + shortSeekMs));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, $"+{shortSeekMs / 1000.0:0.#}s");
                break;
            case Key.J:
                CommitSeek(Math.Max(0, currentMs - longSeekMs));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekBack, $"-{longSeekMs / 1000.0:0.#}s");
                break;
            case Key.L:
                CommitSeek(Math.Min(lengthMs, currentMs + longSeekMs));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, $"+{longSeekMs / 1000.0:0.#}s");
                break;
            case Key.Home:
                CommitSeek(0);
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekBack, "Start");
                break;
            case Key.End:
                CommitSeek(Math.Max(0, lengthMs - 1));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, "End");
                break;
            case Key.M:
                _vlcPlayer.Mute = !_vlcPlayer.Mute;
                UpdateVolumeIcon();
                ShowPlayerActionFeedback(_vlcPlayer.Mute ? PlayerFeedbackIcon.Mute : PlayerFeedbackIcon.Volume,
                    _vlcPlayer.Mute ? "Muted" : $"{_vlcPlayer.Volume}%");
                break;
            case Key.Up:
                // Through the slider's own Value, not _vlcPlayer.Volume
                // directly -- PlayerVolumeSlider_ValueChanged already sets
                // the volume, un-mutes if it was muted, and updates the
                // icon, so driving it from here keeps the slider's own
                // displayed position in sync too rather than duplicating
                // that logic a second time.
                PlayerVolumeSlider.Value = Math.Min(100, PlayerVolumeSlider.Value + 5);
                ShowPlayerActionFeedback(PlayerFeedbackIcon.Volume, $"{(int)PlayerVolumeSlider.Value}%");
                break;
            case Key.Down:
                PlayerVolumeSlider.Value = Math.Max(0, PlayerVolumeSlider.Value - 5);
                ShowPlayerActionFeedback(PlayerFeedbackIcon.Volume, $"{(int)PlayerVolumeSlider.Value}%");
                break;
            case Key.F:
                ToggleFullscreen_Click(this, e);
                break;
            case Key.D0 or Key.NumPad0: CommitSeek(0); break;
            case Key.D1 or Key.NumPad1: CommitSeek(lengthMs * 1 / 10); break;
            case Key.D2 or Key.NumPad2: CommitSeek(lengthMs * 2 / 10); break;
            case Key.D3 or Key.NumPad3: CommitSeek(lengthMs * 3 / 10); break;
            case Key.D4 or Key.NumPad4: CommitSeek(lengthMs * 4 / 10); break;
            case Key.D5 or Key.NumPad5: CommitSeek(lengthMs * 5 / 10); break;
            case Key.D6 or Key.NumPad6: CommitSeek(lengthMs * 6 / 10); break;
            case Key.D7 or Key.NumPad7: CommitSeek(lengthMs * 7 / 10); break;
            case Key.D8 or Key.NumPad8: CommitSeek(lengthMs * 8 / 10); break;
            case Key.D9 or Key.NumPad9: CommitSeek(lengthMs * 9 / 10); break;
            default:
                return; // Not one of ours -- leave e.Handled alone, let it bubble normally.
        }

        e.Handled = true;
    }

    /// <summary>
    /// Called once, from App.xaml.cs's startup firewall-setup background task,
    /// after Interop.FirewallRules.AddRulesElevated finishes. Mutates and saves
    /// THIS window's own _settings instance -- not a separate AppSettings.Load()
    /// -- since every other setting this window ever saves goes through this
    /// same instance; saving a different copy would just get clobbered back to
    /// false by the next unrelated _settings.Save() elsewhere in this file (see
    /// App.xaml.cs's own comment at the call site for the full story). Hops
    /// onto the UI thread since it's called from a background thread and
    /// _settings is otherwise only ever touched from here.
    /// </summary>
    public void MarkFirewallRulesAttempted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _settings.FirewallRulesAttempted = true;
            _settings.Save();
        });
    }

    public void ToggleVisible()
    {
        if (IsVisible)
        {
            CloseOverlay();
        }
        else
        {
            if (_settings.EnableAnimations)
            {
                FadeWindowIn(_scrim);
                _logo.ShowWithIntro();
                FadeWindowIn(this);
            }
            else
            {
                _scrim.Show();
                _logo.ShowWithIntro();
                Show();
            }
            Activate();
            // Set (and re-Reposition()'d) BEFORE Show() below -- see
            // StatusOverlay.IsHudOpen's own comment: while the HUD is open
            // this alone forces the indicator to the true screen edge,
            // taskbar or not, so it needs to already be true by the time
            // the window actually becomes visible, not caught up to on the
            // next 300ms poll tick.
            _statusOverlay.IsHudOpen = true;
            _statusOverlay.Reposition();
            if (_settings.ShowStatusIndicator)
            {
                _statusOverlay.Show();
                WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(_statusOverlay).Handle);
            }
            _toastOverlay.Show();
            _toastOverlay.UpdatePosition(true);
            WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(_toastOverlay).Handle);
            RefreshUpdatePromptVisibility();
            RefreshOverlayLogVisibilityAndMode();

            if (_settings.ShowDisclaimer)
                _disclaimer.Show();

            // Same "shown/hidden in lockstep with the HUD" shape as Disclaimer
            // just above -- not an always-on desktop fixture like Status/Toast,
            // despite living in its own top-level window for the same
            // AllowsTransparency/native-HWND reasons those do (see
            // RecentClipsOverlay.xaml's own comment). _lastScreen doubles as
            // "whichever screen is currently showing" here (see its own
            // comment) since the HUD reopens onto whatever screen it was left
            // on, without going through ShowScreen again.
            UpdateRecentClipsOverlayVisibility(_lastScreen);

            // Otherwise this waits for the next 1s poll tick to reappear if
            // still streaming -- fine in practice, but immediate is free here.
            UpdateStreamingBoxVisibility();
        }
    }

    /// <summary>Sets/clears the one tracked deferred-update prompt and shows/hides it to match. Safe to call whether or not the HUD is currently open -- only actually shows the window when it is.</summary>
    private void SetPendingUpdate(string? componentDisplayName, Action? install)
    {
        _pendingUpdateName = componentDisplayName;
        _pendingUpdateInstall = install;
        RefreshUpdatePromptVisibility();
    }

    private void RefreshUpdatePromptVisibility()
    {
        if (IsVisible && _pendingUpdateName is not null && _pendingUpdateInstall is not null)
            _updatePrompt.ShowPrompt(_pendingUpdateName, _pendingUpdateInstall);
        else
            _updatePrompt.HidePrompt();
    }

    // ---------------------------------------------------------------- screens

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

    /// <summary>
    /// Fade + a subtle scale-in on whichever panel just became active, purely
    /// cosmetic (BeginAnimation, not a blocking wait) so it doesn't change
    /// ShowScreen's own synchronous behavior -- every caller that populates
    /// content right after (LoadGallery, etc.) still runs immediately,
    /// unaffected.
    ///
    /// Deliberately not a crossfade -- two different attempts at keeping the
    /// outgoing panel visible-and-fading (instead of collapsing it
    /// immediately, like this one does) both looked worse in practice than
    /// the old panel just disappearing outright: fixing the window resize
    /// timing traded a content-clipping glitch for an empty-dead-space
    /// glitch instead. Collapsing the old panel synchronously sidesteps that
    /// whole class of problem.
    ///
    /// No slide either -- an earlier version added one and it read as
    /// awkward: sliding an entire panel's worth of tiles/text/buttons as one
    /// block draws the eye to everything moving at once and looks "swimmy"
    /// rather than smooth. CubicEase (no overshoot) for opacity, since
    /// overshooting a fade looks like flicker; BackEase's slight overshoot-
    /// then-settle on the scale specifically is what reads as "alive"
    /// rather than mechanical.
    /// </summary>
    /// <summary>
    /// Split into Prepare (before newPanel becomes visible) and Start (after
    /// everything else in ShowScreen has settled) specifically for the
    /// AllowsTransparency="True" experiment -- see ShowScreen's own comment
    /// for why the forced UpdateLayout() call in between needs the panel
    /// ALREADY in its invisible/shrunk starting state, not its old combined
    /// form which set that state only after the layout pass had already
    /// happened.
    /// </summary>
    private static void PrepareAnimatePanelIn(FrameworkElement panel, bool useCache)
    {
        // BitmapCache rasterizes the panel once and animates that cached
        // bitmap instead of re-rendering the actual vector content on every
        // frame -- cuts the per-frame render cost this panel adds on top of
        // the layered window's own full-window blit. Cleared once the
        // animation completes so it isn't kept around outside the transition.
        //
        // useCache=false for Gallery (see caller): LoadGallery() runs right
        // after ShowScreen returns and populates/mutates the tile grid
        // (thumbnails loading in) while this fade is still in flight -- a
        // cached bitmap has to re-rasterize every time that content changes
        // underneath it, and doing that mid-animation caused a solid-frame /
        // opacity-dip / solid-frame flicker (a stale cache frame briefly
        // visible during regeneration).
        //
        // useCache=false for Player too, for a different reason: BitmapCache
        // can only rasterize WPF's own rendered content, not a native child
        // HWND -- VLC's video surface, attached into PlayerVideoView shortly
        // after this via OpenInPlayer's own deferred callback. The exact
        // same "airspace" problem documented elsewhere in this app (see
        // StopPlayerPlayback), just hitting BitmapCache instead of
        // Visibility/layout this time: a cached bitmap taken before VLC
        // attaches can't include content that isn't WPF's to rasterize,
        // producing a stale/partial frame (a "half window", then the video
        // area rendering as an empty container) until the cache clears at
        // the animation's end and normal compositing takes back over.
        //
        // Every other screen is both fully static once shown AND has no
        // native HWND content, so the cache stays valid for their whole
        // animation.
        if (useCache)
            panel.CacheMode = new BitmapCache();
        panel.RenderTransform = new ScaleTransform(0.96, 0.96);
        panel.RenderTransformOrigin = new Point(0.5, 0.5);
        panel.Opacity = 0;
    }

    private static void StartAnimatePanelIn(FrameworkElement panel)
    {
        // Slightly longer duration, and no overshoot (CubicEase instead of
        // BackEase) -- under a layered window's frame-dropping, overshoot
        // needs enough in-between frames to read as "settle"; with frames
        // getting dropped it read as a bounce/snap instead of a subtle one,
        // so removing it degrades gracefully even when frames are sparse.
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
        // Unconditional, not just when leaving Settings specifically -- cheap
        // no-op via its own IsActive guard if autoscroll isn't running, but
        // navigating away (or even just switching to a different screen and
        // back) while it's active would otherwise leave CompositionTarget.
        // Rendering subscribed forever, ticking a scroll loop against a
        // ScrollViewer nobody's looking at anymore.
        StopSettingsAutoscroll();

        FrameworkElement newPanel = PanelFor(screen);
        bool switchingPanel = newPanel.Visibility != Visibility.Visible;
        // No entrance animation at all for Player -- VLC's video keeps
        // independently decoding/presenting its own frames the whole time
        // OpenInPlayer's deferred attach is pending, on top of whatever this
        // fade+scale animation costs, and both compete for the same
        // layered-window full-window blit on every single frame either one
        // produces. That contention read as the animation itself running at
        // ~5fps -- not fixable by tuning the animation cheaper, since the
        // video's own frame rate isn't something this app controls. Every
        // other screen has no ongoing native rendering competing with it, so
        // they keep the animation.
        //
        // skipEntranceAnimation is CloseOverlay's escape hatch for its own
        // reset-to-Idle call: that reset happens right before FadeWindowOut
        // fades the WHOLE window away, so IdlePanel's own fade+scale (and
        // its BitmapCache) would be competing with that window-level fade
        // for the same layered-window compositing pipeline for literally no
        // visual benefit -- nobody's watching Idle "animate in" while the
        // window is simultaneously vanishing. That contention was the actual
        // cause of a near-black square (RootBorder's own background, since
        // IdlePanel's content starts at Opacity=0) reading as "stuck" for
        // half a second instead of fading -- the window's own fade-out was
        // getting starved by the competing animation, not genuinely stuck.
        bool animateEntrance = switchingPanel && screen != Screen.Player && !skipEntranceAnimation && _settings.EnableAnimations;

        // Three ordered steps, not two -- a switch always involves BOTH a
        // Visibility change AND (often) a Size change, and doing either one
        // first alone exposes the WRONG combination of the two for a frame:
        //
        //  1) Hide the OUTGOING panel first, before anything about size
        //     changes. (Tried resizing first instead, leaving the outgoing
        //     panel visible a moment longer -- for Gallery specifically,
        //     whose tiles reflow with available width, shrinking the window
        //     out from under its still-visible grid squashed it into a tall
        //     vertical strip for a frame. Worse than the original bug.)
        //  2) Resize/reposition while NOTHING switch-relevant is visible --
        //     safe now, nothing to visibly glitch.
        //  3) Only then show the INCOMING panel, already at the correct
        //     final bounds -- it never has to render at a stale size either.
        //
        // (Also tried forcing a synchronous UpdateLayout() between the
        // Visibility swap and the resize instead of reordering at all -- that
        // traded the flash for a worse bug: it could commit a real rendered
        // frame of the new panel already at its full, sometimes-wrong-at-that-
        // point height while AnimatePanelIn's own initial Opacity=0 start
        // state was still in effect -- a big blank box, not just a flicker.)
        IdlePanel.Visibility = Visibility.Collapsed;
        SaveReplayPanel.Visibility = Visibility.Collapsed;
        StartRecordPanel.Visibility = Visibility.Collapsed;
        GalleryPanel.Visibility = Visibility.Collapsed;
        PlayerPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        // PlayerPanel.Collapsed just above does nothing to VLC's native video
        // HWND on its own -- that's the airspace gotcha, but specifically for
        // an ANCESTOR going Collapsed. VideoView itself is an HwndHost, and
        // HwndHost DOES hide its own hosted native window (a real ShowWindow
        // call under the hood) when ITS OWN Visibility changes -- collapsing
        // it directly, not just the ancestor, is what actually removes the
        // native surface from the screen instead of leaving it rendering at
        // stale bounds. (Tried Opacity=0 first, on the theory that
        // AllowsTransparency="True" forces WPF to composite hosted HWND
        // content through an offscreen bitmap where Opacity would apply --
        // confirmed live that it doesn't actually hide it, so this replaces
        // that attempt rather than stacking with it.) Reset back to Visible
        // in OpenInPlayer.
        if (screen != Screen.Player)
        {
            PlayerVideoView.Visibility = Visibility.Collapsed;
            DetachPlayerVideo();
        }
        // Not one of the 6 screen panels above, but just as switch-relevant --
        // it's a separate sibling element (declared after IdlePanel so it wins
        // hit-testing over Gallery's tiles, per its own XAML comment), so its
        // own Collapse used to happen much later in this method, well after
        // the resize below. Left it visible through that brief "everything
        // else hidden, already resized to the new screen's bounds" gap, which
        // is exactly what "a small thin container with just the gear on it"
        // for one frame entering any screen from Idle turned out to be.
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

        // AllowsTransparency="True" experiment: a layered window has to push
        // a fully-composited bitmap for whatever the visual tree looks like
        // at the moment DWM asks for one, instead of a normal window's live
        // incremental repaint -- so with SizeToContent="Height" driving the
        // window's own resize off newPanel's now-visible content, there was
        // a real window where that resize (and this panel's own
        // measure/arrange) hadn't finished yet when a frame got pushed,
        // showing a wrong-sized/partially-arranged cut for a frame or two.
        // The forced UpdateLayout() below closes that window -- but it also
        // means whatever state newPanel is in AT THAT MOMENT gets committed
        // as a real rendered frame, not just silently resolved layout math.
        // First attempt called UpdateLayout() with the panel still at its
        // default fully-visible state, THEN reset it to AnimatePanelIn's
        // invisible/shrunk starting point right after -- so the one frame
        // UpdateLayout forced was the FINAL state, immediately followed by a
        // jump backward to the START state once the animation kicked in:
        // full frame, snap backward, animate forward again. Preparing the
        // panel's starting state FIRST (still while Collapsed, so it isn't
        // visible yet) means the frame UpdateLayout forces is the correct
        // starting one, and the animation that follows only ever moves
        // forward from there.
        if (animateEntrance)
            PrepareAnimatePanelIn(newPanel, useCache: screen is Screen.Idle or Screen.SaveReplay or Screen.StartRecord or Screen.Settings);
        else if (switchingPanel)
        {
            // Player specifically: guarantee a clean, fully-visible state
            // instead of just skipping Prepare/Start -- Opacity/RenderTransform/
            // CacheMode are only ever touched by this animation pair, but
            // resetting explicitly here doesn't depend on that staying true.
            newPanel.Opacity = 1;
            newPanel.RenderTransform = null;
            newPanel.CacheMode = null;
        }

        newPanel.Visibility = Visibility.Visible;
        // Skipped for CloseOverlay's reset-to-Idle call specifically: this
        // synchronous call forces a real paint so a NEWLY OPENED screen's
        // resize/layout is fully settled before the user actually sees or
        // interacts with it -- but a close-triggered reset exists purely to
        // leave state correct for NEXT time the overlay opens, and the
        // window is about to fade out and Hide() immediately after this
        // anyway. Forcing a real paint nobody needs to see was pure added
        // latency between the hotkey press and the fade-out even starting
        // -- "resize to compact size (fully opaque), THEN start a 150ms
        // fade" reads as slower than it should, not as fast as the fade
        // duration alone suggests.
        if (!skipEntranceAnimation)
            UpdateLayout();
        // Deferred, not called inline here -- UpdateStreamingBoxVisibility can
        // Show()/reposition a SEPARATE top-level window (StreamingStatusOverlay),
        // and doing that synchronously in the middle of ShowScreen can pump the
        // Windows message queue enough to force MainWindow itself to repaint
        // before its own Visibility/resize work above has actually settled --
        // reintroducing the exact same resize/repaint race this method was
        // reordered to fix in the first place, just triggered by this call
        // instead of the Width/Left/Top assignments this time. Same lesson,
        // same fix: let this transition finish completely first.
        Dispatcher.BeginInvoke(new Action(UpdateStreamingBoxVisibility), DispatcherPriority.Loaded);

        // GalleryStatus is the SAME TextBlock as the Idle tile's "X clips"
        // subtitle -- LoadGallery() (browsing) sets it to a SHALLOW count of
        // just the currently-viewed folder's own files, not a recursive total,
        // so it stomps the real total the moment Gallery's opened at all (e.g.
        // clips organized into subfolders would show far fewer, or zero, right
        // there in the root). Recomputing the real recursive count every time
        // Idle becomes the active screen means that stale per-folder number
        // never lingers on the tile once you're back, regardless of which path
        // got you there.
        if (screen == Screen.Idle)
            _ = RefreshGalleryCountAsync();

        if (animateEntrance)
            StartAnimatePanelIn(newPanel);

        // The gear only makes sense on the idle screen -- it isn't a fourth tile,
        // so it shouldn't linger once you've navigated away from the row it sits above.
        TopRightButtons.Visibility = screen == Screen.Idle ? Visibility.Visible : Visibility.Collapsed;

        // Only ever CLOSES it here, never opens it -- OpenInPlayer (the only
        // caller that ever passes Screen.Player, see its own comment) is the
        // sole place that reopens it, via a deferred force-close-then-reopen
        // needed for WPF's Placement="Relative" Popup to reliably recompute
        // position on every clip, not just the first. Having this line also
        // set IsOpen = true for the Player case meant OpenInPlayer's own
        // close+reopen ran on TOP of the true this line had just set --
        // true->false->true instead of one clean false->true -- and that
        // extra cycle was a real regression: a black/grey/black flash right
        // as a clip opens.
        if (screen != Screen.Player)
            PlayerOverlayPopup.IsOpen = false;

        // NOT the full StopPlayerPlayback() -- DetachPlayerVideo() already ran
        // up above, before the resize. This is only the slow half (LibVLC's
        // own Stop()/Dispose(), observed anywhere from ~0.5s to several
        // seconds depending on the clip), and it needs to stay OFF the UI
        // thread specifically here: this whole method call chain
        // (BackToGallery_Click -> ShowScreen) is synchronous, so calling the
        // blocking version inline meant NOTHING from this method -- not the
        // resize, not the Collapse above, nothing -- actually got painted to
        // the screen until this blocking call returned control to the
        // dispatcher. That's what "video stays overlayed on Gallery, cut in
        // half, for exactly N seconds depending on which clip" actually was:
        // every visual change this method makes was queued correctly the
        // whole time, just never given a chance to render.
        if (screen != Screen.Player)
            DisposeVlcPlayerAsync();

        if (screen is Screen.Idle or Screen.SaveReplay or Screen.StartRecord or Screen.Gallery or Screen.Settings)
            _lastScreen = screen;

        // Idle-only visibility -- see UpdateRecentClipsOverlayVisibility's
        // own comment. Uses the real target `screen` here (not _lastScreen),
        // so Player correctly hides it even though Player is excluded from
        // the _lastScreen assignment just above.
        UpdateRecentClipsOverlayVisibility(screen);

        IntPtr toastHwnd = new WindowInteropHelper(_toastOverlay).Handle;
        if (toastHwnd != IntPtr.Zero)
        {
            WindowZOrder.BringToFrontWithoutActivating(toastHwnd);
        }
    }

    /// <summary>
    /// Gallery and Player are meant to feel noticeably bigger than the compact
    /// pill, not literally edge-to-edge fullscreen -- a comfortable panel over
    /// the game, matching the original design concept's proportions. Width
    /// comes from the primary screen's own size (capped, so it doesn't get
    /// absurd on huge monitors); since the window uses SizeToContent="Height",
    /// the actual on-screen height is driven by content, not a Window.Height
    /// set here.
    ///
    /// The cap used to be 1500 -- right around what 78% of a 1080p screen's
    /// width already comes out to (1920 * 0.78 ~= 1498), so it only ever
    /// bound 1080p in practice. A 1440p screen (2560 * 0.78 ~= 1998) hit that
    /// exact same 1500 cap instead of actually getting bigger, so Gallery/
    /// Player looked identically sized on both -- not "capped for huge
    /// monitors" like the comment always meant, just silently capped for
    /// everyone above 1080p. Raised to 2000 so 1440p gets its real,
    /// essentially-uncapped size and only screens meaningfully bigger than
    /// that (4K and up) actually hit the ceiling.
    /// </summary>
    private double BigWidth() => Math.Min(TargetScreenBounds.Width * 0.78, 2000);

    private void ApplyBigScreenSize()
    {
        // Sized from the actual video column's width using a 16:9 ratio, not a
        // guessed fraction of screen height: picking the height independently of
        // the video's real aspect ratio was letterboxing it (grey bars either
        // side) whenever the guess didn't match. The rail column is a fixed 90px
        // and RootBorder has a 1px border each side.
        double videoColumnWidth = Width - 90 - 2;
        double contentHeight = Math.Max(videoColumnWidth * 9.0 / 16.0, 320);

        // Gallery uses the exact same height as the Player's video area, not its
        // own separately-tuned fraction -- the two screens are meant to feel like
        // the same size panel, not different sizes depending on which you're on.
        PlayerVideoHost.Height = contentHeight;
        GalleryScrollHost.MaxHeight = contentHeight;
        Top = TargetScreenBounds.Y + BigTop;
    }

    private void BackToIdle_Click(object sender, MouseButtonEventArgs e) => ShowScreen(Screen.Idle);

    private void BackToGallery_Click(object sender, MouseButtonEventArgs e)
    {
        // Cancel first, unconditionally, before anything else below. The
        // in-place rename TextBox's own LostFocus handler auto-commits --
        // clicking the back arrow moves focus away from it as a side effect,
        // which used to fire that commit (potentially calling OpenInPlayer
        // to refresh the renamed title) in a race against this method's own
        // ShowScreen(Gallery) just below, each fighting over which screen
        // ends up showing. Reverting explicitly here removes the race
        // instead of trying to out-order it.
        _cancelPlayerRename?.Invoke();

        // Same "back" affordance does double duty, matching Escape's own
        // handling right below it: while Trim is open, the first press
        // cancels the trim (same as TrimCancel_Click) instead of leaving
        // Player entirely -- jumping straight out from mid-trim would
        // silently discard whatever the user was doing with no confirmation
        // at all, worse than fullscreen's own "one press just backs out of
        // the sub-mode" precedent right below, which this now matches.
        if (TrimPanel.Visibility == Visibility.Visible)
        {
            TrimCancel_Click(sender, e);
            return;
        }

        // First press just backs out of fullscreen (still on this clip,
        // sidebar/controls restored), and only a press after that actually
        // leaves Player for Gallery. Jumping straight to Gallery from
        // fullscreen would skip past the in-between state entirely and feel
        // like the button did too much.
        if (_isPlayerFullscreen)
        {
            ExitPlayerFullscreen();
            return;
        }

        // Usually Gallery (see _playerBackTarget's own comment), but Idle
        // when this clip was opened from the Recent Clips overlay instead of
        // by navigating through Gallery.
        ShowScreen(_playerBackTarget);
        if (_playerBackTarget == Screen.Gallery)
            LoadGallery();
    }

    /// <summary>
    /// Shared by the tray icon's "Hide/Show Status Overlay" menu item and
    /// Settings' own "Show status indicator" toggle -- both flip the same
    /// window and the same persisted setting, so either one stays in sync
    /// with the other (see AppSettings.ShowStatusIndicator's own comment).
    /// </summary>
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

    private void ShowStatusIndicatorToggle_Click(object sender, RoutedEventArgs e)
    {
        ToggleStatusOverlay();
        _trayManager?.UpdateStatus(_obs.IsConnected, _statusOverlay.IsVisible);
    }

    private void DefaultAudioTrackSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DefaultAudioTrackSelector.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out int index))
            return;
        _settings.DefaultPlayerAudioTrackIndex = index;
        _settings.Save();
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
        // Items are declared in the same TopLeft/TopRight/BottomLeft/BottomRight
        // order the enum itself is, so SelectedIndex maps straight across --
        // simpler than a Tag round-trip through Enum.Parse for four values
        // that won't be reordered independently of the enum.
        _settings.StatusIndicatorLocation = (StatusIndicatorLocation)StatusIndicatorLocationSelector.SelectedIndex;
        _settings.Save();
        _statusOverlay.Reposition();
        UpdateStatusIndicatorPreview();
    }

    /// <summary>
    /// Mirrors StatusOverlay's own ApplyLayout on the small mock badge strip
    /// in Settings' Preview box, so the corner/orientation combo the user
    /// just picked is visible immediately without having to go find the real
    /// (usually click-through, easy to lose track of) indicator on screen.
    /// </summary>
    private void UpdateStatusIndicatorPreview()
    {
        bool horizontal = _settings.StatusIndicatorOrientation == StatusIndicatorOrientation.Horizontal;
        bool isLeft = _settings.StatusIndicatorLocation is StatusIndicatorLocation.TopLeft or StatusIndicatorLocation.BottomLeft;
        bool isTop = _settings.StatusIndicatorLocation is StatusIndicatorLocation.TopLeft or StatusIndicatorLocation.TopRight;

        StatusIndicatorPreviewPanel.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        StatusIndicatorPreviewPanel.HorizontalAlignment = isLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        StatusIndicatorPreviewPanel.VerticalAlignment = isTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        // Matches StatusOverlay's own real 5px gap (see the preview's XAML
        // comment on why this whole mockup is drawn true-to-scale).
        Thickness gap = horizontal ? new Thickness(5, 0, 0, 0) : new Thickness(0, 5, 0, 0);
        for (int i = 0; i < StatusIndicatorPreviewPanel.Children.Count; i++)
        {
            if (StatusIndicatorPreviewPanel.Children[i] is FrameworkElement badge)
                badge.Margin = i == 0 ? default : gap;
        }
    }

    /// <summary>
    /// Keeps the preview box a genuine 16:9 rectangle as the Settings panel
    /// resizes (window resize, DPI change, scrollbar appearing/disappearing)
    /// -- WPF has no native "lock aspect ratio" property, so Height is
    /// derived from the just-measured Width here instead of a flat guess.
    /// Guarded against a no-op re-set: setting Height fires this same
    /// SizeChanged again, and while Width doesn't actually change as a
    /// result (this row's width is driven by the ScrollViewer, not by this
    /// Border's own Height), re-assigning an unchanged value would still be
    /// one extra pointless layout pass every time.
    /// </summary>
    private void StatusIndicatorPreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
            return;
        double targetHeight = e.NewSize.Width * 9.0 / 16.0;
        // Height starts as NaN (Auto, unset in XAML) -- Math.Abs(NaN - x) is
        // itself NaN, which a ">" comparison always treats as false, so the
        // very first measure pass needs its own explicit check or this
        // would never fire at all.
        if (double.IsNaN(StatusIndicatorPreviewBorder.Height) || Math.Abs(StatusIndicatorPreviewBorder.Height - targetHeight) > 0.5)
            StatusIndicatorPreviewBorder.Height = targetHeight;
    }

    /// <summary>Circle = idle (matches the universal "record" glyph); red square = recording (matches "stop").</summary>
    private void SetRecordIcon(bool active)
    {
        RecordDot.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        RecordSquare.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// StreamingStatusOverlay is its own separate floating window now, not an
    /// element inside MainWindow (see its own XAML comment for why -- this
    /// window is AllowsTransparency="False", needed for the VLC video surface
    /// it hosts to render at all), so it's no longer implicitly shown/hidden
    /// by anything in here directly. Needs 3 conditions checked explicitly
    /// instead: actually streaming, this HUD is actually open right now, and
    /// Idle is the screen currently showing (this is a reminder for the main
    /// screen, not something that should linger while looking at Gallery/
    /// Settings/etc., or after the HUD's been closed). Called from every
    /// place any of those three can change, plus MainWindow's own
    /// SizeChanged/LocationChanged (see constructor) so the reposition below
    /// self-corrects once this window's real post-switch bounds are known --
    /// right here, synchronously after a screen switch, ActualHeight in
    /// particular isn't necessarily settled yet (SizeToContent="Height").
    /// </summary>
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

    private async Task RefreshStatusAsync()
    {
        _trayManager?.UpdateStatus(_obs.IsConnected, _statusOverlay.IsVisible);

        // Runs before the connected/disconnected branch below (and its early
        // return) on purpose -- a dropped OBS connection should clear this
        // badge quickly too, not leave it flashing on stale data forever.
        // 4s is roughly 2x the ~2s interval EncoderOverloadDetected re-fires
        // at while a real overload is ongoing, enough slack to not flicker
        // off between two individual re-emits.
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

            // _serverEnabledAtStartup is a one-time snapshot taken when this
            // app itself launched (or last Reconfigure()'d) -- it goes stale
            // the moment the user flips the server on/off in OBS's own UI
            // WHILE Backtrack keeps running, which is the normal case here
            // (Backtrack usually launches once and stays open for the whole
            // session). Read the config file fresh every tick instead so
            // this reacts to a live toggle, not just what was true at boot.
            //
            // All of it (file read, process enumeration, and the possible
            // config rewrite below) is real synchronous OS I/O -- offloaded
            // via Task.Run so this 1s poll, which keeps firing indefinitely
            // for as long as OBS stays disconnected (including a fresh
            // install with no config file yet), doesn't block the UI thread
            // doing it every single tick.
            bool serverEnabledNow = _serverEnabledAtStartup;
            if (!_settings.ObsIsRemote)
            {
                (bool enabledNow, bool autoFixed) = await Task.Run(() =>
                {
                    (bool enabled, string? _) = ObsConfigReader.ReadLocalConfig();

                    // Local mode + server off + OBS not even running yet:
                    // silently flip server_enabled in obs-websocket's own
                    // config now, so the moment the user actually launches
                    // OBS it just connects, no manual trip to Tools >
                    // WebSocket Server Settings required. Gated on "not
                    // running" specifically -- that config file is only read
                    // at OBS's own startup, so rewriting it while OBS is
                    // already open with the server off wouldn't do anything
                    // until a restart anyway, and risks racing OBS saving its
                    // own settings back to the same file. This also means
                    // telling the user to "restart OBS" (below) is a real
                    // fix, not just a suggestion: the moment they close it,
                    // this same check fixes the file, and the next launch
                    // picks it up.
                    if (!enabled && Process.GetProcessesByName("obs64").Length == 0 && ObsConfigReader.TryEnableServer())
                        return (true, true);
                    return (enabled, false);
                });
                serverEnabledNow = enabledNow;
                if (autoFixed)
                    AppLog.Write("ObsConfigReader.TryEnableServer: OBS's WebSocket server was off and OBS wasn't running -- enabled it for the next launch.");
            }

            // Keep the field itself current too, not just this tick's local
            // variable -- LoadReplayRowsAsync/LoadRecordRowsAsync (BufRowsPanel/
            // RecRowsPanel's own "not connected" messaging) still read
            // _serverEnabledAtStartup directly rather than doing their own live
            // config read, so without this they could show a stale message
            // (e.g. "server disabled") contradicting this tooltip's fresh one.
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
            // Fired together, not one at a time -- these 6 are mutually
            // independent OBS requests. Awaiting each immediately (the original
            // shape) meant this tick's total latency was the SUM of all of
            // them; kicking them all off first and only then awaiting means
            // it's the MAX of the slowest one instead, which is what actually
            // keeps this comfortably inside the 1s poll interval.
            Task<RecordStatus> recStatusTask = _obs.GetRecordStatusAsync();
            Task<List<RecordRow>> recordRowsTask = _obs.ListRecordRowsAsync();
            Task<bool> replayBufferActiveTask = _obs.GetReplayBufferActiveAsync();
            Task<List<ReplayRow>> replayRowsTask = _obs.ListReplayRowsAsync();
            Task<bool> streamActiveTask = _obs.GetStreamActiveAsync();
            Task<bool> virtualCamActiveTask = _obs.GetVirtualCamActiveAsync();

            RecordStatus recStatus = await recStatusTask;
            // Not just OBS's single global recording: a Source Record filter can be
            // recording independently of it (started from its own row in the Start
            // Recording menu, or from ControlPanelDock's own button in OBS), so the
            // Idle tile must reflect that too -- otherwise it can sit idle while a
            // filter is visibly recording, same backwards-looking problem the Save
            // Replay pill's own row aggregation (right below) already solves for
            // buffers. The count (not just whether any are active) is what
            // RecordTile_Click itself uses to decide direct-stop vs. menu, so the
            // label below mirrors that exact logic instead of guessing separately.
            int activeRecordRowCount;
            try
            {
                List<RecordRow> recordRows = await recordRowsTask;

                // Start tracking any row that's newly recording, and drop
                // tracking for any row that's no longer actively recording --
                // TryAdd leaves an already-tracked row's original timestamp
                // alone (so it keeps counting from its real start, not this
                // poll tick), and removing a stopped row means if it starts
                // again later it gets a genuinely fresh "since", not a stale
                // reused one from its earlier run.
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
                foreach (string staleKey in newlyStoppedKeys)
                    _recordRowActiveSinceUtc.Remove(staleKey);

                // No RecordStateChanged-style event exists for a filter's own
                // recording (that's specific to OBS's own main output), so
                // this poll is the only place a per-row "started"/"stopped"
                // toast can come from at all -- including a row started or
                // stopped via its own hotkey bound directly in OBS, entirely
                // outside Backtrack's own UI, which previously never toasted
                // anything (see BuildRecordRowButton's own click handler,
                // which used to be the ONLY place this toast fired, and so
                // only ever covered a click made inside Backtrack's HUD).
                // Skipped entirely on the first poll after startup/reconnect
                // (_recordRowPollSeeded) so a row already recording when
                // Backtrack attaches doesn't look like it "just started".
                if (_recordRowPollSeeded)
                {
                    foreach (string key in newlyStartedKeys)
                        _toastOverlay.ShowRecording(started: true, resolvedPath: null);
                    foreach (string key in newlyStoppedKeys)
                    {
                        string? path = _recordRowInfoByKey.TryGetValue(key, out var info) && !string.IsNullOrEmpty(info.SourceName) && !string.IsNullOrEmpty(info.FilterName)
                            ? await _obs.GetRecordRowDestinationFolderAsync(info.SourceName, info.FilterName)
                            : null;
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

            // "Stop Recording" only when a click would actually stop something
            // directly (see RecordTile_Click) -- exactly one thing recording total,
            // main or a single filter row. Nothing recording, or more than one
            // thing recording at once (ambiguous which to stop), both fall back to
            // the menu instead, so "Start Recording" stays accurate as what a click
            // actually does in either of those cases too.
            bool singleActiveTarget = (recStatus.Active && activeRecordRowCount == 0) || (!recStatus.Active && activeRecordRowCount == 1);
            RecordLabel.Text = singleActiveTarget ? "Stop Recording" : "Start Recording";
            SetRecordIcon(recordingAnything);

            // The pill shows whichever of "main recording" and "every
            // currently-active row" has actually been going the LONGEST, not
            // just whichever one happens to be main vs. a filter -- so it
            // stays correct through any combination: main stops but a row
            // that was already recording keeps going (row's own real elapsed
            // time keeps showing, doesn't reset), or two different rows
            // started at two different times (the earlier one wins, same as
            // it would if it were the main recording instead). No per-filter
            // duration is available from the bridge, so each row's own
            // elapsed time is "since Backtrack first noticed it recording"
            // (_recordRowActiveSinceUtc, maintained above), which can
            // undercount if it was already running before Backtrack opened
            // or this poll caught it. recStatus's own duration is exact by
            // comparison -- obs-websocket tracks the real start time for the
            // main output specifically -- so it's used as-is, not converted
            // through the same wall-clock approximation as the rows.
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
                    bestIsMainPaused = false; // pausing only applies to the main recording, not a row
                }
            }
            RecordStatusText.Text = bestDurationMs is long ms
                ? (bestIsMainPaused ? $"{FormatDuration(ms)} (Paused)" : FormatDuration(ms))
                : "--:--";
            _statusOverlay.SetRecording(recordingAnything);

            // Not just OBS's single global replay-buffer flag: obs-replay-slider (and
            // obs-source-record, exposed through the same bridge) can each have their
            // own buffer armed independently of it, so a row showing green (Status == 1)
            // must count as "on" here too -- otherwise this pill can say "Off" while a
            // buffer row is visibly active, which is exactly backwards.
            //
            // Status == 2 is a row in an ERROR state (e.g. a buffer that failed to
            // save), not merely inactive. Active must outrank error, though, not the
            // other way around -- with multiple buffers, one being broken doesn't mean
            // replay saving as a whole is down if another one is still working; saying
            // "Error" while a buffer is visibly green and armed is just as backwards as
            // saying "Off" was. Error only wins when NOTHING is currently active.
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
                // Bridge unreachable this ONE tick (out of one per second) -- reusing
                // the last successful read instead of resetting to false avoids the
                // pill flickering to "Off" for a moment while a row is genuinely still
                // active, which is exactly as backwards as the two cases above.
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

            // Keeps the container correct on every poll tick, not just on the
            // StreamingStateChanged event transitions -- covers Backtrack
            // opening (or OBS reconnecting) while already live, which the
            // event alone would never fire for.
            try
            {
                _isStreaming = await streamActiveTask;
                _statusOverlay.SetStreaming(_isStreaming);
                UpdateStreamingBoxVisibility();
            }
            catch
            {
                // Leave it showing whatever it last correctly showed.
            }

            // Same "every poll tick, not just the event" reasoning as
            // streaming above -- covers Backtrack opening (or OBS
            // reconnecting) while the Virtual Camera is already running.
            try
            {
                _statusOverlay.SetVirtualCamActive(await virtualCamActiveTask);
            }
            catch
            {
                // Leave it showing whatever it last correctly showed.
            }
        }
        catch
        {
            // A request failing mid-poll (e.g. OBS closing right now) just means
            // we show stale values for one tick; the next Disconnected event fixes it.
        }
    }

    /// <summary>Rolls over into "h:mm:ss" once past an hour instead of showing e.g. "78:04".</summary>
    private static string FormatDuration(long ms)
    {
        int totalSeconds = (int)(ms / 1000);
        int h = totalSeconds / 3600;
        int m = totalSeconds / 60 % 60;
        int s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }

    private async void RecordTile_Click(object sender, RoutedEventArgs e)
    {
        // Starting (nothing recording yet) opens the same kind of menu Save
        // Replay does, since obs-replay-slider's Control Panel dock means
        // there can be several independent per-source recordings to choose
        // from, not just the one global one. Stopping is a direct toggle
        // instead ONLY when there's exactly one thing recording right now
        // (main, or a single filter row) -- there's nothing to choose between
        // in that case. If more than one thing is recording at once, it's
        // ambiguous which this click should stop, so that also falls back to
        // the menu rather than guessing.
        try
        {
            // Same "not connected to OBS" screen Save Replay already shows
            // (LoadRecordRowsAsync's own early-return message) -- this used
            // to just silently return here instead, so clicking Start
            // Recording while disconnected did nothing at all, not even
            // navigate to the screen.
            if (!_obs.IsConnected)
            {
                ShowScreen(Screen.StartRecord);
                _ = LoadRecordRowsAsync();
                return;
            }

            RecordStatus mainStatus = await _obs.GetRecordStatusAsync();
            List<RecordRow> activeRows = (await _obs.ListRecordRowsAsync()).Where(r => r.Status == RecordStatusRecording).ToList();

            if (mainStatus.Active && activeRows.Count == 0)
            {
                await _obs.StopMainRecordAsync();
                await RefreshStatusAsync();
            }
            else if (!mainStatus.Active && activeRows.Count == 1)
            {
                RecordRow row = activeRows[0];
                await _obs.StopRecordRowAsync(row.Key);
                // No direct toast here -- RefreshStatusAsync (called right
                // below) now detects this same stop itself via polling and
                // toasts for it, same as it would for a hotkey-triggered
                // stop. A toast here too would double up.
                await RefreshStatusAsync();
            }
            else
            {
                ShowScreen(Screen.StartRecord);
                _ = LoadRecordRowsAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't toggle recording: {ex.Message}", "Backtrack");
        }
    }

    private void SaveReplayTile_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(Screen.SaveReplay);
        _ = LoadReplayRowsAsync();
    }

    private async void GalleryTile_Click(object sender, RoutedEventArgs e)
    {
        _currentGalleryFolder = null;
        _currentRemoteGalleryFolder = null;
        // Paired-for-sharing devices care about the OTHER PC's clips, not
        // this one's -- jump straight to the remote view rather than making
        // that a second click through a "This PC"/IP tab switcher. See
        // RefreshGallerySourceTabsVisibility, which keeps that switcher
        // hidden unconditionally now.
        _galleryIsRemote = !string.IsNullOrEmpty(_settings.PairedPeerSecret);
        RefreshGallerySourceTabsVisibility();

        if (_galleryIsRemote)
        {
            // Populate remote cards before revealing Gallery screen so it never renders a blank frame
            await LoadRemoteGalleryAsync();
            ShowScreen(Screen.Gallery);
        }
        else
        {
            ShowScreen(Screen.Gallery);
            LoadGallery();
        }
    }

    /// <summary>
    /// The old "This PC"/"<IP>" tab switcher is gone -- GalleryTile_Click
    /// already decides local vs. remote up front based on pairing state, so
    /// there's nothing left for a switcher to do in normal use. Kept as a
    /// method (rather than deleting the row's code entirely) purely for the
    /// "got unpaired while sitting on the Remote view" fallback below, which
    /// still needs to run wherever pairing state can change.
    /// </summary>
    private void RefreshGallerySourceTabsVisibility()
    {
        GallerySourceTabs.Visibility = Visibility.Collapsed;
        bool paired = !string.IsNullOrEmpty(_settings.PairedPeerSecret);
        if (!paired && _galleryIsRemote)
        {
            // Got unpaired while showing the remote gallery -- there's
            // nothing left to show there, so fall back to local rather than
            // leaving Gallery stuck on a now-meaningless "Remote" view.
            _galleryIsRemote = false;
            _currentRemoteGalleryFolder = null;
        }
    }

    private void GalleryLocalTab_Click(object sender, RoutedEventArgs e)
    {
        if (!_galleryIsRemote)
            return;
        GalleryFilterBox.Text = string.Empty; // see OpenGalleryFolder's own comment
        _galleryIsRemote = false;
        RefreshGallerySourceTabsVisibility();
        LoadGallery();
    }

    private void GalleryRemoteTab_Click(object sender, RoutedEventArgs e)
    {
        if (_galleryIsRemote || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return;
        GalleryFilterBox.Text = string.Empty; // see OpenGalleryFolder's own comment
        _galleryIsRemote = true;
        _currentRemoteGalleryFolder = null;
        RefreshGallerySourceTabsVisibility();
        LoadGallery();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(Screen.Settings);
        // A WPF ScrollViewer keeps its own vertical offset across a plain
        // Visibility toggle -- ShowScreen collapses/re-shows SettingsPanel
        // rather than tearing it down, so without this, reopening Settings
        // scrolled halfway down (Status Indicators, Experimental, ...) left
        // it there instead of back at GENERAL where every visit starts.
        // NOT reset from DisplaySelector_SelectionChanged's own
        // ShowScreen(Screen.Settings) call, which re-anchors the window
        // while ALREADY on this screen mid-interaction with that same
        // dropdown: resetting scroll there would be a genuine regression,
        // not a fix.
        SettingsScrollHost.ScrollToTop();
        LoadSettingsUi();
        _ = LoadBufferVisibilityUi();
        _ = LoadRecordFolderUi();
        RefreshRamDiskRemoteGating();
        RefreshPluginStatusRemoteGating();
    }

    // "Fullscreen" here means the video+transport column filling the whole
    // target screen, sidebar collapsed out of the way -- not real OS
    // fullscreen (WindowStyle="None" already has no chrome to remove, and a
    // borderless topmost HUD window doesn't have a meaningful "restore"
    // affordance if it took over the whole desktop the OS's own way).
    private const string FullscreenEnterIcon = "M7,14H5v5h5v-2H7V14zM5,10h2V7h3V5H5V10zM17,17h-3v2h5v-5h-2V17zM14,5v2h3v3h2V5H14z";
    private const string FullscreenExitIcon = "M5,16h3v3h2v-5H5V16zM8,8H5v2h5V5H8V8zM14,19h2v-3h3v-2h-5V19zM16,5h-2v5h5V8h-3V5z";

    // Material Design Icons (Apache-2.0), same sourcing as the folder glyph
    // in BuildFolderCard -- real vector glyphs, not hand-approximated shapes.
    private const string VolumeUpIcon = "M3,9v6h4l5,5V4L7,9H3z M16.5,12c0,-1.77 -1.02,-3.29 -2.5,-4.03v8.05c1.48,-0.73 2.5,-2.26 2.5,-4.02z M14,3.23v2.06c2.89,0.86 5,3.54 5,6.71s-2.11,5.85 -5,6.71v2.06c4.01,-0.91 7,-4.49 7,-8.77s-2.99,-7.86 -7,-8.77z";
    private const string VolumeOffIcon = "M16.5,12c0,-1.77 -1.02,-3.29 -2.5,-4.03v2.21l2.45,2.45c0.03,-0.2 0.05,-0.41 0.05,-0.63z M19,12c0,0.94 -0.2,1.82 -0.54,2.64l1.51,1.51C20.63,14.91 21,13.5 21,12c0,-4.28 -2.99,-7.86 -7,-8.77v2.06c2.89,0.86 5,3.54 5,6.71z M4.27,3L3,4.27L7.73,9H3v6h4l5,5v-6.73l4.25,4.25c-0.67,0.52 -1.42,0.93 -2.25,1.18v2.06c1.38,-0.31 2.63,-0.95 3.69,-1.81L19.73,21L21,19.73L4.27,3z M12,4L9.91,6.09L12,8.18V4z";
    private const string FeedbackPlayIcon = "M8,5v14l11,-7z";
    private const string FeedbackPauseIcon = "M6,19h4V5H6V19z M14,5v14h4V5H14z";
    private const string FeedbackSeekForwardIcon = "M4,18l8.5,-6L4,6v12z M13,6v12l8.5,-6L13,6z";
    private const string FeedbackSeekBackIcon = "M11,18V6l-8.5,6L11,18z M20,18V6l-8.5,6L20,18z";

    private enum PlayerFeedbackIcon { Play, Pause, SeekForward, SeekBack, Volume, Mute }

    /// <summary>
    /// Brief centered flash for a keyboard-triggered action, matching
    /// YouTube's own on-screen feedback for its shortcuts. Position is
    /// computed here, not fixed in XAML, since PlayerVideoView's real size
    /// differs between normal Player and fullscreen. Uses the same
    /// close+reopen dance as this file's other Placement="Relative" Popups
    /// (see CLAUDE.md's own note on why) so the recentering above actually
    /// takes effect every time, not just the first.
    /// </summary>
    private void ShowPlayerActionFeedback(PlayerFeedbackIcon icon, string? text = null)
    {
        PlayerActionFeedbackIcon.Data = Geometry.Parse(icon switch
        {
            PlayerFeedbackIcon.Play => FeedbackPlayIcon,
            PlayerFeedbackIcon.Pause => FeedbackPauseIcon,
            PlayerFeedbackIcon.SeekForward => FeedbackSeekForwardIcon,
            PlayerFeedbackIcon.SeekBack => FeedbackSeekBackIcon,
            PlayerFeedbackIcon.Mute => VolumeOffIcon,
            _ => VolumeUpIcon,
        });
        PlayerActionFeedbackText.Text = text ?? string.Empty;
        PlayerActionFeedbackText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

        double videoWidth = PlayerVideoView.ActualWidth;
        double videoHeight = PlayerVideoView.ActualHeight;
        PlayerActionFeedbackPopup.HorizontalOffset = (videoWidth - PlayerActionFeedbackBorder.Width) / 2;
        PlayerActionFeedbackPopup.VerticalOffset = (videoHeight - PlayerActionFeedbackBorder.Height) / 2;

        PlayerActionFeedbackBorder.BeginAnimation(OpacityProperty, null); // cancel any fade-out already in progress
        PlayerActionFeedbackBorder.Opacity = 1;
        PlayerActionFeedbackPopup.IsOpen = false;
        Dispatcher.BeginInvoke(new Action(() => PlayerActionFeedbackPopup.IsOpen = true), DispatcherPriority.Loaded);

        _actionFeedbackHideTimer.Stop();
        _actionFeedbackHideTimer.Start();
    }

    private bool _isPlayerFullscreen;
    private double _preFullscreenWidth;
    private double _preFullscreenLeft;

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlayerFullscreen)
            ExitPlayerFullscreen();
        else
            EnterPlayerFullscreen();
    }

    private void EnterPlayerFullscreen()
    {
        _isPlayerFullscreen = true;
        _preFullscreenWidth = Width;
        _preFullscreenLeft = Left;

        PlayerSidebar.Visibility = Visibility.Collapsed;
        PlayerSidebarColumn.Width = new GridLength(0);

        // Captured BEFORE reparenting below, while it's still measured from
        // its normal docked position -- its own natural height doesn't
        // depend on which parent hosts it (Auto-sized content throughout),
        // so this stays valid once it's moved into the overlay Popup.
        double transportBarHeight = PlayerTransportBar.ActualHeight;

        Rect targetBounds = TargetScreenBounds;

        // The video now gets the FULL available space, not space minus the
        // transport bar -- the bar overlays the bottom of the video instead
        // of reserving its own row below it, so "fullscreen" actually means
        // edge-to-edge video. Still fit-not-stretch (letterbox whichever
        // dimension doesn't match 16:9) rather than distorting the video or
        // pushing the window taller than the real screen.
        double videoWidth = targetBounds.Width;
        double videoHeight = videoWidth * 9.0 / 16.0;
        if (videoHeight > targetBounds.Height)
        {
            videoHeight = targetBounds.Height;
            videoWidth = videoHeight * 16.0 / 9.0;
        }

        Width = videoWidth;
        PlayerVideoHost.Height = videoHeight;
        Left = targetBounds.X + (targetBounds.Width - Width) / 2;
        Top = targetBounds.Y + Math.Max((targetBounds.Height - videoHeight) / 2, 0);

        // Reparent the live transport bar into the fullscreen overlay Popup
        // (see the Popup's own XAML comment for why a Popup at all, and why
        // reparenting instead of a duplicate) -- same control, same event
        // handlers, just a different visual parent while fullscreen is active.
        PlayerVideoColumnDock.Children.Remove(PlayerTransportBar);
        // The bar's own opaque PanelBg background would otherwise completely
        // hide PlayerFullscreenTransportBorder's semi-transparent backdrop
        // underneath it (same bounds, drawn on top) -- transparent here so
        // the video actually shows through behind the overlaid controls,
        // which is the whole point of overlaying instead of docking.
        PlayerTransportBar.Background = Brushes.Transparent;
        PlayerFullscreenTransportBorder.Child = PlayerTransportBar;

        // Floating pill, not a full-bleed bar: inset from both the video's
        // sides and its bottom edge, not flush against any of them. Width is
        // driven from here rather than a XAML binding to PlayerVideoView's
        // ActualWidth, specifically so it can be narrower than the video;
        // HorizontalOffset centers it in the freed-up space. The Border's
        // own Padding="20,6" (in XAML) adds to PlayerTransportBar's natural
        // height/width once reparented as its Child, so the gap math below
        // accounts for that padding, not just the bar's own bare size.
        const double transportPillSideInset = 40;
        const double transportPillBottomGap = 16;
        const double transportPillVerticalPadding = 12; // Border's Padding="20,6": 6 top + 6 bottom
        const double transportPillHorizontalPadding = 40; // Border's Padding="20,6": 20 left + 20 right
        PlayerFullscreenTransportBorder.Width = videoWidth - transportPillSideInset;
        // Also tried forcing PlayerTransportRow.Width (the Grid the seek
        // track's own "*" column lives in) explicitly, cascading this same
        // approach one level deeper -- reverted, see
        // ReopenPlayerFullscreenTransportPopup's own comment: playback got
        // stuck at end-of-clip (stuck play button, seeking backward stopped
        // working) right after that change, not root-caused yet. Left at
        // just PlayerTransportBar's own explicit Width for now.
        PlayerTransportBar.Width = PlayerFullscreenTransportBorder.Width - transportPillHorizontalPadding;
        PlayerFullscreenTransportPopup.HorizontalOffset = transportPillSideInset / 2;
        PlayerFullscreenTransportPopup.VerticalOffset =
            videoHeight - (transportBarHeight + transportPillVerticalPadding) - transportPillBottomGap;

        // Extra breathing room added as Margin on the popup's own INNER
        // content, not as more Popup.HorizontalOffset/VerticalOffset -- the
        // Popup's base 10,10 offset (unchanged, XAML default) is placed
        // relative to VideoView, an HwndHost, and pushing that number higher
        // wasn't visibly moving anything, reported live across three
        // attempts (10->28->40, no visible change each time). Whatever's
        // going on with Popup-relative-to-HwndHost placement precision,
        // ordinary Margin on content already inside the (correctly
        // positioned) popup is plain WPF layout with no HwndHost placement
        // math involved at all.
        //
        // PlayerTitleBarHost (the outer Grid) also needs to grow to match --
        // its own Height is a fixed 46 in XAML, sized for the pill at its
        // default (no margin) position; a top margin alone just pushes the
        // content into that same fixed box and clips it, reported live as
        // "cropped from the top and the bottom" back when this was 34 (the
        // pill's own padding wasn't part of the picture yet then).
        //
        // On the pill itself (PlayerTitlePill), not the StackPanel inside it
        // -- margin on the inner content alone would leave the pill's own
        // background/rounded-corner chip sitting still at the corner while
        // just its content shifted inside it, visibly detached from its own
        // backdrop.
        PlayerTitlePill.Margin = new Thickness(8, 2, 0, 0);
        PlayerTitleBarHost.Height = 46 + 2;

        // This window is about to cover the Scrim's own top-left corner
        // (where its exit button lives) with the video -- collapse that
        // button so it can't show through regardless of exact bounds/
        // z-order; see SetExitButtonVisible's own comment.
        _scrim.SetExitButtonVisible(false);

        // RootBorder's 1px hairline is right for every other screen (it
        // reads as the HUD panel's own edge), but in fullscreen it reads as
        // "this is still just a bordered app window", not actual fullscreen
        // video -- gone for the duration, restored in ExitPlayerFullscreen.
        RootBorder.BorderThickness = new Thickness(0);

        PlayerFullscreenIcon.Data = Geometry.Parse(FullscreenExitIcon);
        PlayerFullscreenButton.ToolTip = "Exit fullscreen";
        ReopenPlayerOverlayPopup();
        ReopenPlayerFullscreenTransportPopup();
    }

    private void ExitPlayerFullscreen()
    {
        _isPlayerFullscreen = false;

        RootBorder.BorderThickness = new Thickness(1);

        PlayerSidebar.Visibility = Visibility.Visible;
        PlayerSidebarColumn.Width = new GridLength(90);

        PlayerFullscreenTransportPopup.IsOpen = false;
        PlayerFullscreenTransportBorder.Child = null;
        PlayerTransportBar.ClearValue(BackgroundProperty);
        PlayerTransportBar.ClearValue(WidthProperty);
        DockPanel.SetDock(PlayerTransportBar, Dock.Bottom);
        PlayerVideoColumnDock.Children.Insert(0, PlayerTransportBar);

        Width = _preFullscreenWidth;
        ApplyBigScreenSize(); // recomputes PlayerVideoHost.Height and Top for the restored width
        Left = _preFullscreenLeft;

        PlayerTitlePill.Margin = new Thickness(0);
        PlayerTitleBarHost.Height = 46;
        _scrim.SetExitButtonVisible(true);

        PlayerFullscreenIcon.Data = Geometry.Parse(FullscreenEnterIcon);
        PlayerFullscreenButton.ToolTip = "Fullscreen";
        ReopenPlayerOverlayPopup();
    }

    // Reverted the extra UpdateLayout()/UpdatePlayerSeekUi() calls this used
    // to make here (chasing the seek-bar-fill-looks-short cosmetic issue) --
    // right after adding those, playback got stuck at end-of-clip: seeking
    // backward and pressing Play again both stopped doing anything. Not
    // fully root-caused yet, but reported live immediately after that
    // change, so reverting first rather than layering another fix on top of
    // a change that broke actual playback. Back to just the plain
    // close+reopen every other popup in this file uses.
    private void ReopenPlayerFullscreenTransportPopup()
    {
        PlayerFullscreenTransportPopup.IsOpen = false;
        Dispatcher.BeginInvoke(new Action(() => PlayerFullscreenTransportPopup.IsOpen = true), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Forces PlayerOverlayPopup's Placement="Relative" position to actually
    /// recompute -- it only reliably does that on a real IsOpen false->true
    /// transition, not just because PlayerVideoView's bounds changed while
    /// it stayed open (see OpenInPlayer's own comment on this same quirk).
    /// Deferred to DispatcherPriority.Loaded since the new bounds from
    /// Enter/ExitPlayerFullscreen aren't necessarily settled yet right here.
    /// </summary>
    private void ReopenPlayerOverlayPopup()
    {
        PlayerOverlayPopup.IsOpen = false;
        Dispatcher.BeginInvoke(new Action(() => PlayerOverlayPopup.IsOpen = true), DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------ save replay

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

        // Still tracked (above) for save notifications even while hidden -- a
        // buffer saved via its own OBS hotkey should still announce a real
        // name, hiding it here only means "don't show it as a button to save
        // from," not "pretend it doesn't exist."
        List<ReplayRow> visibleRows = rows.Where(r => !_settings.HiddenBufferLabels.Contains(r.Label)).ToList();
        _lastReplayRows = visibleRows;

        if (visibleRows.Count == 0)
        {
            AddInfoLine(BufRowsPanel, "All buffers are hidden -- unhide one in Settings > Buffers.");
            return;
        }

        // Online (armed) buffers first -- everything else keeps its original order after them.
        foreach (ReplayRow row in visibleRows.OrderBy(r => r.Status == 1 ? 0 : 1))
            BufRowsPanel.Children.Add(BuildRowButton(row));

        BufRowsPanel.Children.Add(BuildSharedClipLengthControl(visibleRows));
    }

    /// <summary>
    /// Settings > Buffers: one toggle row per buffer the bridge currently
    /// reports, independent of whether it's presently hidden -- a hidden
    /// buffer still needs to show up here so it can be turned back on.
    /// </summary>
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

        // Always first, same as the Start Recording menu's own "Full Scene"
        // row -- this is OBS's own global recording, not a Source Record
        // filter, so it doesn't come from the bridge at all (native
        // obs-websocket only) and exists independently of whether any
        // filters are found below.
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

    /// <summary>
    /// Unlike a Source Record filter's folder (constrained to somewhere inside
    /// ClipsFolder, since Gallery only ever browses within that tree -- see
    /// PickRecordRowFolderAsync/PickBufferDestFolderAsync), OBS's own global
    /// recording path is deliberately NOT constrained here: it's the same
    /// setting as OBS's own Settings > Output > Recording Path, which
    /// legitimately might point somewhere entirely outside Backtrack's clips
    /// folder (a separate drive, an existing recordings library, etc.), and
    /// this is just exposing OBS's own setting, not a Backtrack-specific
    /// per-row override like the filter case is.
    /// </summary>
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

    private string DescribeRecordRowDestDir(string? destDir)
    {
        if (string.IsNullOrEmpty(destDir))
            return "Not set -- recordings stay wherever this filter writes them";
        // Unlike a Replay row's DestDir (Backtrack's own optional override,
        // genuinely absent until someone explicitly picks a folder), a Source
        // Record filter's "path" setting always has some real value in OBS --
        // there's no true "unset" state to read here. Sitting at the plain
        // clips folder root with no subfolder reads the same as "nobody's
        // customized this" to a user, so treat that the same as Not Set too,
        // rather than showing "Main clips folder" for every untouched filter.
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

    /// <summary>
    /// Local-only display override for a buffer/recording-source row -- see
    /// LocalRowNameOverrides' own comment on why this is keyed by the row's
    /// real Label rather than its Key, and why it links buffers/recording
    /// sources backed by the same filter automatically. Every OBS-facing call
    /// (SetReplayRowDestDirAsync, GetRecordRowDestinationFolderAsync, toast
    /// text via _rowLabels, etc.) still passes the REAL originalLabel around
    /// unchanged -- this only ever gets called at the point something is
    /// actually rendered on screen.
    /// </summary>
    private string DisplayLabel(string originalLabel) =>
        _settings.LocalRowNameOverrides.TryGetValue(originalLabel, out string? custom) ? custom : originalLabel;

    private void SetLocalRowNameOverride(string originalLabel, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, originalLabel, StringComparison.Ordinal))
            _settings.LocalRowNameOverrides.Remove(originalLabel);
        else
            _settings.LocalRowNameOverrides[originalLabel] = newName;
        _settings.Save();
    }

    /// <summary>
    /// Wires double-click-to-rename onto a Settings row's name TextBlock --
    /// swaps in a TextBox pre-filled with the current display name, commits
    /// on Enter/LostFocus, Escape cancels. Same interaction shape as the
    /// Gallery's own BeginRename. originalLabel (not whatever's currently
    /// displayed) is always what gets stored as the override's key, so
    /// renaming an already-renamed row still keys off the real underlying
    /// row rather than chaining onto its own display text. onRenamed
    /// refreshes both the Buffers and Recording Sources lists (not just
    /// whichever one this row came from) since the two are linked -- see
    /// LocalRowNameOverrides' own comment.
    /// </summary>
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
            // Hidden buffers don't need their destination fussed over here --
            // this only reappears once the buffer's turned back on. Reacts
            // live to the toggle below, not just on next Settings open.
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

        // This browses (and constrains picks to) THIS PC's own ClipsFolder
        // tree -- see IsWithinClipsFolder's own comment -- which has nothing
        // to do with the transmitter's filesystem when OBS is remote. There's
        // no way to browse a REMOTE folder tree through a local dialog, so
        // rather than let it silently produce a path that's meaningless once
        // pushed to the filter on the other PC, disable the picker entirely
        // when OBS is on a different PC -- same "this control genuinely can't
        // do anything useful right now" reasoning as RefreshRamDiskRemoteGating's
        // own local-section disabling.
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
    private string DescribeRowDestDir(string destDir)
    {
        if (string.IsNullOrEmpty(destDir))
            return "Not set -- clips stay wherever this buffer writes them";
        return IsWithinClipsFolder(destDir, out string relative)
            ? (relative.Length == 0 ? "Main clips folder" : relative)
            : destDir; // outside the clips folder somehow (e.g. set by hand) -- show the raw path rather than hide it
    }

    /// <summary>True if path is ClipsFolder itself or somewhere under it; relative is "" for the former.</summary>
    private bool IsWithinClipsFolder(string path, out string relative)
    {
        string clipsFolder = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (full.Equals(clipsFolder, StringComparison.OrdinalIgnoreCase))
        {
            relative = "";
            return true;
        }
        if (full.StartsWith(clipsFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            relative = full[(clipsFolder.Length + 1)..];
            return true;
        }
        relative = "";
        return false;
    }

    /// <summary>
    /// Lets one specific buffer's clips land in their own subfolder of the main
    /// clips folder -- e.g. a distinct folder per game/source. Constrained to
    /// somewhere inside ClipsFolder, not anywhere on disk, since Gallery only
    /// ever browses within that same tree (see LoadGallery's own comment).
    /// </summary>
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

        // Status 0 (the grey dot -- buffer never armed/running for this row
        // at all, nothing an unbound "(unbound)" hotkey and no green dot
        // don't already say) used to stay clickable anyway: Save Replay on
        // a buffer that was never started sends a real save_row request
        // that can never actually complete, since there's nothing buffered
        // to flush -- ShowProcessingClip's toast below then just sits there
        // forever, since the ReplaySaved event that would dismiss it never
        // fires. Same "disable rather than let a click do nothing useful"
        // treatment as BuildRecordRowButton's own Inactive/NoSignal rows;
        // Error (2) stays clickable on purpose, same as there -- a real
        // buffer that failed its last save is still worth letting the user
        // retry, unlike one that was never running in the first place.
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
                // save_row itself returns almost instantly -- it only flushes the
                // buffer to disk. The actual trim down to this row's clip length
                // happens afterward, async, on the OBS side (see
                // ShowProcessingClip's own comment), so this button
                // re-enabling is NOT the clip being ready.
                //
                // No direct ShowProcessingClip call here anymore -- _obs.ReplaySaving
                // (elsewhere in this file) now fires for this exact same click too,
                // since it's driven by the real OBS-side event, not just a click
                // handler. Calling it here AS WELL used to fire it twice for one
                // save: once instantly on click, then again moments later once
                // ReplaySaving actually arrived, which wipes the still-filling
                // first toast and restarts a fresh one from 0% -- reported live
                // as "the toast plays once then starts over from the beginning",
                // worse (more visible) over a real network round-trip to a remote
                // OBS than a local one. _obs.ReplaySaved is what dismisses it,
                // via CompleteProcessingClip.
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

    // ------------------------------------------------------------ start recording

    // Mirrors obs-replay-slider's ControlPanelDock status-int convention (see
    // its own control-panel-dock.cpp) -- kept in sync by hand since there's no
    // shared header across the process boundary, same as several other
    // setting-name/value conventions this file already relies on matching.
    private const int RecordStatusInactive = 0;  // underlying source isn't actively capturing anything
    private const int RecordStatusStopped = 1;   // capturing fine, just not recording
    private const int RecordStatusRecording = 2;
    private const int RecordStatusError = 3;     // was recording, the output stopped with a failure
    // Distinct from Inactive: the source itself is visible/enabled, but the
    // device has no signal right now (Elgato unplugged, Window Capture with
    // no window selected, etc.) -- clicking Start used to silently arm
    // record_mode=Always and then immediately look like it failed once
    // nothing actually started recording. Disabled below same as Inactive,
    // just with its own label so it's clear WHY.
    private const int RecordStatusNoSignal = 4;

    /// <summary>
    /// "Main" (OBS's own single global recording) plus one row per Source
    /// Record filter obs-replay-slider's Control Panel dock is tracking --
    /// same shape as LoadReplayRowsAsync, but each row is a start/stop toggle
    /// instead of a one-shot save.
    /// </summary>
    private async Task LoadRecordRowsAsync()
    {
        RecRowsPanel.Children.Clear();

        if (!_obs.IsConnected)
        {
            AddInfoLine(RecRowsPanel, !_serverEnabledAtStartup
                ? "OBS's WebSocket server is disabled -- enable it in OBS: Tools > WebSocket Server Settings."
                : "Not connected to OBS.");
            return;
        }

        // Always shown first, even if no Source Record filters exist yet --
        // this is the same recording the Idle tile's own icon reflects. Only
        // ever Recording/Stopped -- OBS's plain GetRecordStatus has no
        // equivalent of a filter's "underlying source inactive" or "error"
        // signal for the whole scene.
        try
        {
            RecordStatus mainStatus = await _obs.GetRecordStatusAsync();
            RecRowsPanel.Children.Add(BuildRecordRowButton("Full Scene", mainStatus.Active ? RecordStatusRecording : RecordStatusStopped,
                start: _obs.StartMainRecordAsync, stop: _obs.StopMainRecordAsync));
        }
        catch (Exception ex)
        {
            AddInfoLine(RecRowsPanel, $"Couldn't read OBS's recording status: {ex.Message}");
        }

        List<RecordRow> rows;
        try
        {
            rows = await _obs.ListRecordRowsAsync();
        }
        catch (Exception ex)
        {
            AddInfoLine(RecRowsPanel, $"Could not reach the Replay Slider bridge: {ex.Message}");
            AddInfoLine(RecRowsPanel, "Needs the patched obs-replay-slider build (see vendor/obs-replay-slider).");
            return;
        }

        // Stopped/Recording (both actually clickable/meaningful right now)
        // sort above Inactive/NoSignal (both disabled, nothing to do with
        // them -- see BuildRecordRowButton's own comment) -- same idea as
        // LoadReplayRowsAsync's own OrderBy just below, keeping the rows you
        // can actually act on from getting buried under a pile of hidden or
        // signal-less ones. OrderBy is stable, so rows within each group
        // keep the bridge's own original order relative to each other.
        List<RecordRow> visibleRows = rows.Where(r => !_settings.HiddenBufferLabels.Contains(r.Label))
            .OrderBy(r => r.Status is RecordStatusStopped or RecordStatusRecording ? 0 : 1)
            .ToList();
        foreach (RecordRow row in visibleRows)
        {
            string key = row.Key;
            RecRowsPanel.Children.Add(BuildRecordRowButton(DisplayLabel(row.Label), row.Status,
                start: () => _obs.StartRecordRowAsync(key), stop: () => _obs.StopRecordRowAsync(key), hotkey: row.Hotkey));
        }
    }

    private const long BytesPerGb = 1024L * 1024L * 1024L;

    /// <summary>Sum of every recognized clip file's size under ClipsFolder, recursive -- same VideoExtensions list the Gallery itself uses, so this matches what a user actually sees as "clips" there.</summary>
    private long GetClipsFolderUsageBytes()
    {
        if (string.IsNullOrWhiteSpace(_settings.ClipsFolder) || !Directory.Exists(_settings.ClipsFolder))
            return 0;
        try
        {
            return Directory.EnumerateFiles(_settings.ClipsFolder, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0; // best effort -- a transient read error here shouldn't block recording on its own
        }
    }

    /// <summary>
    /// Settings > Clips > Storage limit. Checked right before anything that
    /// would create a NEW clip (starting a recording row, saving a replay
    /// row) -- a hard stop once ClipsFolder's total size reaches the limit,
    /// not an auto-cleanup; nothing gets deleted on your behalf here, see
    /// RunAutoDeleteOldClips for the separate opt-in that does.
    /// </summary>
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

    /// <summary>
    /// Settings > Clips > Auto-delete old clips. Runs once at startup (see
    /// caller) and on a repeating timer -- unlike the storage limit above,
    /// this one DOES delete: anything past the configured age, sent to the
    /// Recycle Bin (RecycleBin.Delete, same as every other clip deletion in
    /// this app -- see DeleteClip), never a permanent File.Delete.
    /// </summary>
    private void RunAutoDeleteOldClips()
    {
        if (!_settings.AutoDeleteOldClipsEnabled)
            return;

        string folder = _settings.ClipsFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        DateTime cutoff = DateTime.Now.AddDays(-_settings.AutoDeleteOldClipsAfterDays);
        List<string> oldClips;
        try
        {
            oldClips = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && File.GetLastWriteTime(f) < cutoff)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Auto-delete old clips: couldn't scan clips folder: {ex.Message}");
            return;
        }

        if (oldClips.Count == 0)
            return;

        int deleted = 0;
        foreach (string f in oldClips)
        {
            if (RecycleBin.Delete(f))
                deleted++;
        }

        AppLog.Write($"Auto-delete old clips: removed {deleted}/{oldClips.Count} clip(s) older than {_settings.AutoDeleteOldClipsAfterDays} day(s).");
        if (deleted > 0)
        {
            _toastOverlay.ShowOldClipsAutoDeleted(deleted, _settings.AutoDeleteOldClipsAfterDays);
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
        }
    }

    private DispatcherTimer? _autoDeleteOldClipsTimer;

    /// <summary>Called once from the constructor and again every time the setting's toggled/edited in Settings -- restarts the timer (or stops it) rather than assuming it was never running.</summary>
    private void RestartAutoDeleteOldClipsTimer()
    {
        _autoDeleteOldClipsTimer?.Stop();
        _autoDeleteOldClipsTimer = null;

        if (!_settings.AutoDeleteOldClipsEnabled)
            return;

        RunAutoDeleteOldClips(); // also sweep once immediately, not just on the first tick 6 hours from now
        _autoDeleteOldClipsTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _autoDeleteOldClipsTimer.Tick += (_, _) => RunAutoDeleteOldClips();
        _autoDeleteOldClipsTimer.Start();
    }

    private Button BuildRecordRowButton(string label, int status, Func<Task> start, Func<Task> stop, string? hotkey = null)
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

        // hotkey == null means "no hotkey info available for this row" (the
        // Full Scene row -- OBS's native Start/Stop Recording hotkey isn't
        // queryable over obs-websocket) -- skip it entirely rather than
        // showing a misleading "(unbound)". A Source Record filter row
        // always passes a real (possibly empty) string from RecordRow.Hotkey,
        // same "(unbound)" convention as the Save Replay screen's own rows.
        // Sits to the LEFT of the status dot/label, in the same row -- not
        // stacked underneath -- so it reads as one line, same shape as
        // ReplayRow's own dot+hotkey row on the Save Replay screen.
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

        // Inactive used to be dead code on the plugin side (every row only
        // ever reported Stopped/Recording/Error -- see obs-replay-slider's
        // control-panel-dock.cpp), so leaving this clickable regardless of
        // status was harmless: it could never actually BE Inactive. Now that
        // it's a real, meaningfully-detected state (hidden via its scene's
        // eye icon), clicking Start on it would just try to record a source
        // that genuinely has nothing to capture -- disable it instead of
        // leaving that as a confusing no-op/failure. NoSignal (source
        // visible/enabled, but the device itself has nothing to capture --
        // e.g. a capture card unplugged) gets the same treatment: clicking
        // Start used to silently arm record_mode=Always and then look like
        // it failed the moment the next poll showed nothing actually
        // recording. Both share this disabled path; only the label above
        // differs, so it's clear WHICH of the two is actually going on.
        if (status == RecordStatusInactive || status == RecordStatusNoSignal)
        {
            button.IsEnabled = false;
            return button;
        }

        button.Click += async (_, _) =>
        {
            // Only starting a NEW recording is gated -- stopping one already in
            // progress is always allowed regardless of the storage limit, same
            // reasoning as never blocking a delete.
            if (!recording && TryBlockForStorageLimit(out string? blockMessage))
            {
                MessageBox.Show(this, blockMessage, "Backtrack");
                return;
            }
            button.IsEnabled = false;
            try
            {
                // Nothing to choose between except "recording" vs "everything
                // else" -- Stopped/Error both just attempt a start, same as
                // clicking the equivalent button in ControlPanelDock itself
                // would (e.g. retrying a row stuck in Error).
                await (recording ? stop() : start());

                // Optimistic: reflects what THIS click just successfully asked
                // OBS to do, immediately, rather than waiting on the 2s
                // cooldown below and then a full LoadRecordRowsAsync() re-query
                // to find out. That combination used to be the only way this
                // dot/text ever updated at all -- OBS's own native UI reflects
                // a start/stop instantly since it's driving the change directly,
                // while this was structured to always wait a couple of seconds
                // even though the request Backtrack just made had already
                // succeeded by this point. LoadRecordRowsAsync() below still
                // runs after the cooldown as the real reconciliation pass (e.g.
                // if OBS silently rejected the change for some reason), this
                // just stops the FIRST correct render from being needlessly
                // delayed behind it.
                dot.Fill = (Brush)FindResource(recording ? "Text2" : "Rec");
                stateText.Text = recording ? "Stopped" : "Recording";
                button.Style = (Style)FindResource(recording ? "BufRowButtonNoHover" : "BufRowButton");

                // No direct toast here -- RefreshStatusAsync's own ~1s poll
                // now detects this same start/stop itself and toasts for it,
                // the same way it would for a hotkey-triggered change made
                // entirely outside Backtrack's UI. A toast here too would
                // double up once that poll tick catches up (within ~1s).
                // Keep this row disabled for a couple seconds before it can be
                // toggled again -- rapid-fire start/stop (e.g. an accidental
                // double click) can starve GPU encoder sessions across cycles
                // the same way hammering OBS's own native Start/Stop Recording
                // button would; this isn't fixing that mechanism (out of this
                // app's control), just making an accidental repeat click less
                // likely. Also covers record_mode taking effect asynchronously,
                // same reasoning as the row buttons in obs-replay-slider's own dock.
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

    /// <summary>
    /// One slider for every buffer -- simpler than juggling a separate length
    /// per row, at the cost of them no longer being independently adjustable.
    /// Applies the same length to every row the plugin currently reports.
    /// </summary>
    private const int MinClipSeconds = 15;

    /// <summary>Squares the 0-1 fraction so low values (what almost everyone actually wants) get most of the track, not a couple of pixels at the start.</summary>
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

    /// <summary>Sets a Slider's Value from a mouse position, clamped to the slider's own bounds/range -- shared by the length slider's mouse-down and mouse-move handlers so both compute it identically.</summary>
    private static void SetSliderValueFromMouse(Slider slider, Point mousePos)
    {
        double width = slider.ActualWidth;
        if (width <= 0)
            return;
        double ratio = Math.Clamp(mousePos.X / width, 0.0, 1.0);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }

    private Border BuildSharedClipLengthControl(List<ReplayRow> rows)
    {
        // Can't save more clip than the buffer actually holds -- ReplayBufferMinutes
        // is the same "Replay buffer length" value Settings pushes to every Source
        // Record filter via SetReplayBufferDurationAsync (see BufferDurationSlider),
        // so it's already known locally without a bridge round trip. Floor is still
        // MinClipSeconds even if someone set the buffer shorter than that -- the
        // slider needs a non-zero range to render sensibly either way.
        int maxSeconds = Math.Max(MinClipSeconds, _settings.ReplayBufferMinutes * 60);
        int initial = Math.Min(rows.Count > 0 ? rows[0].LengthSeconds : 60, maxSeconds);

        var label = new TextBlock { Text = "Clip length", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Text1"), VerticalAlignment = VerticalAlignment.Center };
        // IsMoveToPointEnabled jumps the value on click but doesn't hand off
        // to a drag session unless the click happened to land exactly on the
        // thumb -- clicking anywhere else and dragging just didn't do
        // anything after that first jump, unlike the Player's seek bar
        // (PlayerSeekTrack_MouseDown/Move), which captures the mouse itself
        // and recomputes the value on every move regardless of where the
        // drag started. Same fix here instead of relying on the Slider's own
        // click-to-point handling: explicit mouse capture below replicates
        // that "click anywhere, then drag, it just works" feel, while the
        // slider's actual visual style (RowLengthSlider) is untouched.
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
            e.Handled = true; // stop the native Thumb/Track drag logic from also engaging
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

    // ---------------------------------------------------------------- gallery

    private static readonly string[] VideoExtensions = GalleryFormats.VideoExtensions;

    private async Task RefreshGalleryCountAsync()
    {
        // Paired devices open straight into the remote gallery (see
        // GalleryTile_Click), so the Idle tile showing THIS PC's own local
        // count there was always going to read as "0 clips" (or just wrong)
        // for anyone actually using this as a receiver -- match whichever
        // gallery a tile click would actually land on.
        if (!string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            RemoteGalleryListing? rootListing = await _pairing.ListRemoteGalleryAsync("");
            if (rootListing is not null)
            {
                int total = rootListing.Files.Count;
                foreach (string folder in rootListing.Folders)
                    total += await CountRemoteClipsRecursiveAsync(folder);
                GalleryStatus.Text = total == 1 ? "1 clip" : $"{total} clips";
                return;
            }
            // Unreachable right now (peer offline, etc.) -- fall through to
            // the local count rather than showing a stale/wrong number.
        }

        int count = await Task.Run(CountClips);
        GalleryStatus.Text = count == 1 ? "1 clip" : $"{count} clips";
    }

    /// <summary>
    /// Recursive total for one subtree of the remote PC's clips folder --
    /// list_gallery only ever returns one folder's immediate children, not a
    /// real recursive listing, so this walks it a folder at a time the same
    /// way CountClips walks the local tree via SearchOption.AllDirectories.
    /// One network round trip per folder -- fine for the handful of
    /// per-buffer subfolders Backtrack's own Gallery actually creates, not
    /// meant for an arbitrarily deep tree.
    /// </summary>
    private async Task<int> CountRemoteClipsRecursiveAsync(string relativePath)
    {
        RemoteGalleryListing? listing = await _pairing.ListRemoteGalleryAsync(relativePath);
        if (listing is null)
            return 0;

        int count = listing.Files.Count;
        foreach (string folder in listing.Folders)
            count += await CountRemoteClipsRecursiveAsync($"{relativePath}/{folder}");
        return count;
    }

    private int CountClips()
    {
        try
        {
            return Directory.Exists(_settings.ClipsFolder)
                ? Directory.EnumerateFiles(_settings.ClipsFolder, "*", SearchOption.AllDirectories)
                    .Count(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// The single most-recently-written clip anywhere in the whole clips
    /// tree (not just the folder currently being viewed) -- drives the blue
    /// "newest" dot on both BuildClipCard (the clip itself) and
    /// BuildFolderCard (any folder that leads to it), so the trail is
    /// followable while browsing into subfolders, not just visible once
    /// you're already in the right one. Same recursive-scan cost/precedent
    /// as CountClips right above; not worth caching across LoadGallery calls
    /// since a new recording landing is exactly the case this needs to
    /// notice on the very next refresh.
    /// </summary>
    private string? GetNewestClipPath()
    {
        try
        {
            string? newest = Directory.Exists(_settings.ClipsFolder)
                ? Directory.EnumerateFiles(_settings.ClipsFolder, "*", SearchOption.AllDirectories)
                    .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new FileInfo(f))
                    // Same glitched-clip filter LoadGallery/RefreshRecentClipsOverlay
                    // already apply -- without it, this could point the "newest"
                    // dot at a sub-2s glitched clip that's hidden from the Gallery
                    // list entirely, leading nowhere a user could actually find it.
                    .Where(f => TryGetCachedDurationMs(f) is not < 2000)
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault()
                    ?.FullName
                : null;
            // Path.GetFullPath, not just FileInfo.FullName as-is -- matches the same
            // normalization OpenGalleryFolder/GoUpGalleryFolder already rely on for
            // comparing paths (see their own Path.GetFullPath(...).TrimEnd(...) calls),
            // so this side of the comparison can't silently drift out of sync with
            // dir.FullName/file.FullName at the call sites below over some formatting
            // difference (trailing separator, etc.) neither side alone would catch.
            return newest is null ? null : Path.GetFullPath(newest);
        }
        catch
        {
            return null;
        }
    }

    private void GalleryFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        GalleryFilterPlaceholder.Visibility = GalleryFilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _galleryFilterDebounceTimer.Stop();
        _galleryFilterDebounceTimer.Start();
    }

    private void LoadGallery()
    {
        if (_galleryIsRemote)
        {
            _ = LoadRemoteGalleryAsync();
            return;
        }

        GalleryGrid.Children.Clear();
        _galleryCardSelection.Clear();
        _selectedClipPaths.Clear();
        RefreshGallerySelectionUi();
        UpdateGalleryPathBar();

        string folder = GalleryFolder;

        if (!Directory.Exists(folder))
        {
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = $"Folder doesn't exist yet: {folder}\n\nSet a folder that actually has your clips in Settings.",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
                TextWrapping = TextWrapping.Wrap,
                Width = BigWidth() - 40,
            });
            return;
        }

        // Trimmed once here rather than inside each LINQ query below -- both
        // the subfolder and file queries need it, and GalleryFilterBox.Text
        // itself never changes mid-query.
        string filter = GalleryFilterBox.Text.Trim();

        List<DirectoryInfo> subfolders;
        List<FileInfo> files;
        try
        {
            subfolders = Directory.GetDirectories(folder)
                .Select(d => new DirectoryInfo(d))
                .Where(d => filter.Length == 0 || d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            files = Directory.EnumerateFiles(folder)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !_pendingDeletePaths.Contains(Path.GetFullPath(f)))
                .Where(f => filter.Length == 0 || Path.GetFileNameWithoutExtension(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                // Filters out obviously-invalid/glitched recordings (e.g. a save
                // triggered right as OBS started, or a buffer barely armed before
                // saving) -- only when the duration is actually known already
                // (cached from a prior thumbnail pass); a clip that hasn't been
                // probed yet shows optimistically rather than being hidden on a
                // guess, and gets filtered retroactively once its real duration
                // comes back (see BuildClipCard).
                // A bare "< 2000" (not "> 0 and < 2000") matters here: a relational
                // pattern never matches null, so an unprobed clip (null) already
                // falls through to "is not" = true on its own -- adding "> 0" as an
                // extra guard against that case was redundant, and actively wrong,
                // since it also let a genuine 0ms glitched clip slip through the
                // same way (0 isn't > 0 either).
                .Where(f => TryGetCachedDurationMs(f) is not < 2000)
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
        }
        catch (Exception ex)
        {
            GalleryGrid.Children.Add(new TextBlock { Text = $"Couldn't read that folder: {ex.Message}", Foreground = (Brush)FindResource("Rec"), TextWrapping = TextWrapping.Wrap });
            return;
        }

        if (subfolders.Count == 0 && files.Count == 0)
        {
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = filter.Length == 0 ? "No clips in this folder yet." : $"Nothing here matches \"{filter}\".",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
            });
            GalleryStatus.Text = "0 clips";
            return;
        }

        string? newestClipPath = GetNewestClipPath();

        foreach (DirectoryInfo dir in subfolders)
        {
            // Path.GetFullPath on dir.FullName too, not just newestClipPath's own
            // side -- normalizes both to the exact same form GetNewestClipPath
            // already produces, so this can't miss a match over a formatting
            // difference neither .FullName alone happens to hit.
            string dirFull = Path.GetFullPath(dir.FullName);
            // Ancestor check via a trailing separator, not a bare StartsWith
            // -- otherwise a folder named e.g. "Clips" would false-positive
            // match a sibling "Clips2" that happens to share the prefix.
            bool leadsToNewest = newestClipPath is not null
                && newestClipPath.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            GalleryGrid.Children.Add(BuildFolderCard(dir.Name, () => OpenGalleryFolder(dir.FullName), leadsToNewest));
        }

        foreach (FileInfo file in files)
            GalleryGrid.Children.Add(BuildClipCard(file,
                isNewest: newestClipPath is not null && string.Equals(Path.GetFullPath(file.FullName), newestClipPath, StringComparison.OrdinalIgnoreCase)));

        GalleryStatus.Text = files.Count == 1 ? "1 clip" : $"{files.Count} clips";
    }

    /// <summary>
    /// The folder browsing here is scoped to the clips folder tree -- both so "Up"
    /// has an unambiguous stopping point and so mass-move destinations picked via
    /// the OS folder dialog land somewhere this same view can browse back to.
    /// </summary>
    private void UpdateGalleryPathBar()
    {
        if (_galleryIsRemote)
        {
            bool remoteAtRoot = _currentRemoteGalleryFolder is null;
            GalleryPathBar.Visibility = remoteAtRoot ? Visibility.Collapsed : Visibility.Visible;
            if (!remoteAtRoot)
                GalleryPathText.Text = _currentRemoteGalleryFolder;
            return;
        }

        bool atRoot = _currentGalleryFolder is null;
        GalleryPathBar.Visibility = atRoot ? Visibility.Collapsed : Visibility.Visible;
        if (atRoot)
            return;

        string root = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(GalleryFolder).TrimEnd(Path.DirectorySeparatorChar);
        string relative = full.Length > root.Length ? full[(root.Length + 1)..] : full;
        GalleryPathText.Text = relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private void OpenGalleryFolder(string path)
    {
        // Filter is scoped to whatever folder is currently showing -- carrying
        // a stale term into a different folder would just look like that
        // folder is missing clips instead of the term simply not matching there.
        GalleryFilterBox.Text = string.Empty;
        string root = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _currentGalleryFolder = null;
        }
        else
        {
            _currentGalleryFolder = full;
        }
        LoadGallery();
    }

    /// <summary>Descends into a subfolder of the paired PC's own ClipsFolder root -- relativePath uses '/' throughout regardless of either PC's actual OS.</summary>
    private void OpenRemoteGalleryFolder(string name)
    {
        GalleryFilterBox.Text = string.Empty; // see OpenGalleryFolder's own comment
        _currentRemoteGalleryFolder = _currentRemoteGalleryFolder is null ? name : $"{_currentRemoteGalleryFolder}/{name}";
        LoadGallery();
    }

    private void GalleryUp_Click(object sender, MouseButtonEventArgs e)
    {
        GalleryFilterBox.Text = string.Empty; // see OpenGalleryFolder's own comment
        if (_galleryIsRemote)
        {
            if (_currentRemoteGalleryFolder is not null)
            {
                int lastSlash = _currentRemoteGalleryFolder.LastIndexOf('/');
                _currentRemoteGalleryFolder = lastSlash < 0 ? null : _currentRemoteGalleryFolder[..lastSlash];
            }
            LoadGallery();
            return;
        }

        string root = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string current = Path.GetFullPath(GalleryFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase) || !current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _currentGalleryFolder = null;
        }
        else
        {
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) || !parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                _currentGalleryFolder = null;
            }
            else
            {
                _currentGalleryFolder = parent;
            }
        }
        LoadGallery();
    }

    /// <summary>
    /// Remote counterpart of the local LoadGallery() above -- same shape (folder
    /// tiles first, then clip tiles, then a status count), fetched over the
    /// pairing connection instead of the local filesystem. Thumbnails are
    /// fetched from the transmitter PC's own local cache (see
    /// LoadRemoteThumbnailAsync/HandleGetThumbnailAsync), not generated here.
    /// No selection/mass-actions/trim, though -- those operate on local files
    /// by design; a remote clip plays by streaming now (see
    /// OpenRemoteClipStreaming), and none of that can apply to it unless it
    /// also happens to already be sitting in RemoteCache (SyncRemoteClipsAsync).
    /// </summary>
    private async Task LoadRemoteGalleryAsync()
    {
        _galleryCardSelection.Clear();
        _selectedClipPaths.Clear();
        RefreshGallerySelectionUi();
        UpdateGalleryPathBar();
        GalleryStatus.Text = "Loading...";

        RemoteGalleryListing? listing = await _pairing.ListRemoteGalleryAsync(_currentRemoteGalleryFolder ?? "");
        if (listing is null)
        {
            // Only on the true->false transition -- see _remotePcWasConnected's
            // own comment on why a first-ever failed attempt doesn't also
            // fire this.
            if (_remotePcWasConnected)
            {
                _remotePcWasConnected = false;
                _toastOverlay.ShowRemotePcDisconnected(_settings.PairedPeerHost ?? _settings.PairedPeerName ?? "The remote PC");
            }
            GalleryGrid.Children.Clear();
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = $"Couldn't reach {_settings.PairedPeerName}'s Backtrack -- make sure it's running and paired.",
                FontSize = 12,
                Foreground = (Brush)FindResource("Rec"),
                TextWrapping = TextWrapping.Wrap,
                Width = BigWidth() - 40,
            });
            GalleryStatus.Text = "";
            return;
        }
        _remotePcWasConnected = true;

        // Remote counterpart to GetNewestClipPath() -- best effort: a failed
        // fetch just means no "newest" dot shows this refresh, not a broken
        // gallery load.
        string? newestRemotePath = await _pairing.GetRemoteNewestClipPathAsync();

        // Same filtering as the local LoadGallery -- current folder only, by name.
        string filter = GalleryFilterBox.Text.Trim();

        // Same "hide it while its undo toast is still counting down" trick
        // as the local Gallery's own _pendingDeletePaths (see QueueDeleteWithUndo) --
        // relativePath-keyed instead of local-full-path-keyed, since a remote
        // card has no local FileInfo to key off of.
        List<RemoteGalleryFile> files = listing.Files
            .Where(f => !_pendingRemoteDeletePaths.Contains(RemoteClipRelativePath(f.Name)))
            .Where(f => filter.Length == 0 || Path.GetFileNameWithoutExtension(f.Name).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<string> folders = listing.Folders
            .Where(name => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (folders.Count == 0 && files.Count == 0)
        {
            GalleryGrid.Children.Clear();
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = filter.Length == 0 ? "No clips in this folder yet." : $"Nothing here matches \"{filter}\".",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
            });
            GalleryStatus.Text = "0 clips";
            return;
        }

        var newCards = new List<UIElement>();
        foreach (string name in folders)
        {
            string folderRelPath = RemoteClipRelativePath(name);
            // Ancestor check via a trailing separator, same reasoning as the
            // local Gallery's own leadsToNewest just above -- otherwise a
            // folder named e.g. "Clips" would false-positive match a sibling
            // "Clips2" that happens to share the prefix.
            bool leadsToNewest = newestRemotePath is not null
                && (string.Equals(newestRemotePath, folderRelPath, StringComparison.OrdinalIgnoreCase)
                    || newestRemotePath.StartsWith(folderRelPath + "/", StringComparison.OrdinalIgnoreCase));
            newCards.Add(BuildFolderCard(name, () => OpenRemoteGalleryFolder(name), leadsToNewest));
        }

        foreach (RemoteGalleryFile file in files)
            newCards.Add(BuildRemoteClipCard(file,
                isNewest: newestRemotePath is not null && string.Equals(RemoteClipRelativePath(file.Name), newestRemotePath, StringComparison.OrdinalIgnoreCase)));

        GalleryGrid.Children.Clear();
        foreach (UIElement card in newCards)
            GalleryGrid.Children.Add(card);

        GalleryStatus.Text = files.Count == 1 ? "1 clip" : $"{files.Count} clips";
    }

    private string RemoteClipRelativePath(string fileName) =>
        _currentRemoteGalleryFolder is null ? fileName : $"{_currentRemoteGalleryFolder}/{fileName}";

    /// <summary>
    /// Walks the WHOLE paired PC's clips tree (every subfolder, not just one
    /// folder at a time like ListRemoteGalleryAsync itself) and returns every
    /// clip found, each with its own relative path already computed. Shared
    /// by SyncRemoteClipsAsync (which then filters this down to what's
    /// actually missing locally) and RefreshRecentClipsOverlayRemoteAsync
    /// (which just needs the most-recently-modified few, same idea as the
    /// local quick-gallery overlay's own recursive scan). Null return means
    /// unreachable (transmitter offline, wrong password, etc.) -- both
    /// callers already have their own "just try again later" fallback for
    /// that, so this doesn't need one of its own.
    /// </summary>
    private async Task<List<(string RelativePath, RemoteGalleryFile File)>?> ListAllRemoteClipsAsync()
    {
        var foldersToWalk = new Queue<string?>();
        foldersToWalk.Enqueue(null); // null == root, same convention as _currentRemoteGalleryFolder
        var all = new List<(string RelativePath, RemoteGalleryFile File)>();

        while (foldersToWalk.Count > 0)
        {
            string? folder = foldersToWalk.Dequeue();
            RemoteGalleryListing? listing = await _pairing.ListRemoteGalleryAsync(folder ?? "");
            if (listing is null)
                return null;

            foreach (string subfolder in listing.Folders)
                foldersToWalk.Enqueue(folder is null ? subfolder : $"{folder}/{subfolder}");

            foreach (RemoteGalleryFile file in listing.Files)
                all.Add((folder is null ? file.Name : $"{folder}/{file.Name}", file));
        }

        return all;
    }

    /// <summary>
    /// Background counterpart to OpenRemoteClipAsync's on-demand, one-clip-
    /// at-a-time download: pulls down anything genuinely missing from this
    /// PC's own RemoteCache, across the WHOLE tree. Pure add-only mirror by
    /// design -- never deletes a local RemoteCache file just because it's
    /// since vanished remotely (that's a real, separate, deliberately-
    /// untouched case: an already-downloaded copy staying put even after the
    /// transmitter's own original is gone is the whole point of it being a
    /// local CACHE, not a live view). Skips anything in
    /// _pendingRemoteDeletePaths -- a clip the user just deleted from the
    /// remote Gallery here is still sitting on the transmitter for a few
    /// seconds until that request lands, and this walk running mid-flight
    /// shouldn't re-download something about to be gone anyway.
    ///
    /// Two phases, not one combined walk-and-download pass -- listing every
    /// folder (ListAllRemoteClipsAsync) is cheap, metadata only, and needs to
    /// happen in full BEFORE the first download starts, so `progress` can
    /// report a real "X of Y" fraction across the WHOLE tree from the very
    /// first file, instead of a number that keeps moving the goalposts as
    /// later folders are still being discovered.
    /// </summary>
    private async Task SyncRemoteClipsAsync(IProgress<double>? progress = null)
    {
        List<(string RelativePath, RemoteGalleryFile File)>? all = await ListAllRemoteClipsAsync();
        // Unreachable -- give up quietly rather than erroring; the periodic
        // timer (or the next Gallery visit) tries again from scratch.
        if (all is null)
            return;

        var toDownload = new List<(string RelativePath, RemoteGalleryFile File, string DestPath)>();
        foreach ((string relativePath, RemoteGalleryFile file) in all)
        {
            if (_pendingRemoteDeletePaths.Contains(relativePath))
                continue;

            string destPath = GetRemoteClipCachePath(relativePath, file.Name);
            if (File.Exists(destPath))
                continue;

            toDownload.Add((relativePath, file, destPath));
        }

        if (toDownload.Count == 0)
        {
            progress?.Report(1.0);
            return;
        }

        for (int i = 0; i < toDownload.Count; i++)
        {
            (string relativePath, RemoteGalleryFile file, string destPath) = toDownload[i];
            int completed = i; // captured per-iteration for the sub-progress closure below
            // Sub-progress WITHIN this one file (same IProgress<double> shape
            // DownloadRemoteClipAsync already reports for a single clip in
            // OpenRemoteClipAsync) folded into the overall fraction, so the
            // number moves smoothly through one big clip instead of jumping
            // only once it's fully landed.
            var fileProgress = progress is null ? null : new Progress<double>(p => progress.Report((completed + p) / toDownload.Count));

            // Failure (locked, disappeared mid-walk, a transient network
            // hiccup) isn't fatal to the rest of the tree -- DownloadRemoteClipAsync
            // already reports it as (false, error) rather than throwing;
            // just move on, this file gets picked up again next pass.
            await _pairing.DownloadRemoteClipAsync(relativePath, destPath, fileProgress);
            progress?.Report((double)(i + 1) / toDownload.Count);
        }
    }

    private Border BuildRemoteClipCard(RemoteGalleryFile file, bool isNewest = false)
    {
        var iconHost = new Border
        {
            Background = (Brush)FindResource("ThumbnailBg"),
            Height = 118,
            Cursor = Cursors.Hand,
            ClipToBounds = true,
        };
        // Play-triangle placeholder, shown until the real frame arrives (see
        // LoadRemoteThumbnailAsync below) -- the transmitter PC already has
        // this clip thumbnailed for its own local Gallery in the common case
        // (PrewarmGalleryThumbnailsAsync runs there too), so this is usually
        // a quick fetch, not a fresh generation.
        var playGlyph = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M8,5.14V19.14L19,12.14L8,5.14Z"),
            Fill = (Brush)FindResource("Text1"),
            Width = 38,
            Height = 38,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };

        var iconGrid = new Grid();
        iconGrid.Children.Add(playGlyph);
        iconGrid.Children.Add(thumbImage);
        iconHost.Child = iconGrid;

        var title = new TextBlock
        {
            Text = file.Name,
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 7, 0, 1),
            Cursor = Cursors.IBeam,
        };
        double mb = file.Size / (1024.0 * 1024.0);
        // Same today-vs-not truncation as the local card (BuildClipCard) --
        // this was always using the "not today" format unconditionally,
        // showing a redundant "Aug 12" on a clip made minutes ago just
        // because it came from the remote gallery instead of the local one.
        DateTime modified = file.Modified.ToLocalTime();
        string dateText = modified.Date == DateTime.Today ? modified.ToString("h:mm tt") : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock
        {
            Text = $"{dateText} · {mb:0.#} MB",
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
        };

        UIElement titleRow = isNewest ? WithNewestDot(title, "Newest clip") : title;
        var content = new StackPanel();
        content.Children.Add(iconHost);
        content.Children.Add(titleRow);
        content.Children.Add(sub);

        var card = new Border { Width = 210, Child = content };
        string relativePath = RemoteClipRelativePath(file.Name);

        // Click-to-open lives on iconHost specifically, not the whole card --
        // same reason BuildClipCard's local equivalent puts it on `thumb`
        // alone, not `card`: title needs its OWN double-click (rename) below
        // without that also bubbling up into opening the clip.
        _ = LoadRemoteThumbnailAsync(relativePath, file, thumbImage);
        iconHost.MouseLeftButtonUp += (_, _) => OpenRemoteClipStreaming(relativePath, file);

        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                BeginRenameRemote(card, title, relativePath, file);
        };

        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var openFolderItem = new MenuItem { Header = "Open file location", Style = (Style)FindResource("DarkMenuItem") };
        openFolderItem.Click += (_, _) => _ = OpenRemoteClipFileLocationAsync(relativePath, file);
        var copyPathItem = new MenuItem { Header = "Copy path", Style = (Style)FindResource("DarkMenuItem") };
        copyPathItem.Click += (_, _) => _ = CopyRemoteClipPathAsync(relativePath, file);
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem"), Foreground = (Brush)FindResource("Rec") };
        deleteItem.Click += (_, _) => DeleteRemoteClip(relativePath, file);
        contextMenu.Items.Add(openFolderItem);
        contextMenu.Items.Add(copyPathItem);
        contextMenu.Items.Add(deleteItem);
        iconHost.ContextMenu = contextMenu;

        return card;
    }

    /// <summary>
    /// Downloads this remote clip into the local per-peer cache if it isn't
    /// already there (same cache OpenRemoteClipAsync itself uses -- opening
    /// it once already leaves it cached for this to just reveal instantly),
    /// then reveals THAT local copy in Explorer. There's no way to open a
    /// window onto the transmitter PC's own filesystem from here; the local
    /// cached copy is the closest real equivalent "Open file location" can
    /// mean for a clip that lives on a different machine.
    /// </summary>
    private async Task OpenRemoteClipFileLocationAsync(string relativePath, RemoteGalleryFile file)
    {
        string destPath = GetRemoteClipCachePath(relativePath, file.Name);
        if (!File.Exists(destPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            (bool success, string? error) = await _pairing.DownloadRemoteClipAsync(relativePath, destPath);
            if (!success)
            {
                MessageBox.Show(this, $"Couldn't download that clip: {error}", "Backtrack");
                return;
            }
        }
        RevealInExplorerAndClose(destPath);
    }

    /// <summary>Same "ensure the local cache copy exists first" reasoning as OpenRemoteClipFileLocationAsync just above -- there's no real local path to copy for a clip that lives on a different machine until it's actually cached.</summary>
    private async Task CopyRemoteClipPathAsync(string relativePath, RemoteGalleryFile file)
    {
        string destPath = GetRemoteClipCachePath(relativePath, file.Name);
        if (!File.Exists(destPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            (bool success, string? error) = await _pairing.DownloadRemoteClipAsync(relativePath, destPath);
            if (!success)
            {
                MessageBox.Show(this, $"Couldn't download that clip: {error}", "Backtrack");
                return;
            }
        }
        Clipboard.SetText(destPath);
    }

    /// <summary>
    /// Same confirm-then-undo-toast shape as local DeleteClip/QueueDeleteWithUndo,
    /// just with the actual delete itself (still real, on the OTHER PC -- see
    /// HandleDeleteClip) deferred to onExpire instead of running immediately.
    /// The network round trip only ever happens once the undo window has
    /// actually run out, so hitting Undo genuinely cancels it before the
    /// stream PC ever finds out -- not just a local-only "put it back" the
    /// way it would have to be for something already sent across the wire.
    /// </summary>
    private void DeleteRemoteClip(string relativePath, RemoteGalleryFile file)
    {
        ShowConfirmDialog(
            $"Are you sure you want to delete \"{file.Name}\"? This deletes the real clip on {_settings.PairedPeerName}'s PC (sent to its recycle bin there), not just this view.",
            "Delete",
            confirmed =>
            {
                if (confirmed)
                    QueueRemoteDeleteWithUndo(relativePath, file.Name, file);
            });
    }

    /// <summary>
    /// Remote counterpart of QueueDeleteWithUndo. `file` is optional --
    /// PlayerDelete_Click doesn't have a RemoteGalleryFile handy (Player only
    /// ever knows the local cached FileInfo it's playing), so it's null
    /// there and this just skips the local-cache-cleanup step, which needs
    /// file.Modified/file.Size to recompute the thumbnail cache key anyway.
    /// </summary>
    private void QueueRemoteDeleteWithUndo(string relativePath, string displayName, RemoteGalleryFile? file)
    {
        _pendingRemoteDeletePaths.Add(relativePath);
        if (GalleryPanel.Visibility == Visibility.Visible)
            LoadGallery();
        // Same "instantly gone, not just after the undo window expires" shape
        // as QueueDeleteWithUndo's own local version -- RefreshRecentClipsOverlay's
        // _pendingRemoteDeletePaths filter is what makes this take effect
        // immediately regardless of which screen the delete was triggered from.
        RefreshRecentClipsOverlay();

        _toastOverlay.ShowDeleteUndo(displayName,
            onExpire: () =>
            {
                _pendingRemoteDeletePaths.Remove(relativePath);
                _ = FinishRemoteDeleteAsync(relativePath, displayName, file);
            },
            onUndo: () =>
            {
                _pendingRemoteDeletePaths.Remove(relativePath);
                Dispatcher.BeginInvoke(() =>
                {
                    LoadGallery();
                    RefreshRecentClipsOverlay();
                });
            });
    }

    private async Task FinishRemoteDeleteAsync(string relativePath, string displayName, RemoteGalleryFile? file)
    {
        (bool success, string? error) = await _pairing.DeleteRemoteClipAsync(relativePath);
        if (!success)
        {
            // Discarded, not awaited -- this method is itself async (unlike
            // QueueDeleteWithUndo's plain onExpire lambda, which never
            // triggers this same CS4014), so an unawaited DispatcherOperation
            // needs the explicit discard to say "yes, fire-and-forget is the
            // point" rather than a real oversight.
            _ = Dispatcher.BeginInvoke(() => MessageBox.Show(this, $"Couldn't delete \"{displayName}\": {error}", "Backtrack"));
        }
        else if (file is not null)
        {
            // Best-effort cleanup of whatever this PC itself cached for that
            // clip -- the real delete already succeeded regardless of
            // whether either of these exist or this cleanup itself fails.
            try { File.Delete(GetRemoteClipCachePath(relativePath, file.Name)); } catch { /* best effort */ }
            string? thumbCache = GetRemoteThumbnailCachePath(relativePath, file.Modified, file.Size);
            if (thumbCache is not null)
            {
                try { File.Delete(thumbCache); } catch { /* best effort */ }
            }
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
            else
                _ = RefreshGalleryCountAsync();
        });
    }

    /// <summary>
    /// Remote counterpart of BeginRename -- same in-place TextBox swap, but
    /// commits via RenameRemoteClipAsync (a real rename on the OTHER PC, see
    /// HandleRenameClip) instead of a local File.Move. Doesn't try to rename
    /// this PC's own local cached copy (if any) to match -- it's keyed on
    /// the OLD filename and just goes stale/orphaned; the clip re-downloads
    /// fresh under its new name next time it's opened, same as any clip
    /// this PC has never cached before.
    /// </summary>
    private void BeginRenameRemote(Border card, TextBlock title, string relativePath, RemoteGalleryFile file)
    {
        _isRenamingCard = true;
        bool finished = false;

        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Margin = title.Margin,
            Background = (Brush)FindResource("RowBg"),
            Foreground = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(0),
        };

        var stack = (StackPanel)card.Child;
        int index = stack.Children.IndexOf(title);
        stack.Children.RemoveAt(index);
        stack.Children.Insert(index, box);

        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (!finished) { finished = true; _ = CommitRenameAsync(); } }
            else if (e.Key == Key.Escape) { e.Handled = true; if (!finished) { finished = true; _isRenamingCard = false; LoadGallery(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; _ = CommitRenameAsync(); } };

        async Task CommitRenameAsync()
        {
            _isRenamingCard = false;
            string newName = box.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != Path.GetFileNameWithoutExtension(file.Name))
            {
                (bool success, string? error, _) = await _pairing.RenameRemoteClipAsync(relativePath, newName);
                if (!success)
                    MessageBox.Show(this, $"Couldn't rename: {error}", "Backtrack");
            }
            LoadGallery();
        }
    }

    /// <summary>
    /// Fetches (and locally caches, per peer) a remote clip's thumbnail --
    /// keyed on relativePath + the file's modified/size as reported by the
    /// listing (same idea as GetThumbnailCachePath's local key), so a changed
    /// remote file gets a fresh thumbnail instead of showing a stale frame
    /// forever. The play-triangle placeholder stays put on any failure --
    /// not worth surfacing an error over a missing thumbnail.
    /// </summary>
    /// <summary>
    /// Deterministic per-peer thumbnail cache path for one remote clip --
    /// shared by LoadRemoteThumbnailAsync (which downloads into it if it's
    /// not already there) and OpenRemoteClipAsync's loading UI (which only
    /// ever reads it, since Gallery already triggered the download for its
    /// own card by the time a clip's actually clickable). Null if not
    /// paired -- same "nothing to key this against" case as its caller.
    /// </summary>
    private string? GetRemoteThumbnailCachePath(string relativePath, DateTime modified, long size)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerDeviceId))
            return null;

        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Backtrack", "RemoteThumbnails", _settings.PairedPeerDeviceId);
        string key = $"{relativePath}|{modified.Ticks}|{size}";
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(cacheDir, $"{hash}.jpg");
    }

    private async Task LoadRemoteThumbnailAsync(string relativePath, RemoteGalleryFile file, Image target)
    {
        string? cachePath = GetRemoteThumbnailCachePath(relativePath, file.Modified, file.Size);
        if (cachePath is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        if (!File.Exists(cachePath))
        {
            (bool success, _) = await _pairing.DownloadRemoteThumbnailAsync(relativePath, cachePath);
            if (!success)
                return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(cachePath);
            bitmap.EndInit();
            bitmap.Freeze();
            target.Source = bitmap;
        }
        catch
        {
            // Cached file is somehow unreadable -- leave the play-triangle placeholder.
        }
    }

    // Bumped by every OpenInPlayer call (local clip, or a remote clip that
    // already finished downloading) AND at the start of every
    // OpenRemoteClipAsync call -- i.e. any "the user wants to be watching
    // THIS clip now" event, whichever kind of clip it is. OpenRemoteClipAsync
    // captures it as its own "am I still the one the user actually wants"
    // token. Fixes a real reported glitch: click clip A, click clip B (local
    // OR remote) before A's download finishes, and A's download completing
    // later would still unconditionally call OpenInPlayer(A) -- yanking
    // playback back from B (already playing) to A mid-clip, with zero
    // indication why. Every await in OpenRemoteClipAsync below is followed
    // by a check against this so a superseded request just quietly gives up
    // instead.
    private long _clipOpenToken;

    /// <summary>
    /// Opens a remote clip in the Player WITHOUT downloading it first --
    /// libvlc plays straight off RemoteClipStreamServer's own local HTTP
    /// relay, which pulls bytes from the paired PC over the network as
    /// playback needs them (see its own class comment). Requested directly:
    /// clips should stream, not require a manual full download before you
    /// can watch anything. Deliberately never writes the clip to disk here
    /// at all -- no local copy survives after playback, unlike
    /// DownloadRemoteClipAsync's own RemoteCache.
    ///
    /// The one exception: if SyncRemoteClipsAsync's own periodic background
    /// mirror (a separate, independently-requested feature) already grabbed
    /// this exact clip, playing the real local copy it already has is
    /// strictly better than streaming a redundant second copy over the
    /// network for no reason -- and it's the only way Trim/Rename/Delete
    /// ever become available for a remote clip at all (see OpenInPlayer's
    /// own Player actions), all of which need a genuine local file and
    /// stay unavailable during pure streaming, same as the remote Gallery's
    /// own long-documented "no mass-actions/trim on an undownloaded remote
    /// clip" limitation.
    /// </summary>
    private void OpenRemoteClipStreaming(string relativePath, RemoteGalleryFile file)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerDeviceId))
            return;

        // Always streams, deliberately, even if SyncRemoteClipsAsync's own
        // background mirror already has a local copy of this exact clip --
        // preferring the local copy here was the actual bug reported live
        // ("why is it still syncing instead of streaming?"): entering the
        // remote Gallery used to run a full sync pass first, which routinely
        // finished downloading a clip before it was ever clicked, so this
        // fast-path effectively won every time and streaming never actually
        // ran. "Always stream, never cache" was the explicit answer when
        // this was first asked about; this is that, literally.
        long myToken = ++_clipOpenToken;
        string? thumbnailCachePath = GetRemoteThumbnailCachePath(relativePath, file.Modified, file.Size);
        ShowPlayerLoadingUi(file.Name, thumbnailCachePath);
        // ShowPlayerLoadingUi only clears _currentPlayerFile (no local file
        // for a streamed clip, ever) -- this is still a real, actionable
        // remote clip though, just without a local copy backing it. Trim/
        // Rename/Delete all key off THIS, not _currentPlayerFile, so they
        // stay available while streaming instead of silently no-op'ing --
        // see PlayerDelete_Click/PlayerRename_Click/RunTrimAsync's own
        // streaming branches.
        _currentPlayerRemoteOrigin = (relativePath, _settings.PairedPeerDeviceId);

        // No FileInfo at all for a streamed clip -- these come straight from
        // the metadata list_gallery already gave us (RemoteGalleryFile),
        // same info a real download's FileInfo would've reported anyway.
        StatSize.Text = $"{file.Size / 1024.0 / 1024.0:0.#} MB";
        StatDate.Text = $"{file.Modified.ToLocalTime():MMM d, yyyy h:mm tt}";

        string streamUrl = _remoteStreamServer.PrepareStream(relativePath);
        _currentStreamToken = streamUrl[(streamUrl.LastIndexOf('/') + 1)..];
        var mediaUri = new Uri(streamUrl);
        Dispatcher.BeginInvoke(new Action(() => StartPlayerPlayback(mediaUri, myToken, hideFreezeFrameOnFirstPlay: true)), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The token segment of whatever stream URL is currently playing (see
    /// PrepareStream) -- null while playing a real local file. Only
    /// meaningful use so far: after a remote rename while streaming, the
    /// session RemoteClipStreamServer already has open for this token still
    /// remembers the OLD relative path, so any FUTURE seek on the same
    /// still-open player would ask for a path that no longer exists on the
    /// transmitter. Updating the session in place (see PlayerRename_Click)
    /// fixes that without needing to restart playback over a brand new URL.
    /// </summary>
    private string? _currentStreamToken;

    /// <summary>
    /// Deterministic per-peer local cache path for one remote clip's actual
    /// video file -- shared by OpenRemoteClipAsync, the remote card's context
    /// menu ("Open file location"/"Delete" need to know where a local copy
    /// would be), and remote-origin cleanup after a remote delete/rename.
    /// Doesn't create the directory (unlike OpenRemoteClipAsync's own use of
    /// it, which downloads into it) -- callers that only ever read from here
    /// don't need it to exist.
    /// </summary>
    private string GetRemoteClipCachePath(string relativePath, string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Backtrack", "RemoteCache", _settings.PairedPeerDeviceId ?? "",
        Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "",
        fileName);

    /// <summary>
    /// Immediate Player screen switch for a remote clip that hasn't finished
    /// downloading yet -- same shape as OpenInPlayer's own screen-switch/
    /// freeze-frame steps, but there's no local FileInfo to work from until
    /// the download actually lands, so this covers the video area with
    /// whatever thumbnail Gallery's card already fetched (near-instant,
    /// already on disk from LoadRemoteThumbnailAsync) instead of leaving
    /// Player looking blank/frozen for however long the transfer takes.
    /// OpenInPlayer takes back over (with a real FileInfo, and its own
    /// freshly-generated local thumbnail) once the download finishes.
    /// </summary>
    private void ShowPlayerLoadingUi(string title, string? thumbnailCachePath)
    {
        _currentPlayerFile = null;
        _trimStart = null;
        _trimEnd = null;
        TrimPanel.Visibility = Visibility.Collapsed;
        PlayerTransportRow.Visibility = Visibility.Visible;
        MoveTransportControlsForTrim(intoTrimRow: false);
        StopPreviewLoop();
        ResetPlaybackSpeed();
        PlayerVideoView.Visibility = Visibility.Visible;

        ShowScreen(Screen.Player);
        PlayerTitle.Text = title;
        ReopenPlayerOverlayPopup();

        StatSize.Text = "";
        StatDate.Text = "";
        StatResolution.Text = "";
        StatFps.Text = "";

        StopPlayerPlayback();

        if (thumbnailCachePath is not null && File.Exists(thumbnailCachePath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(thumbnailCachePath);
                bitmap.EndInit();
                bitmap.Freeze();
                PlayerFreezeFrame.Source = bitmap;
            }
            catch
            {
                // Leave whatever PlayerFreezeFrame last had rather than fail over this.
            }
        }

        PlayerFreezeFramePopup.IsOpen = false;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            PlayerFreezeFramePopup.IsOpen = true;
            // NOT started here on purpose, unlike ShowPlayerFreezeFrame's own
            // timer start -- this cover needs to stay up for as long as the
            // download takes (could be several seconds), not the fixed
            // decode-glitch window that timer is tuned for. OpenInPlayer's
            // later ShowPlayerFreezeFrame call, once the file's actually on
            // disk, is what starts the real timed hide.
            _freezeFrameTimer.Stop();
            ReopenPlayerOverlayPopup();
        }), DispatcherPriority.Loaded);
    }

    private void ToggleClipSelected(FileInfo file)
    {
        if (!_selectedClipPaths.Add(file.FullName))
            _selectedClipPaths.Remove(file.FullName);
        RefreshGallerySelectionUi();
    }

    private void RefreshGallerySelectionUi()
    {
        int count = _selectedClipPaths.Count;
        GallerySelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GallerySelectionCountText.Text = count == 1 ? "1 selected" : $"{count} selected";

        foreach (var (file, circle, thumb) in _galleryCardSelection)
        {
            bool selected = _selectedClipPaths.Contains(file.FullName);
            circle.Background = selected ? (Brush)FindResource("Green") : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            circle.BorderBrush = selected ? (Brush)FindResource("Green") : (Brush)FindResource("Text0");

            // Selection mode active -> every circle stays visible, not just the
            // hovered one. Mode inactive -> only the mouse-over hover handlers
            // decide visibility, except a card the mouse isn't over right now needs
            // hiding explicitly here too (e.g. selection was just cleared via
            // Cancel or a mass action, not by moving the mouse off it).
            if (count > 0)
                circle.Visibility = Visibility.Visible;
            else if (!thumb.IsMouseOver)
                circle.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelSelection_Click(object sender, RoutedEventArgs e)
    {
        _selectedClipPaths.Clear();
        RefreshGallerySelectionUi();
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        List<FileInfo> targets = _galleryCardSelection
            .Where(c => _selectedClipPaths.Contains(c.File.FullName))
            .Select(c => c.File)
            .ToList();
        if (targets.Count == 0)
            return;

        string message = targets.Count == 1
            ? $"Are you sure you want to delete \"{targets[0].Name}\"? This will send it to your recycle bin."
            : $"Are you sure you want to delete {targets.Count} clips? This will send them to your recycle bin.";

        ShowConfirmDialog(message, "Delete", confirmed =>
        {
            if (confirmed)
            {
                _selectedClipPaths.Clear();
                if (targets.Count == 1)
                    QueueDeleteWithUndo(targets[0]);
                else
                    QueueMultiDeleteWithUndo(targets);
            }
        });
    }

    private void MoveSelected_Click(object sender, RoutedEventArgs e)
    {
        List<FileInfo> targets = _galleryCardSelection
            .Where(c => _selectedClipPaths.Contains(c.File.FullName))
            .Select(c => c.File)
            .ToList();
        if (targets.Count == 0)
            return;

        var dialog = new OpenFolderDialog { InitialDirectory = GalleryFolder };
        if (dialog.ShowDialog(this) != true)
            return;

        string destination = dialog.FolderName;
        foreach (FileInfo file in targets)
        {
            try
            {
                string dest = Path.Combine(destination, file.Name);
                if (File.Exists(dest))
                    dest = Path.Combine(destination, $"{Path.GetFileNameWithoutExtension(file.Name)}_{DateTime.Now:HHmmss}{file.Extension}");
                File.Move(file.FullName, dest);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't move \"{file.Name}\": {ex.Message}", "Backtrack");
            }
        }

        LoadGallery();
    }

    /// <summary>
    /// Shared by both the local Gallery and a remote peer's Gallery -- the
    /// only two things that ever actually differed between them (used to be
    /// two near-identical ~35-line methods, BuildFolderCard(DirectoryInfo)
    /// and BuildRemoteFolderCard(string)) were the displayed name and what
    /// opening the folder means, both of which the caller already knows.
    /// </summary>
    private Border BuildFolderCard(string name, Action onOpen, bool leadsToNewest = false)
    {
        var iconHost = new Border
        {
            Background = (Brush)FindResource("ThumbnailBg"),
            Height = 118,
            Cursor = Cursors.Hand,
        };

        // Real folder glyph (Google's Material Design Icons "folder" outline,
        // Apache-2.0), not a hand-rolled tab+rectangle approximation -- drawn as a
        // vector Path so it stays crisp at any size instead of shipping a bitmap.
        // System.Windows.Shapes is intentionally not `using`'d file-wide: this file
        // already uses "Path" everywhere to mean System.IO.Path, so the shape type
        // is fully qualified here instead.
        var folderGlyph = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z"),
            Fill = (Brush)FindResource("Text1"),
            Width = 46,
            Height = 38,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconHost.Child = folderGlyph;

        var title = new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 7, 0, 1),
        };

        var sub = new TextBlock { Text = "Folder", FontSize = 11, Foreground = (Brush)FindResource("Text2") };

        UIElement titleRow = leadsToNewest ? WithNewestDot(title, "Contains the newest clip") : title;
        var content = new StackPanel();
        content.Children.Add(iconHost);
        content.Children.Add(titleRow);
        content.Children.Add(sub);

        var card = new Border { Width = 210, Child = content, Cursor = Cursors.Hand };
        card.MouseLeftButtonUp += (_, _) => onOpen();

        return card;
    }

    /// <summary>
    /// Puts a small blue dot immediately to the left of `title`, in the
    /// title's own row -- the "this is (or leads to) the newest clip"
    /// marker BuildFolderCard/BuildClipCard both use. `title`'s own
    /// top/bottom margin moves onto the wrapping row (title itself gets a
    /// small left margin instead) so overall card spacing doesn't change.
    ///
    /// This used to overlay the dot on the whole card via a Grid, floating
    /// it at the vertical center of everything (icon + title + subtitle
    /// stacked) -- centered on nothing in particular, and it sat ON TOP of
    /// the thumbnail instead of making room for itself next to the actual
    /// label. Putting it in the title's own row and letting the text shift
    /// right instead reads as an actual inline marker, not a floating badge.
    ///
    /// NewestClip is a fixed brand color like Rec/Stream/Green, not Accent --
    /// see Theme.Dark.xaml's own comment on that key.
    /// </summary>
    private StackPanel WithNewestDot(TextBlock title, string tooltip)
    {
        Thickness titleMargin = title.Margin;
        title.Margin = new Thickness(4, 0, 0, 0);

        // System.Windows.Shapes.Ellipse fully qualified, not `using`'d file-wide --
        // same reasoning as the Path glyph a bit above this (System.IO.Path collision).
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

    private Border BuildClipCard(FileInfo file, bool isNewest = false)
    {
        // Neutral placeholder shown until the real frame loads in behind it
        // (LoadThumbnailAsync, kicked off below) -- not a fake thumbnail like the
        // old per-file color, just what's visible during the brief async load.
        var thumb = new Border
        {
            Background = (Brush)FindResource("ThumbnailBg"),
            Height = 118,
            Cursor = Cursors.Hand,
            ClipToBounds = true,
        };
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };

        // Hover-revealed selection circle, corner-anchored over the thumbnail.
        // Hidden by default; stays visible on every card once anything anywhere
        // is selected (see RefreshGallerySelectionUi), not just the hovered one --
        // that's what turns "click a circle" into an actual multi-select mode.
        var selectCircle = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderBrush = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
        };

        var thumbHost = new Grid();
        thumbHost.Children.Add(thumbImage);
        thumbHost.Children.Add(selectCircle);
        thumb.Child = thumbHost;

        thumb.MouseEnter += (_, _) => selectCircle.Visibility = Visibility.Visible;
        thumb.MouseLeave += (_, _) =>
        {
            if (_selectedClipPaths.Count == 0)
                selectCircle.Visibility = Visibility.Collapsed;
        };
        selectCircle.MouseLeftButtonUp += (_, e) =>
        {
            // Stops this from also bubbling up to thumb's own click handler below,
            // which would otherwise immediately re-toggle (or open the player) on
            // the same physical click.
            e.Handled = true;
            ToggleClipSelected(file);
        };

        // Clicking the thumbnail plays the clip directly -- no separate Play button --
        // unless a mass-selection is already active, in which case every click (not
        // just the circle) toggles that clip instead, matching the Google Photos-style
        // "select mode" the circle puts the whole grid into.
        thumb.MouseLeftButtonUp += (_, _) =>
        {
            if (_selectedClipPaths.Count > 0)
                ToggleClipSelected(file);
            else
                OpenInPlayer(file);
        };

        _galleryCardSelection.Add((file, selectCircle, thumb));

        var title = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 7, 0, 1),
            Cursor = Cursors.IBeam,
        };

        DateTime modified = file.LastWriteTime;
        string subText = modified.Date == DateTime.Today
            ? modified.ToString("h:mm tt")
            : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock { Text = subText, FontSize = 11, Foreground = (Brush)FindResource("Text2") };

        // Duration on the opposite side of the date, same row. Filled in right
        // away if already cached (from a prior thumbnail pass); otherwise left
        // blank and picked up by LoadThumbnailAsync once generation finishes.
        var durationText = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        long? knownDurationMs = TryGetCachedDurationMs(file);
        if (knownDurationMs is long ms)
            durationText.Text = FormatDuration(ms);

        var subRow = new Grid();
        subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(sub, 0);
        Grid.SetColumn(durationText, 1);
        subRow.Children.Add(sub);
        subRow.Children.Add(durationText);

        UIElement titleRow = isNewest ? WithNewestDot(title, "Newest clip") : title;
        var content = new StackPanel();
        content.Children.Add(thumb);
        content.Children.Add(titleRow);
        content.Children.Add(subRow);

        _ = LoadThumbnailAsync(file, thumbImage, knownDurationMs is null ? durationText : null);

        // No margin needed here -- GalleryGrid's ItemWidth/ItemHeight (264x212 vs. this
        // card's 240-wide, ~186-tall content) already reserve the gutter uniformly on
        // every cell, top-left aligned by default, so the leftover space itself becomes
        // the gap to the next card without needing a per-card Margin to also add one.
        var card = new Border { Width = 210, Child = content };

        // Only worth showing when the clip isn't already local -- this is the
        // "bring it from the stream PC to this one" action. Everything else
        // (rename, delete, open folder) moved to double-click/right-click, so
        // this is the one remaining action row, and only appears when relevant.
        if (IsNetworkPath(_settings.ClipsFolder))
        {
            var copyBtn = new Button { Content = "Copy here", Style = (Style)FindResource("IconButton"), Margin = new Thickness(0, 6, 0, 0) };
            copyBtn.Click += async (_, _) => await CopyToThisPcAsync(file, copyBtn);
            content.Children.Add(copyBtn);
        }

        // Double-click the title to rename, instead of a separate button.
        // Extracted, unguarded rename-commit helper inside BeginRename: LostFocus
        // fires a second time when the TextBox is removed from the tree to restore
        // the label (removing a focused element fires its own LostFocus), which
        // would otherwise re-run a guarded "commit" a second time against a stale
        // FileInfo. Guarding BeginRename itself would also skip the real work on
        // the legitimate first call, so the `finished` flag lives at the two call
        // sites inside it instead.
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                BeginRename(card, title, file);
        };

        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var openFolderItem = new MenuItem { Header = "Open file location", Style = (Style)FindResource("DarkMenuItem") };
        openFolderItem.Click += (_, _) => RevealInExplorerAndClose(file.FullName);
        var copyPathItem = new MenuItem { Header = "Copy path", Style = (Style)FindResource("DarkMenuItem") };
        copyPathItem.Click += (_, _) => Clipboard.SetText(file.FullName);
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem"), Foreground = (Brush)FindResource("Rec") };
        deleteItem.Click += (_, _) => DeleteClip(file, card);
        contextMenu.Items.Add(openFolderItem);
        contextMenu.Items.Add(copyPathItem);
        contextMenu.Items.Add(deleteItem);
        thumb.ContextMenu = contextMenu;

        return card;
    }

    private static readonly SemaphoreSlim ThumbnailGenerationLock = new(1, 1);

    private static string GetThumbnailCachePath(FileInfo file)
    {
        string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Backtrack", "thumbnails");
        Directory.CreateDirectory(cacheDir);
        // Keyed on path + last-write-time + size, not just the path, so a
        // replaced file (e.g. Trim's "replace original") gets a fresh thumbnail
        // instead of showing the old clip's frame forever.
        string key = $"{file.FullName}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(cacheDir, $"{hash}.jpg");
    }

    // A tiny sidecar next to the thumbnail (same hash, so it invalidates together
    // with it) holding the clip's length in milliseconds -- captured for free from
    // player.Length during thumbnail generation, which already has to briefly play
    // the file anyway, rather than running a whole separate probe pass.
    private static string GetDurationCachePath(FileInfo file) => Path.ChangeExtension(GetThumbnailCachePath(file), ".duration");

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

    /// <summary>
    /// Grabs a real frame from partway into the clip via a headless LibVLC
    /// player + TakeSnapshot, caches it to disk, and loads it into the given
    /// Image once ready. Serialized through one semaphore (not run in parallel
    /// per card) -- generating several of these at once is heavy CPU/decode
    /// work, and this app is already forced onto software decode in some
    /// environments (see --avcodec-hw=none).
    /// </summary>
    private async Task<string?> EnsureThumbnailCachedAsync(FileInfo file)
    {
        string cachePath = GetThumbnailCachePath(file);
        // Both need to exist, not just the jpg -- a thumbnail cached before the
        // .duration sidecar existed (i.e. every clip already thumbnailed once
        // this feature shipped) would otherwise short-circuit here forever and
        // never pick up a duration, since generation only reruns when the cache
        // is considered missing.
        bool durationCached = File.Exists(GetDurationCachePath(file));
        if (File.Exists(cachePath) && durationCached)
            return cachePath;

        if (_libVlc is null)
            return null;

        await ThumbnailGenerationLock.WaitAsync();
        try
        {
            if (!File.Exists(cachePath) || !File.Exists(GetDurationCachePath(file)))
                await GenerateThumbnailAsync(file, cachePath);
        }
        finally
        {
            ThumbnailGenerationLock.Release();
        }

        return File.Exists(cachePath) ? cachePath : null;
    }

    /// <summary>
    /// Generates and caches thumbnails for every clip in the background,
    /// starting right at launch -- by the time the user actually opens Gallery,
    /// most/all of them should already be sitting on disk, so it loads
    /// instantly instead of visibly generating them on demand. Sequential
    /// (shares the same semaphore as LoadThumbnailAsync), so it never competes
    /// with a thumbnail the user is actually looking at right now.
    /// </summary>
    private async Task PrewarmGalleryThumbnailsAsync()
    {
        if (_libVlc is null || !Directory.Exists(_settings.ClipsFolder))
            return;

        List<FileInfo> files;
        try
        {
            files = Directory.EnumerateFiles(_settings.ClipsFolder)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime) // newest first -- most likely to be looked at soonest
                .ToList();
        }
        catch
        {
            return;
        }

        foreach (FileInfo file in files)
            await EnsureThumbnailCachedAsync(file);
    }

    private async Task LoadThumbnailAsync(FileInfo file, Image target, TextBlock? durationTarget = null)
    {
        string? cachePath = await EnsureThumbnailCachedAsync(file);

        // Generation (if it ran) also drops the duration sidecar as a side
        // effect, so a card built before that duration was known gets it
        // filled in here once thumbnailing catches up.
        if (durationTarget is not null && TryGetCachedDurationMs(file) is long ms)
            durationTarget.Text = FormatDuration(ms);

        if (cachePath is null)
            return;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(cachePath);
            bitmap.EndInit();
            bitmap.Freeze();
            target.Source = bitmap;
        }
        catch
        {
            // Cached file is somehow unreadable -- leave the neutral placeholder.
        }
    }

    private async Task GenerateThumbnailAsync(FileInfo file, string cachePath)
    {
        // This LibVLCSharp version (3.10.0) doesn't expose a dedicated
        // thumbnailer API -- TakeSnapshot via a real (briefly) playing
        // MediaPlayer is the only option it actually has. What made the first
        // attempt bad wasn't TakeSnapshot itself, it was two real bugs: no
        // render target meant libvlc opened its own floating window per clip
        // (fixed below via _thumbnailSinkHwnd), and audio was left on, so each
        // one was audible too (fixed via :no-audio).
        await Task.Run(() =>
        {
            try
            {
                using var media = new LibVlc.Media(_libVlc!, new Uri(file.FullName));
                media.AddOption(":no-audio");
                using var player = new LibVlc.MediaPlayer(media) { Hwnd = _thumbnailSinkHwnd, Mute = true };
                using var playingSignal = new ManualResetEventSlim(false);

                player.Playing += (_, _) => playingSignal.Set();
                player.EncounteredError += (_, _) => playingSignal.Set();

                player.Play();
                if (!playingSignal.Wait(TimeSpan.FromSeconds(5)))
                {
                    player.Stop();
                    return;
                }

                // Free to capture here -- already have a real, playing MediaPlayer for
                // the snapshot itself, no separate probe needed.
                try { File.WriteAllText(Path.ChangeExtension(cachePath, ".duration"), player.Length.ToString()); }
                catch { /* not worth failing the thumbnail over */ }

                long seekTarget = Math.Min(2000, Math.Max(player.Length / 4, 0));
                if (seekTarget > 0)
                    player.Time = seekTarget;
                Thread.Sleep(150);

                player.TakeSnapshot(0, cachePath, 480, 0);
                for (int i = 0; i < 15 && !File.Exists(cachePath); i++)
                    Thread.Sleep(100);

                player.Stop();
            }
            catch
            {
                // No thumbnail this time -- the placeholder stays, not worth surfacing an error for.
            }
        });
    }

    private void BeginRename(Border card, TextBlock title, FileInfo file)
    {
        _isRenamingCard = true;
        bool finished = false;

        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Margin = title.Margin,
            Background = (Brush)FindResource("RowBg"),
            Foreground = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(0),
        };

        var stack = (StackPanel)card.Child;
        int index = stack.Children.IndexOf(title);
        stack.Children.RemoveAt(index);
        stack.Children.Insert(index, box);

        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (!finished) { finished = true; CommitRename(); } }
            else if (e.Key == Key.Escape) { e.Handled = true; if (!finished) { finished = true; _isRenamingCard = false; LoadGallery(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; CommitRename(); } };

        void CommitRename()
        {
            _isRenamingCard = false;
            string newName = box.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != Path.GetFileNameWithoutExtension(file.Name))
            {
                try
                {
                    string newPath = Path.Combine(file.DirectoryName!, newName + file.Extension);
                    File.Move(file.FullName, newPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Couldn't rename: {ex.Message}", "Backtrack");
                }
            }
            LoadGallery();
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

    private void QueueDeleteWithUndo(FileInfo file)
    {
        string fullPath = Path.GetFullPath(file.FullName);
        _pendingDeletePaths.Add(fullPath);
        LoadGallery();
        // Same "instantly gone, not just after the undo window expires" shape
        // as LoadGallery() just above -- RefreshRecentClipsOverlay's own
        // _pendingDeletePaths filter is what makes this take effect
        // immediately regardless of which screen the delete was triggered
        // from (Gallery, Player, or the overlay's own tile menu).
        RefreshRecentClipsOverlay();

        _toastOverlay.ShowDeleteUndo(file.Name,
            onExpire: () =>
            {
                _pendingDeletePaths.Remove(fullPath);
                if (!RecycleBin.Delete(fullPath))
                {
                    Dispatcher.BeginInvoke(() => MessageBox.Show(this, $"Couldn't delete \"{file.Name}\".", "Backtrack"));
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        LoadGallery();
                    else
                        _ = RefreshGalleryCountAsync();
                    RefreshRecentClipsOverlay();
                });
            },
            onUndo: () =>
            {
                _pendingDeletePaths.Remove(fullPath);
                Dispatcher.BeginInvoke(() =>
                {
                    LoadGallery();
                    RefreshRecentClipsOverlay();
                });
            });
    }

    /// <summary>
    /// One toast for the whole batch instead of QueueDeleteWithUndo called in
    /// a loop -- that used to fire a separate ShowDeleteUndo per clip (each
    /// with its own 60fps DispatcherTimer for the progress bar), which was
    /// the actual cause of Backtrack visibly slowing down when deleting
    /// several clips at once, not just visual clutter from the stacked
    /// toasts. Undo/expire both apply to the entire batch together, same as
    /// a single delete's own all-or-nothing behavior.
    /// </summary>
    private void QueueMultiDeleteWithUndo(List<FileInfo> files)
    {
        var fullPaths = files.Select(f => Path.GetFullPath(f.FullName)).ToList();
        foreach (string fullPath in fullPaths)
            _pendingDeletePaths.Add(fullPath);
        LoadGallery();
        // See QueueDeleteWithUndo's identical comment.
        RefreshRecentClipsOverlay();

        _toastOverlay.ShowMultiDeleteUndo(files.Count,
            onExpire: () =>
            {
                var failed = new List<string>();
                foreach (string fullPath in fullPaths)
                {
                    _pendingDeletePaths.Remove(fullPath);
                    if (!RecycleBin.Delete(fullPath))
                        failed.Add(Path.GetFileName(fullPath));
                }
                if (failed.Count > 0)
                {
                    Dispatcher.BeginInvoke(() => MessageBox.Show(this,
                        $"Couldn't delete {failed.Count} clip(s): {string.Join(", ", failed)}.", "Backtrack"));
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        LoadGallery();
                    else
                        _ = RefreshGalleryCountAsync();
                    RefreshRecentClipsOverlay();
                });
            },
            onUndo: () =>
            {
                foreach (string fullPath in fullPaths)
                    _pendingDeletePaths.Remove(fullPath);
                Dispatcher.BeginInvoke(() =>
                {
                    LoadGallery();
                    RefreshRecentClipsOverlay();
                });
            });
    }

    private void DeleteClip(FileInfo file, Border card)
    {
        ShowConfirmDialog(
            $"Are you sure you want to delete \"{file.Name}\"? This will send it to your recycle bin.",
            "Delete",
            confirmed =>
            {
                if (confirmed)
                {
                    QueueDeleteWithUndo(file);
                }
            });
    }

    // ----------------------------------------------------------------- player

    /// <summary>Resolves a clip that may live on a remote stream PC's share back to a real local path when possible, since LibVLC plays a UNC path fine but some operations (trim export) want a plain string path either way -- kept for symmetry/clarity at call sites.</summary>
    private static string ResolveLocalClipPath(FileInfo file) => file.FullName;

    /// <summary>
    /// Loads this clip's already-generated Gallery thumbnail into
    /// PlayerFreezeFrame and opens the Popup covering the video with it --
    /// masking the first glitchy moment of decode (see the Popup's own XAML
    /// comment). Fire-and-forget from OpenInPlayer, same as this file's
    /// other UI-triggered async work -- almost always near-instant since the
    /// thumbnail was already generated for the Gallery card that got clicked
    /// to open this clip, but if it's ever slow, the cover just opens a beat
    /// late rather than blocking playback on it.
    ///
    /// _freezeFrameTimer starts right here, only once IsOpen actually flips
    /// to true -- NOT back in OpenInPlayer at Play() time. It used to start
    /// there, which meant the countdown was already running (and eating into
    /// its own budget) before there was anything on screen yet to cover;
    /// however long the thumbnail load + deferred Popup reopen below took
    /// came straight out of the cover's real on-screen time.
    /// </summary>
    private async void ShowPlayerFreezeFrame(FileInfo file)
    {
        await LoadThumbnailAsync(file, PlayerFreezeFrame);
        PlayerFreezeFramePopup.IsOpen = false;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            PlayerFreezeFramePopup.IsOpen = true;
            _freezeFrameTimer.Stop();
            _freezeFrameTimer.Start();

            // Two independent Popups (this one and PlayerOverlayPopup, the
            // back button/title) racing their own opens -- WPF stacks
            // most-recently-opened on top, and this one's open is timing-
            // dependent on the thumbnail load above finishing, so it could
            // win that race and end up covering the back button/title until
            // it closed again. Reasserting the back button/title's Popup
            // right here, immediately after this one opens, guarantees it
            // ends up on top regardless of how that race actually went.
            ReopenPlayerOverlayPopup();
        }), DispatcherPriority.Loaded);
    }

    private void OpenInPlayer(FileInfo file)
    {
        if (_libVlc is null)
        {
            MessageBox.Show(this, "The video player failed to initialize (LibVLC).", "Backtrack");
            return;
        }

        // Default back target -- see _playerBackTarget's own comment. Set
        // unconditionally here so every OTHER caller (Gallery cards, remote
        // clip open, etc.) keeps the existing Gallery behavior without each
        // needing to remember to set this themselves; only
        // ShowMainWindowAndOpenInPlayer overrides it, and only after this
        // call returns.
        _playerBackTarget = Screen.Gallery;

        // See _clipOpenToken's own comment -- this counts as "the user wants
        // to be watching THIS clip now" regardless of whether it got here via
        // a plain local-clip click or a remote download that just finished,
        // so any older still-in-flight remote download for a DIFFERENT clip
        // knows it's been superseded and should give up quietly instead of
        // yanking playback back to itself once it finishes.
        _clipOpenToken++;
        // Cleared unconditionally here -- OpenRemoteClipAsync (the only
        // other caller that ever wants this set) always calls OpenInPlayer
        // FIRST, then sets _currentPlayerRemoteOrigin itself right after, so
        // clearing it up front here can never race that back out.
        _currentPlayerRemoteOrigin = null;

        _currentPlayerFile = file;
        _trimStart = null;
        _trimEnd = null;
        TrimPanel.Visibility = Visibility.Collapsed;
        PlayerTransportRow.Visibility = Visibility.Visible;
        MoveTransportControlsForTrim(intoTrimRow: false);
        StopPreviewLoop();
        ResetPlaybackSpeed();

        // Undoes ShowScreen's own Collapse of VideoView itself (set whenever
        // Player was left -- see its comment); needed here too, not just
        // once, since the SAME VideoView is reused clip to clip rather than
        // recreated.
        PlayerVideoView.Visibility = Visibility.Visible;

        ShowScreen(Screen.Player);
        PlayerTitle.Text = Path.GetFileNameWithoutExtension(file.Name);

        // ShowScreen itself only ever CLOSES PlayerOverlayPopup now (see its
        // own comment) -- reopening it is entirely on this method, the only
        // caller that ever shows Player, so this is always exactly one clean
        // close+reopen cycle, not one on top of another. Still needed even
        // when Player was already the active screen (opening a second clip
        // without going back to Gallery first): WPF's Placement="Relative"
        // Popup only reliably recomputes its position on a real false->true
        // IsOpen transition, not just because the video underneath changed
        // while it stayed open -- without forcing that transition here, a
        // second clip in a row ends up stuck at the first clip's position.
        // Deferred to DispatcherPriority.Loaded (after this call's own layout
        // pass, not immediately) since PlayerVideoView's ActualWidth/position
        // right here isn't necessarily settled yet -- reopening too early
        // would just cache another stale position instead of fixing anything.
        // (ReopenPlayerOverlayPopup -- Enter/ExitPlayerFullscreen share this
        // exact same need, for the exact same reason.)
        ReopenPlayerOverlayPopup();
        ShowPlayerFreezeFrame(file);

        StatSize.Text = $"{file.Length / 1024.0 / 1024.0:0.#} MB";
        StatDate.Text = $"{file.LastWriteTime:MMM d, yyyy h:mm tt}";
        StatResolution.Text = "";
        StatFps.Text = "";

        StopPlayerPlayback();

        // Deferred to DispatcherPriority.Loaded (same reasoning as the popup
        // reopen above): attaching VLC's native HWND and starting playback
        // synchronously, right as ShowScreen just made the Player panel
        // visible, meant the layered window (AllowsTransparency="True")
        // could push its first composited frame before the panel's own
        // layout (grid rows, PlayerVideoView's bounds) had actually
        // resolved -- visible as the video area rendering cut in half for a
        // couple of frames. Letting layout fully settle first, then
        // attaching the video into an already-stable area, avoids that.
        //
        // Loaded priority is BELOW Input priority, though -- a second real
        // click (switching clips fast) jumps the queue ahead of a still-
        // pending deferred start from the first click, so two of these can
        // end up scheduled before either has run. myToken (captured NOW,
        // synchronously, right after _clipOpenToken was bumped above) is
        // what StartPlayerPlayback checks to make sure it's still the last
        // one scheduled before it touches VideoView.MediaPlayer at all --
        // see that method's own comment for what happens without this.
        long myToken = _clipOpenToken;
        var mediaUri = new Uri(ResolveLocalClipPath(file));
        Dispatcher.BeginInvoke(new Action(() => StartPlayerPlayback(mediaUri, myToken)), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The deferred half of OpenInPlayer, split out specifically so it can
    /// await _pendingVlcDisposeTask first. Reopening a clip right after
    /// leaving Player (ShowScreen's own DisposeVlcPlayerAsync -- see its
    /// comment) used to race a brand-new MediaPlayer's Direct3D11 setup
    /// against the PREVIOUS one's still-in-progress background teardown,
    /// both targeting the same VideoView HWND. libvlc doesn't queue or fail
    /// loudly when that render target isn't cleanly available -- it just
    /// opens its own floating "VLC (Direct3D11 output)" window instead
    /// (same fallback already documented on the headless thumbnail player
    /// above, for the same underlying reason: no explicit render target).
    /// Waiting for the old teardown to actually finish first closes that gap.
    ///
    /// The SAME failure mode also had a second, more direct cause: switching
    /// clips fast enough queues more than one of these deferred calls before
    /// either runs (see OpenInPlayer's own comment on myToken/Loaded
    /// priority), and this used to just create a brand-new MediaPlayer and
    /// claim VideoView.MediaPlayer unconditionally -- with no dispose of
    /// whichever MediaPlayer the PREVIOUS deferred call in the same queue had
    /// just created and attached moments earlier. Two real MediaPlayer
    /// instances contending for the same render target hits the exact same
    /// libvlc fallback as the stale-teardown race above. myToken fixes both
    /// at once: a call that's no longer the latest just returns before
    /// creating anything, so at most one MediaPlayer ever gets created per
    /// burst of clicks.
    /// </summary>
    /// <summary>
    /// hideFreezeFrameOnFirstPlay: OpenInPlayer's own local-file path already
    /// starts the freeze-frame-hide timer itself, before this method even
    /// runs (see ShowPlayerFreezeFrame) -- it knows the moment playback is
    /// about to begin, since a local file has no real "not ready yet" delay.
    /// OpenRemoteClipStreaming has no such moment to hand off from (network
    /// buffering time is unknown up front, unlike a local decode), so it
    /// leaves the cover up via ShowPlayerLoadingUi and asks THIS method to
    /// start that same timer instead, but only once real playback has
    /// actually begun (the Playing event below), not immediately.
    /// </summary>
    private async void StartPlayerPlayback(Uri mediaUri, long myToken, bool hideFreezeFrameOnFirstPlay = false)
    {
        // Re-checked here, not just trusted from OpenInPlayer's own earlier
        // check -- this runs from a deferred callback across an await, no
        // longer in the same synchronous flow the compiler could narrow
        // _libVlc's nullability through.
        if (_libVlc is null)
            return;

        // A newer clip was opened before this deferred call even got to run
        // (see OpenInPlayer's own comment) -- bail before touching anything,
        // rather than creating a MediaPlayer nobody wants and leaving it to
        // fight the actually-current one for the same render target.
        if (myToken != _clipOpenToken)
            return;

        if (_pendingVlcDisposeTask is Task pending)
        {
            await pending;
            _pendingVlcDisposeTask = null;
        }

        // Re-checked after the await above for the same reason -- another
        // clip could've been opened during however long that dispose took.
        if (myToken != _clipOpenToken)
            return;

        _vlcPlayer = new LibVlc.MediaPlayer(_libVlc);
        PlayerVideoView.MediaPlayer = _vlcPlayer;
        _playerHasEnded = false;

        using var media = new LibVlc.Media(_libVlc, mediaUri);
        _vlcPlayer.Play(media);

        // Explicit, not just "set the slider to 100 and let ValueChanged
        // propagate it" -- that was the actual bug (reported live as clips
        // starting muted). WPF's Slider doesn't raise ValueChanged when the
        // new value equals the current one, so if the slider was ALREADY at
        // 100 (true for the very first clip opened all session, matching
        // its own XAML default), nothing ever told this brand new
        // _vlcPlayer to actually BE unmuted at 100 -- it was left entirely
        // to whatever LibVLC's own real default turned out to be, which
        // isn't reliably "unmuted" in practice (Mute may reflect shared
        // audio-output state, not a clean per-instance default). Setting
        // both directly here guarantees it regardless of the slider's own
        // prior value or event semantics; after Play(), not before -- libvlc's
        // own audio output isn't necessarily set up yet before playback has
        // actually started, so Volume/Mute writes before this point aren't
        // reliably guaranteed to stick either. Slider/icon still synced to match.
        _vlcPlayer.Volume = 100;
        _vlcPlayer.Mute = false;
        PlayerVolumeSlider.Value = 100;
        UpdateVolumeIcon();

        // _freezeFrameTimer itself starts from ShowPlayerFreezeFrame, once
        // the cover is actually visible -- not from here. Starting it at
        // this fixed point used to shrink the cover's real on-screen time
        // by however long the thumbnail load + deferred Popup reopen took,
        // since that countdown was already running before there was
        // anything on screen to cover with.

        bool tracksLoaded = false;
        bool volumeConfirmed = false;
        bool freezeFrameHidden = false;
        _vlcPlayer.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            PlayIcon.Visibility = Visibility.Collapsed;
            PauseIcon.Visibility = Visibility.Visible;

            if (hideFreezeFrameOnFirstPlay && !freezeFrameHidden)
            {
                freezeFrameHidden = true;
                _freezeFrameTimer.Stop();
                _freezeFrameTimer.Start();
            }

            if (_vlcPlayer.Media is not null)
            {
                var videoTrack = _vlcPlayer.Media.Tracks.FirstOrDefault(t => t.TrackType == LibVlc.TrackType.Video).Data.Video;
                if (videoTrack.Width > 0 && videoTrack.Height > 0)
                    StatResolution.Text = $"{videoTrack.Width} x {videoTrack.Height}";
                if (videoTrack.FrameRateDen > 0)
                    StatFps.Text = $"{(double)videoTrack.FrameRateNum / videoTrack.FrameRateDen:0.##} fps";
            }

            // Track info isn't known the instant Play() is called -- LibVLC parses the
            // media asynchronously, so reading Media.Tracks right after Play() (the old
            // bug here) always saw an empty list and hid the audio selector even on
            // clips that do have a track. By the time Playing fires, parsing has
            // actually finished, so this is the first point where Tracks is reliable.
            if (!tracksLoaded)
            {
                tracksLoaded = true;
                LoadAudioTracks();
            }

            // Same class of bug as tracksLoaded above, just for volume: the
            // Volume=100/Mute=false write right after Play() (below, this
            // method) is already known unreliable before playback has really
            // started (see that write's own comment) -- it can silently fail
            // to stick, or UpdateVolumeIcon can read Volume back as 0 before
            // libvlc's real audio output is actually up, showing "Muted"
            // despite Mute genuinely being false (reported live: "shows
            // muted even though you can kinda hear audio"). Re-affirm and
            // re-display once Playing fires, when both are finally reliable
            // -- the earlier write stays too, for the common case where it
            // already worked and this is just belt-and-suspenders.
            if (!volumeConfirmed)
            {
                volumeConfirmed = true;
                _vlcPlayer.Volume = (int)PlayerVolumeSlider.Value;
                _vlcPlayer.Mute = false;
                UpdateVolumeIcon();
            }
        });
        _vlcPlayer.Paused += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            PlayIcon.Visibility = Visibility.Visible;
            PauseIcon.Visibility = Visibility.Collapsed;
        });
        _vlcPlayer.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            PlayIcon.Visibility = Visibility.Visible;
            PauseIcon.Visibility = Visibility.Collapsed;
            _playerHasEnded = true;

            // _seekTimer keeps ticking (only ever stopped by full teardown,
            // not by pause/end) and _vlcPlayer.Time/.Length can themselves
            // go unreliable once libvlc is actually past end-of-stream --
            // left running, UpdatePlayerSeekUi kept re-reading whatever
            // Time reset/settled to and overwriting the fill bar with it,
            // reported live as the seek bar never actually landing at the
            // end once playback finished. Stopped here, and the fill/thumb/
            // time text snapped to their real "fully played" values
            // directly instead of trusting another timer tick to land on
            // them. RestartEndedPlayback restarts the timer once playback
            // actually resumes.
            _seekTimer.Stop();
            PlayerSeekFill.Width = PlayerSeekTrack.ActualWidth;
            PlayerSeekThumb.Margin = new Thickness(PlayerSeekTrack.ActualWidth - 7, 0, 0, 0);
            PlayerCurrentTime.Text = PlayerDurationText.Text;
        });

        _seekTimer.Start();
    }

    /// <summary>Shown whenever there's at least one audio track -- not just when there's a choice to make, since seeing "Track 1" confirms audio was actually detected at all.</summary>
    private void LoadAudioTracks()
    {
        if (_vlcPlayer?.Media is null)
            return;

        var tracks = _vlcPlayer.Media.Tracks.Where(t => t.TrackType == LibVlc.TrackType.Audio).ToList();
        if (tracks.Count == 0)
        {
            AudioTrackCombo.Visibility = Visibility.Collapsed;
            return;
        }

        var options = tracks.Select((t, i) => new AudioTrackOption(t.Id, string.IsNullOrEmpty(t.Description) ? $"Track {i + 1}" : t.Description)).ToList();

        AudioTrackCombo.Visibility = Visibility.Visible;
        AudioTrackCombo.ItemsSource = options;
        // Settings > Player's own default track (1-6, matched positionally --
        // see DefaultPlayerAudioTrackIndex's own comment), falling back to
        // whichever one this clip lists first (index 0, the old unconditional
        // behavior) if it's unset (0) or this clip doesn't even have that
        // many tracks.
        int preferredIndex = _settings.DefaultPlayerAudioTrackIndex - 1;
        AudioTrackCombo.SelectedIndex = preferredIndex >= 0 && preferredIndex < options.Count ? preferredIndex : 0;

        // The very first SetAudioTrack call on a freshly-started clip
        // (fired by the SelectedIndex assignment above, via
        // AudioTrackCombo_SelectionChanged) doesn't always actually engage
        // libvlc's audio output -- confirmed live: a clip opened with no
        // audio at all on the correct/default track, until manually
        // switching the dropdown to a different track and back, at which
        // point it started working. That's a known LibVLC quirk (the audio
        // output isn't necessarily fully attached the very first time a
        // track gets selected), not anything specific to which track was
        // picked. -1 is libvlc's own "no audio track" id -- bouncing
        // through it and back reproduces the same "switch away, switch
        // back" re-negotiation that fixed it by hand, without depending on
        // a second real track existing (a single-track clip hits this too)
        // and without an audible blip of different content (silence isn't
        // audibly different from silence). A bare repeat of the SAME real
        // id wouldn't reliably reproduce this -- some LibVLC builds no-op a
        // SetAudioTrack call that just repeats the id already considered
        // active, which is exactly the state this needs to break out of.
        int desiredId = options[AudioTrackCombo.SelectedIndex].Id;
        Dispatcher.BeginInvoke(() =>
        {
            if (_vlcPlayer is null)
                return;
            _vlcPlayer.SetAudioTrack(-1);
            _vlcPlayer.SetAudioTrack(desiredId);
        }, DispatcherPriority.Background);
    }

    private sealed record AudioTrackOption(int Id, string Name);

    private void AudioTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vlcPlayer is null || AudioTrackCombo.SelectedItem is not AudioTrackOption opt)
            return;

        _vlcPlayer.SetAudioTrack(opt.Id);
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vlcPlayer is null)
            return;
        if (_vlcPlayer.IsPlaying)
        {
            _vlcPlayer.Pause();
        }
        else if (_playerHasEnded)
        {
            RestartEndedPlayback(0);
        }
        else
        {
            _vlcPlayer.Play();
        }
    }

    private void PlayerVolumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vlcPlayer is null)
            return;
        _vlcPlayer.Mute = !_vlcPlayer.Mute;
        UpdateVolumeIcon();
        ShowPlayerActionFeedback(_vlcPlayer.Mute ? PlayerFeedbackIcon.Mute : PlayerFeedbackIcon.Volume,
            _vlcPlayer.Mute ? "Muted" : $"{_vlcPlayer.Volume}%");
    }

    // Wired to both PlayerVolumeButton itself and PlayerVolumePopup's own
    // content Border -- entering either keeps it open, leaving either starts
    // the close debounce (_volumePopupCloseDebounce, set up in the
    // constructor). Needed because the popup and its button are two
    // genuinely separate elements with a small visual gap between them;
    // closing on a bare MouseLeave meant crossing that gap on the way from
    // the button up into the slider closed it before you ever got there.
    private void PlayerVolumeArea_MouseEnter(object sender, MouseEventArgs e)
    {
        _volumePopupCloseDebounce.Stop();
        PlayerVolumePopup.IsOpen = true;
    }

    private void PlayerVolumeArea_MouseLeave(object sender, MouseEventArgs e)
    {
        _volumePopupCloseDebounce.Stop();
        _volumePopupCloseDebounce.Start();
    }

    private void PlayerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vlcPlayer is null)
            return;
        _vlcPlayer.Volume = (int)e.NewValue;
        // Dragging the level back up un-mutes, matching most other players'
        // own volume sliders -- otherwise raising the slider while muted
        // would look like it did nothing.
        if (e.NewValue > 0 && _vlcPlayer.Mute)
            _vlcPlayer.Mute = false;
        UpdateVolumeIcon();
    }

    private void UpdateVolumeIcon()
    {
        if (_vlcPlayer is null)
            return;
        bool showMuted = _vlcPlayer.Mute || _vlcPlayer.Volume <= 0;
        PlayerVolumeIcon.Data = Geometry.Parse(showMuted ? VolumeOffIcon : VolumeUpIcon);
    }

    /// <summary>
    /// The only reliable way found to resume playback once libvlc has
    /// actually reached end-of-stream -- a bare Play() (or just writing
    /// .Time) on an ended MediaPlayer is a known LibVLC quirk that silently
    /// does nothing; the pipeline needs an explicit Stop() first to actually
    /// become resumable, then Play(), then a seek to wherever playback
    /// should actually resume from (0 for "restart from the beginning" via
    /// PlayPauseButton, or wherever the user clicked for CommitSeek).
    /// </summary>
    private void RestartEndedPlayback(long resumeAtMs)
    {
        if (_vlcPlayer is null)
            return;

        _playerHasEnded = false;
        _vlcPlayer.Stop();
        _vlcPlayer.Play();
        if (resumeAtMs > 0)
            _vlcPlayer.Time = resumeAtMs;
        _seekTimer.Start(); // EndReached stops it; resuming needs it running again
    }

    private void UpdatePlayerSeekUi()
    {
        if (_vlcPlayer is null || _isScrubbing)
            return;

        long lengthMs = _vlcPlayer.Length;
        long timeMs = _vlcPlayer.Time;

        if (lengthMs <= 0)
            return;

        // See PreviewLoopButton_Click's own comment -- reads the trim
        // boundaries live rather than a snapshot taken when looping started.
        if (_previewLooping && _trimStart is not null && _trimEnd is not null && timeMs >= _trimEnd.Value.TotalMilliseconds)
            CommitSeek((long)_trimStart.Value.TotalMilliseconds);

        PlayerCurrentTime.Text = FormatDuration(Math.Max(timeMs, 0));
        PlayerDurationText.Text = FormatDuration(Math.Max(lengthMs, 0));

        double ratio = Math.Clamp((double)timeMs / lengthMs, 0.0, 1.0);
        double trackWidth = PlayerSeekTrack.ActualWidth;

        if (trackWidth > 0)
        {
            PlayerSeekFill.Width = ratio * trackWidth;
            PlayerSeekThumb.Margin = new Thickness(ratio * trackWidth - 7, 0, 0, 0);
        }

        // Keeps the trim timeline's own playhead moving while it's open --
        // guarded by _isScrubbing same as everything above (both seek bars
        // share that one flag), so this never fights a Start/End handle
        // drag either (those don't touch _isScrubbing at all, only Seek
        // mode on the trim timeline itself does).
        if (TrimPanel.Visibility == Visibility.Visible)
            UpdateTrimTimelineUi();
    }

    private void PlayerSeekTrack_MouseEnter(object sender, MouseEventArgs e)
    {
        _isHoveringSeekTrack = true;
        PlayerSeekBg.Height = 8;
        PlayerSeekFill.Height = 8;
        PlayerSeekBuffer.Height = 8;
        PlayerSeekThumb.Visibility = Visibility.Visible;
    }

    private void PlayerSeekTrack_MouseLeave(object sender, MouseEventArgs e)
    {
        _isHoveringSeekTrack = false;
        if (!_isScrubbing)
        {
            PlayerSeekBg.Height = 4;
            PlayerSeekFill.Height = 4;
            PlayerSeekBuffer.Height = 4;
            PlayerSeekThumb.Visibility = Visibility.Collapsed;
            SeekTooltipPopup.IsOpen = false;
        }
    }

    private void PlayerSeekTrack_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isScrubbing = true;
        PlayerSeekTrack.CaptureMouse();
        ProcessSeekInput(e.GetPosition(PlayerSeekTrack));
    }

    private void PlayerSeekTrack_MouseMove(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(PlayerSeekTrack);
        double trackWidth = PlayerSeekTrack.ActualWidth;
        if (trackWidth <= 0 || _vlcPlayer == null) return;

        double ratio = Math.Clamp(pos.X / trackWidth, 0.0, 1.0);
        long durationMs = Math.Max(1, _vlcPlayer.Length);
        long hoverMs = (long)(ratio * durationMs);

        SeekTooltipText.Text = FormatDuration(hoverMs);
        SeekTooltipPopup.HorizontalOffset = pos.X - 15;
        SeekTooltipPopup.VerticalOffset = -30;
        SeekTooltipPopup.IsOpen = true;

        if (_isScrubbing)
        {
            ProcessSeekInput(pos);
        }
    }

    private void PlayerSeekTrack_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isScrubbing)
        {
            _isScrubbing = false;
            PlayerSeekTrack.ReleaseMouseCapture();

            if (!_isHoveringSeekTrack)
            {
                PlayerSeekBg.Height = 4;
                PlayerSeekFill.Height = 4;
                PlayerSeekBuffer.Height = 4;
                PlayerSeekThumb.Visibility = Visibility.Collapsed;
                SeekTooltipPopup.IsOpen = false;
            }

            ProcessSeekInput(e.GetPosition(PlayerSeekTrack), immediate: true);
        }
    }

    private void ProcessSeekInput(Point mousePos, bool immediate = false)
    {
        if (_vlcPlayer == null) return;
        double trackWidth = PlayerSeekTrack.ActualWidth;
        if (trackWidth <= 0) return;

        double ratio = Math.Clamp(mousePos.X / trackWidth, 0.0, 1.0);
        long durationMs = Math.Max(1, _vlcPlayer.Length);
        _targetSeekMs = (long)(ratio * durationMs);

        PlayerSeekFill.Width = ratio * trackWidth;
        PlayerSeekThumb.Margin = new Thickness(ratio * trackWidth - 7, 0, 0, 0);
        PlayerCurrentTime.Text = FormatDuration(_targetSeekMs);

        if (immediate)
        {
            _seekDebounceTimer.Stop();
            if (_vlcPlayer.IsSeekable)
            {
                CommitSeek(_targetSeekMs);
            }
        }
        else
        {
            _seekDebounceTimer.Stop();
            _seekDebounceTimer.Start();
        }
    }

    // Seeking near the start reproduces the same glitchy decode warmup as a
    // fresh clip open on some clips, and used to get the same freeze-frame
    // cover treatment as OpenInPlayer -- removed on request, so seeking back
    // to the first couple of seconds now just shows the glitch rather than
    // masking it with a cover. The open-time cover (ShowPlayerFreezeFrame)
    // is untouched.
    private void CommitSeek(long ms)
    {
        if (_vlcPlayer is null)
            return;

        // IsSeekable can itself report false once libvlc has reached
        // end-of-stream (same underlying quirk as PlayPauseButton_Click's
        // own RestartEndedPlayback use) -- a plain Time= write would just be
        // silently ignored in that state anyway, so route through the same
        // Stop()+Play() revival instead of bailing out on IsSeekable here.
        if (_playerHasEnded)
        {
            RestartEndedPlayback(ms);
            return;
        }

        if (!_vlcPlayer.IsSeekable)
            return;

        _vlcPlayer.Time = ms;
    }

    /// <summary>
    /// The fast, synchronous half of tearing Player down: everything that
    /// actually needs to happen before the WINDOW resizes to whatever screen
    /// is being switched to, chiefly detaching the VideoView (see its own
    /// comment below). ShowScreen calls this FIRST, before it resizes --
    /// calling only the combined StopPlayerPlayback() there instead (which
    /// also runs the slow Stop()/Dispose() further down) doesn't block
    /// visually, but it does mean this fast detach doesn't actually run
    /// until ShowScreen reaches ITS OWN later StopPlayerPlayback() call,
    /// well after the resize -- so the native video HWND was still attached,
    /// and still rendering, at the OLD Player-sized bounds while the window
    /// had already resized around it to Gallery's (or whatever screen's)
    /// bounds. Reported live as "the video stays overlayed on the new
    /// screen, cut in half, for about a second" -- distinct from (though
    /// visually similar to) the older bug StopPlayerPlayback's own comment
    /// below describes, which was about ordering WITHIN this method, not
    /// about WHEN ShowScreen calls it.
    /// </summary>
    private void DetachPlayerVideo()
    {
        // Otherwise leftover state from the clip being torn down here (cover
        // still open, timer still counting down toward closing it) would
        // bleed into whatever gets opened next.
        _freezeFrameTimer.Stop();
        PlayerFreezeFramePopup.IsOpen = false;

        // Reset fullscreen state whenever Player is torn down (this runs on
        // every switch away from Player, per ShowScreen), not just via
        // ExitPlayerFullscreen -- otherwise the sidebar/column/icon state
        // from a fullscreen session would silently carry over into the NEXT
        // time Player opens, even for a totally different clip.
        if (_isPlayerFullscreen)
        {
            _isPlayerFullscreen = false;
            RootBorder.BorderThickness = new Thickness(1);
            PlayerSidebar.Visibility = Visibility.Visible;
            PlayerSidebarColumn.Width = new GridLength(90);
            PlayerFullscreenTransportPopup.IsOpen = false;
            PlayerFullscreenTransportBorder.Child = null;
            PlayerTransportBar.ClearValue(BackgroundProperty);
            PlayerTransportBar.ClearValue(WidthProperty);
            DockPanel.SetDock(PlayerTransportBar, Dock.Bottom);
            PlayerVideoColumnDock.Children.Insert(0, PlayerTransportBar);
            PlayerTitlePill.Margin = new Thickness(0);
            PlayerTitleBarHost.Height = 46;
            _scrim.SetExitButtonVisible(true);
            PlayerFullscreenIcon.Data = Geometry.Parse(FullscreenEnterIcon);
            PlayerFullscreenButton.ToolTip = "Fullscreen";
        }

        _seekTimer.Stop();
        // Detach the VideoView from the player FIRST, before Stop()/Dispose()
        // below -- those two are genuinely slow (LibVLC tearing down its own
        // decode/render pipeline, up to ~1s observed), and they used to run
        // first, meaning the native video HWND kept right on rendering for
        // that whole blocking call even though PlayerPanel/the overlay Popup
        // had already gone Collapsed/closed moments earlier -- reported live
        // as "the back button and title disappear but the video stays for a
        // second". Native content is its own top-level surface regardless of
        // WPF's own Visibility (see PlayerOverlayPopup's own "airspace"
        // comment) -- only actually detaching the player from the VideoView
        // makes it go blank, and doing that first means it happens instantly,
        // even though the underlying Stop()/Dispose() teardown still takes
        // its own time afterward.
        PlayerVideoView.MediaPlayer = null;
    }

    /// <summary>
    /// Full teardown: the fast detach above, then the slow part (LibVLC's
    /// own Stop()/Dispose(), up to ~1s observed). Safe to call repeatedly --
    /// DetachPlayerVideo's own resets are all idempotent, and _vlcPlayer
    /// being already null here is a normal, harmless case (ShowScreen calls
    /// DetachPlayerVideo up front, then reaches this same combined method
    /// again later in its own sequence).
    /// </summary>
    private void StopPlayerPlayback()
    {
        DetachPlayerVideo();
        DisposeVlcPlayerSync();
    }

    private void DisposeVlcPlayerSync()
    {
        if (_vlcPlayer is not null)
        {
            _vlcPlayer.Stop();
            _vlcPlayer.Dispose();
            _vlcPlayer = null;
        }
    }

    /// <summary>
    /// Same teardown as DisposeVlcPlayerSync, just off the UI thread -- for
    /// ShowScreen's own screen-switch path specifically, where blocking on
    /// this was the actual cause of the "video stays overlayed, cut in half"
    /// bug (see that call site's own comment for the fuller story). Every
    /// OTHER caller of StopPlayerPlayback (OpenInPlayer, the delete-with-undo
    /// flows, etc.) keeps the synchronous version on purpose -- e.g. a delete
    /// right after leaving Player genuinely needs LibVLC to have actually
    /// released its file handle first, not just have a disposal queued.
    /// libvlc's own calls are safe off the UI thread (a native library, not
    /// a WPF-bound one); Stop()/Dispose() don't touch any WPF element.
    /// </summary>
    private void DisposeVlcPlayerAsync()
    {
        if (_vlcPlayer is null)
            return;

        LibVlc.MediaPlayer playerToDispose = _vlcPlayer;
        _vlcPlayer = null;
        // Tracked so StartPlayerPlayback can await it before a newly reopened
        // clip creates its own MediaPlayer against the same VideoView HWND --
        // see that method's own comment.
        _pendingVlcDisposeTask = Task.Run(() =>
        {
            try
            {
                playerToDispose.Stop();
                playerToDispose.Dispose();
            }
            catch
            {
                // Best-effort teardown -- nothing meaningful to recover here.
            }
        });
    }

    /// <summary>
    /// Reveals the clip in Explorer and closes the overlay, same idea as
    /// RevealInExplorerAndClose -- but lands back on Gallery instead of
    /// trying to resume Player. Player's video surface is a native VLC HWND
    /// and its floating overlay Popup is placed relative to it (see
    /// PlayerOverlayPopup's own comment on "airspace"); neither survives a
    /// hide/show round trip the way ShowScreen normally drives them; leaving
    /// Player "as-is" through that trip previously left the video black and
    /// the Popup stuck at its pre-hide screen position, and going Idle then
    /// back into Gallery afterward could crash on the half-torn-down state.
    /// Gallery is plain WPF state and reopens exactly as it was left, with
    /// the clip still right there to reopen in one click -- consistent with
    /// _lastScreen elsewhere in this file never treating Player as a screen
    /// safe to auto-resume either.
    /// </summary>
    private void PlayerFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlayerFile is null)
            return;
        RevealInExplorer(_currentPlayerFile.FullName);
        ShowScreen(Screen.Gallery);
        LoadGallery();
        CloseOverlay(preserveScreen: true);
    }

    /// <summary>
    /// Swaps PlayerTitle for an inline TextBox in place, same pattern as the
    /// Gallery cards' rename. Same double-invocation footgun applies here too:
    /// removing the focused TextBox to restore the label fires its own
    /// LostFocus, which would re-run a guarded commit a second time against a
    /// stale FileInfo -- the `finished` flag is guarded at both call sites,
    /// not inside CommitRename itself, so the legitimate first call still runs.
    /// </summary>
    /// <summary>Double-clicking the title in the video player overlay is just a shortcut into the exact same rename flow as the Rename button on the rail -- same field, same in-place TextBox swap, so nothing else needs to know which one triggered it.</summary>
    private void PlayerTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            PlayerRename_Click(sender, e);
    }

    private void PlayerRename_Click(object sender, RoutedEventArgs e)
    {
        // A streamed clip has no local FileInfo -- rename needs
        // _currentPlayerRemoteOrigin alone, same reasoning as
        // PlayerDelete_Click's own fix.
        if (_currentPlayerFile is null && _currentPlayerRemoteOrigin is null)
            return;
        _isPlayerRenaming = true;
        FileInfo? file = _currentPlayerFile;
        string currentName = file?.Name ?? Path.GetFileName(_currentPlayerRemoteOrigin!.Value.RelativePath);
        bool finished = false;

        var stack = (StackPanel)PlayerTitle.Parent;
        int index = stack.Children.IndexOf(PlayerTitle);

        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(currentName),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            MinWidth = 120,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Brushes.White,
        };

        stack.Children.RemoveAt(index);
        stack.Children.Insert(index, box);

        _cancelPlayerRename = () => { if (!finished) { finished = true; RevertBox(); } };

        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { if (!finished) { finished = true; CommitRename(); } }
            else if (ke.Key == Key.Escape) { ke.Handled = true; if (!finished) { finished = true; RevertBox(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; CommitRename(); } };

        void RevertBox()
        {
            _isPlayerRenaming = false;
            _cancelPlayerRename = null;
            stack.Children.Remove(box);
            stack.Children.Insert(index, PlayerTitle);
        }

        async void CommitRename()
        {
            _isPlayerRenaming = false;
            _cancelPlayerRename = null;
            string newName = box.Text.Trim();
            if (string.IsNullOrEmpty(newName) || newName == Path.GetFileNameWithoutExtension(currentName))
            {
                RevertBox();
                return;
            }

            if (file is null)
            {
                // Pure streaming -- no local file to move at all, straight to
                // the transmitter's own rename (HandleRenameClip), same as
                // Delete's remote-only path. Playback itself keeps running
                // uninterrupted throughout (no StopPlayerPlayback/OpenInPlayer
                // needed -- the underlying stream connection doesn't care
                // what the file's called), just the title and the tracked
                // relative path update once it's confirmed.
                (string relPath, string deviceId) = _currentPlayerRemoteOrigin!.Value;
                stack.Children.Remove(box);
                stack.Children.Insert(index, PlayerTitle);
                (bool success, string? error, string? newRelPath) = await _pairing.RenameRemoteClipAsync(relPath, newName);
                if (success)
                {
                    string finalRelPath = newRelPath ?? relPath;
                    _currentPlayerRemoteOrigin = (finalRelPath, deviceId);
                    PlayerTitle.Text = newName;
                    if (_currentStreamToken is not null)
                        _remoteStreamServer.UpdateSessionPath(_currentStreamToken, finalRelPath);
                }
                else
                {
                    MessageBox.Show(this, $"Couldn't rename on {_settings.PairedPeerName}'s PC: {error}", "Backtrack");
                }
                return;
            }

            // Captured before OpenInPlayer below clears it (same
            // reasoning as RunTrimAsync's own remoteOrigin capture).
            (string RelativePath, string DeviceId)? remoteOrigin = _currentPlayerRemoteOrigin;
            try
            {
                StopPlayerPlayback();
                string newPath = Path.Combine(file.DirectoryName!, newName + file.Extension);
                File.Move(file.FullName, newPath);
                _currentPlayerFile = new FileInfo(newPath);
                PlayerTitle.Text = Path.GetFileNameWithoutExtension(_currentPlayerFile.Name);
                stack.Children.Remove(box);
                stack.Children.Insert(index, PlayerTitle);
                OpenInPlayer(_currentPlayerFile);

                if (remoteOrigin is (string relPath2, string deviceId2))
                {
                    // Restore what OpenInPlayer just cleared, then mirror
                    // the rename to the real clip on the stream PC --
                    // see HandleRenameClip.
                    _currentPlayerRemoteOrigin = remoteOrigin;
                    (bool success, string? error, string? newRelPath) = await _pairing.RenameRemoteClipAsync(relPath2, newName);
                    if (success)
                        _currentPlayerRemoteOrigin = (newRelPath ?? relPath2, deviceId2);
                    else
                        MessageBox.Show(this, $"Renamed locally, but couldn't rename on {_settings.PairedPeerName}'s PC: {error}", "Backtrack");
                }
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't rename: {ex.Message}", "Backtrack");
            }
            RevertBox();
        }
    }

    private void PlayerDelete_Click(object sender, RoutedEventArgs e)
    {
        // A streamed clip has no local FileInfo at all -- delete needs
        // _currentPlayerRemoteOrigin alone, not both, or every remote
        // delete during streaming would silently no-op (reported live as
        // "trimming, deleting, renaming COMPLETELY broken remotely" --
        // QueueRemoteDeleteWithUndo already accepts file: null below and
        // was never actually the problem, this guard was).
        if (_currentPlayerFile is null && _currentPlayerRemoteOrigin is null)
            return;

        FileInfo? file = _currentPlayerFile;
        (string RelativePath, string DeviceId)? remoteOrigin = _currentPlayerRemoteOrigin;
        string displayName = file?.Name ?? Path.GetFileName(remoteOrigin!.Value.RelativePath);

        string message = remoteOrigin is null
            ? $"Are you sure you want to delete \"{displayName}\"? This will send it to your recycle bin."
            : $"Delete \"{displayName}\"? This deletes the original clip on {_settings.PairedPeerName}'s PC (sent to its Recycle Bin there){(file is null ? "." : ", and the cached copy here.")}";

        ShowConfirmDialog(
            message,
            "Delete",
            confirmed =>
            {
                if (!confirmed)
                    return;

                _currentPlayerFile = null;
                _currentPlayerRemoteOrigin = null;
                StopPlayerPlayback();
                ShowScreen(Screen.Gallery);

                if (remoteOrigin is (string relPath, _))
                {
                    // This local file (if any -- null while streaming) is
                    // just a downloaded cache copy, not the real clip --
                    // delete it outright right away (no undo toast for IT
                    // specifically, nothing meaningful to
                    // undo about a cache copy), then run the actual remote
                    // delete through the same undo-toast flow the Gallery
                    // card's own remote delete uses. `file: null` since
                    // there's no RemoteGalleryFile handy here to also key a
                    // remote thumbnail-cache cleanup off of.
                    if (file is not null)
                    {
                        try { File.Delete(file.FullName); } catch { /* best effort */ }
                    }
                    QueueRemoteDeleteWithUndo(relPath, displayName, file: null);
                }
                else
                {
                    QueueDeleteWithUndo(file!); // remoteOrigin null means this is a genuinely local clip -- file is never null here
                }
            });
    }

    // -------------------------------------------------------- playback speed

    private void PlayerSpeedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vlcPlayer is null)
            return;
        _playbackSpeedIndex = (_playbackSpeedIndex + 1) % PlaybackSpeeds.Length;
        float speed = PlaybackSpeeds[_playbackSpeedIndex];
        _vlcPlayer.SetRate(speed);
        PlayerSpeedText.Text = speed == (int)speed ? $"{(int)speed}x" : $"{speed}x";
    }

    private void ResetPlaybackSpeed()
    {
        _playbackSpeedIndex = 1; // 1f
        PlayerSpeedText.Text = "1x";
        // No SetRate(1f) call needed here -- this only ever runs right before
        // a fresh MediaPlayer is created/reused for a new clip (see
        // ShowPlayerLoadingUi/OpenInPlayer), which already starts at normal
        // speed on its own.
    }

    // ------------------------------------------------------------------ trim

    // Original PlayerTransportRow Grid.Column for each control
    // MoveTransportControlsForTrim reparents -- restored when moving them
    // back out of the trim row.
    private const int PlayPauseButtonHomeColumn = 0;
    private const int AudioTrackComboHomeColumn = 4;
    private const int PlayerSpeedButtonHomeColumn = 5;
    private const int PlayerVolumeButtonHomeColumn = 6;
    private const int PlayerFullscreenButtonHomeColumn = 7;

    // PlayerTransportButton style fixes Play/Pause at 42x42 (deliberately
    // oversized for its normal role as the Player's primary control -- see
    // the style's own comment) -- too big to sit comfortably among the
    // other trim-row icons, which are ~16px glyphs in a compact bare
    // button. A local Width/Height override (which wins over the style's
    // own Setters) shrinks it while it's reparented into the trim row;
    // explicitly setting it back to the style's real values on the way out
    // restores it exactly, rather than relying on ClearValue subtlety.
    private const double PlayPauseButtonNormalSize = 42;
    private const double PlayPauseButtonTrimSize = 28;

    /// <summary>
    /// Opening defaults the selection to the WHOLE clip (handles at each
    /// end) -- there's no "Set start"/"Set end" button anymore to build the
    /// range up from nothing, so starting from "everything selected, drag
    /// either handle inward to trim it away" matches how every real editor's
    /// trim timeline actually opens. PlayerTransportRow itself (current
    /// time, seek bar, duration) is collapsed entirely while trimming --
    /// the trim timeline already covers scrubbing and playback looping on
    /// its own, so none of that earns its vertical space back until Trim
    /// closes, and collapsing the whole row (not just hiding pieces of it)
    /// actually shrinks the window instead of leaving empty space.
    /// Play/Pause and audio track/speed/volume/fullscreen are the
    /// exception: those five get reparented up onto the trim action row
    /// instead of just disappearing with the rest of the row (see
    /// MoveTransportControlsForTrim's own comment).
    /// </summary>
    private void PlayerTrim_Click(object sender, RoutedEventArgs e)
    {
        bool opening = TrimPanel.Visibility != Visibility.Visible;
        TrimPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        PlayerTransportRow.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        MoveTransportControlsForTrim(opening);
        if (opening)
        {
            long lengthMs = _vlcPlayer?.Length ?? 0;
            _trimStart ??= TimeSpan.Zero;
            _trimEnd ??= TimeSpan.FromMilliseconds(Math.Max(0, lengthMs));
            BuildTrimRuler();
            UpdateTrimTimelineUi();
        }
        else
        {
            StopPreviewLoop();
        }
    }

    /// <summary>
    /// Moves Play/Pause and the audio track/speed/volume/fullscreen
    /// controls between PlayerTransportRow and their trim-row homes (the
    /// same instances, not copies -- WPF can't render one element in two
    /// places at once). Popups anchored to PlayerSpeedButton/
    /// PlayerVolumeButton via PlacementTarget={Binding ElementName=...}
    /// keep working regardless of which panel currently parents the button
    /// itself. Play/Pause goes to the FRONT of TrimActionButtons (leading
    /// Preview/Replace/Save/Cancel, same position it leads from in the
    /// normal transport row) and gets shrunk down to fit there; the other
    /// four go into TrimTransportExtras, right-aligned same as before.
    /// </summary>
    private void MoveTransportControlsForTrim(bool intoTrimRow)
    {
        PlayPauseButton.Width = PlayPauseButton.Height = intoTrimRow ? PlayPauseButtonTrimSize : PlayPauseButtonNormalSize;
        // Loses PlayerTransportButton's own filled-circle background while
        // in the trim row -- BareIconButton's template is just a
        // ContentPresenter (see its own comment), so this keeps the same
        // Play/Pause icon content, just without the surrounding circle,
        // matching the other trim buttons' own bare look.
        PlayPauseButton.Style = (Style)FindResource(intoTrimRow ? "BareIconButton" : "PlayerTransportButton");
        // A right margin only while in the trim row -- PlayerTransportRow's
        // own gap to PlayerCurrentTime already comes from THAT element's
        // own left margin, so a margin here too would only double it up.
        PlayPauseButton.Margin = intoTrimRow ? new Thickness(0, 0, 10, 0) : default;
        Reparent(PlayPauseButton, intoTrimRow ? TrimActionButtons : PlayerTransportRow, intoTrimRow ? null : PlayPauseButtonHomeColumn, insertAtFront: intoTrimRow);
        Reparent(AudioTrackCombo, intoTrimRow ? TrimTransportExtras : PlayerTransportRow, intoTrimRow ? null : AudioTrackComboHomeColumn);
        Reparent(PlayerSpeedButton, intoTrimRow ? TrimTransportExtras : PlayerTransportRow, intoTrimRow ? null : PlayerSpeedButtonHomeColumn);
        Reparent(PlayerVolumeButton, intoTrimRow ? TrimTransportExtras : PlayerTransportRow, intoTrimRow ? null : PlayerVolumeButtonHomeColumn);
        Reparent(PlayerFullscreenButton, intoTrimRow ? TrimTransportExtras : PlayerTransportRow, intoTrimRow ? null : PlayerFullscreenButtonHomeColumn);

        static void Reparent(FrameworkElement element, Panel newParent, int? gridColumn, bool insertAtFront = false)
        {
            if (element.Parent is Panel oldParent && !ReferenceEquals(oldParent, newParent))
                oldParent.Children.Remove(element);
            if (gridColumn is int col)
                Grid.SetColumn(element, col);
            if (!newParent.Children.Contains(element))
            {
                if (insertAtFront)
                    newParent.Children.Insert(0, element);
                else
                    newParent.Children.Add(element);
            }
        }
    }

    private void TrimCancel_Click(object sender, RoutedEventArgs e)
    {
        _trimStart = null;
        _trimEnd = null;
        TrimStartText.Text = "0:00";
        TrimEndText.Text = "0:00";
        TrimStatusText.Text = "";
        TrimPanel.Visibility = Visibility.Collapsed;
        PlayerTransportRow.Visibility = Visibility.Visible;
        MoveTransportControlsForTrim(intoTrimRow: false);
        StopPreviewLoop();
    }

    private void TrimStartHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _trimDragMode = TrimDragMode.Start;
        TrimTimelineTrack.CaptureMouse();
        // Stops this from also bubbling up to TrimTimelineTrack_MouseDown,
        // which would otherwise ALSO start a Seek drag from the same click.
        e.Handled = true;
    }

    private void TrimEndHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _trimDragMode = TrimDragMode.End;
        TrimTimelineTrack.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>Clicking the open track itself (not a handle -- those set e.Handled and never reach here) scrubs playback, same as the normal transport seek bar.</summary>
    private void TrimTimelineTrack_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _trimDragMode = TrimDragMode.Seek;
        _isScrubbing = true;
        TrimTimelineTrack.CaptureMouse();
        ProcessTrimTimelineInput(e.GetPosition(TrimTimelineTrack));
    }

    private void TrimTimelineTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (_trimDragMode == TrimDragMode.None)
            return;
        ProcessTrimTimelineInput(e.GetPosition(TrimTimelineTrack));
    }

    private void TrimTimelineTrack_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_trimDragMode == TrimDragMode.None)
            return;

        bool wasSeek = _trimDragMode == TrimDragMode.Seek;
        TrimTimelineTrack.ReleaseMouseCapture();
        TrimHandleTooltipPopup.IsOpen = false;
        if (wasSeek)
        {
            ProcessTrimTimelineInput(e.GetPosition(TrimTimelineTrack), immediate: true);
            _isScrubbing = false;
        }
        _trimDragMode = TrimDragMode.None;
    }

    private void TrimTimelineTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        BuildTrimRuler();
        UpdateTrimTimelineUi();
    }

    /// <summary>
    /// Shared math for all three drag modes -- dragging TrimStartHandle
    /// clamps to just before the current end (can't drag past it), dragging
    /// TrimEndHandle clamps to just after the current start, and dragging
    /// the open track scrubs playback through the same CommitSeek/
    /// _seekDebounceTimer path the normal transport seek bar uses (see
    /// ProcessSeekInput's own near-identical shape -- kept as a separate
    /// method rather than shared, since it reads/writes TrimTimelineTrack's
    /// own elements, not PlayerSeekTrack's).
    /// </summary>
    private void ProcessTrimTimelineInput(Point pos, bool immediate = false)
    {
        if (_vlcPlayer is null)
            return;
        double trackWidth = TrimTimelineTrack.ActualWidth;
        if (trackWidth <= 0)
            return;

        long lengthMs = Math.Max(1, _vlcPlayer.Length);
        double ratio = Math.Clamp(pos.X / trackWidth, 0.0, 1.0);
        long ms = (long)(ratio * lengthMs);

        switch (_trimDragMode)
        {
            case TrimDragMode.Start:
            {
                long endMs = (long)(_trimEnd ?? TimeSpan.FromMilliseconds(lengthMs)).TotalMilliseconds;
                ms = Math.Clamp(ms, 0, Math.Max(0, endMs - 1));
                _trimStart = TimeSpan.FromMilliseconds(ms);
                ShowTrimHandleTooltip(pos.X, ms);
                break;
            }
            case TrimDragMode.End:
            {
                long startMs = (long)(_trimStart ?? TimeSpan.Zero).TotalMilliseconds;
                ms = Math.Clamp(ms, Math.Min(lengthMs, startMs + 1), lengthMs);
                _trimEnd = TimeSpan.FromMilliseconds(ms);
                ShowTrimHandleTooltip(pos.X, ms);
                break;
            }
            case TrimDragMode.Seek:
                _targetSeekMs = ms;
                PlayerCurrentTime.Text = FormatDuration(ms);
                if (immediate)
                {
                    _seekDebounceTimer.Stop();
                    if (_vlcPlayer.IsSeekable)
                        CommitSeek(ms);
                    else
                        _seekDebounceTimer.Start();
                }
                else
                {
                    _seekDebounceTimer.Stop();
                    _seekDebounceTimer.Start();
                }
                break;
        }

        UpdateTrimTimelineUi();
    }

    private void ShowTrimHandleTooltip(double x, long ms)
    {
        TrimHandleTooltipText.Text = FormatDuration(ms);
        TrimHandleTooltipPopup.HorizontalOffset = x - 15;
        TrimHandleTooltipPopup.VerticalOffset = -30;
        TrimHandleTooltipPopup.IsOpen = true;
    }

    /// <summary>
    /// Repositions everything on the trim timeline from current state:
    /// TrimSelectedRange as a single bright band spanning [start, end]
    /// over TrimTrackBg's dim base (the standard trim-editor look -- one
    /// highlighted band reads as "this is what gets kept" more clearly than
    /// two separate "dimmed before"/"dimmed after" overlays would), the two
    /// handles centered on their own boundary, and the playhead reflecting
    /// live playback position. Called on open, every drag-move tick, every
    /// track resize, and from UpdatePlayerSeekUi's own regular tick so the
    /// playhead keeps moving while the panel's open and nothing's being
    /// dragged.
    /// </summary>
    private void UpdateTrimTimelineUi()
    {
        if (_vlcPlayer is null)
            return;
        double trackWidth = TrimTimelineTrack.ActualWidth;
        if (trackWidth <= 0)
            return;

        long lengthMs = Math.Max(1, _vlcPlayer.Length);
        long startMs = (long)(_trimStart ?? TimeSpan.Zero).TotalMilliseconds;
        long endMs = (long)(_trimEnd ?? TimeSpan.FromMilliseconds(lengthMs)).TotalMilliseconds;

        double startX = Math.Clamp((double)startMs / lengthMs, 0, 1) * trackWidth;
        double endX = Math.Clamp((double)endMs / lengthMs, 0, 1) * trackWidth;

        TrimSelectedRange.Margin = new Thickness(startX, 0, 0, 0);
        TrimSelectedRange.Width = Math.Max(0, endX - startX);

        // Clamped to stay fully within [0, trackWidth], not just centered on
        // startX/endX -- at either extreme (trim start at 0:00, or end at
        // the very end of the clip) centering the handle exactly ON that
        // boundary pushes half its own width past the track's edge, which
        // read as the handle getting cut off by the window itself rather
        // than just sitting flush against the track's own edge.
        const double handleWidth = 10;
        double maxHandleX = Math.Max(0, trackWidth - handleWidth);
        TrimStartHandle.Margin = new Thickness(Math.Clamp(startX - handleWidth / 2, 0, maxHandleX), 0, 0, 0);
        TrimEndHandle.Margin = new Thickness(Math.Clamp(endX - handleWidth / 2, 0, maxHandleX), 0, 0, 0);

        const double playheadWidth = 2;
        double playRatio = Math.Clamp((double)_vlcPlayer.Time / lengthMs, 0, 1);
        double playheadX = Math.Clamp(playRatio * trackWidth - playheadWidth / 2, 0, Math.Max(0, trackWidth - playheadWidth));
        TrimPlayhead.Margin = new Thickness(playheadX, 0, 0, 0);

        TrimStartText.Text = FormatDuration(startMs);
        TrimEndText.Text = FormatDuration(endMs);
    }

    /// <summary>Evenly-spaced time labels along the top of the timeline -- rebuilt on open and on resize (the label text itself never needs a mid-drag update, only the positions would, and those don't change without a resize).</summary>
    private void BuildTrimRuler()
    {
        TrimRulerCanvas.Children.Clear();
        if (_vlcPlayer is null)
            return;
        double trackWidth = TrimTimelineTrack.ActualWidth;
        if (trackWidth <= 0)
            return;
        long lengthMs = Math.Max(1, _vlcPlayer.Length);

        const int tickCount = 6;
        for (int i = 0; i < tickCount; i++)
        {
            double ratio = i / (double)(tickCount - 1);
            double x = ratio * trackWidth;
            long ms = (long)(ratio * lengthMs);

            var tick = new Border { Width = 1, Height = 5, Background = (Brush)FindResource("Hairline") };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, 0);
            TrimRulerCanvas.Children.Add(tick);

            var label = new TextBlock { Text = FormatDuration(ms), FontSize = 9.5, Foreground = (Brush)FindResource("Text2") };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // First/last labels clamp inside the track's own bounds instead
            // of centering on their tick, which would otherwise overhang off
            // either edge of the panel.
            double labelX = i == 0 ? x : i == tickCount - 1 ? x - label.DesiredSize.Width : x - label.DesiredSize.Width / 2;
            Canvas.SetLeft(label, labelX);
            Canvas.SetTop(label, 6);
            TrimRulerCanvas.Children.Add(label);
        }
    }

    private async void TrimReplace_Click(object sender, RoutedEventArgs e) => await RunTrimAsync(replaceOriginal: true);

    private async void TrimSaveNew_Click(object sender, RoutedEventArgs e) => await RunTrimAsync(replaceOriginal: false);

    /// <summary>
    /// Loops playback between the two trim handles -- lets the exact
    /// selection get reviewed repeatedly before committing to Replace/Save,
    /// instead of guessing the boundaries, trimming, checking the result,
    /// and re-trimming if they were off. The actual loop check runs off the
    /// same _seekTimer tick UpdatePlayerSeekUi already uses (100ms), not a
    /// separate timer -- reads _trimStart/_trimEnd live each tick, so
    /// adjusting either boundary while looping takes effect immediately
    /// without needing to stop and restart the loop.
    /// </summary>
    private void PreviewLoopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vlcPlayer is null)
            return;

        if (_previewLooping)
        {
            StopPreviewLoop();
            return;
        }

        if (_trimStart is null || _trimEnd is null || _trimEnd <= _trimStart)
        {
            MessageBox.Show(this, "Set both a start and an end point first.", "Backtrack");
            return;
        }

        _previewLooping = true;
        // Swaps the icon itself (see the XAML's own comment on
        // PreviewLoopIcon/PreviewStopIcon) -- NOT PreviewLoopButton.Content,
        // which used to be a plain string assignment here and would
        // silently replace the whole icon with literal text the moment
        // this ran.
        PreviewLoopIcon.Visibility = Visibility.Collapsed;
        PreviewStopIcon.Visibility = Visibility.Visible;
        CommitSeek((long)_trimStart.Value.TotalMilliseconds);
        if (!_vlcPlayer.IsPlaying)
            _vlcPlayer.Play();
    }

    private void StopPreviewLoop()
    {
        _previewLooping = false;
        PreviewLoopIcon.Visibility = Visibility.Visible;
        PreviewStopIcon.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Exports via LibVLC's own transcode/sout chain (no ffmpeg dependency) using a
    /// second, headless MediaPlayer so the visible preview player keeps playing
    /// undisturbed. Runs roughly real-time, not instantly.
    /// </summary>
    private async Task RunTrimAsync(bool replaceOriginal)
    {
        if (_trimStart is null || _trimEnd is null || _trimEnd <= _trimStart)
        {
            MessageBox.Show(this, "Set both a start and end point first (end must be after start).", "Backtrack");
            return;
        }

        // Streaming (no local file at all) -- runs entirely on the
        // transmitter's own side instead, no clip bytes cross the network
        // in either direction. See TrimRemoteClipAsync/HandleTrimClipAsync's
        // own comments for why. This used to just silently do nothing
        // (blocked by the old _currentPlayerFile is null check above),
        // reported live as "trimming COMPLETELY broken remotely".
        if (_currentPlayerFile is null)
        {
            if (_currentPlayerRemoteOrigin is not null)
            {
                await RunRemoteTrimAsync(replaceOriginal);
                return;
            }

            // Both null -- neither a local file NOR a tracked remote origin.
            // Used to silently return here with zero feedback at all (no
            // toast, no error, nothing), which is indistinguishable from
            // Trim just not working -- reported live as exactly that
            // ("it just doesn't work"). Showing this explicitly, and
            // logging it, is what actually tells us whether THIS is the
            // real failure (a state-tracking bug losing the remote origin
            // somewhere) versus the request genuinely reaching the
            // transmitter and failing there -- see HandleTrimClipAsync's
            // own logging for that side.
            AppLog.Write("[trim_clip] RunTrimAsync: both _currentPlayerFile and _currentPlayerRemoteOrigin are null -- nothing to trim, this is the actual failure");
            MessageBox.Show(this, "Nothing to trim -- this clip isn't tracked as either a local file or a remote clip right now. Try reopening it.", "Backtrack");
            return;
        }

        if (_libVlc is null)
            return;

        FileInfo sourceFile = _currentPlayerFile;
        TimeSpan start = _trimStart.Value;
        TimeSpan end = _trimEnd.Value;
        // Captured now -- every OpenInPlayer call below (both branches call
        // it once the local trim's done) clears _currentPlayerRemoteOrigin
        // unconditionally, same as any other clip open. This is what makes
        // the trim result actually get sent back to the clip's real PC
        // afterward, further down in each branch.
        (string RelativePath, string DeviceId)? remoteOrigin = _currentPlayerRemoteOrigin;

        string tempOut = Path.Combine(Path.GetTempPath(), $"cc_trim_{Guid.NewGuid():N}{sourceFile.Extension}");

        _isTrimming = true;
        TrimReplaceButton.IsEnabled = false;
        TrimSaveNewButton.IsEnabled = false;
        TrimStatusText.Text = "Trimming...";

        // Stop the preview before exporting, not just before the later file copy:
        // leaving it running meant two simultaneous LibVLC decode sessions on the
        // same engine (preview + export, both forced onto software decode), which
        // was heavy enough that the whole app appeared to freeze during export.
        // The source file also needs to be free of any open handle before it can
        // later be overwritten in the replace-original path.
        StopPlayerPlayback();

        try
        {
            await Task.Run(() => ExportTrim(sourceFile.FullName, tempOut, start, end));

            if (replaceOriginal)
            {
                bool? userConfirmed = null;
                ShowConfirmDialog("Replace the original clip with this trimmed version? This can't be undone.", "Replace", c => userConfirmed = c);
                while (!userConfirmed.HasValue && IsVisible)
                {
                    await Task.Delay(50);
                }
                if (userConfirmed != true)
                {
                    File.Delete(tempOut);
                    OpenInPlayer(sourceFile);
                    return;
                }
                File.Copy(tempOut, sourceFile.FullName, overwrite: true);
                File.Delete(tempOut);
                _currentPlayerFile = new FileInfo(sourceFile.FullName);
                OpenInPlayer(_currentPlayerFile);

                if (remoteOrigin is (string relPath, _))
                {
                    // Restore what OpenInPlayer just cleared -- still the
                    // SAME remote clip, just replaced with a trimmed version.
                    _currentPlayerRemoteOrigin = remoteOrigin;
                    TrimStatusText.Text = $"Sending to {_settings.PairedPeerName}'s PC...";
                    (bool upSuccess, string? upError, _) = await _pairing.UploadRemoteClipAsync(relPath, _currentPlayerFile.FullName, overwrite: true);
                    if (!upSuccess)
                        MessageBox.Show(this, $"Trimmed locally, but couldn't send it back to {_settings.PairedPeerName}'s PC: {upError}", "Backtrack");
                }
            }
            else
            {
                string newName = $"{Path.GetFileNameWithoutExtension(sourceFile.Name)} (trimmed){sourceFile.Extension}";
                string destPath = Path.Combine(sourceFile.DirectoryName!, newName);
                int i = 2;
                while (File.Exists(destPath))
                {
                    destPath = Path.Combine(sourceFile.DirectoryName!, $"{Path.GetFileNameWithoutExtension(sourceFile.Name)} (trimmed {i}){sourceFile.Extension}");
                    i++;
                }
                File.Copy(tempOut, destPath, overwrite: false);
                File.Delete(tempOut);
                _ = RefreshGalleryCountAsync();
                OpenInPlayer(sourceFile);

                if (remoteOrigin is (string relPath, _))
                {
                    // Still viewing the same original remote clip (this
                    // branch reopens sourceFile unchanged, not the new
                    // trimmed copy) -- restore what OpenInPlayer just
                    // cleared, then send the NEW trimmed copy to the stream
                    // PC as an additional clip alongside the original
                    // (overwrite:false lets HandlePutClipAsync dedupe the
                    // name server-side if needed, same as this already did
                    // locally just above).
                    _currentPlayerRemoteOrigin = remoteOrigin;
                    int lastSlash = relPath.LastIndexOf('/');
                    string folderPrefix = lastSlash < 0 ? "" : relPath[..lastSlash];
                    string remoteDestRelPath = folderPrefix.Length == 0 ? Path.GetFileName(destPath) : $"{folderPrefix}/{Path.GetFileName(destPath)}";
                    TrimStatusText.Text = $"Sending to {_settings.PairedPeerName}'s PC...";
                    (bool upSuccess, string? upError, _) = await _pairing.UploadRemoteClipAsync(remoteDestRelPath, destPath, overwrite: false);
                    if (!upSuccess)
                        MessageBox.Show(this, $"Trimmed locally, but couldn't send the new clip to {_settings.PairedPeerName}'s PC: {upError}", "Backtrack");
                }
            }

            // No "Done." status text -- TrimPanel collapses on the very next
            // line anyway, so it only ever rendered for a single stray
            // frame, reading as random leftover text near the end of the
            // timeline rather than a real completion message.
            TrimPanel.Visibility = Visibility.Collapsed;
            PlayerTransportRow.Visibility = Visibility.Visible;
            MoveTransportControlsForTrim(intoTrimRow: false);
        }
        catch (Exception ex)
        {
            TrimStatusText.Text = "";
            MessageBox.Show(this, $"Trim failed: {ex.Message}", "Backtrack");
            OpenInPlayer(sourceFile);
        }
        finally
        {
            _isTrimming = false;
            TrimReplaceButton.IsEnabled = true;
            TrimSaveNewButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The streaming counterpart of RunTrimAsync's local logic -- sends
    /// trim_clip and lets the transmitter do the actual encode against its
    /// own real file (see TrimRemoteClipAsync), so this never downloads or
    /// uploads any clip bytes, just the one request/response. Only
    /// meaningfully different from the local path in what happens
    /// afterward: a "replace" changes the exact file this Player is
    /// actively streaming out from under itself, so rather than try to
    /// reload that stream in place mid-playback (a real race against
    /// whatever's already buffered), this just returns to Gallery and lets
    /// reopening the clip fresh pick up the new, genuinely shorter result.
    /// "Save as new" leaves the original (and this Player's current
    /// playback) untouched, so nothing needs to restart at all.
    /// </summary>
    private async Task RunRemoteTrimAsync(bool replaceOriginal)
    {
        (string relPath, string _) = _currentPlayerRemoteOrigin!.Value;
        TimeSpan start = _trimStart!.Value;
        TimeSpan end = _trimEnd!.Value;
        AppLog.Write($"[trim_clip] RunRemoteTrimAsync entered: path='{relPath}' {start}-{end} replace={replaceOriginal}");

        if (replaceOriginal)
        {
            bool? userConfirmed = null;
            ShowConfirmDialog("Replace the original clip with this trimmed version? This can't be undone.", "Replace", c => userConfirmed = c);
            while (!userConfirmed.HasValue && IsVisible)
                await Task.Delay(50);
            if (userConfirmed != true)
            {
                AppLog.Write("[trim_clip] replace not confirmed -- aborted");
                return;
            }

            // The clip currently STREAMING right now holds a real open read
            // handle on the transmitter's own copy of this file (see
            // StreamFileResponseAsync) for as long as playback keeps going --
            // trim_clip's own final overwrite on that PC needs to WRITE to
            // that exact file, which a still-open reader blocks outright,
            // not just briefly. Stopping playback here (same idea as
            // RunTrimAsync's local path already does before its own export)
            // is what actually lets that read handle go on the transmitter's
            // side. Not needed for "save as new" -- that writes a brand new
            // file, and a second concurrent READ of the original doesn't
            // conflict with the stream's own read.
            //
            // DetachPlayerVideo()+DisposeVlcPlayerAsync(), NOT the plain
            // StopPlayerPlayback() the local path uses -- that one blocks the
            // UI thread on _vlcPlayer.Dispose() (see DisposeVlcPlayerSync's
            // own comment on exactly this), which is fine for a local file
            // but confirmed live to hang the WHOLE APP ("not responding")
            // here specifically: disposing a MediaPlayer mid network-stream
            // took long enough to be a real, visible freeze, not the near-
            // instant local-file teardown that call was written for.
            DetachPlayerVideo();
            DisposeVlcPlayerAsync();
        }

        _isTrimming = true;
        TrimReplaceButton.IsEnabled = false;
        TrimSaveNewButton.IsEnabled = false;
        TrimStatusText.Text = $"Trimming on {_settings.PairedPeerName}'s PC...";

        try
        {
            (bool success, string? error, _) = await _pairing.TrimRemoteClipAsync(relPath, start, end, replaceOriginal);
            AppLog.Write(success ? "[trim_clip] RunRemoteTrimAsync: succeeded" : $"[trim_clip] RunRemoteTrimAsync: failed -- {error}");
            if (!success)
            {
                MessageBox.Show(this, $"Couldn't trim on {_settings.PairedPeerName}'s PC: {error}", "Backtrack");
                return;
            }

            TrimPanel.Visibility = Visibility.Collapsed;
            PlayerTransportRow.Visibility = Visibility.Visible;
            MoveTransportControlsForTrim(intoTrimRow: false);

            if (replaceOriginal)
            {
                StopPlayerPlayback();
                ShowScreen(Screen.Gallery);
                LoadGallery();
                RefreshRecentClipsOverlay();
            }
            else
            {
                _ = RefreshGalleryCountAsync();
                MessageBox.Show(this, $"Trimmed clip saved on {_settings.PairedPeerName}'s PC.", "Backtrack");
            }
        }
        finally
        {
            _isTrimming = false;
            TrimReplaceButton.IsEnabled = true;
            TrimSaveNewButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Headless counterpart to RunTrimAsync -- reused ExportTrim directly, no
    /// dialogs/UI (the confirm-before-replace step already happened on the
    /// RECEIVER side, before it even sent the request; see
    /// PlayerTrimReplace/SaveNew_Click's own remote branch), since this runs
    /// in response to a trim_clip request from a paired PC, off this app's
    /// own UI thread. Same "(trimmed)"/"(trimmed 2)" dedup naming as the
    /// local save-as-new path, for the exact same reason: two remote trims
    /// of the same clip shouldn't silently clobber each other.
    /// </summary>
    private async Task<(bool Success, string? Error, string? NewFileName)> TrimClipForRemoteAsync(string fullPath, double startSeconds, double endSeconds, bool replaceOriginal)
    {
        var file = new FileInfo(fullPath);
        var start = TimeSpan.FromSeconds(startSeconds);
        var end = TimeSpan.FromSeconds(endSeconds);
        string tempOut = Path.Combine(Path.GetTempPath(), $"cc_trim_{Guid.NewGuid():N}{file.Extension}");
        AppLog.Write($"[trim_clip] TrimClipForRemoteAsync: '{fullPath}' {start}-{end} replace={replaceOriginal}, exporting to '{tempOut}'");

        try
        {
            await Task.Run(() => ExportTrim(fullPath, tempOut, start, end));

            // The single most useful line if this is silently doing nothing --
            // ExportTrim itself has no return value, so this is the only
            // direct evidence of whether the actual libvlc encode produced
            // real output at all before anything downstream (the copy,
            // renaming, etc.) even runs.
            long tempOutSize = File.Exists(tempOut) ? new FileInfo(tempOut).Length : -1;
            AppLog.Write($"[trim_clip] ExportTrim finished -- tempOut {(tempOutSize < 0 ? "does not exist" : $"is {tempOutSize} bytes")}");
            if (tempOutSize <= 0)
                return (false, "The trim produced no output file (libvlc export failed silently) -- check this PC's own log around ExportTrim for details.", null);

            if (replaceOriginal)
            {
                // The original is a real, actively-managed clip file --
                // something else on this PC (this same Backtrack's own
                // thumbnail generation, antivirus briefly scanning it, etc.)
                // can genuinely have it open for a moment. Confirmed live:
                // "The process cannot access the file ... because it is
                // being used by another process", with zero retry before
                // this, killing an otherwise-successful trim over a
                // transient lock. Same bounded-retry reasoning as
                // ApplySelfUpdateAsync's own robocopy step.
                await CopyWithRetryAsync(tempOut, fullPath, overwrite: true);
                File.Delete(tempOut);
                AppLog.Write($"[trim_clip] replaced '{fullPath}' in place");
                return (true, null, file.Name);
            }

            string newName = $"{Path.GetFileNameWithoutExtension(file.Name)} (trimmed){file.Extension}";
            string destPath = Path.Combine(file.DirectoryName!, newName);
            int i = 2;
            while (File.Exists(destPath))
            {
                newName = $"{Path.GetFileNameWithoutExtension(file.Name)} (trimmed {i}){file.Extension}";
                destPath = Path.Combine(file.DirectoryName!, newName);
                i++;
            }
            await CopyWithRetryAsync(tempOut, destPath, overwrite: false);
            File.Delete(tempOut);
            AppLog.Write($"[trim_clip] saved as new file '{destPath}'");
            return (true, null, newName);
        }
        catch (Exception ex)
        {
            AppLog.WriteError("[trim_clip] TrimClipForRemoteAsync threw", ex);
            try { File.Delete(tempOut); } catch { /* best effort */ }
            return (false, ex.Message, null);
        }
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

    private void ExportTrim(string sourcePath, string destPath, TimeSpan start, TimeSpan end)
    {
        if (_libVlc is null)
            return;

        using var media = new LibVlc.Media(_libVlc, new Uri(sourcePath));
        media.AddOption($":start-time={start.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        media.AddOption($":stop-time={end.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        media.AddOption($":sout=#std{{access=file,mux=mp4,dst={destPath.Replace("\\", "/")}}}");
        media.AddOption(":sout-keep");

        using var exportPlayer = new LibVlc.MediaPlayer(media);
        using var done = new System.Threading.ManualResetEventSlim(false);
        bool encounteredError = false;

        exportPlayer.EndReached += (_, _) => done.Set();
        // Previously both handlers just did done.Set() with no way to tell
        // which one fired -- ExportTrim returned normally either way, and
        // RunTrimAsync's "Replace original" path then unconditionally
        // File.Copy(..., overwrite: true)'d whatever (possibly empty or
        // partial) file resulted, silently destroying the user's saved clip
        // on a real transcode failure (codec quirk, disk pressure, a seek
        // LibVLC struggled with).
        exportPlayer.EncounteredError += (_, _) =>
        {
            encounteredError = true;
            done.Set();
        };

        exportPlayer.Play();
        if (!done.Wait(TimeSpan.FromMinutes(10)))
            throw new TimeoutException("Trim export took too long.");
        exportPlayer.Stop();

        if (encounteredError)
            throw new InvalidOperationException("LibVLC reported an error during trim export.");

        // Belt-and-suspenders: a transcode can also report success
        // (EndReached) while still leaving nothing usable on disk (e.g. a
        // start/stop-time range LibVLC accepted but couldn't actually
        // produce output for) -- verify a real, non-empty file exists before
        // the caller trusts this as a successful export.
        if (!File.Exists(destPath) || new FileInfo(destPath).Length == 0)
            throw new InvalidOperationException("Trim export produced no output file.");
    }

    // --------------------------------------------------------------- settings

    private void LoadSettingsUi()
    {
        // Rebuilt every time Settings actually opens, not just once at
        // startup -- the whole point of themes being discovered from disk
        // is that a file can change (or a new one appear) while Backtrack
        // is running; this is what makes that show up without a restart.
        BuildThemeSwatches();
        RefreshThemeSwatchSelection();
        EnableAnimationsToggle.IsChecked = _settings.EnableAnimations;

        DiagnosticLogToggle.IsChecked = _settings.DiagnosticLogEnabled;
        OpenDiagnosticLogButton.Visibility = _settings.DiagnosticLogEnabled ? Visibility.Visible : Visibility.Collapsed;

        // One-time nudge, not a permanent lock -- Developer Mode is the real
        // authority on dev-build status now (UpdateService.IsDevBuild), so
        // this only ever pre-sets it once, the first time Settings notices a
        // location mismatch; every load after that just reflects whatever
        // the toggle is actually set to, freely changeable either direction.
        if (!_settings.DeveloperModeAutoSuggested)
        {
            _settings.DeveloperModeAutoSuggested = true;
            _settings.Save();
            if (UpdateService.IsRunningFromDevLocation)
            {
                SetDeveloperModeEnabled(true);
                DeveloperModeLockedNoteText.Visibility = Visibility.Visible;
            }
        }
        DeveloperModeToggle.IsChecked = _settings.DeveloperModeEnabled;

        DisableHardwareAccelToggle.IsChecked = _settings.DisableHardwareAcceleration;

        ShowRecentClipsToggle.IsChecked = _settings.ShowRecentClipsOverlay;
        LaunchWithWindowsToggle.IsChecked = _settings.LaunchWithWindows;
        ClipsFolderText.Text = _settings.ClipsFolder;
        BufferDurationSlider.Value = _settings.ReplayBufferMinutes;
        RefreshBufferDurationUi();

        BuffersSection.Visibility = _settings.ObsIsRemote ? Visibility.Collapsed : Visibility.Visible;
        RecordingsSection.Visibility = _settings.ObsIsRemote ? Visibility.Collapsed : Visibility.Visible;

        ObsRemoteToggle.IsChecked = _settings.ObsIsRemote;
        ObsRemoteFields.Visibility = _settings.ObsIsRemote ? Visibility.Visible : Visibility.Collapsed;
        ObsHostBox.Text = _settings.ObsHost;
        ObsPortBox.Text = _settings.ObsPort.ToString();
        ObsPasswordBox.Password = _settings.ObsRemotePassword;

        ShowDisclaimerToggle.IsChecked = _settings.ShowDisclaimer;
        ShowStatusIndicatorToggle.IsChecked = _settings.ShowStatusIndicator;
        // Same unsubscribe/resubscribe reasoning as StatusIndicatorOrientationSelector
        // just below -- a programmatic SelectedIndex assignment fires
        // SelectionChanged too, which would otherwise re-save from just opening Settings.
        DefaultAudioTrackSelector.SelectionChanged -= DefaultAudioTrackSelector_SelectionChanged;
        DefaultAudioTrackSelector.SelectedIndex = Math.Clamp(_settings.DefaultPlayerAudioTrackIndex, 0, 6);
        DefaultAudioTrackSelector.SelectionChanged += DefaultAudioTrackSelector_SelectionChanged;

        // Unsubscribed/resubscribed around SelectedIndex -- see
        // OverlayLogModeSelector's identical comment just below; a
        // programmatic assignment fires SelectionChanged too, which would
        // otherwise re-save+Reposition() just from opening Settings.
        StatusIndicatorOrientationSelector.SelectionChanged -= StatusIndicatorOrientationSelector_SelectionChanged;
        StatusIndicatorOrientationSelector.SelectedIndex = _settings.StatusIndicatorOrientation == StatusIndicatorOrientation.Vertical ? 1 : 0;
        StatusIndicatorOrientationSelector.SelectionChanged += StatusIndicatorOrientationSelector_SelectionChanged;

        StatusIndicatorLocationSelector.SelectionChanged -= StatusIndicatorLocationSelector_SelectionChanged;
        StatusIndicatorLocationSelector.SelectedIndex = (int)_settings.StatusIndicatorLocation;
        StatusIndicatorLocationSelector.SelectionChanged += StatusIndicatorLocationSelector_SelectionChanged;

        UpdateStatusIndicatorPreview();

        // Self-heal, not just lock: Developer Mode may have been turned on
        // in a PRIOR session, before this forced-auto-update-disable feature
        // existed (or from a settings.json saved by an older build), so
        // DisableBacktrackAutoUpdate itself can still be false on disk even
        // though DeveloperModeEnabled is true. Reading it here without this
        // meant the toggle rendered visibly OFF despite being correctly
        // locked (IsEnabled=false) -- the lock and the checked-state were
        // being sourced from two different truths. Force them back in sync
        // here too, the same way SetDeveloperModeEnabled does at the moment
        // the dev-mode toggle itself gets clicked.
        if (_settings.DeveloperModeEnabled && !_settings.DisableBacktrackAutoUpdate)
        {
            _settings.DisableBacktrackAutoUpdate = true;
            _settings.Save();
        }
        DisableBacktrackAutoUpdateToggle.IsChecked = _settings.DisableBacktrackAutoUpdate;
        // Locked ON while Developer Mode is already active from a previous
        // session -- see SetDeveloperModeEnabled's own comment; this covers
        // the case where Settings is opened fresh with dev mode already on,
        // not just the moment the dev mode toggle itself gets clicked.
        DisableBacktrackAutoUpdateToggle.IsEnabled = !_settings.DeveloperModeEnabled;
        DisablePluginAutoUpdateToggle.IsChecked = _settings.DisablePluginAutoUpdate;
        HotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.HotkeyModifiers, (uint)_settings.HotkeyVirtualKey);

        LoadDisplaySelector();

        ShareClipsToggle.IsChecked = _settings.ShareClipsEnabled;
        RefreshShareClipsUi();
        RefreshPairingStatusUi();
        RenderDiscoveredDevices();

        RamDiskToggle.IsChecked = _settings.RamDiskEnabled;
        RamDiskFields.Visibility = _settings.RamDiskEnabled ? Visibility.Visible : Visibility.Collapsed;
        RamDiskDriveBox.Text = _settings.RamDiskDriveLetter.ToString();
        RamDiskSizeBox.Text = _settings.RamDiskSizeMb.ToString();
        RefreshRamDiskStatusText();

        StorageLimitToggle.IsChecked = _settings.StorageLimitEnabled;
        StorageLimitFields.Visibility = _settings.StorageLimitEnabled ? Visibility.Visible : Visibility.Collapsed;
        StorageLimitGbBox.Text = _settings.StorageLimitGb.ToString("0.#");
        RefreshStorageLimitStatusText();

        AutoDeleteOldClipsToggle.IsChecked = _settings.AutoDeleteOldClipsEnabled;
        AutoDeleteOldClipsFields.Visibility = _settings.AutoDeleteOldClipsEnabled ? Visibility.Visible : Visibility.Collapsed;
        AutoDeleteOldClipsDaysBox.Text = _settings.AutoDeleteOldClipsAfterDays.ToString();

        OverlayLogToggle.IsChecked = _settings.OverlayLogEnabled;
        OverlayLogModeFields.Visibility = _settings.OverlayLogEnabled ? Visibility.Visible : Visibility.Collapsed;
        // Unsubscribed/resubscribed around setting SelectedIndex -- unlike
        // ToggleButton.Click, ComboBox's SelectionChanged DOES fire from a
        // programmatic assignment, which would otherwise re-save+refresh the
        // overlay log just from opening Settings (same reasoning as DisplaySelector).
        OverlayLogModeSelector.SelectionChanged -= OverlayLogModeSelector_SelectionChanged;
        OverlayLogModeSelector.SelectedIndex = _settings.OverlayLogMode == "Backtrack" ? 1 : 0;
        OverlayLogModeSelector.SelectionChanged += OverlayLogModeSelector_SelectionChanged;

        // Deliberately NOT resetting the update status dots/text here. They
        // start grey from XAML (see BacktrackStatusDot etc.) before any check
        // has run, and the real checks (startup, hourly, manual button) keep
        // them accurate independent of whether Settings is even open --
        // forcing them back to grey every time this screen opens was undoing
        // an already-confirmed green/red result for no reason, making an
        // update that genuinely WAS just verified look unconfirmed again.
    }

    private sealed record DisplayOption(string DeviceName, string Name);

    private void LoadDisplaySelector()
    {
        List<DisplayInfo> displays = DisplayMonitors.GetAll();
        // Real monitor model name (e.g. "AG276QZD") when EDID lookup finds one --
        // falls back to a generic "Display N" for whatever monitor doesn't
        // expose it (some don't include a name descriptor in their EDID at all).
        var options = displays.Select((d, i) => new DisplayOption(
            d.DeviceName,
            $"{d.FriendlyName ?? $"Display {i + 1}"}{(d.IsPrimary ? " (Primary)" : "")} - {(int)d.BoundsDiu.Width}x{(int)d.BoundsDiu.Height}")).ToList();

        // Unsubscribed/resubscribed around populating -- ItemsSource/SelectedValue
        // assignment below would otherwise fire SelectionChanged and immediately
        // re-save+reposition everything just from opening Settings.
        DisplaySelector.SelectionChanged -= DisplaySelector_SelectionChanged;
        DisplaySelector.ItemsSource = options;
        DisplaySelector.SelectedValue = string.IsNullOrEmpty(_settings.DisplayDeviceName)
            ? options.FirstOrDefault(o => displays.First(d => d.DeviceName == o.DeviceName).IsPrimary)?.DeviceName
            : _settings.DisplayDeviceName;
        if (DisplaySelector.SelectedItem is null && options.Count > 0)
            DisplaySelector.SelectedIndex = 0;
        DisplaySelector.SelectionChanged += DisplaySelector_SelectionChanged;
    }

    private void DisplaySelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisplaySelector.SelectedValue is not string deviceName)
            return;

        string? previousDeviceName = _settings.DisplayDeviceName;
        _settings.DisplayDeviceName = deviceName;
        _settings.Save();

        // Re-anchors every already-open window to the new monitor immediately,
        // rather than waiting for whatever would naturally reposition it next
        // (MainWindow's own next screen change; some of the auxiliary overlays
        // otherwise only ever position once, in their constructor).
        ShowScreen(Screen.Settings);
        _statusOverlay.Reposition();
        _scrim.Reposition();
        _disclaimer.Reposition();
        _logo.Reposition();
        _toastOverlay.UpdatePosition(true);

        // RecentClipsOverlay was missing from this list entirely.
        if (_settings.ShowRecentClipsOverlay)
        {
            RefreshRecentClipsOverlay();
            RepositionRecentClipsOverlayForDisplayChange(previousDeviceName);
        }
    }

    /// <summary>
    /// Preserves RELATIVE position (e.g. "near the bottom-right corner")
    /// across a monitor switch, not the raw absolute pixel offset --
    /// ClampRecentClipsOverlayOnScreen's plain clamp collapsed a position
    /// near one monitor's right edge to the far LEFT edge of a second
    /// monitor sitting further right in virtual-desktop space, since the
    /// old absolute X was simply less than the new monitor's own minimum X;
    /// that's the right behavior for a same-monitor resolution SHRINK
    /// (still used there), but not for actually switching which monitor
    /// this is on. No-ops (falls back to PositionRecentClipsOverlay's own
    /// default corner) if the overlay's never been dragged, same as every
    /// other position-related path here.
    /// </summary>
    private void RepositionRecentClipsOverlayForDisplayChange(string? previousDeviceName)
    {
        if (_settings.RecentClipsOverlayX is not double x || _settings.RecentClipsOverlayY is not double y)
        {
            PositionRecentClipsOverlay();
            return;
        }

        Rect oldBounds = DisplayMonitors.ResolveBoundsDiu(previousDeviceName);
        Rect newBounds = TargetScreenBounds; // already reflects the NEW _settings.DisplayDeviceName, set just above

        double relativeX = oldBounds.Width > 0 ? (x - oldBounds.X) / oldBounds.Width : 0;
        double relativeY = oldBounds.Height > 0 ? (y - oldBounds.Y) / oldBounds.Height : 0;

        double width = _recentClipsOverlay.ActualWidth > 0 ? _recentClipsOverlay.ActualWidth : 260;
        double height = _recentClipsOverlay.ActualHeight > 0 ? _recentClipsOverlay.ActualHeight : 100;
        double newX = newBounds.X + relativeX * newBounds.Width;
        double newY = newBounds.Y + relativeY * newBounds.Height;
        // Still clamped afterward -- a relative position can round to just
        // past the edge once the overlay's own width/height is accounted
        // for, or the new monitor could be smaller than the old one.
        double clampedX = Math.Clamp(newX, newBounds.X, Math.Max(newBounds.X, newBounds.X + newBounds.Width - width));
        double clampedY = Math.Clamp(newY, newBounds.Y, Math.Max(newBounds.Y, newBounds.Y + newBounds.Height - height));

        _recentClipsOverlay.Left = clampedX;
        _recentClipsOverlay.Top = clampedY;
        _settings.RecentClipsOverlayX = clampedX;
        _settings.RecentClipsOverlayY = clampedY;
        _settings.Save();
    }

    /// <summary>
    /// SystemEvents.DisplaySettingsChanged's handler -- the automatic
    /// counterpart to DisplaySelector_SelectionChanged just above (which
    /// only fires from a manual "pick a different monitor" in Settings).
    /// A real Windows resolution change needs the same re-anchoring even
    /// though the user never touched that dropdown and the HUD might be
    /// hidden, on any screen, or not even summoned yet.
    /// </summary>
    private void RepositionAllForDisplayChange()
    {
        try
        {
            // ShowScreen recomputes MainWindow's own Left/Top/Width/Height
            // fresh from TargetScreenBounds every time it runs -- but
            // ToggleVisible's show path doesn't call it, so without this,
            // simply reopening the HUD after a resolution change would've
            // kept reusing Left/Top computed against the OLD screen size,
            // which after a shrink can land entirely off the new desktop.
            if (IsVisible)
                ShowScreen(_lastScreen, skipEntranceAnimation: true);

            _statusOverlay.Reposition();
            _scrim.Reposition();
            _disclaimer.Reposition();
            _logo.Reposition();
            _toastOverlay.UpdatePosition(true);
            UpdateStreamingBoxVisibility();
            ClampRecentClipsOverlayOnScreen();
        }
        catch (Exception ex)
        {
            // Best effort -- a resolution change is rare and this is purely
            // a recovery path; a failure here shouldn't be allowed to crash
            // an otherwise-still-working app.
            AppLog.WriteError("Reposition after display settings changed", ex);
        }
    }

    /// <summary>
    /// RecentClipsOverlay's saved X/Y is a user-DRAGGED absolute position,
    /// unlike every other overlay here -- PositionRecentClipsOverlay() would
    /// just reapply that same now-possibly-off-screen value unchanged, so a
    /// resolution shrink needs its own clamp back into the new visible
    /// bounds rather than a plain Reposition() call, which would discard
    /// the user's chosen spot entirely and snap it back to a corner.
    /// </summary>
    private void ClampRecentClipsOverlayOnScreen()
    {
        if (_settings.RecentClipsOverlayX is not double x || _settings.RecentClipsOverlayY is not double y)
            return;

        Rect bounds = TargetScreenBounds;
        double width = _recentClipsOverlay.ActualWidth > 0 ? _recentClipsOverlay.ActualWidth : 260;
        double height = _recentClipsOverlay.ActualHeight > 0 ? _recentClipsOverlay.ActualHeight : 100;
        double clampedX = Math.Clamp(x, bounds.X, Math.Max(bounds.X, bounds.X + bounds.Width - width));
        double clampedY = Math.Clamp(y, bounds.Y, Math.Max(bounds.Y, bounds.Y + bounds.Height - height));

        _recentClipsOverlay.Left = clampedX;
        _recentClipsOverlay.Top = clampedY;
        if (clampedX != x || clampedY != y)
        {
            _settings.RecentClipsOverlayX = clampedX;
            _settings.RecentClipsOverlayY = clampedY;
            _settings.Save();
        }
    }

    private void ObsRemoteToggle_Click(object sender, RoutedEventArgs e)
    {
        ObsRemoteFields.Visibility = ObsRemoteToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // -------------------------------------------------------------- pairing

    /// <summary>
    /// Also shows/hides AuthorizedDeviceRow -- pulled out of the status
    /// subtitle text (used to read "...authorized: {name}" crammed in there)
    /// into its own row with a Deauthorize button, since that text alone
    /// gave no way to actually revoke access short of turning sharing off
    /// entirely (which also stops announcing/accepting new pairing requests,
    /// not just this one device's).
    /// </summary>
    private void RefreshShareClipsUi()
    {
        bool hasAuthorizedDevice = !string.IsNullOrEmpty(_settings.AuthorizedClientName);

        if (!_settings.ShareClipsEnabled)
        {
            ShareClipsStatusText.Text = "Off";
        }
        else if (hasAuthorizedDevice)
        {
            ShareClipsStatusText.Text = $"Sharing as \"{Environment.MachineName}\"";
        }
        else
        {
            ShareClipsStatusText.Text = $"Sharing as \"{Environment.MachineName}\", waiting for a PC to pair";
        }

        // Only shown while sharing is actually on too -- a stale authorized
        // device from before "Share my clips" got turned off would otherwise
        // show a Deauthorize button for a connection that's already refused
        // regardless (StopPairingServer means nothing gets through even with
        // a valid secret), which reads as "this device still has access"
        // when it doesn't.
        AuthorizedDeviceRow.Visibility = _settings.ShareClipsEnabled && hasAuthorizedDevice ? Visibility.Visible : Visibility.Collapsed;
        AuthorizedDeviceNameText.Text = _settings.AuthorizedClientName ?? "";
    }

    private void DeauthorizeButton_Click(object sender, RoutedEventArgs e)
    {
        string? name = _settings.AuthorizedClientName;
        if (name is null)
            return;

        if (MessageBox.Show(this, $"Remove \"{name}\"'s access to this PC's clips? It'll need to pair again to reconnect.",
                "Backtrack", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        _settings.AuthorizedClientDeviceId = null;
        _settings.AuthorizedClientName = null;
        _settings.AuthorizedClientSecret = null;
        _settings.Save();
        RefreshShareClipsUi();
    }

    private void ShareClipsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = ShareClipsToggle.IsChecked == true;
        _settings.ShareClipsEnabled = enabled;
        _settings.Save();

        if (enabled)
        {
            _pairing.StartAnnouncing();
            _pairing.StartPairingServer();
        }
        else
        {
            _pairing.StopAnnouncing();
            _pairing.StopPairingServer();
        }

        RefreshShareClipsUi();
    }

    private void RefreshPairingStatusUi()
    {
        if (!string.IsNullOrEmpty(_settings.PairedPeerName))
        {
            PairingStatusText.Text = $"Paired with \"{_settings.PairedPeerName}\"";
            UnpairButton.Visibility = Visibility.Visible;
        }
        else
        {
            PairingStatusText.Text = "Not paired";
            UnpairButton.Visibility = Visibility.Collapsed;
        }
        RefreshGallerySourceTabsVisibility();
    }

    private void UnpairButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PairedPeerDeviceId = null;
        _settings.PairedPeerName = null;
        _settings.PairedPeerHost = null;
        _settings.PairedPeerPort = 0;
        _settings.PairedPeerSecret = null;
        _settings.Save();
        RefreshPairingStatusUi();
    }

    /// <summary>Rebuilds the discovered-devices list from scratch each time -- simplest way to stay in sync with a set that changes as peers appear/expire, matching the same pattern already used for the Gallery grid and buffer rows.</summary>
    private void RenderDiscoveredDevices()
    {
        DiscoveredDevicesPanel.Children.Clear();

        if (!string.IsNullOrEmpty(_settings.PairedPeerName))
            return; // Already paired -- no point offering to pair with something else too.

        var peers = _pairing.DiscoveredPeers;
        if (peers.Count == 0)
        {
            DiscoveredDevicesPanel.Children.Add(new TextBlock
            {
                Text = "No other Backtrack PCs found on this network yet. Make sure the other PC has \"Share my clips\" turned on.",
                FontSize = 10.5,
                Foreground = (Brush)FindResource("Text2"),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (DiscoveredPeer peer in peers)
            DiscoveredDevicesPanel.Children.Add(BuildDiscoveredDeviceRow(peer));
    }

    private Border BuildDiscoveredDeviceRow(DiscoveredPeer peer)
    {
        var name = new TextBlock { Text = peer.DeviceName, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = (Brush)FindResource("Text0"), VerticalAlignment = VerticalAlignment.Center };
        var statusText = new TextBlock { Text = "", FontSize = 10.5, Foreground = (Brush)FindResource("Text2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var pairButton = new Button { Content = "Pair", Style = (Style)FindResource("IconButton"), Margin = new Thickness(0) };

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(name);
        left.Children.Add(statusText);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(pairButton, 1);
        row.Children.Add(left);
        row.Children.Add(pairButton);

        pairButton.Click += async (_, _) =>
        {
            pairButton.IsEnabled = false;
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            try
            {
                PairingResult result = await _pairing.RequestPairingAsync(peer,
                    onCodeReceived: code => Dispatcher.BeginInvoke(() => statusText.Text = $"Code: {code}, waiting for approval..."),
                    cts.Token);

                switch (result.Outcome)
                {
                    case PairingOutcome.Approved:
                        statusText.Text = "Paired!";
                        RefreshPairingStatusUi();
                        RenderDiscoveredDevices();
                        return;
                    case PairingOutcome.Denied:
                        statusText.Text = string.IsNullOrEmpty(result.Error) ? "Request denied." : result.Error;
                        break;
                    case PairingOutcome.TimedOut:
                        statusText.Text = "Request timed out.";
                        break;
                    default:
                        statusText.Text = $"Failed: {result.Error}";
                        break;
                }
            }
            finally
            {
                pairButton.IsEnabled = true;
            }
        };

        return new Border { BorderBrush = (Brush)FindResource("Hairline"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 8), Child = row };
    }

    /// <summary>
    /// Same pairing flow as a discovered device, just against a manually-typed
    /// address instead of one found via LAN broadcast -- for Tailscale/VPN or any
    /// network where UDP broadcast doesn't reach. Builds a synthetic DiscoveredPeer
    /// so it can reuse PairingService.RequestPairingAsync unchanged; the handshake
    /// itself is plain TCP, so it doesn't care how the address was obtained.
    /// </summary>
    private async void ManualPairButton_Click(object sender, RoutedEventArgs e)
    {
        string input = ManualPairAddressBox.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            ManualPairStatusText.Text = "Enter an address first.";
            return;
        }

        string address = input;
        int port = PairingService.DefaultPairingPort;
        int colonIndex = input.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(input[(colonIndex + 1)..], out int parsedPort))
        {
            address = input[..colonIndex];
            port = parsedPort;
        }

        var peer = new DiscoveredPeer(DeviceId: "manual", DeviceName: address, Address: address, PairingPort: port, LastSeen: DateTime.UtcNow);

        ManualPairButton.IsEnabled = false;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        try
        {
            PairingResult result = await _pairing.RequestPairingAsync(peer,
                onCodeReceived: code => Dispatcher.BeginInvoke(() => ManualPairStatusText.Text = $"Code: {code}, waiting for approval..."),
                cts.Token);

            switch (result.Outcome)
            {
                case PairingOutcome.Approved:
                    ManualPairStatusText.Text = "Paired!";
                    RefreshPairingStatusUi();
                    RenderDiscoveredDevices();
                    return;
                case PairingOutcome.Denied:
                    ManualPairStatusText.Text = string.IsNullOrEmpty(result.Error) ? "Request denied." : result.Error;
                    break;
                case PairingOutcome.TimedOut:
                    ManualPairStatusText.Text = "Request timed out. Check the address and that the other PC has \"Share my clips\" on.";
                    break;
                default:
                    ManualPairStatusText.Text = $"Failed: {result.Error}";
                    break;
            }
        }
        finally
        {
            ManualPairButton.IsEnabled = true;
        }
    }

    private void ApplyObsConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool remote = ObsRemoteToggle.IsChecked == true;
            if (remote && string.IsNullOrWhiteSpace(ObsHostBox.Text))
            {
                MessageBox.Show(this, "Enter the stream PC's address first.", "Backtrack");
                return;
            }

            _settings.ObsIsRemote = remote;
            _settings.ObsHost = ObsHostBox.Text.Trim();
            _settings.ObsPort = int.TryParse(ObsPortBox.Text.Trim(), out int p) ? p : 4455;
            _settings.ObsRemotePassword = ObsPasswordBox.Password;
            _settings.Save();

            BuffersSection.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            RecordingsSection.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            if (!remote)
            {
                _ = LoadBufferVisibilityUi();
                _ = LoadRecordFolderUi();
            }

            (string url, string? password, _serverEnabledAtStartup) = ResolveObsConnection();
            _obs.Reconfigure(url, password);
            _ = RefreshStatusAsync();
            RefreshRamDiskRemoteGating();
            RefreshPluginStatusRemoteGating();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't apply that OBS connection: {ex.Message}", "Backtrack");
        }
    }

    /// <summary>
    /// RAM disk is the one setting that's genuinely local to whichever PC runs
    /// OBS. Hides the local section when OBS is remote and shows the transmitter-control
    /// panel instead.
    /// </summary>
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

    private async void ApplyRemoteRamDiskSettings_Click(object sender, RoutedEventArgs e)
    {
        string driveText = RemoteRamDiskDriveBox.Text.Trim().TrimEnd(':');
        if (driveText.Length != 1 || !char.IsLetter(driveText[0]))
        {
            MessageBox.Show(this, "Drive letter must be a single letter, e.g. R.", "Backtrack");
            return;
        }
        if (!int.TryParse(RemoteRamDiskSizeBox.Text.Trim(), out int sizeMb) || sizeMb < 256)
        {
            MessageBox.Show(this, "Size must be a number of megabytes, at least 256.", "Backtrack");
            return;
        }

        bool enabled = RemoteRamDiskToggle.IsChecked == true;
        (bool success, string? error) = await _pairing.SetRemoteRamDiskSettingsAsync(enabled, char.ToUpperInvariant(driveText[0]), sizeMb);
        if (!success)
        {
            MessageBox.Show(this, $"Couldn't apply on the transmitter PC: {error}", "Backtrack");
            return;
        }

        await LoadRemoteRamDiskUi();
    }

    /// <summary>See RefreshRamDiskRemoteGating's comment -- same reasoning, different setting.</summary>
    private void RefreshPluginStatusRemoteGating()
    {
        bool remote = _settings.ObsIsRemote;
        LocalPluginStatusRows.IsEnabled = !remote;
        PluginStatusRemoteNotice.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        RemotePluginSection.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;

        if (remote)
            RefreshRemotePluginStatusText();
    }

    private void RefreshRemotePluginStatusText()
    {
        RemotePluginStatusText.Text = string.IsNullOrEmpty(_settings.PairedPeerSecret)
            ? "Not paired with a transmitter PC yet -- pair with it first (below, in OBS section)."
            : $"Paired with {_settings.PairedPeerName}. Click \"Check & update\" to check its plugin versions.";
    }

    private async void CheckRemotePluginsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            RefreshRemotePluginStatusText();
            return;
        }

        CheckRemotePluginsButton.IsEnabled = false;
        RemotePluginStatusText.Text = $"Checking on {_settings.PairedPeerName}...";
        try
        {
            PluginVersionsSnapshot? snapshot = await _pairing.CheckRemotePluginUpdatesAsync();
            if (snapshot is null)
            {
                RemotePluginStatusText.Text = $"Couldn't reach {_settings.PairedPeerName}'s Backtrack -- make sure it's running.";
                RemotePluginRows.Visibility = Visibility.Collapsed;
                return;
            }

            RemotePluginStatusText.Text = $"Checked on {_settings.PairedPeerName}.";
            RemotePluginRows.Visibility = Visibility.Visible;
            SetUpdateStatus(RemoteReplaySliderStatusDot, RemoteReplaySliderVersionText, snapshot.ReplaySlider.InstalledVersion, snapshot.ReplaySlider.Ok);
            SetUpdateStatus(RemoteSourceRecordStatusDot, RemoteSourceRecordVersionText, snapshot.SourceRecord.InstalledVersion, snapshot.SourceRecord.Ok);
        }
        finally
        {
            CheckRemotePluginsButton.IsEnabled = true;
        }
    }

    private readonly Dictionary<string, Border> _themeSwatches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds Settings' theme-picker swatches from ThemeManager.DiscoverThemes
    /// -- every color comes from actually reading each theme's own real
    /// ResourceDictionary (PanelBg/Accent/Text0/Text2), not a hardcoded
    /// per-theme literal that can drift out of sync with it (see
    /// ThemeSwatchesPanel's own XAML comment for the bug that caused).
    /// Called from the constructor AND every time Settings actually opens
    /// (LoadSettingsUi) -- unlike the old fixed 5-theme version, the set of
    /// themes here can genuinely change at runtime (a user drops in, edits,
    /// or removes a Theme.*.xaml file while Backtrack is running), so this
    /// can't just build once and assume it's still accurate later.
    /// </summary>
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
    }

    // Highlight ring around whichever swatch matches the currently active
    // theme; Green isn't tied to "success" here, just the app's one
    // consistent selection accent, and it reads fine regardless of how many
    // swatches there are since it's a fixed brand color, not a themed neutral.
    private void RefreshThemeSwatchSelection()
    {
        var selected = new SolidColorBrush(Color.FromRgb(0x3E, 0xCF, 0x8E));
        foreach ((string themeId, Border swatch) in _themeSwatches.Select(kv => (kv.Key, kv.Value)))
            swatch.BorderBrush = string.Equals(ThemeManager.Current, themeId, StringComparison.OrdinalIgnoreCase) ? selected : Brushes.Transparent;
    }

    // ------------------------------------------------------ theme swatch drag-scroll

    // Null while not dragging; set on mouse-down to the press point and the
    // ScrollViewer's own HorizontalOffset at that moment, both needed to
    // compute how far to scroll on each subsequent MouseMove.
    private Point? _themeSwatchesDragStart;
    private double _themeSwatchesDragStartOffset;
    // Distinguishes an actual drag from a plain click that happened not to
    // move the mouse at all -- past this many pixels of movement, treat it
    // as a scroll gesture and swallow the eventual MouseLeftButtonUp (below)
    // so it can't also land on whichever swatch happens to be under the
    // cursor when the drag ends, misfiring that swatch's own theme-select
    // click. Below the threshold, the click passes through normally.
    private const double ThemeSwatchesDragThreshold = 4;
    private bool _themeSwatchesDragged;

    private void ThemeSwatchesScroll_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _themeSwatchesDragStart = e.GetPosition(ThemeSwatchesScroll);
        _themeSwatchesDragStartOffset = ThemeSwatchesScroll.HorizontalOffset;
        _themeSwatchesDragged = false;
        // Capture on the ScrollViewer itself, not a swatch -- this needs to
        // keep receiving MouseMove even once the cursor drags off whichever
        // swatch happened to be under the initial press.
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
        // A real drag happened -- this Preview (tunneling) handler firing
        // first and marking Handled stops the swatch's own bubbling
        // MouseLeftButtonUp click handler from also firing for the same
        // physical click, same as WPF's Preview/bubble pairing always
        // works. A plain click (never crossed the threshold) leaves this
        // unhandled, so the swatch underneath still gets its normal click.
        if (_themeSwatchesDragged)
            e.Handled = true;
    }

    private void ThemeSwatchesScroll_PreviewMouseLeave(object sender, MouseEventArgs e)
    {
        // Mouse leaving the ScrollViewer entirely while a button is still
        // down (dragged past its edge, or a capture loss from Alt-Tab etc.)
        // -- release cleanly rather than leaving a stale capture/drag state
        // that would otherwise only clear on the next unrelated click.
        if (_themeSwatchesDragStart is null)
            return;
        ThemeSwatchesScroll.ReleaseMouseCapture();
        _themeSwatchesDragStart = null;
    }

    // ------------------------------------------------------ settings autoscroll

    // Real middle-click "autoscroll", not a hold-and-drag -- a single
    // middle click sets a fixed reference point (does NOT track the press
    // point going forward) and enters continuous-scroll mode; the mouse
    // button does not need to stay held down. Scroll velocity each frame
    // is proportional to how far the CURRENT cursor position has drifted
    // from that fixed reference point -- a virtual joystick, center at the
    // click, above it scrolls up, below it scrolls down, farther away is
    // faster. A second middle click (anywhere, not just back at the
    // reference point) exits it. This matches how middle-click autoscroll
    // actually works in every browser, unlike the earlier hold-the-button
    // drag version this replaced.
    //
    // CompositionTarget.Rendering (WPF's equivalent of
    // requestAnimationFrame -- fires once per composed frame, not on a
    // fixed timer) drives the loop so it's tied to the current mouse
    // position at each frame, not to MouseMove events; the cursor can sit
    // perfectly still below the reference point and scrolling still
    // continues, exactly like real autoscroll.
    private bool _settingsAutoscrollActive;
    private double _settingsAutoscrollStartY;

    // Pixels/frame per pixel of distance from the reference point --
    // tuned by feel, not derived from anything. AutoscrollDeadZone stops a
    // few pixels of natural hand jitter right at the reference point from
    // reading as a real scroll intent.
    private const double AutoscrollSensitivity = 0.06;
    private const double AutoscrollDeadZone = 4;

    private void SettingsScrollHost_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _settingsAutoscrollActive)
            return;
        // Stops the middle click from also reaching whatever's underneath
        // (a settings row, a ComboBox, etc.) -- purely a scroll gesture.
        e.Handled = true;

        _settingsAutoscrollStartY = e.GetPosition(SettingsScrollHost).Y;
        _settingsAutoscrollActive = true;
        // Captured for the whole hold, not just at the click point -- the
        // cursor is expected to roam anywhere on screen (including off
        // SettingsScrollHost entirely) while the button's held down, and
        // capture is what keeps Mouse.GetPosition below reporting real
        // coordinates relative to it regardless of where the cursor
        // physically is, AND what guarantees the eventual mouse-up still
        // reaches SettingsScrollHost_PreviewMouseUp even if the cursor's
        // no longer over this element when the button comes back up.
        SettingsScrollHost.CaptureMouse();
        SettingsScrollHost.Cursor = Cursors.SizeAll;
        CompositionTarget.Rendering += SettingsAutoscroll_Tick;
    }

    /// <summary>Autoscroll runs only while the middle button is actually held down -- releasing it (anywhere; capture means this still fires even off SettingsScrollHost) stops it, same as the fixed-reference-point joystick behavior otherwise.</summary>
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
        // null, not a forced Cursors.Arrow -- restores whatever cursor
        // actually belongs under the mouse right now (a row's own Hand
        // cursor, a ComboBox's, etc.) instead of overriding it.
        SettingsScrollHost.Cursor = null;
    }

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

    private void ShowRecentClipsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = ShowRecentClipsToggle.IsChecked == true;
        _settings.ShowRecentClipsOverlay = enabled;

        // Clearing the saved spot here (not just hiding the window) is what
        // actually makes "turn it off, turn it back on" a real reset --
        // previously this left RecentClipsOverlayX/Y untouched, so re-
        // enabling just re-showed it at the exact same dragged-to spot,
        // which isn't what "reset" means to someone toggling it off and on
        // specifically to move it back. PositionRecentClipsOverlay falls
        // back to PositionInBottomRightCorner whenever these are null.
        if (!enabled)
        {
            _settings.RecentClipsOverlayX = null;
            _settings.RecentClipsOverlayY = null;
        }
        _settings.Save();

        // We're on the Settings screen right now, so this always ends up
        // hiding it (Idle-only -- see UpdateRecentClipsOverlayVisibility's
        // own comment) regardless of `enabled`; it'll reappear on its own
        // next time the HUD lands back on Idle. Still routed through the
        // shared helper rather than an unconditional Hide() so the logic
        // for "should it actually be visible" only lives in one place.
        UpdateRecentClipsOverlayVisibility(_lastScreen);
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

        // Turning Developer Mode ON also turns the diagnostic log file on --
        // the whole point of Developer Mode (full exception detail via
        // AppLog.WriteError) only actually goes anywhere useful once
        // something's persisting it; without this it's silently a no-op
        // until someone separately remembers to flip the other toggle too.
        // Deliberately one-directional: turning Developer Mode back OFF
        // doesn't turn the log back off, since someone may still want
        // whatever's already logging independent of dev mode.
        if (enabled && !_settings.DiagnosticLogEnabled)
            SetDiagnosticLogEnabled(true);

        // Developer Mode forces "Disable Backtrack auto-updates" ON and
        // locks it (checked, IsEnabled=false, can't be clicked) -- a dev
        // build auto-updating itself over the developer's own local build is
        // never what's wanted while actively developing against it. Unlike
        // the diagnostic log above, this one IS undone automatically when
        // Developer Mode goes back off, since there's no reason to keep
        // auto-updates disabled once dev mode itself is no longer the
        // reason they were.
        _settings.DisableBacktrackAutoUpdate = enabled;
        _settings.Save();
        DisableBacktrackAutoUpdateToggle.IsChecked = enabled;
        DisableBacktrackAutoUpdateToggle.IsEnabled = !enabled;
    }

    private void DisableHardwareAccelToggle_Click(object sender, RoutedEventArgs e)
    {
        // Not applied live -- see AppSettings.DisableHardwareAcceleration's
        // own comment on why this needs a fresh process (App.xaml.cs reads it
        // once, before any window is created).
        _settings.DisableHardwareAcceleration = DisableHardwareAccelToggle.IsChecked == true;
        _settings.Save();
        MessageBox.Show(this, "This takes effect the next time Backtrack starts.", "Backtrack");
    }

    /// <summary>Settings > Appearance > Theme > "Open themes folder" -- drop a Theme.*.xaml file in here and it shows up in the swatch picker next time Settings opens, no rebuild (see ThemeManager.DiscoverThemes).</summary>
    private void OpenThemesFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ThemeManager.ThemesFolder); // shouldn't be missing (theme files ship there), but a fresh/broken install is still openable rather than erroring
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ThemeManager.ThemesFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the themes folder: {ex.Message}", "Backtrack");
        }
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

    // Uses a Scheduled Task (not the Run registry key) so the app can launch
    // already elevated/consistent across Windows updates without a UAC prompt
    // every boot; Task Scheduler is also easier to inspect/remove by hand than
    // a Run key buried in the registry.
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

    // %SystemRoot%\System32\schtasks.exe explicitly, not just "schtasks.exe"
    // relying on PATH resolution -- a broken/nonstandard PATH (locked-down
    // corporate images, some antivirus PATH sanitizing, etc.) would make
    // Process.Start throw Win32Exception "The system cannot find the file
    // specified" for what's actually a PATH problem, not a real schtasks
    // failure, and that exception's own message doesn't make that obvious --
    // easy to misread as a permissions/elevation error instead.
    private static string SchtasksPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    private static void CreateOrUpdateStartupTask()
    {
        // /RL LIMITED explicit, not just omitted -- omitting it relies on
        // schtasks.exe's own default, which is LIMITED when run from a
        // non-elevated process, but documented inconsistently (and reported
        // inconsistently in practice) for what happens when the CALLING
        // process itself is already elevated (e.g. someone ran Backtrack "As
        // administrator" at least once). Spelling it out removes that
        // ambiguity entirely instead of depending on an implicit default that
        // may not behave the same on every machine.
        //
        // Backtrack never needs to run elevated (only the RAM disk driver
        // install does, and that's its own separate, explicit UAC prompt via
        // RamDisk.cs) -- LIMITED is correct regardless of what account or
        // elevation state created this task.
        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        var psi = new ProcessStartInfo(SchtasksPath,
            $"/Create /F /SC ONLOGON /RL LIMITED /TN \"{ScheduledTaskName}\" /TR \"\\\"{exePath}\\\"\"")
        { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        using Process proc = Process.Start(psi)!;
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "schtasks.exe failed to create the startup task."
                : $"schtasks.exe failed to create the startup task: {stderr.Trim()}");
    }

    private static void DeleteStartupTask()
    {
        // Mirrors CreateOrUpdateStartupTask's own error handling -- this used
        // to never check the exit code at all, so a real failure here (schtasks
        // itself missing/blocked, a permissions issue) was swallowed silently:
        // the toggle would save as "off" and look successful even though the
        // scheduled task was still sitting there, unremoved. "Task doesn't
        // exist" specifically (schtasks' own message for that, checked by
        // substring since its exact wording has varied across Windows
        // versions) is the one failure that's actually fine to ignore --
        // toggling off a task that was never successfully created in the
        // first place is a normal case, not an error worth surfacing.
        var psi = new ProcessStartInfo(SchtasksPath, $"/Delete /F /TN \"{ScheduledTaskName}\"")
        { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        using Process proc = Process.Start(psi)!;
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0 && !stderr.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "schtasks.exe failed to remove the startup task."
                : $"schtasks.exe failed to remove the startup task: {stderr.Trim()}");
    }

    private void ChangeClipsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog { InitialDirectory = _settings.ClipsFolder };
            if (dialog.ShowDialog(this) == true)
            {
                _settings.ClipsFolder = dialog.FolderName;
                _settings.Save();
                ClipsFolderText.Text = _settings.ClipsFolder;
                _ = RefreshGalleryCountAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't change the clips folder: {ex.Message}", "Backtrack");
        }
    }

    private void QuitApp_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    /// <summary>
    /// Two presses, not one: the first only checks and turns the button into
    /// "Update" if it found anything, so pressing "Check now" never silently
    /// installs something the user didn't ask for yet. The second press (now
    /// showing "Update") is what actually runs the real install pipeline --
    /// same CheckForUpdatesAsync the automatic hourly check uses, so it still
    /// respects the OBS-busy defer/prompt logic rather than forcing through.
    /// </summary>
    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            if (_manualUpdateReady)
            {
                _manualUpdateReady = false;
                CheckUpdatesButton.Content = "Applying...";
                await CheckForUpdatesAsync(isManualTrigger: true);
                CheckUpdatesButton.Content = "Check now";
                return;
            }

            CheckUpdatesButton.Content = "Checking...";

            (bool backtrackAvail, string backtrackVer) = UpdateService.IsDevBuild
                ? (false, $"{UpdateService.CurrentAppVersion.ToString(3)} (dev build)")
                : await CheckSelfAvailabilityAsync();
            (bool replayAvail, string replayVer) = await CheckPluginAvailabilityAsync("obs-replay-slider", "replay-slider.dll",
                name => name.Contains("windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                () => _settings.LastAppliedReplaySliderReleaseAt, v => _settings.LastAppliedReplaySliderReleaseAt = v,
                () => _settings.LastAppliedReplaySliderDigest, v => _settings.LastAppliedReplaySliderDigest = v);
            (bool sourceAvail, string sourceVer) = await CheckPluginAvailabilityAsync("obs-source-record", "source-record.dll",
                name => name.Contains("windows-installer", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                () => _settings.LastAppliedSourceRecordReleaseAt, v => _settings.LastAppliedSourceRecordReleaseAt = v,
                () => _settings.LastAppliedSourceRecordDigest, v => _settings.LastAppliedSourceRecordDigest = v);

            SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, backtrackAvail ? $"{backtrackVer} (update available)" : backtrackVer, ok: backtrackAvail ? null : true);
            SetUpdateStatus(ReplaySliderStatusDot, ReplaySliderVersionText, replayAvail ? $"{replayVer} (update available)" : replayVer, ok: replayAvail ? null : true);
            SetUpdateStatus(SourceRecordStatusDot, SourceRecordVersionText, sourceAvail ? $"{sourceVer} (update available)" : sourceVer, ok: sourceAvail ? null : true);

            _manualUpdateReady = backtrackAvail || replayAvail || sourceAvail;
            CheckUpdatesButton.Content = _manualUpdateReady ? "Apply" : "Check now";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------ hotkey capture

    private static string FormatHotkey(GlobalHotkey.Modifiers modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(GlobalHotkey.Modifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(GlobalHotkey.Modifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(GlobalHotkey.Modifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(GlobalHotkey.Modifiers.Win)) parts.Add("Win");
        parts.Add(((char)virtualKey).ToString());
        return string.Join("+", parts);
    }

    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey)
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
                _hotkey = new GlobalHotkey(this, modifiers, virtualKey);
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

    protected override void OnClosed(EventArgs e)
    {
        _trayManager.Dispose();
        StopPlayerPlayback();
        _libVlc?.Dispose();
        _hotkey?.Dispose();
        // Tied to this app's own lifetime, not left mounted independent of it --
        // see InitializeRamDiskAsync. No-op if it was never mounted.
        if (_settings.RamDiskEnabled)
            RamDisk.Unmount(_settings.RamDiskDriveLetter);
        base.OnClosed(e);
    }
}
