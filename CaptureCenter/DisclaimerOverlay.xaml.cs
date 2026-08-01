using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace CaptureCenter;

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
        SizeChanged += (_, _) =>
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = SystemParameters.PrimaryScreenHeight - ActualHeight - 14;
        };
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
