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

        SizeChanged += (_, _) => Reposition();
        Loaded += (_, _) => ToolWindow.Enable(new WindowInteropHelper(this).Handle);
    }

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
