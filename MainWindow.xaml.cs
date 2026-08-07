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
using Backtrack.Updates;
using Microsoft.Win32;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
    private enum Screen { Idle, SaveReplay, Gallery, Player, Settings }

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

    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _micTimer;
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
    // recording/streaming/replaying (see ObsService.IsAnyOutputActiveAsync).
    // Only one tracked at a time -- good enough in practice, since hitting
    // this at all is already the rare case. RefreshUpdatePromptVisibility
    // shows/hides _updatePrompt to match both "is there one pending" and
    // "is the HUD actually open right now".
    private string? _pendingUpdateName;
    private Action? _pendingUpdateInstall;
    private readonly LogoOverlay _logo;
    private readonly PairingRequestOverlay _pairingRequestOverlay;
    private readonly AppSettings _settings;
    private readonly UpdateService _updates = new();

    // True once a manual "Check now" press has found something available and
    // the button has turned into "Update" -- the actual install only happens
    // on the SECOND press, once the user has explicitly seen there's
    // something to install rather than it just happening silently on the
    // first click. Doesn't affect the automatic hourly check, which still
    // applies updates on its own the moment it's safe to (see CheckForUpdatesAsync).
    private bool _manualUpdateReady;
    private readonly PairingService _pairing;
    private readonly Dictionary<string, string> _rowLabels = new();
    private List<ReplayRow> _lastReplayRows = new();
    private GlobalHotkey? _hotkey;
    private Screen _lastScreen = Screen.Idle;
    private readonly SystemTrayManager _trayManager;

    private bool _isRenamingCard;
    private bool _isPlayerRenaming;
    private bool _isTrimming;
    private readonly HashSet<string> _pendingDeletePaths = new(StringComparer.OrdinalIgnoreCase);

    // --------------------------------------------------------------- LibVLC / Player

    private LibVlc.LibVLC? _libVlc;
    private LibVlc.MediaPlayer? _vlcPlayer;
    private FileInfo? _currentPlayerFile;
    private readonly DispatcherTimer _seekTimer;
    private readonly DispatcherTimer _seekDebounceTimer;
    private bool _isScrubbing = false;
    private bool _isHoveringSeekTrack = false;
    private long _targetSeekMs = 0;
    private IntPtr _thumbnailSinkHwnd;

    // Hotkey capture (Settings)
    private bool _capturingHotkey;

    // Trim
    private TimeSpan? _trimStart;
    private TimeSpan? _trimEnd;

    // --------------------------------------------------------------- Gallery folders / selection

    // null means "at the clips-folder root" -- kept nullable instead of always holding
    // a path so GalleryTile_Click can reset browsing back to the top with one write,
    // and so GalleryUp_Click has an unambiguous "there's no further up" state.
    private string? _currentGalleryFolder;
    private string GalleryFolder => _currentGalleryFolder ?? _settings.ClipsFolder;

    private readonly HashSet<string> _selectedClipPaths = new(StringComparer.OrdinalIgnoreCase);
    // Rebuilt every LoadGallery() call -- lets mass actions and the selection-circle
    // visuals look up a card's controls by file without threading extra state through
    // BuildClipCard's return value (still just a Border, used everywhere else as one).
    private readonly List<(FileInfo File, Border Circle, Border Thumb)> _galleryCardSelection = new();

    public MainWindow(StatusOverlay statusOverlay, ToastOverlay toastOverlay, ScrimOverlay scrim, DisclaimerOverlay disclaimer, LogoOverlay logo, PairingRequestOverlay pairingRequestOverlay)
    {
        InitializeComponent();
        _statusOverlay = statusOverlay;
        _pairingRequestOverlay = pairingRequestOverlay;
        _toastOverlay = toastOverlay;
        _scrim = scrim;
        _disclaimer = disclaimer;
        _logo = logo;
        _settings = AppSettings.Load();

        _pairing = new PairingService(_settings);
        _pairing.PairingRequested += (deviceName, code, requestId) => Dispatcher.BeginInvoke(() =>
        {
            _pairingRequestOverlay.ShowRequest(deviceName, code,
                onAllow: () => _pairing.ApproveRequest(requestId),
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

        _scrim.Dismissed += () => Dispatcher.BeginInvoke(CloseOverlay);
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
            }
        });
        _obs.ReplaySaved += (key, path) => Dispatcher.BeginInvoke(async () =>
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

            _toastOverlay.ShowReplaySaved(label, path);
            AppLog.Write($"{label} saved to '{path}'");
            ShowObsModeMessage($"Replay saved to '{path}'");
            _ = RefreshGalleryCountAsync();
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

        // Same reasoning as RAM disk above -- plugin version checks/updates read
        // and write C:\Program Files\obs-studio on THIS machine (see
        // UpdateService), so a paired receiver PC needs this run here, not on
        // itself. Dispatcher.InvokeAsync because incoming pairing requests are
        // handled on PairingService's own network thread, not the UI thread, and
        // CheckAndApplyPluginUpdateAsync touches UI elements (dot/version text)
        // directly.
        _pairing.CheckAndApplyPluginUpdatesRemotely = async () =>
        {
            PluginVersionInfo replaySlider = await await Dispatcher.InvokeAsync(() =>
                CheckAndApplyPluginUpdateAsync("obs-replay-slider", "Replay Slider", "replay-slider.dll", ReplaySliderStatusDot, ReplaySliderVersionText,
                    name => name.Contains("windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                    () => _settings.LastAppliedReplaySliderReleaseAt, v => _settings.LastAppliedReplaySliderReleaseAt = v,
                    () => _settings.LastAppliedReplaySliderDigest, v => _settings.LastAppliedReplaySliderDigest = v));
            PluginVersionInfo sourceRecord = await await Dispatcher.InvokeAsync(() =>
                CheckAndApplyPluginUpdateAsync("obs-source-record", "Source Record", "source-record.dll", SourceRecordStatusDot, SourceRecordVersionText,
                    name => name.Contains("windows-installer", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                    () => _settings.LastAppliedSourceRecordReleaseAt, v => _settings.LastAppliedSourceRecordReleaseAt = v,
                    () => _settings.LastAppliedSourceRecordDigest, v => _settings.LastAppliedSourceRecordDigest = v));
            return new PluginVersionsSnapshot(replaySlider, sourceRecord);
        };

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await RefreshStatusAsync();

        _micTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _micTimer.Tick += (_, _) => _statusOverlay.SetMicStatus(_obs.GetMicStatus());

        _seekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _seekTimer.Tick += (_, _) => UpdatePlayerSeekUi();

        _seekDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _seekDebounceTimer.Tick += (_, _) =>
        {
            _seekDebounceTimer.Stop();
            if (_vlcPlayer != null && _vlcPlayer.IsSeekable && _targetSeekMs >= 0)
            {
                _vlcPlayer.Time = _targetSeekMs;
            }
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

        try
        {
            LibVlc.Core.Initialize();
            // --avcodec-hw=none: disables hardware-accelerated decoding. Without this,
            // the video surface can come up as a blank white swapchain with nothing
            // ever drawn into it on machines/VMs where GPU decode acceleration isn't
            // reliably available -- software decode is slower but actually paints frames.
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

        InitializeOverlayLog();
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
    /// </summary>
    private async Task RefreshObsModeLogAsync()
    {
        if (!_settings.OverlayLogEnabled || !IsVisible || _settings.OverlayLogMode == "Backtrack")
            return;

        if (!_obs.IsConnected)
        {
            _overlayLog.SetObsLine("");
            return;
        }

        try
        {
            ObsStats stats = await _obs.GetStatsAsync();
            string? warning = ComputeObsOverloadWarning(stats);
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
            return (ShouldApplyUpdate(release, versionBumped, getLastApplied, setLastApplied, getLastDigest, setLastDigest), installed.ToString(3));
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
            bool available = ShouldApplyUpdate(release, versionBumped,
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
    private async Task CheckForUpdatesAsync()
    {
        await CheckAndApplyPluginUpdateAsync("obs-replay-slider", "Replay Slider", "replay-slider.dll", ReplaySliderStatusDot, ReplaySliderVersionText,
            name => name.Contains("windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
            () => _settings.LastAppliedReplaySliderReleaseAt, v => _settings.LastAppliedReplaySliderReleaseAt = v,
            () => _settings.LastAppliedReplaySliderDigest, v => _settings.LastAppliedReplaySliderDigest = v);
        await CheckAndApplyPluginUpdateAsync("obs-source-record", "Source Record", "source-record.dll", SourceRecordStatusDot, SourceRecordVersionText,
            name => name.Contains("windows-installer", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
            () => _settings.LastAppliedSourceRecordReleaseAt, v => _settings.LastAppliedSourceRecordReleaseAt = v,
            () => _settings.LastAppliedSourceRecordDigest, v => _settings.LastAppliedSourceRecordDigest = v);

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
            await CheckAndApplySelfUpdateAsync();
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
    private bool ShouldApplyUpdate(ReleaseInfo release, bool versionBumped,
        Func<DateTimeOffset?> getLastApplied, Action<DateTimeOffset?> setLastApplied,
        Func<string?> getLastDigest, Action<string?> setLastDigest)
    {
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

    private async Task<PluginVersionInfo> CheckAndApplyPluginUpdateAsync(string repo, string displayName, string dllFileName, System.Windows.Shapes.Ellipse dot, TextBlock versionText, Func<string, bool> assetPredicate,
        Func<DateTimeOffset?> getLastApplied, Action<DateTimeOffset?> setLastApplied, Func<string?> getLastDigest, Action<string?> setLastDigest)
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
            if (!ShouldApplyUpdate(release, versionBumped, getLastApplied, setLastApplied, getLastDigest, setLastDigest))
            {
                SetUpdateStatus(dot, versionText, installed.ToString(3), ok: true);
                ClearPendingUpdateIfMatches(displayName);
                return new PluginVersionInfo(installed.ToString(3), true);
            }

            async Task ApplyAsync()
            {
                _toastOverlay.ShowUpdateInProgress(displayName);
                await _updates.InstallPluginUpdateAsync(release.DownloadUrl);
                RecordUpdateApplied(release, setLastApplied, setLastDigest);
                AppLog.Write($"{displayName} updated to {release.Version}");
                _toastOverlay.ShowUpdateApplied(displayName, release.Version);
                SetUpdateStatus(dot, versionText, release.Version, ok: true);
                ClearPendingUpdateIfMatches(displayName);
            }

            // Installing a plugin update means closing OBS out from under
            // whatever it's doing (InstallPluginUpdateAsync's CloseObsIfRunningAsync)
            // -- never worth it mid-recording/stream/replay without asking
            // first. Deferred to the bottom-left prompt instead, so the user
            // can force it through right now if they'd rather do that.
            if (await _obs.IsAnyOutputActiveAsync())
            {
                SetUpdateStatus(dot, versionText, $"{installed.ToString(3)} (update waiting for OBS to be idle)", ok: null);
                SetPendingUpdate(displayName, () => _ = ApplyAsync());
                return new PluginVersionInfo(installed.ToString(3), null);
            }

            await ApplyAsync();
            return new PluginVersionInfo(release.Version, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check/apply failed for {repo}: {ex.Message}");
            SetUpdateStatus(dot, versionText, installed.ToString(3), ok: false);
            return new PluginVersionInfo(installed.ToString(3), false);
        }
    }

    private async Task CheckAndApplySelfUpdateAsync()
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
            if (!ShouldApplyUpdate(release, versionBumped,
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
                await _updates.ApplySelfUpdateAsync(release.DownloadUrl, release.Version);
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
            if (await _obs.IsAnyOutputActiveAsync())
            {
                SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, $"{installed.ToString(3)} (update waiting for OBS to be idle)", ok: null);
                SetPendingUpdate("Backtrack", () => _ = ApplyAsync());
                return;
            }

            await ApplyAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Self-update check/apply failed: {ex.Message}");
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
                    "RAM disk turned off. The plugin's clip destination has been pointed back at your Clips folder automatically.\n\n" +
                    $"One more manual step, same as when you turned it on: in OBS, go to Settings > Output > Replay Buffer and change its output path back from {oldDrive}:\\ to a real folder (e.g. your Clips folder) -- " +
                    "OBS doesn't expose a way for Backtrack to do this part for you automatically, so replay saves will fail until you do.",
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

    private void CloseOverlay()
    {
        if (!IsCriticalOperationActive())
        {
            _lastScreen = Screen.Idle;
            ShowScreen(Screen.Idle);
        }
        else
        {
            ShowScreen(_lastScreen);
        }

        Hide();
        _scrim.Hide();
        _disclaimer.Hide();
        _logo.Hide();
        _toastOverlay.UpdatePosition(false);
        _updatePrompt.HidePrompt();
        RefreshOverlayLogVisibilityAndMode();
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !IsVisible)
            return;

        e.Handled = true;
        if (_activeConfirmDialog != null && _activeConfirmDialog.IsLoaded)
        {
            _activeConfirmDialog.Close();
            _activeConfirmDialog = null;
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
    }

    public void ToggleVisible()
    {
        if (IsVisible)
        {
            CloseOverlay();
        }
        else
        {
            _scrim.Show();
            _logo.ShowWithIntro();
            Show();
            Activate();
            _statusOverlay.Show();
            WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(_statusOverlay).Handle);
            _toastOverlay.Show();
            _toastOverlay.UpdatePosition(true);
            WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(_toastOverlay).Handle);
            RefreshUpdatePromptVisibility();
            RefreshOverlayLogVisibilityAndMode();

            if (_settings.ShowDisclaimer)
                _disclaimer.Show();
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
        Screen.Gallery => GalleryPanel,
        Screen.Player => PlayerPanel,
        Screen.Settings => SettingsPanel,
        _ => IdlePanel,
    };

    /// <summary>Fade + slight slide-up on whichever panel just became active, purely cosmetic (BeginAnimation, not a blocking wait) so it doesn't change ShowScreen's own synchronous behavior -- every caller that populates content right after (LoadGallery, etc.) still runs immediately, unaffected.</summary>
    private static void AnimatePanelIn(FrameworkElement panel)
    {
        var slide = new TranslateTransform(0, 10);
        panel.RenderTransform = slide;
        panel.Opacity = 0;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
    }

    private void ShowScreen(Screen screen)
    {
        FrameworkElement newPanel = PanelFor(screen);
        bool switchingPanel = newPanel.Visibility != Visibility.Visible;

        IdlePanel.Visibility = screen == Screen.Idle ? Visibility.Visible : Visibility.Collapsed;
        SaveReplayPanel.Visibility = screen == Screen.SaveReplay ? Visibility.Visible : Visibility.Collapsed;
        GalleryPanel.Visibility = screen == Screen.Gallery ? Visibility.Visible : Visibility.Collapsed;
        PlayerPanel.Visibility = screen == Screen.Player ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = screen == Screen.Settings ? Visibility.Visible : Visibility.Collapsed;

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

        if (switchingPanel)
            AnimatePanelIn(newPanel);

        // The gear only makes sense on the idle screen -- it isn't a fourth tile,
        // so it shouldn't linger once you've navigated away from the row it sits above.
        TopRightButtons.Visibility = screen == Screen.Idle ? Visibility.Visible : Visibility.Collapsed;

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

        PlayerOverlayPopup.IsOpen = screen == Screen.Player;

        if (screen != Screen.Player)
            StopPlayerPlayback();

        if (screen is Screen.Idle or Screen.SaveReplay or Screen.Gallery or Screen.Settings)
            _lastScreen = screen;

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
    /// </summary>
    private double BigWidth() => Math.Min(TargetScreenBounds.Width * 0.78, 1500);

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
        ShowScreen(Screen.Gallery);
        LoadGallery();
    }

    private void ToggleStatusOverlay()
    {
        if (_statusOverlay.IsVisible)
        {
            _statusOverlay.Hide();
        }
        else
        {
            _statusOverlay.Show();
            WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(_statusOverlay).Handle);
        }
    }

    /// <summary>Circle = idle (matches the universal "record" glyph); red square = recording (matches "stop").</summary>
    private void SetRecordIcon(bool active)
    {
        RecordDot.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        RecordSquare.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshStatusAsync()
    {
        _trayManager?.UpdateStatus(_obs.IsConnected, _statusOverlay.IsVisible);

        if (!_obs.IsConnected)
        {
            ConnDot.Fill = (Brush)FindResource("Rec");
            ConnStatusText.Text = "OBS Disconnected";
            ConnStatusText.ToolTip = !_serverEnabledAtStartup
                ? "OBS's WebSocket server is disabled -- enable it in OBS: Tools > WebSocket Server Settings"
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
            return;
        }

        ConnDot.Fill = (Brush)FindResource("Green");
        ConnStatusText.Text = "OBS Connected";
        ConnStatusText.ToolTip = "Connected to OBS";

        try
        {
            RecordStatus recStatus = await _obs.GetRecordStatusAsync();
            // Label stays "Stop Recording" even while paused -- clicking it calls
            // ToggleRecordAsync, which only ever sends Start/StopRecord (there's no
            // pause/resume button here), so "Resume Recording" would be a lie about
            // what a click actually does.
            RecordLabel.Text = recStatus.Active ? "Stop Recording" : "Start Recording";
            SetRecordIcon(recStatus.Active);
            RecordStatusText.Text = !recStatus.Active ? "--:--"
                : recStatus.Paused ? $"{FormatDuration(recStatus.DurationMs)} (Paused)"
                : FormatDuration(recStatus.DurationMs);
            _statusOverlay.SetRecording(recStatus.Active);

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
            bool replayBufferActive = await _obs.GetReplayBufferActiveAsync();
            bool anyRowActive;
            bool anyRowError;
            try
            {
                List<ReplayRow> rows = await _obs.ListReplayRowsAsync();
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
        try
        {
            if (!_obs.IsConnected)
                return;
            await _obs.ToggleRecordAsync();
            await RefreshStatusAsync();
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

    private void GalleryTile_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(Screen.Gallery);
        _currentGalleryFolder = null;
        LoadGallery();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(Screen.Settings);
        LoadSettingsUi();
        _ = LoadBufferVisibilityUi();
        RefreshRamDiskRemoteGating();
        RefreshPluginStatusRemoteGating();
    }

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
    {
        // Placeholder toggle -- the window is already at its widest layout size
        // for Gallery/Player; true OS fullscreen isn't meaningful for a HUD overlay.
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
        var name = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Text0"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(toggle, 1);
        topGrid.Children.Add(name);
        topGrid.Children.Add(toggle);

        var folderLabel = new TextBlock
        {
            Text = DescribeRowDestDir(row.DestDir),
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var folderButton = new Button { Content = "Folder", Style = (Style)FindResource("FlatButton"), VerticalAlignment = VerticalAlignment.Center };
        folderButton.Click += async (_, _) => await PickBufferDestFolderAsync(row.Key, folderLabel);

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

        var name = new TextBlock { Text = row.Label, FontWeight = FontWeights.Bold, FontSize = 12.5, Foreground = (Brush)FindResource("Text0") };
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
        button.Click += async (_, _) =>
        {
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

    /// <summary>
    /// One slider for every buffer -- simpler than juggling a separate length
    /// per row, at the cost of them no longer being independently adjustable.
    /// Applies the same length to every row the plugin currently reports.
    /// </summary>
    private const int MinClipSeconds = 15;
    private const int MaxClipSeconds = 3600;

    /// <summary>Squares the 0-1 fraction so low values (what almost everyone actually wants) get most of the track, not a couple of pixels at the start.</summary>
    private static int SliderPosToSeconds(double pos)
    {
        double t = pos / 1000.0;
        return (int)Math.Round(MinClipSeconds + (MaxClipSeconds - MinClipSeconds) * t * t);
    }

    private static double SecondsToSliderPos(int seconds)
    {
        double t = Math.Sqrt(Math.Clamp((seconds - MinClipSeconds) / (double)(MaxClipSeconds - MinClipSeconds), 0, 1));
        return t * 1000.0;
    }

    private Border BuildSharedClipLengthControl(List<ReplayRow> rows)
    {
        int initial = rows.Count > 0 ? rows[0].LengthSeconds : 60;

        var label = new TextBlock { Text = "Clip length", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Text1"), VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Style = (Style)FindResource("RowLengthSlider"), Value = SecondsToSliderPos(initial), Margin = new Thickness(10, 0, 10, 0) };
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
        slider.ValueChanged += (_, e) => lengthText.Text = FormatDuration(SliderPosToSeconds(e.NewValue) * 1000L);
        slider.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            int seconds = SliderPosToSeconds(slider.Value);
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

    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".flv", ".mov" };

    private async Task RefreshGalleryCountAsync()
    {
        int count = await Task.Run(CountClips);
        GalleryStatus.Text = count == 1 ? "1 clip" : $"{count} clips";
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

    private void LoadGallery()
    {
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

        List<DirectoryInfo> subfolders;
        List<FileInfo> files;
        try
        {
            subfolders = Directory.GetDirectories(folder)
                .Select(d => new DirectoryInfo(d))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            files = Directory.EnumerateFiles(folder)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !_pendingDeletePaths.Contains(Path.GetFullPath(f)))
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
                Text = "No clips in this folder yet.",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
            });
            GalleryStatus.Text = "0 clips";
            return;
        }

        foreach (DirectoryInfo dir in subfolders)
            GalleryGrid.Children.Add(BuildFolderCard(dir));

        foreach (FileInfo file in files)
            GalleryGrid.Children.Add(BuildClipCard(file));

        GalleryStatus.Text = files.Count == 1 ? "1 clip" : $"{files.Count} clips";
    }

    /// <summary>
    /// The folder browsing here is scoped to the clips folder tree -- both so "Up"
    /// has an unambiguous stopping point and so mass-move destinations picked via
    /// the OS folder dialog land somewhere this same view can browse back to.
    /// </summary>
    private void UpdateGalleryPathBar()
    {
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

    private void GalleryUp_Click(object sender, MouseButtonEventArgs e)
    {
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
                foreach (FileInfo file in targets)
                {
                    QueueDeleteWithUndo(file);
                }
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

    private Border BuildFolderCard(DirectoryInfo dir)
    {
        var iconHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 30)),
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
            Text = dir.Name,
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 7, 0, 1),
        };

        var sub = new TextBlock { Text = "Folder", FontSize = 11, Foreground = (Brush)FindResource("Text2") };

        var content = new StackPanel();
        content.Children.Add(iconHost);
        content.Children.Add(title);
        content.Children.Add(sub);

        var card = new Border { Width = 210, Child = content, Cursor = Cursors.Hand };
        card.MouseLeftButtonUp += (_, _) => OpenGalleryFolder(dir.FullName);

        return card;
    }

    private Border BuildClipCard(FileInfo file)
    {
        // Neutral placeholder shown until the real frame loads in behind it
        // (LoadThumbnailAsync, kicked off below) -- not a fake thumbnail like the
        // old per-file color, just what's visible during the brief async load.
        var thumb = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 30)),
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

        var content = new StackPanel();
        content.Children.Add(thumb);
        content.Children.Add(title);
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
        openFolderItem.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullName}\"") { UseShellExecute = true });
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem") };
        deleteItem.Click += (_, _) => DeleteClip(file, card);
        contextMenu.Items.Add(openFolderItem);
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
                });
            },
            onUndo: () =>
            {
                _pendingDeletePaths.Remove(fullPath);
                Dispatcher.BeginInvoke(() => LoadGallery());
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

    private void OpenInPlayer(FileInfo file)
    {
        if (_libVlc is null)
        {
            MessageBox.Show(this, "The video player failed to initialize (LibVLC).", "Backtrack");
            return;
        }

        _currentPlayerFile = file;
        _trimStart = null;
        _trimEnd = null;
        TrimPanel.Visibility = Visibility.Collapsed;

        ShowScreen(Screen.Player);
        PlayerTitle.Text = Path.GetFileNameWithoutExtension(file.Name);

        StatSize.Text = $"{file.Length / 1024.0 / 1024.0:0.#} MB";
        StatDate.Text = $"{file.LastWriteTime:MMM d, yyyy h:mm tt}";
        StatResolution.Text = "";
        StatFps.Text = "";

        StopPlayerPlayback();

        _vlcPlayer = new LibVlc.MediaPlayer(_libVlc);
        PlayerVideoView.MediaPlayer = _vlcPlayer;

        using var media = new LibVlc.Media(_libVlc, new Uri(ResolveLocalClipPath(file)));
        _vlcPlayer.Play(media);

        bool tracksLoaded = false;
        _vlcPlayer.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            PlayIcon.Visibility = Visibility.Collapsed;
            PauseIcon.Visibility = Visibility.Visible;
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

        AudioTrackCombo.Visibility = Visibility.Visible;
        AudioTrackCombo.ItemsSource = tracks.Select((t, i) => new AudioTrackOption(t.Id, string.IsNullOrEmpty(t.Description) ? $"Track {i + 1}" : t.Description)).ToList();
        AudioTrackCombo.SelectedIndex = 0;
    }

    private sealed record AudioTrackOption(int Id, string Name);

    private void AudioTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vlcPlayer is not null && AudioTrackCombo.SelectedItem is AudioTrackOption opt)
            _vlcPlayer.SetAudioTrack(opt.Id);
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vlcPlayer is null)
            return;
        if (_vlcPlayer.IsPlaying)
            _vlcPlayer.Pause();
        else
            _vlcPlayer.Play();
    }

    private void UpdatePlayerSeekUi()
    {
        if (_vlcPlayer is null || _isScrubbing)
            return;

        long lengthMs = _vlcPlayer.Length;
        long timeMs = _vlcPlayer.Time;

        if (lengthMs <= 0)
            return;

        PlayerCurrentTime.Text = FormatDuration(Math.Max(timeMs, 0));
        PlayerDurationText.Text = FormatDuration(Math.Max(lengthMs, 0));

        double ratio = Math.Clamp((double)timeMs / lengthMs, 0.0, 1.0);
        double trackWidth = PlayerSeekTrack.ActualWidth;

        if (trackWidth > 0)
        {
            PlayerSeekFill.Width = ratio * trackWidth;
            PlayerSeekThumb.Margin = new Thickness(ratio * trackWidth - 7, 0, 0, 0);
        }
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
                _vlcPlayer.Time = _targetSeekMs;
            }
        }
        else
        {
            _seekDebounceTimer.Stop();
            _seekDebounceTimer.Start();
        }
    }

    private void StopPlayerPlayback()
    {
        _seekTimer.Stop();
        if (_vlcPlayer is not null)
        {
            _vlcPlayer.Stop();
            _vlcPlayer.Dispose();
            _vlcPlayer = null;
        }
        PlayerVideoView.MediaPlayer = null;
    }

    private void PlayerFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlayerFile is null)
            return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentPlayerFile.FullName}\"") { UseShellExecute = true });
    }

    /// <summary>
    /// Swaps PlayerTitle for an inline TextBox in place, same pattern as the
    /// Gallery cards' rename. Same double-invocation footgun applies here too:
    /// removing the focused TextBox to restore the label fires its own
    /// LostFocus, which would re-run a guarded commit a second time against a
    /// stale FileInfo -- the `finished` flag is guarded at both call sites,
    /// not inside CommitRename itself, so the legitimate first call still runs.
    /// </summary>
    private void PlayerRename_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlayerFile is null)
            return;
        _isPlayerRenaming = true;
        FileInfo file = _currentPlayerFile;
        bool finished = false;

        var stack = (StackPanel)PlayerTitle.Parent;
        int index = stack.Children.IndexOf(PlayerTitle);

        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
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
            stack.Children.Remove(box);
            stack.Children.Insert(index, PlayerTitle);
        }

        void CommitRename()
        {
            _isPlayerRenaming = false;
            string newName = box.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != Path.GetFileNameWithoutExtension(file.Name))
            {
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
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Couldn't rename: {ex.Message}", "Backtrack");
                }
            }
            RevertBox();
        }
    }

    private void PlayerDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlayerFile is null)
            return;

        FileInfo file = _currentPlayerFile;

        ShowConfirmDialog(
            $"Are you sure you want to delete \"{file.Name}\"? This will send it to your recycle bin.",
            "Delete",
            confirmed =>
            {
                if (confirmed)
                {
                    _currentPlayerFile = null;
                    StopPlayerPlayback();
                    ShowScreen(Screen.Gallery);
                    QueueDeleteWithUndo(file);
                }
            });
    }

    // ------------------------------------------------------------------ trim

    private void PlayerTrim_Click(object sender, RoutedEventArgs e)
    {
        TrimPanel.Visibility = TrimPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TrimSetStart_Click(object sender, RoutedEventArgs e)
    {
        if (_vlcPlayer is null)
            return;
        _trimStart = TimeSpan.FromMilliseconds(_vlcPlayer.Time);
        TrimStartText.Text = FormatDuration((long)_trimStart.Value.TotalMilliseconds);
    }

    private void TrimSetEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_vlcPlayer is null)
            return;
        _trimEnd = TimeSpan.FromMilliseconds(_vlcPlayer.Time);
        TrimEndText.Text = FormatDuration((long)_trimEnd.Value.TotalMilliseconds);
    }

    private void TrimCancel_Click(object sender, RoutedEventArgs e)
    {
        _trimStart = null;
        _trimEnd = null;
        TrimStartText.Text = "0:00";
        TrimEndText.Text = "0:00";
        TrimStatusText.Text = "";
        TrimPanel.Visibility = Visibility.Collapsed;
    }

    private async void TrimReplace_Click(object sender, RoutedEventArgs e) => await RunTrimAsync(replaceOriginal: true);

    private async void TrimSaveNew_Click(object sender, RoutedEventArgs e) => await RunTrimAsync(replaceOriginal: false);

    /// <summary>
    /// Exports via LibVLC's own transcode/sout chain (no ffmpeg dependency) using a
    /// second, headless MediaPlayer so the visible preview player keeps playing
    /// undisturbed. Runs roughly real-time, not instantly.
    /// </summary>
    private async Task RunTrimAsync(bool replaceOriginal)
    {
        if (_libVlc is null || _currentPlayerFile is null || _trimStart is null || _trimEnd is null || _trimEnd <= _trimStart)
        {
            MessageBox.Show(this, "Set both a start and end point first (end must be after start).", "Backtrack");
            return;
        }

        FileInfo sourceFile = _currentPlayerFile;
        TimeSpan start = _trimStart.Value;
        TimeSpan end = _trimEnd.Value;

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
            }

            TrimStatusText.Text = "Done.";
            TrimPanel.Visibility = Visibility.Collapsed;
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
        LaunchWithWindowsToggle.IsChecked = _settings.LaunchWithWindows;
        ClipsFolderText.Text = _settings.ClipsFolder;
        BufferDurationSlider.Value = _settings.ReplayBufferMinutes;
        RefreshBufferDurationUi();

        ObsRemoteToggle.IsChecked = _settings.ObsIsRemote;
        ObsRemoteFields.Visibility = _settings.ObsIsRemote ? Visibility.Visible : Visibility.Collapsed;
        ObsHostBox.Text = _settings.ObsHost;
        ObsPortBox.Text = _settings.ObsPort.ToString();
        ObsPasswordBox.Password = _settings.ObsRemotePassword;

        ShowDisclaimerToggle.IsChecked = _settings.ShowDisclaimer;
        HotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.HotkeyModifiers, (uint)_settings.HotkeyVirtualKey);

        LoadDisplaySelector();

        ShareClipsToggle.IsChecked = _settings.ShareClipsEnabled;
        RefreshShareClipsStatusText();
        RefreshPairingStatusUi();
        RenderDiscoveredDevices();

        RamDiskToggle.IsChecked = _settings.RamDiskEnabled;
        RamDiskFields.Visibility = _settings.RamDiskEnabled ? Visibility.Visible : Visibility.Collapsed;
        RamDiskDriveBox.Text = _settings.RamDiskDriveLetter.ToString();
        RamDiskSizeBox.Text = _settings.RamDiskSizeMb.ToString();
        RefreshRamDiskStatusText();

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
            $"{d.FriendlyName ?? $"Display {i + 1}"}{(d.IsPrimary ? " (Primary)" : "")} — {(int)d.BoundsDiu.Width}x{(int)d.BoundsDiu.Height}")).ToList();

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
    }

    private void ObsRemoteToggle_Click(object sender, RoutedEventArgs e)
    {
        ObsRemoteFields.Visibility = ObsRemoteToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // -------------------------------------------------------------- pairing

    private void RefreshShareClipsStatusText()
    {
        if (!_settings.ShareClipsEnabled)
        {
            ShareClipsStatusText.Text = "Off";
        }
        else if (!string.IsNullOrEmpty(_settings.AuthorizedClientName))
        {
            ShareClipsStatusText.Text = $"Sharing as \"{Environment.MachineName}\", authorized: {_settings.AuthorizedClientName}";
        }
        else
        {
            ShareClipsStatusText.Text = $"Sharing as \"{Environment.MachineName}\", waiting for a PC to pair";
        }
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

        RefreshShareClipsStatusText();
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
    /// OBS -- unlike buffer duration/hidden buffers, which already work fine
    /// remotely since they're just obs-websocket calls. Greys out the local
    /// section (mounting a drive here would be a silent no-op if OBS is remote)
    /// and shows the transmitter-control panel instead.
    /// </summary>
    private void RefreshRamDiskRemoteGating()
    {
        bool remote = _settings.ObsIsRemote;
        RamDiskRemoteNotice.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        LocalRamDiskSection.IsEnabled = !remote;
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

    private void ShowDisclaimerToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowDisclaimer = ShowDisclaimerToggle.IsChecked == true;
        _settings.Save();
        if (!_settings.ShowDisclaimer)
            _disclaimer.Hide();
        else if (IsVisible)
            _disclaimer.Show();
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

    private static void CreateOrUpdateStartupTask()
    {
        // No /RL HIGHEST -- that requests the task run elevated, which schtasks.exe
        // itself refuses to REGISTER unless the calling process already has admin
        // rights (Access is denied), regardless of what happens at ONLOGON time.
        // Backtrack never needs to run elevated (only the RAM disk driver install
        // does, and that's its own separate, explicit UAC prompt via RamDisk.cs),
        // so this was failing "Launch with Windows" outright for any non-admin
        // account -- which is most of them -- for a feature that has no actual
        // reason to need elevation at all.
        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        var psi = new ProcessStartInfo("schtasks.exe",
            $"/Create /F /SC ONLOGON /TN \"{ScheduledTaskName}\" /TR \"\\\"{exePath}\\\"\"")
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
        var psi = new ProcessStartInfo("schtasks.exe", $"/Delete /F /TN \"{ScheduledTaskName}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using Process proc = Process.Start(psi)!;
        proc.WaitForExit();
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
                CheckUpdatesButton.Content = "Check now";
                await CheckForUpdatesAsync();
                return;
            }

            // Backtrack's own self-update never runs on a dev build (see the
            // comment in CheckForUpdatesAsync) -- don't even bother checking
            // availability here, since a dev build's digest never matches and
            // would always misreport "update available" for a self-update that
            // will never actually be offered.
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
            CheckUpdatesButton.Content = _manualUpdateReady ? "Update" : "Check now";
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
