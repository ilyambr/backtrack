using System.Windows;
using System.Windows.Controls;

namespace Backtrack.UI.Settings.Sections;

public partial class SettingsObsSection : UserControl
{
    public SettingsObsSection()
    {
        InitializeComponent();
    }

    private MainWindow? Main => MainWindow.Instance ?? Window.GetWindow(this) as MainWindow;

    private void ObsRemoteToggle_Click(object sender, RoutedEventArgs e) => Main?.ObsRemoteToggle_Click(sender, e);
    private void ApplyObsConnection_Click(object sender, RoutedEventArgs e) => Main?.ApplyObsConnection_Click(sender, e);
    private void CheckRemotePluginsButton_Click(object sender, RoutedEventArgs e) => Main?.CheckRemotePluginsButton_Click(sender, e);
    private void ShareClipsToggle_Click(object sender, RoutedEventArgs e) => Main?.ShareClipsToggle_Click(sender, e);
    private void DeauthorizeButton_Click(object sender, RoutedEventArgs e) => Main?.DeauthorizeButton_Click(sender, e);
    private void UnpairButton_Click(object sender, RoutedEventArgs e) => Main?.UnpairButton_Click(sender, e);
    private void ManualPairButton_Click(object sender, RoutedEventArgs e) => Main?.ManualPairButton_Click(sender, e);
    private void RefreshRemoteThumbnailsButton_Click(object sender, RoutedEventArgs e) => Main?.RefreshRemoteThumbnailsButton_Click(sender, e);
}
