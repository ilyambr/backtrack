using System.Runtime.InteropServices;
using System.Windows;

namespace Backtrack.Interop;

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
