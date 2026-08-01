using System.Runtime.InteropServices;
using System.Windows;

namespace CaptureCenter.Interop;

/// <summary>Wraps GetCursorPos -- used to detect when the mouse has moved away from a window that hid itself on hover (a hidden window can't raise its own MouseLeave).</summary>
public static class CursorPos
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    public static Point Get()
    {
        GetCursorPos(out POINT p);
        return new Point(p.X, p.Y);
    }
}
