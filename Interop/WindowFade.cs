using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Backtrack.Interop;

/// <summary>
/// Fades a window in/out via the native AnimateWindow blend transition
/// instead of a WPF Opacity animation -- MainWindow is AllowsTransparency=
/// "False" (required for VLC's native video HWND; see StopPlayerPlayback's
/// own comments on that), and a non-layered window silently ignores
/// Window.Opacity animation entirely, so a plain WPF fade wouldn't actually
/// show anything. AnimateWindow works at the DWM level regardless of the
/// window's own layered/non-layered style -- DWM composites the window's
/// real rendered pixels (including native child HWNDs like VLC's) for the
/// transition, so this is also safe for the exact "airspace" reasons a WPF-
/// internal Visibility/opacity swap wouldn't be.
///
/// FadeIn uses WindowInteropHelper.EnsureHandle() (creates the HWND without
/// showing it) rather than Window.Show() first -- AnimateWindow's blend-in
/// only animates a window that's currently hidden at the Win32 level;
/// calling Show() first would leave nothing left to animate into view.
/// Callers should still call Show()/Hide() themselves right after this
/// returns (cheap, safe no-ops on an already-shown/-hidden window) so
/// Window.IsVisible definitely reflects the end state even if some edge
/// case ever left the underlying WM_SHOWWINDOW-driven sync out of step.
/// </summary>
public static class WindowFade
{
    private const uint AW_HIDE = 0x00010000;
    private const uint AW_BLEND = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AnimateWindow(IntPtr hWnd, uint dwTime, uint dwFlags);

    public static void FadeIn(Window window, int durationMs = 180)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        AnimateWindow(hwnd, (uint)durationMs, AW_BLEND);
    }

    public static void FadeOut(Window window, int durationMs = 150)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
            AnimateWindow(hwnd, (uint)durationMs, AW_BLEND | AW_HIDE);
    }
}
