using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Backtrack.UI.Settings.Sections;

public partial class SettingsAdvancedSection : UserControl
{
    public SettingsAdvancedSection()
    {
        InitializeComponent();
    }

    private MainWindow? Main => MainWindow.Instance ?? Window.GetWindow(this) as MainWindow;

    private void ExperimentalHeader_Click(object sender, MouseButtonEventArgs e) => Main?.ExperimentalHeader_Click(sender, e);
    private void RamDiskToggle_Click(object sender, RoutedEventArgs e) => Main?.RamDiskToggle_Click(sender, e);
    private void SuggestRamDiskSize_Click(object sender, RoutedEventArgs e) => Main?.SuggestRamDiskSize_Click(sender, e);
    private void ApplyRamDiskSettings_Click(object sender, RoutedEventArgs e) => Main?.ApplyRamDiskSettings_Click(sender, e);
    private void ApplyRemoteRamDiskSettings_Click(object sender, RoutedEventArgs e) => Main?.ApplyRemoteRamDiskSettings_Click(sender, e);
    private void OverlayLogToggle_Click(object sender, RoutedEventArgs e) => Main?.OverlayLogToggle_Click(sender, e);
    private void OverlayLogModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => Main?.OverlayLogModeSelector_SelectionChanged(sender, e);
    private void EnableAnimationsToggle_Click(object sender, RoutedEventArgs e) => Main?.EnableAnimationsToggle_Click(sender, e);
    private void DiagnosticLogToggle_Click(object sender, RoutedEventArgs e) => Main?.DiagnosticLogToggle_Click(sender, e);
    private void OpenDiagnosticLogButton_Click(object sender, RoutedEventArgs e) => Main?.OpenDiagnosticLogButton_Click(sender, e);
    private void DeveloperModeToggle_Click(object sender, RoutedEventArgs e) => Main?.DeveloperModeToggle_Click(sender, e);
    private void DisableHardwareAccelToggle_Click(object sender, RoutedEventArgs e) => Main?.DisableHardwareAccelToggle_Click(sender, e);
    private void DestructiveHeader_Click(object sender, MouseButtonEventArgs e) => Main?.DestructiveHeader_Click(sender, e);
    private void ClearSettingsCacheButton_Click(object sender, RoutedEventArgs e) => Main?.ClearSettingsCacheButton_Click(sender, e);
    private void ClearClipsDirectoryButton_Click(object sender, RoutedEventArgs e) => Main?.ClearClipsDirectoryButton_Click(sender, e);
    private void UninstallBacktrackButton_Click(object sender, RoutedEventArgs e) => Main?.UninstallBacktrackButton_Click(sender, e);
    private void UninstallSourceRecordButton_Click(object sender, RoutedEventArgs e) => Main?.UninstallSourceRecordButton_Click(sender, e);
    private void UninstallReplaySliderButton_Click(object sender, RoutedEventArgs e) => Main?.UninstallReplaySliderButton_Click(sender, e);
}
