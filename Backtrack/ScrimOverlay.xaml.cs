using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Backtrack.Interop;

namespace Backtrack;

public partial class ScrimOverlay : Window
{
    public event Action? Dismissed;

    public ScrimOverlay()
    {
        InitializeComponent();
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Loaded += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            ToolWindow.Enable(hwnd);
            NoActivate.Enable(hwnd);
        };
    }

    // Left-click only, by design -- right-click should do nothing at all, not
    // dismiss. NoActivate.Enable (see constructor) is what actually stops
    // right-click from breaking the z-order instead: it suppresses the OS-level
    // window activation a click would otherwise trigger as a side effect, which
    // is what was jumping this window in front of MainWindow. WPF's own mouse
    // events aren't affected by that flag, so this handler still fires normally.
    private void Scrim_MouseDown(object sender, MouseButtonEventArgs e) => Dismissed?.Invoke();

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke();
}
