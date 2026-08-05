using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Navigation;
using Backtrack.Interop;

namespace Backtrack;

public partial class DisclaimerOverlay : Window
{
    public DisclaimerOverlay()
    {
        InitializeComponent();

        System.Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        string versionText = version is null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
        VersionRun.Text = $"Backtrack v{versionText} Alpha";

        // SizeChanged, not Loaded -- a window that starts Visibility="Hidden" can
        // fire Loaded before its first real layout pass finishes, leaving
        // ActualWidth/Height at 0 and positioning this off-screen. SizeChanged
        // fires again once the real (wrapped-text) size is known, self-correcting.
        SizeChanged += (_, _) => Reposition();
        Loaded += (_, _) => ToolWindow.Enable(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Re-reads the configured display -- called again from Settings if the user changes which monitor Backtrack shows on mid-session.</summary>
    public void Reposition()
    {
        Rect bounds = DisplayMonitors.ResolveBoundsDiu(AppSettings.Load().DisplayDeviceName);
        Left = bounds.X + (bounds.Width - ActualWidth) / 2;
        Top = bounds.Y + bounds.Height - ActualHeight - 14;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
