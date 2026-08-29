using System.Runtime.InteropServices;

namespace Backtrack;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = false)]
    internal static extern bool GetCursorPos(out POINT lpPoint);
}
