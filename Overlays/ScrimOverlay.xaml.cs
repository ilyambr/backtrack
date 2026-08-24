using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Backtrack.Interop;

namespace Backtrack;

public partial class ScrimOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public event Action? Dismissed;

    public ScrimOverlay()
    {
        InitializeComponent();
        Reposition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
            IntPtr hwnd = source.Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Prevent Windows from ever promoting ScrimOverlay above MainWindow when clicked
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>Re-reads the configured display and re-covers it -- called again from Settings if the user changes which monitor Backtrack shows on mid-session.</summary>
    public void Reposition()
    {
        Rect bounds = DisplayMonitors.ResolveBoundsDiu(AppSettings.Load().DisplayDeviceName);
        Left = bounds.X;
        Top = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private DateTime _ignoreClicksUntilUtc = DateTime.MinValue;

    /// <summary>
    /// Temporarily ignores background/scrim dismiss clicks for the specified duration (default 400ms).
    /// Used when opening or switching tabs/screens so fast double-clicks don't immediately exit or crash the player.
    /// </summary>
    public void ArmDismissCooldown(int ms = 400)
    {
        _ignoreClicksUntilUtc = DateTime.UtcNow.AddMilliseconds(ms);
    }

    // Any click on the dim area dismisses -- not just left.
    private void Scrim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DateTime.UtcNow < _ignoreClicksUntilUtc)
            return;
        Dismissed?.Invoke();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow < _ignoreClicksUntilUtc)
            return;
        Dismissed?.Invoke();
    }

    /// <summary>
    /// Player fullscreen deliberately covers this window's own top-left
    /// corner with the video, but this button sits in a separate Topmost
    /// window from MainWindow -- whether it actually ends up hidden behind
    /// the video depends on exact window bounds/z-order lining up, which
    /// fullscreen's letterboxing can't always guarantee. Collapsing it
    /// outright removes that dependency entirely; Escape and the in-video
    /// fullscreen-exit button both still reach the same close path.
    /// </summary>
    public void SetExitButtonVisible(bool visible) =>
        ExitButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
