using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CaptureCenter.Interop;
using CaptureCenter.Obs;
using CaptureCenter.Pairing;
using CaptureCenter.Updates;
using Microsoft.Win32;
using LibVlc = LibVLCSharp.Shared;

namespace CaptureCenter;

public partial class MainWindow : Window
{
    private enum Screen { Idle, SaveReplay, Gallery, Player, Settings }

    private const double CompactWidth = 460;
    private const double WideWidth = 680;

    // LogoOverlay sits at a fixed Top=20 with Height=46 (bottom edge at 66), so the
    // compact HUD panel needs to start clear of that, not at the same Top=40 both
    // windows used to share back when the logo was drawn inside MainWindow itself.
    private const double CompactTop = 76;
    private const string RunKeyName = "CaptureCenter";
    private const string ScheduledTaskName = "CaptureCenterAutostart";

    private readonly ObsService _obs;
    private bool _serverEnabledAtStartup;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _micTimer;
    private readonly StatusOverlay _statusOverlay;
    private readonly ToastOverlay _toastOverlay;
    private readonly ScrimOverlay _scrim;
    private readonly DisclaimerOverlay _disclaimer;
    private readonly LogoOverlay _logo;
    private readonly PairingRequestOverlay _pairingRequestOverlay;
    private readonly AppSettings _settings;
    private readonly UpdateService _updates = new();
    private readonly DispatcherTimer _updateTimer;
    private readonly PairingService _pairing;
    private readonly Dictionary<string, string> _rowLabels = new();
    private List<ReplayRow> _lastReplayRows = new();
    private GlobalHotkey? _hotkey;
    private Screen _lastScreen = Screen.Idle;

    // --------------------------------------------------------------- LibVLC / Player

    private LibVlc.LibVLC? _libVlc;
    private LibVlc.MediaPlayer? _vlcPlayer;
    private FileInfo? _currentPlayerFile;
    private readonly DispatcherTimer _seekTimer;
    private bool _seekDragging;

    // Hotkey capture (Settings)
    private bool _capturingHotkey;

    // Trim
    private TimeSpan? _trimStart;
    private TimeSpan? _trimEnd;

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

        string url;
        string? password;
        (url, password, _serverEnabledAtStartup) = ResolveObsConnection();
        _obs = new ObsService(url, password);

        // Events, not polling -- these fire the instant OBS says so, whether or
        // not the HUD is even open, which is the only way "did it actually save"
        // can be answered truthfully instead of guessed at.
        _obs.RecordingStateChanged += (active, path) => Dispatcher.BeginInvoke(() => _toastOverlay.ShowRecording(active, path));
        _obs.ReplaySaved += (key, path) => Dispatcher.BeginInvoke(() =>
        {
            string label = _rowLabels.TryGetValue(key, out string? l) ? l : key;
            _toastOverlay.ShowReplaySaved(label, path);
            _ = RefreshGalleryCountAsync();
        });
        _obs.StateChanged += () => Dispatcher.BeginInvoke(() => _ = PrefetchRowLabelsAsync());

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await RefreshStatusAsync();

        _micTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _micTimer.Tick += (_, _) => _statusOverlay.SetMicStatus(_obs.GetMicStatus());

        _seekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _seekTimer.Tick += (_, _) => UpdatePlayerSeekUi();

        // The window needs a real HWND immediately for RegisterHotKey and the
        // acrylic blur, but must never actually appear until the hotkey is
        // pressed -- EnsureHandle() creates it without calling Show().
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = CompactTop;
        Acrylic.TryEnableBlurBehind(hwnd, 16, 17, 19, 205);
        // This is a hotkey-summoned HUD, not an independent app window -- it and
        // every auxiliary overlay window (Status/Toast/Scrim/Disclaimer/Logo) were
        // showing up as five or six separate Alt+Tab entries for one app, since
        // ShowInTaskbar="False" alone doesn't affect Alt+Tab, only the taskbar.
        ToolWindow.Enable(hwnd);

