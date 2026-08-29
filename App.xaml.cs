using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Backtrack;

public partial class App : Application
{
    private StatusOverlay? _status;
    private ToastOverlay? _toasts;
    private ScrimOverlay? _scrim;
    private DisclaimerOverlay? _disclaimer;
    private LogoOverlay? _logo;
    private StreamingStatusOverlay? _streamingStatus;
    private PairingRequestOverlay? _pairingRequest;
    private RecentClipsOverlay? _recentClips;
    private MainWindow? _main;

    private static Mutex? _appMutex;
    private static EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppSettings startupSettings = AppSettings.Load();

        ThemeManager.Apply(startupSettings.Theme);

        if (startupSettings.DisableHardwareAcceleration)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

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
            _appMutex = new Mutex(true, @"Local\Backtrack_SingleInstance_Mutex_v3", out bool createdNew);
            isPrimary = createdNew;
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

        _status = new StatusOverlay();
        if (startupSettings.ShowStatusIndicator)
            _status.Show();

        _toasts = new ToastOverlay();
        _toasts.Show();

        _scrim = new ScrimOverlay();

        _disclaimer = new DisclaimerOverlay();

        _logo = new LogoOverlay();

        _streamingStatus = new StreamingStatusOverlay();

        _pairingRequest = new PairingRequestOverlay();

        _recentClips = new RecentClipsOverlay();

        _main = new MainWindow(_status, _toasts, _scrim, _disclaimer, _logo, _streamingStatus, _pairingRequest, _recentClips);

        string? updatedVersion = e.Args
            .FirstOrDefault(a => a.StartsWith("--updated=", StringComparison.Ordinal))
            ?.Substring("--updated=".Length);
        if (!string.IsNullOrEmpty(updatedVersion))
        {
            _main.MarkSelfUpdateApplied(updatedVersion);
            Dispatcher.BeginInvoke(() => _toasts.ShowUpdateApplied("Backtrack", updatedVersion), DispatcherPriority.Loaded);
        }

        if (!AppSettings.Load().FirewallRulesAttempted)
        {
            _toasts.ShowFirewallSetup();
            Task.Run(() =>
            {
                (bool success, string? error) = Interop.FirewallRules.AddRulesElevated();
                _main.MarkFirewallRulesAttempted();
                AppLog.Write(success ? "Firewall rules added for clip sharing." : $"Firewall rule setup skipped: {error}");
            });
        }
    }
}
