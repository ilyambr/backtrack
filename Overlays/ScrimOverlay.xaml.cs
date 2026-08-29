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
    public event Action? DragEntered;
    public event Action<Point>? DragHovered;

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
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    public void Reposition()
    {
        Rect bounds = DisplayMonitors.ResolveBoundsDiu(AppSettings.Load().DisplayDeviceName);
        Left = bounds.X;
        Top = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private DateTime _ignoreClicksUntilUtc = DateTime.MinValue;

    public void ArmDismissCooldown(int ms = 400)
    {
        _ignoreClicksUntilUtc = DateTime.UtcNow.AddMilliseconds(ms);
    }

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

    private void Scrim_DragEnter(object sender, DragEventArgs e)
    {
        DragEntered?.Invoke();
        if (NativeMethods.GetCursorPos(out var pt))
            DragHovered?.Invoke(new Point(pt.X, pt.Y));
    }

    private void Scrim_DragOver(object sender, DragEventArgs e)
    {
        DragEntered?.Invoke();
        if (NativeMethods.GetCursorPos(out var pt))
            DragHovered?.Invoke(new Point(pt.X, pt.Y));
    }

    public void SetExitButtonVisible(bool visible) =>
        ExitButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
