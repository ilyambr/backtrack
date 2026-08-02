using System;
using System.Runtime.InteropServices;

namespace Backtrack.Interop;

/// <summary>
/// Real blur-behind (Windows' undocumented-but-widely-used SetWindowCompositionAttribute),
/// so the panel actually looks like a translucent dimmed sheet over the desktop/game
/// instead of a flat, non-blurred rectangle. Best-effort: if it fails on some
/// system/driver combination, the window still has its plain semi-transparent
/// background as a fallback -- this never throws out to the caller.
/// </summary>
public static class Acrylic
{
    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int WCA_ACCENT_POLICY = 19;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>
    /// Real acrylic blur-behind needs two things together: telling DWM the whole
    /// client area is "glass" (so a plain WPF Background="Transparent" -- with
    /// AllowsTransparency left OFF -- actually shows DWM's composited blur
    /// through it instead of rendering opaque black), then the accent policy
    /// itself. AllowsTransparency="True" uses GDI layered windows instead of DWM
    /// composition and silently defeats this -- the window must not use it.
    /// </summary>
    public static void TryEnableBlurBehind(IntPtr hwnd, byte r, byte g, byte b, byte a)
    {
        try
        {
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                GradientColor = (a << 24) | (b << 16) | (g << 8) | r,
            };

            int size = Marshal.SizeOf(accent);
            IntPtr accentPtr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    SizeOfData = size,
                    Data = accentPtr,
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }
        catch
        {
            // Best effort only -- the flat semi-transparent Border is the fallback.
        }
    }
}