        // Vertically centers Gallery/Player once their real height is actually known --
        // SizeToContent="Height" means the window's true height isn't known until after
        // layout runs, so trying to precompute it upfront (transport bar + trim panel,
        // whose visibility toggles) kept drifting off-center. Same fix as DisclaimerOverlay
        // uses for its own bottom positioning.
        SizeChanged += (_, _) => RecenterIfBig();

        RegisterHotkeyFromSettings();

        try
        {
            LibVlc.Core.Initialize();
            // --avcodec-hw=none: disables hardware-accelerated decoding. Without this,
            // the video surface can come up as a blank white swapchain with nothing
            // ever drawn into it on machines/VMs where GPU decode acceleration isn't
            // reliably available -- software decode is slower but actually paints frames.
            _libVlc = new LibVlc.LibVLC("--no-video-title-show", "--avcodec-hw=none");
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

        // Runs in the background regardless of whether the HUD is even open --
        // this app starts hidden and can sit there for hours, so checking only
        // when the user happens to open Settings would mean updates often just
        // never get noticed.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync();
        _updateTimer.Start();
        _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// Checks and silently applies updates for Backtrack itself and for both
    /// companion OBS plugins -- no confirmation prompt by design. Each check is
    /// independent and swallows its own failures (no network, repo has no
    /// releases yet, etc.) so one failing never blocks the others.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        await CheckForUpdatesAsync(statusCallback: null);
    }

