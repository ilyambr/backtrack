using System;
using System.Runtime.InteropServices;

namespace CaptureCenter.Interop;

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

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    public static void TryEnableBlurBehind(IntPtr hwnd, byte r, byte g, byte b, byte a)
    {
        try
        {
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
