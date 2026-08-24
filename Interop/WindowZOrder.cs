using System;
using System.Runtime.InteropServices;

namespace Backtrack.Interop;

/// <summary>
/// Brings a topmost window back to the front of the topmost band without giving
/// it keyboard focus. Needed because among several Topmost="True" windows, Windows
/// puts whichever one was most recently shown/activated at the front -- so showing
/// the Scrim after StatusOverlay/ToastOverlay were first shown at startup would
/// otherwise leave it covering them.
/// </summary>
public static class WindowZOrder
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public static void BringToFrontWithoutActivating(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }
}