    /// <summary>
    /// Runs all three checks and, if given a callback (the manual Settings button),
    /// reports a per-component status line for each -- not just a single pass/fail,
    /// since "nothing happened" is ambiguous with three independent components and
    /// the user should be able to see all three actually got checked.
    /// </summary>
    private async Task CheckForUpdatesAsync(Action<string>? statusCallback)
    {
        string backtrack = await CheckAndApplySelfUpdateAsync();
        string replaySlider = await CheckAndApplyPluginUpdateAsync("obs-replay-slider", "Replay Slider", "replay-slider.dll",
            name => name.Contains("windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        string sourceRecord = await CheckAndApplyPluginUpdateAsync("obs-source-record", "Source Record", "source-record.dll",
            name => name.Contains("windows-installer", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        statusCallback?.Invoke($"Backtrack: {backtrack} · Replay Slider: {replaySlider} · Source Record: {sourceRecord}");
    }

    private async Task<string> CheckAndApplyPluginUpdateAsync(string repo, string displayName, string dllFileName, Func<string, bool> assetPredicate)
    {
        try
        {
            ReleaseInfo? release = await _updates.GetLatestReleaseAsync("ilyambr", repo, assetPredicate);
            if (release?.DownloadUrl is null)
                return "check failed";

            Version installed = _updates.GetInstalledPluginVersion(dllFileName);
            if (!UpdateService.IsNewer(release.Version, installed))
                return "up to date";

            await _updates.InstallPluginUpdateAsync(release.DownloadUrl);
            _ = Dispatcher.BeginInvoke(() => _toastOverlay.ShowUpdateApplied(displayName, release.Version));
            return $"updated to {release.Version}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check/apply failed for {repo}: {ex.Message}");
            return "check failed";
        }
    }

    private async Task<string> CheckAndApplySelfUpdateAsync()
    {
        try
        {
            ReleaseInfo? release = await _updates.GetLatestReleaseAsync("ilyambr", "backtrack",
                name => name.Contains("windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (release?.DownloadUrl is null)
                return "check failed";

            if (!UpdateService.IsNewer(release.Version, UpdateService.CurrentAppVersion))
                return "up to date";

            await _updates.ApplySelfUpdateAsync(release.DownloadUrl);
            // The helper script above is now waiting for this process to exit --
            // shut down cleanly so it can finish the swap and relaunch. Whatever
            // called this never actually sees this return value in that case.
            _ = Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
            return $"updated to {release.Version}, restarting";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Self-update check/apply failed: {ex.Message}");
            return "check failed";
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
    private void CloseOverlay()
    {
        // Falls back to whatever non-Player screen was last showing (Idle by default)
        // rather than leaving Player active: reopening later must never show a
        // screen wired to a _vlcPlayer that CloseOverlay is about to tear down.
        ShowScreen(_lastScreen);
        Hide();
        _scrim.Hide();
        _disclaimer.Hide();
        _logo.Hide();
    }

    private void ToggleVisible()
    {
        if (IsVisible)
        {
            CloseOverlay();
        }
        else
        {
            _scrim.Show();
            _logo.Show();
            Show();
            Activate();
            // Activate() re-asserts THIS window to the front of the topmost band --
            // the always-on overlays must be re-asserted AFTER that, or Activate()
            // here would otherwise bury them behind MainWindow.
            _statusOverlay.Show();
            WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(_statusOverlay).Handle);
            _toastOverlay.Show();
            WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(_toastOverlay).Handle);

            if (_settings.ShowDisclaimer)
                _disclaimer.Show();
        }
    }

    // ---------------------------------------------------------------- screens

    private void ShowScreen(Screen screen)
    {
        IdlePanel.Visibility = screen == Screen.Idle ? Visibility.Visible : Visibility.Collapsed;
        SaveReplayPanel.Visibility = screen == Screen.SaveReplay ? Visibility.Visible : Visibility.Collapsed;
        GalleryPanel.Visibility = screen == Screen.Gallery ? Visibility.Visible : Visibility.Collapsed;
        PlayerPanel.Visibility = screen == Screen.Player ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = screen == Screen.Settings ? Visibility.Visible : Visibility.Collapsed;

        // The gear only makes sense on the idle screen -- it isn't a fourth tile,
        // so it shouldn't linger once you've navigated away from the row it sits above.
        TopRightButtons.Visibility = screen == Screen.Idle ? Visibility.Visible : Visibility.Collapsed;

        bool big = screen is Screen.Gallery or Screen.Player;
        Width = screen == Screen.Settings ? WideWidth : big ? BigWidth() : CompactWidth;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;

        if (big)
        {
            ApplyBigScreenSize();
            // A single SizeChanged firing during this transition can catch an
            // intermediate, not-yet-settled ActualHeight (most visible on the first
            // compact-to-big jump, a big size delta) -- scheduling one more recenter
            // after the dispatcher finishes this layout pass catches that case
            // instead of leaving it looking centered only after a second transition.
            Dispatcher.BeginInvoke(RecenterIfBig, DispatcherPriority.ContextIdle);
        }
        else
        {
            Top = CompactTop;
        }

        PlayerOverlayPopup.IsOpen = screen == Screen.Player;

        if (screen != Screen.Player)
            StopPlayerPlayback();

        if (screen is Screen.Idle or Screen.SaveReplay or Screen.Gallery or Screen.Settings)
            _lastScreen = screen;
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
    private double BigWidth() => Math.Min(SystemParameters.PrimaryScreenWidth * 0.78, 1500);

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

        // Real vertical centering happens in RecenterIfBig, once ActualHeight is
        // actually known -- guessing the total window height up front (transport
        // bar + trim panel, which toggles) kept drifting off. This is just a
        // reasonable placeholder until that first layout pass lands.
        Top = 30;
    }

    private void RecenterIfBig()
    {
        if (GalleryPanel.Visibility == Visibility.Visible || PlayerPanel.Visibility == Visibility.Visible)
            Top = Math.Max((SystemParameters.PrimaryScreenHeight - ActualHeight) / 2, 16);
    }

    private void BackToIdle_Click(object sender, MouseButtonEventArgs e) => ShowScreen(Screen.Idle);

    private void BackToGallery_Click(object sender, MouseButtonEventArgs e)
    {
        ShowScreen(Screen.Gallery);
        LoadGallery();
    }

    // ------------------------------------------------------------- idle tiles

    private async Task RefreshStatusAsync()
    {
        if (!_obs.IsConnected)
        {
            ConnDot.Fill = (Brush)FindResource("Rec");
            ConnStatusText.Text = "OBS Disconnected";
            ConnStatusText.ToolTip = !_serverEnabledAtStartup
                ? "OBS's WebSocket server is disabled -- enable it in OBS: Tools > WebSocket Server Settings"
                : _obs.LastError is null ? "Not connected to OBS" : $"OBS: {_obs.LastError}";
            RecordLabel.Text = "Start Recording";
            RecordStatusText.Text = "--:--";
            RecordDot.Fill = (Brush)FindResource("Text1");
            ReplayStatus.Text = "--:--";
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
            RecordLabel.Text = recStatus.Active ? "Stop Recording" : "Start Recording";
            RecordDot.Fill = (Brush)FindResource(recStatus.Active ? "Rec" : "Text1");
            RecordStatusText.Text = recStatus.Active ? FormatDuration(recStatus.DurationMs) : "--:--";
            _statusOverlay.SetRecording(recStatus.Active);

            // Not just OBS's single global replay-buffer flag: obs-replay-slider (and
            // obs-source-record, exposed through the same bridge) can each have their
            // own buffer armed independently of it, so a row showing green (Status != 0)
            // must count as "on" here too -- otherwise this pill can say "Off" while a
            // buffer row is visibly active, which is exactly backwards.
            bool replayBufferActive = await _obs.GetReplayBufferActiveAsync();
            bool anyRowActive = false;
            try
            {
                anyRowActive = (await _obs.ListReplayRowsAsync()).Any(r => r.Status != 0);
            }
            catch
            {
                // Bridge unreachable this tick -- fall back to just the base OBS flag.
            }
            bool replayActive = replayBufferActive || anyRowActive;

            ReplayStatus.Text = replayActive ? "On" : "Off";
            ReplayStatus.Foreground = (Brush)FindResource(replayActive ? "Green" : "Text2");
            SaveReplayIcon.Foreground = (Brush)FindResource(replayActive ? "Green" : "Text0");
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
            MessageBox.Show(this, $"Couldn't toggle recording: {ex.Message}", "Capture Center");
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
        LoadGallery();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(Screen.Settings);
        LoadSettingsUi();
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

        _lastReplayRows = rows;

        // Online (armed) buffers first -- everything else keeps its original order after them.
        foreach (ReplayRow row in rows.OrderBy(r => r.Status == 1 ? 0 : 1))
            BufRowsPanel.Children.Add(BuildRowButton(row));

        BufRowsPanel.Children.Add(BuildSharedClipLengthControl(rows));
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
                MessageBox.Show(this, $"Save failed: {ex.Message}", "Capture Center");
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
                    MessageBox.Show(this, $"Could not set clip length: {ex.Message}\n\n(Needs the set-row-length bridge update in obs-replay-slider.)", "Capture Center");
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
                ? Directory.EnumerateFiles(_settings.ClipsFolder)
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

        if (!Directory.Exists(_settings.ClipsFolder))
        {
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = $"Folder doesn't exist yet: {_settings.ClipsFolder}\n\nSet a folder that actually has your clips in Settings.",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
                TextWrapping = TextWrapping.Wrap,
                Width = BigWidth() - 40,
            });
            return;
        }

        List<FileInfo> files;
        try
        {
            files = Directory.EnumerateFiles(_settings.ClipsFolder)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
        }
        catch (Exception ex)
        {
            GalleryGrid.Children.Add(new TextBlock { Text = $"Couldn't read that folder: {ex.Message}", Foreground = (Brush)FindResource("Rec"), TextWrapping = TextWrapping.Wrap });
            return;
        }

        if (files.Count == 0)
        {
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = "No clips in this folder yet.",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
            });
            return;
        }

        foreach (FileInfo file in files)
            GalleryGrid.Children.Add(BuildClipCard(file));

        GalleryStatus.Text = files.Count == 1 ? "1 clip" : $"{files.Count} clips";
    }

    private Border BuildClipCard(FileInfo file)
    {
        // Deterministic placeholder color per file -- there's no ffmpeg available
        // to pull a real video frame, so this is an honest stand-in, not a fake thumbnail.
        int hash = file.Name.GetHashCode();
        var thumbColor = Color.FromRgb(
            (byte)(40 + Math.Abs(hash) % 60),
            (byte)(40 + Math.Abs(hash / 7) % 60),
            (byte)(50 + Math.Abs(hash / 13) % 70));

        var thumb = new Border
        {
            Background = new SolidColorBrush(thumbColor),
            Height = 84,
            Cursor = Cursors.Hand,
        };
        thumb.Child = new TextBlock
        {
            Text = "▶",
            FontSize = 20,
            Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        thumb.MouseLeftButtonUp += (_, _) => OpenInPlayer(file);

        var title = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 7, 0, 1),
        };

        DateTime modified = file.LastWriteTime;
        string subText = modified.Date == DateTime.Today
            ? modified.ToString("h:mm tt")
            : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock { Text = subText, FontSize = 11, Foreground = (Brush)FindResource("Text2") };

        var playBtn = new Button { Content = "Play", Style = (Style)FindResource("IconButton") };
        playBtn.Click += (_, _) => OpenInPlayer(file);

        var folderBtn = new Button { Content = "Folder", Style = (Style)FindResource("IconButton") };
        folderBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullName}\"") { UseShellExecute = true });

        var renameBtn = new Button { Content = "Rename", Style = (Style)FindResource("IconButton") };
        var deleteBtn = new Button { Content = "Delete", Style = (Style)FindResource("IconButton") };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(playBtn);
        actions.Children.Add(renameBtn);
        actions.Children.Add(folderBtn);
        actions.Children.Add(deleteBtn);

        // Only worth showing when the clip isn't already local -- this is the
        // "bring it from the stream PC to this one" action.
        if (IsNetworkPath(_settings.ClipsFolder))
        {
            var copyBtn = new Button { Content = "Copy here", Style = (Style)FindResource("IconButton"), Margin = new Thickness(0) };
            copyBtn.Click += async (_, _) => await CopyToThisPcAsync(file, copyBtn);
            actions.Children.Add(copyBtn);
        }
        else
        {
            deleteBtn.Margin = new Thickness(0);
        }

        var content = new StackPanel();
        content.Children.Add(thumb);
        content.Children.Add(title);
        content.Children.Add(sub);
        content.Children.Add(actions);

        var card = new Border { Width = 190, Margin = new Thickness(0, 0, 14, 14), Child = content };

        // Extracted, unguarded rename-commit helper: LostFocus fires a second time
        // when the TextBox is removed from the tree to restore the label (removing a
        // focused element fires its own LostFocus), which would otherwise re-run a
        // guarded "commit" a second time against a stale FileInfo. Guarding this exact
        // method would also skip the real work on the legitimate first call, so instead
        // BeginRename installs a `finished` flag around the two call sites, not inside this helper.
        renameBtn.Click += (_, _) => BeginRename(card, title, file);
        deleteBtn.Click += (_, _) => DeleteClip(file, card);

        return card;
    }

    private void BeginRename(Border card, TextBlock title, FileInfo file)
    {
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
            else if (e.Key == Key.Escape) { if (!finished) { finished = true; LoadGallery(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; CommitRename(); } };

        void CommitRename()
        {
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
                    MessageBox.Show(this, $"Couldn't rename: {ex.Message}", "Capture Center");
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
            MessageBox.Show(this, $"Couldn't copy that clip: {ex.Message}", "Capture Center");
            triggerButton.Content = originalText;
            triggerButton.IsEnabled = true;
        }
    }

    /// <summary>Real Undo: nothing actually happens to the file until the 5s toast expires unclicked -- Undo just stops the timer and leaves the clip untouched.</summary>
    private void DeleteClip(FileInfo file, Border card)
    {
        if (!ConfirmDialog.Ask(this, $"Delete \"{file.Name}\"? You can undo this for a few seconds after.", "Delete"))
            return;

        _toastOverlay.ShowDeleteUndo(file.Name, onExpire: () =>
        {
            if (!RecycleBin.Delete(file.FullName))
                Dispatcher.BeginInvoke(() => MessageBox.Show(this, "Couldn't delete that file.", "Capture Center"));
            Dispatcher.BeginInvoke(() =>
            {
                if (GalleryPanel.Visibility == Visibility.Visible)
                    LoadGallery();
                else
                    _ = RefreshGalleryCountAsync();
            });
        });
    }

    // ----------------------------------------------------------------- player

    /// <summary>Resolves a clip that may live on a remote stream PC's share back to a real local path when possible, since LibVLC plays a UNC path fine but some operations (trim export) want a plain string path either way -- kept for symmetry/clarity at call sites.</summary>
    private static string ResolveLocalClipPath(FileInfo file) => file.FullName;

    private void OpenInPlayer(FileInfo file)
    {
        if (_libVlc is null)
        {
            MessageBox.Show(this, "The video player failed to initialize (LibVLC).", "Capture Center");
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
        if (_vlcPlayer is null || _seekDragging)
            return;

        long lengthMs = _vlcPlayer.Length;
        long timeMs = _vlcPlayer.Time;
        PlayerSeek.Maximum = Math.Max(lengthMs, 1);
        PlayerSeek.Value = Math.Clamp(timeMs, 0, PlayerSeek.Maximum);
        PlayerCurrentTime.Text = FormatDuration(Math.Max(timeMs, 0));
        PlayerDurationText.Text = FormatDuration(Math.Max(lengthMs, 0));
    }

    private void PlayerSeek_DragStarted(object sender, MouseButtonEventArgs e) => _seekDragging = true;

    private void PlayerSeek_DragCompleted(object sender, MouseButtonEventArgs e)
    {
        _seekDragging = false;
        if (_vlcPlayer is not null)
            _vlcPlayer.Time = (long)PlayerSeek.Value;
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
            else if (ke.Key == Key.Escape) { if (!finished) { finished = true; RevertBox(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; CommitRename(); } };

        void RevertBox()
        {
            stack.Children.Remove(box);
            stack.Children.Insert(index, PlayerTitle);
        }

        void CommitRename()
        {
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
                    MessageBox.Show(this, $"Couldn't rename: {ex.Message}", "Capture Center");
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

        if (!ConfirmDialog.Ask(this, $"Delete \"{file.Name}\"? You can undo this for a few seconds after.", "Delete"))
            return;

        StopPlayerPlayback();
        ShowScreen(Screen.Gallery);
        LoadGallery();

        _toastOverlay.ShowDeleteUndo(file.Name, onExpire: () =>
        {
            if (!RecycleBin.Delete(file.FullName))
                Dispatcher.BeginInvoke(() => MessageBox.Show(this, "Couldn't delete that file.", "Capture Center"));
            Dispatcher.BeginInvoke(() =>
            {
                if (GalleryPanel.Visibility == Visibility.Visible)
                    LoadGallery();
            });
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
            MessageBox.Show(this, "Set both a start and end point first (end must be after start).", "Capture Center");
            return;
        }

        FileInfo sourceFile = _currentPlayerFile;
        TimeSpan start = _trimStart.Value;
        TimeSpan end = _trimEnd.Value;

        string tempOut = Path.Combine(Path.GetTempPath(), $"cc_trim_{Guid.NewGuid():N}{sourceFile.Extension}");

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
                if (!ConfirmDialog.Ask(this, "Replace the original clip with this trimmed version? This can't be undone.", "Replace"))
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
            MessageBox.Show(this, $"Trim failed: {ex.Message}", "Capture Center");
            OpenInPlayer(sourceFile);
        }
        finally
        {
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

        exportPlayer.EndReached += (_, _) => done.Set();
        exportPlayer.EncounteredError += (_, _) => done.Set();

        exportPlayer.Play();
        if (!done.Wait(TimeSpan.FromMinutes(10)))
            throw new TimeoutException("Trim export took too long.");
        exportPlayer.Stop();
    }

    // --------------------------------------------------------------- settings

    private void LoadSettingsUi()
    {
        LaunchWithWindowsToggle.IsChecked = _settings.LaunchWithWindows;
        ClipsFolderText.Text = _settings.ClipsFolder;

        ObsRemoteToggle.IsChecked = _settings.ObsIsRemote;
        ObsRemoteFields.Visibility = _settings.ObsIsRemote ? Visibility.Visible : Visibility.Collapsed;
        ObsHostBox.Text = _settings.ObsHost;
        ObsPortBox.Text = _settings.ObsPort.ToString();
        ObsPasswordBox.Password = _settings.ObsRemotePassword;

        ShowDisclaimerToggle.IsChecked = _settings.ShowDisclaimer;
        HotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.HotkeyModifiers, (uint)_settings.HotkeyVirtualKey);

        ShareClipsToggle.IsChecked = _settings.ShareClipsEnabled;
        RefreshShareClipsStatusText();
        RefreshPairingStatusUi();
        RenderDiscoveredDevices();
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
            ShareClipsStatusText.Text = $"Sharing as \"{Environment.MachineName}\" — authorized: {_settings.AuthorizedClientName}";
        }
        else
        {
            ShareClipsStatusText.Text = $"Sharing as \"{Environment.MachineName}\" — waiting for a PC to pair";
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
                    onCodeReceived: code => Dispatcher.BeginInvoke(() => statusText.Text = $"Code: {code} — waiting for approval..."),
                    cts.Token);

                switch (result.Outcome)
                {
                    case PairingOutcome.Approved:
                        statusText.Text = "Paired!";
                        RefreshPairingStatusUi();
                        RenderDiscoveredDevices();
                        return;
                    case PairingOutcome.Denied:
                        statusText.Text = "Request denied.";
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

    private void ApplyObsConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool remote = ObsRemoteToggle.IsChecked == true;
            if (remote && string.IsNullOrWhiteSpace(ObsHostBox.Text))
            {
                MessageBox.Show(this, "Enter the stream PC's address first.", "Capture Center");
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
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't apply that OBS connection: {ex.Message}", "Capture Center");
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
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
            if (enabled)
            {
                var psi = new ProcessStartInfo("schtasks.exe",
                    $"/Create /F /SC ONLOGON /RL HIGHEST /TN \"{ScheduledTaskName}\" /TR \"\\\"{exePath}\\\"\"")
                { UseShellExecute = false, CreateNoWindow = true };
                using Process proc = Process.Start(psi)!;
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    throw new InvalidOperationException("schtasks.exe failed to create the startup task.");
            }
            else
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/Delete /F /TN \"{ScheduledTaskName}\"")
                { UseShellExecute = false, CreateNoWindow = true };
                using Process proc = Process.Start(psi)!;
                proc.WaitForExit();
            }

            _settings.LaunchWithWindows = enabled;
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't update the startup task: {ex.Message}", "Capture Center");
            LaunchWithWindowsToggle.IsChecked = !enabled;
        }
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
            MessageBox.Show(this, $"Couldn't change the clips folder: {ex.Message}", "Capture Center");
        }
    }

    private void QuitApp_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking Backtrack, Replay Slider, and Source Record...";
        try
        {
            await CheckForUpdatesAsync(status => UpdateStatusText.Text = status);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update check failed: {ex.Message}";
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
            MessageBox.Show(this, ex.Message, "Capture Center");
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
        StopPlayerPlayback();
        _libVlc?.Dispose();
        _hotkey?.Dispose();
        base.OnClosed(e);
    }
}
