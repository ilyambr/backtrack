using System.Windows;
using System.Windows.Interop;
using CaptureCenter.Interop;

namespace CaptureCenter;

public partial class StatusOverlay : Window
{
    public StatusOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Left = SystemParameters.PrimaryScreenWidth - Width - 4;
            Top = SystemParameters.PrimaryScreenHeight - Height - 4;
            ClickThrough.Enable(new WindowInteropHelper(this).Handle);
        };
    }

    public void SetRecording(bool active) => RecBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

    public void SetReplayOnline(bool active) => ReplayBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
}
