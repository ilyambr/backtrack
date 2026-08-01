using System.Windows;

namespace CaptureCenter;

public partial class App : Application
{
    // All three windows must live for the whole app lifetime -- Application doesn't
    // otherwise hold a reference to any of them once OnStartup returns, and MainWindow
    // in particular is never registered as the "main" window (it starts hidden).
    private StatusOverlay? _status;
    private ToastOverlay? _toasts;
    private MainWindow? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The status overlay and toast notifications are always visible,
        // independent of the hotkey-summoned HUD -- create and show them first.
        _status = new StatusOverlay();
        _status.Show();

        _toasts = new ToastOverlay();
        _toasts.Show();

        // MainWindow creates its own HWND immediately (for the global hotkey) but
        // is never Shown until the hotkey is pressed -- it's a summonable overlay,
        // not a normal always-visible window.
        _main = new MainWindow(_status, _toasts);
    }
}
