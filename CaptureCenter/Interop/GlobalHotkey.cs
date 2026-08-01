using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CaptureCenter.Interop;

/// <summary>Registers a real OS-level hotkey (RegisterHotKey/WM_HOTKEY), not a Chromium accelerator.</summary>
public sealed class GlobalHotkey : IDisposable
{
    [Flags]
    public enum Modifiers : uint
    {
        Alt = 0x1,
        Control = 0x2,
        Shift = 0x4,
        Win = 0x8,
    }

    private const int WM_HOTKEY = 0x0312;
    private readonly int _id;
    private readonly HwndSource _source;
    private Modifiers _modifiers;
    private uint _virtualKey;

    public event Action? Pressed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public GlobalHotkey(Window window, Modifiers modifiers, uint virtualKey, int id = 0x9001)
    {
        _id = id;
        _modifiers = modifiers;
        _virtualKey = virtualKey;
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Window has no HWND yet");
        _source.AddHook(HwndHook);

        if (!RegisterHotKey(handle, _id, (uint)modifiers, virtualKey))
            throw new InvalidOperationException("Could not register the global hotkey -- something else on this PC is already using it.");
    }

    /// <summary>Switches to a different key combo without needing to recreate the window hook -- restores the previous combo if the new one is already taken elsewhere, rather than leaving no hotkey registered at all.</summary>
    public void Rebind(Modifiers modifiers, uint virtualKey)
    {
        UnregisterHotKey(_source.Handle, _id);
        if (!RegisterHotKey(_source.Handle, _id, (uint)modifiers, virtualKey))
        {
            RegisterHotKey(_source.Handle, _id, (uint)_modifiers, _virtualKey);
            throw new InvalidOperationException("Could not register that hotkey -- something else on this PC is already using it.");
        }

        _modifiers = modifiers;
        _virtualKey = virtualKey;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_source.Handle, _id);
        _source.RemoveHook(HwndHook);
    }
}
