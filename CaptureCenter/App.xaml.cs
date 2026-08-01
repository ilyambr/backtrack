using System.Windows;

namespace CaptureCenter;

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
    private PairingRequestOverlay? _pairingRequest;
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

        // A pairing request can arrive at any time (whoever's on the other PC
        // decides when to click "pair"), independent of whether the HUD is open --
        // created up front like the other always-available overlays, but only
        // actually shown when a request comes in.
        _pairingRequest = new PairingRequestOverlay();

        // MainWindow creates its own HWND immediately (for the global hotkey) but
        // is never Shown until the hotkey is pressed -- it's a summonable overlay,
        // not a normal always-visible window.
        _main = new MainWindow(_status, _toasts, _scrim, _disclaimer, _logo, _pairingRequest);
    }
}
