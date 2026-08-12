using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Backtrack;

public partial class App : Application
{
    // All three windows must live for the whole app lifetime -- Application doesn't
    // otherwise hold a reference to any of them once OnStartup returns, and MainWindow
    // in particular is never registered as the "main" window (it starts hidden).
    private StatusOverlay? _status;
    private ToastOverlay? _toasts;
    private ScrimOverlay? _scrim;
    private DisclaimerOverlay? _disclaimer;
    private LogoOverlay? _logo;
    private StreamingStatusOverlay? _streamingStatus;
    private PairingRequestOverlay? _pairingRequest;
    private MainWindow? _main;

    private static Mutex? _appMutex;
    private static EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before any window is created -- DynamicResource lookups on a
        // window's very first Loaded pass should already see the right
        // theme, not flash the default one then swap.
        ThemeManager.Apply(AppSettings.Load().Theme);

        // %AppData%\Backtrack, same folder settings.json already lives in --
        // not a bare relative "crash.log", which lands wherever the process
        // happens to think its working directory is (inconsistent depending
        // on how it's launched: double-click, a shortcut with a different
        // "Start in" folder, Task Scheduler, etc.) rather than somewhere
        // reliably findable afterward.
        string crashLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Backtrack", "crash.log");
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crashLogPath)!);
                System.IO.File.AppendAllText(crashLogPath, $"[{DateTime.Now}] Domain exception: {ev.ExceptionObject}\n");
            }
            catch { }
        };
        DispatcherUnhandledException += (s, ev) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crashLogPath)!);
                System.IO.File.AppendAllText(crashLogPath, $"[{DateTime.Now}] Dispatcher exception: {ev.Exception}\n");
            }
            catch { }
        };

        bool isPrimary;
        try
        {
            _appMutex = new Mutex(false, @"Local\Backtrack_SingleInstance_Mutex_v3");
            isPrimary = _appMutex.WaitOne(TimeSpan.Zero, false);
        }
        catch (AbandonedMutexException)
        {
            isPrimary = true;
        }
        catch (Exception)
        {
            isPrimary = true;
        }

        if (!isPrimary)
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(@"Local\Backtrack_ShowHud_Event_v3");
                existingEvent?.Set();
            }
            catch { }

            Shutdown();
            return;
        }

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Backtrack_ShowHud_Event_v3");
            ThreadPool.RegisterWaitForSingleObject(_showEvent, (state, timedOut) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_main != null && !_main.IsVisible)
                    {
                        _main.ToggleVisible();
                    }
                });
            }, null, -1, false);
        }
        catch { }

        // The status overlay and toast notifications are always visible,
        // independent of the hotkey-summoned HUD -- create and show them first.
        _status = new StatusOverlay();
        _status.Show();

        _toasts = new ToastOverlay();
        _toasts.Show();

        // Stays hidden until the HUD is summoned -- unlike the two above, this
        // one is never shown on its own.
        _scrim = new ScrimOverlay();

        // Also only shown/hidden in lockstep with the HUD (see MainWindow.ToggleVisible),
        // not an always-on fixture -- unlike Status/Toast, this one only makes
        // sense while the overlay itself is actually open.
        _disclaimer = new DisclaimerOverlay();

        // Also only shown/hidden in lockstep with the HUD -- lives at a fixed
        // screen position, independent of MainWindow's own size, which changes a
        // lot between the compact pill and the big Gallery/Player panel.
        _logo = new LogoOverlay();

        // Unlike the logo, this one DOES need to track MainWindow's own
        // position/size (it's meant to read as attached directly underneath
        // the main pill) -- see MainWindow.UpdateStreamingBoxVisibility and
        // StreamingStatusOverlay.Reposition.
        _streamingStatus = new StreamingStatusOverlay();

        // A pairing request can arrive at any time (whoever's on the other PC
        // decides when to click "pair"), independent of whether the HUD is open --
        // created up front like the other always-available overlays, but only
        // actually shown when a request comes in.
        _pairingRequest = new PairingRequestOverlay();

        // MainWindow creates its own HWND immediately (for the global hotkey) but
        // is never Shown until the hotkey is pressed -- it's a summonable overlay,
        // not a normal always-visible window.
        _main = new MainWindow(_status, _toasts, _scrim, _disclaimer, _logo, _streamingStatus, _pairingRequest);

        string? updatedVersion = e.Args
            .FirstOrDefault(a => a.StartsWith("--updated=", StringComparison.Ordinal))
            ?.Substring("--updated=".Length);
        if (!string.IsNullOrEmpty(updatedVersion))
        {
            Dispatcher.BeginInvoke(() => _toasts.ShowUpdateApplied("Backtrack", updatedVersion), DispatcherPriority.Loaded);
        }

        // Once, ever, per install (gated by FirewallRulesAttempted, set
        // after the first try regardless of outcome) -- adds the inbound/
        // outbound firewall rules Pairing/PairingService.cs's discovery
        // listener and pairing server need. Off the UI thread: the elevated
        // netsh call blocks on Process.WaitForExit() until the user
        // responds to the UAC prompt and it actually finishes, which would
        // freeze the whole app (including the hotkey/tray) if awaited
        // inline here on startup.
        if (!AppSettings.Load().FirewallRulesAttempted)
        {
            _toasts.ShowFirewallSetup();
            Task.Run(() =>
            {
                (bool success, string? error) = Interop.FirewallRules.AddRulesElevated();
                AppSettings current = AppSettings.Load();
                current.FirewallRulesAttempted = true;
                current.Save();
                AppLog.Write(success ? "Firewall rules added for clip sharing." : $"Firewall rule setup skipped: {error}");
            });
        }
    }
}
