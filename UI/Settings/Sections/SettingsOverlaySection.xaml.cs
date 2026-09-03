using System.Windows;
using System.Windows.Controls;

namespace Backtrack.UI.Settings.Sections;

public partial class SettingsOverlaySection : UserControl
{
    public SettingsOverlaySection()
    {
        InitializeComponent();
    }

    private MainWindow? Main => MainWindow.Instance ?? Window.GetWindow(this) as MainWindow;

    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e) => Main?.HotkeyCaptureButton_Click(sender, e);
    private void CancelRecordHotkeyCaptureButton_Click(object sender, RoutedEventArgs e) => Main?.CancelRecordHotkeyCaptureButton_Click(sender, e);
    private void BookmarkHotkeyCaptureButton_Click(object sender, RoutedEventArgs e) => Main?.BookmarkHotkeyCaptureButton_Click(sender, e);
    private void ShowRecentClipsToggle_Click(object sender, RoutedEventArgs e) => Main?.ShowRecentClipsToggle_Click(sender, e);
    private void ShowStatusIndicatorToggle_Click(object sender, RoutedEventArgs e) => Main?.ShowStatusIndicatorToggle_Click(sender, e);
    private void StatusIndicatorOrientationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => Main?.StatusIndicatorOrientationSelector_SelectionChanged(sender, e);
    private void StatusIndicatorLocationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => Main?.StatusIndicatorLocationSelector_SelectionChanged(sender, e);
    private void StatusIndicatorPreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e) => Main?.StatusIndicatorPreviewBorder_SizeChanged(sender, e);
}
