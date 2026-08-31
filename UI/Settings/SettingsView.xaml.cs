using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Backtrack.UI.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private MainWindow? Main => MainWindow.Instance ?? Window.GetWindow(this) as MainWindow;

    private void BackToIdle_Click(object sender, RoutedEventArgs e) => Main?.BackToIdle_Click(sender, e);
    private void DisplaySelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => Main?.DisplaySelector_SelectionChanged(sender, e);
    private void ThemeSwatchesScroll_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Main?.ThemeSwatchesScroll_PreviewMouseLeftButtonDown(sender, e);
    private void ThemeSwatchesScroll_PreviewMouseMove(object sender, MouseEventArgs e) => Main?.ThemeSwatchesScroll_PreviewMouseMove(sender, e);
    private void ThemeSwatchesScroll_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Main?.ThemeSwatchesScroll_PreviewMouseLeftButtonUp(sender, e);
    private void ThemeSwatchesScroll_PreviewMouseLeave(object sender, MouseEventArgs e) => Main?.ThemeSwatchesScroll_PreviewMouseLeave(sender, e);
    private void OpenThemesFolderButton_Click(object sender, RoutedEventArgs e) => Main?.OpenThemesFolderButton_Click(sender, e);
    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => Main?.CheckUpdatesButton_Click(sender, e);
    private void DisableBacktrackAutoUpdateToggle_Click(object sender, RoutedEventArgs e) => Main?.DisableBacktrackAutoUpdateToggle_Click(sender, e);
    private void DisablePluginAutoUpdateToggle_Click(object sender, RoutedEventArgs e) => Main?.DisablePluginAutoUpdateToggle_Click(sender, e);
    private void LaunchWithWindowsToggle_Click(object sender, RoutedEventArgs e) => Main?.LaunchWithWindowsToggle_Click(sender, e);
    private void ShowDisclaimerToggle_Click(object sender, RoutedEventArgs e) => Main?.ShowDisclaimerToggle_Click(sender, e);
    private void DisableAudioCuesToggle_Click(object sender, RoutedEventArgs e) => Main?.DisableAudioCuesToggle_Click(sender, e);
    private void DisableAudioCuesRow_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => Main?.DisableAudioCuesRow_MouseRightButtonUp(sender, e);
    private void AudioCueVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Main?.AudioCueVolumeSlider_ValueChanged(sender, e);
    private void DefaultAudioTrackSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => Main?.DefaultAudioTrackSelector_SelectionChanged(sender, e);
    private void ChangeClipsFolder_Click(object sender, RoutedEventArgs e) => Main?.ChangeClipsFolder_Click(sender, e);
    private void StorageLimitToggle_Click(object sender, RoutedEventArgs e) => Main?.StorageLimitToggle_Click(sender, e);
    private void ApplyStorageLimit_Click(object sender, RoutedEventArgs e) => Main?.ApplyStorageLimit_Click(sender, e);
    private void AutoDeleteOldClipsToggle_Click(object sender, RoutedEventArgs e) => Main?.AutoDeleteOldClipsToggle_Click(sender, e);
    private void ApplyAutoDeleteOldClips_Click(object sender, RoutedEventArgs e) => Main?.ApplyAutoDeleteOldClips_Click(sender, e);
    private void BufferDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Main?.BufferDurationSlider_ValueChanged(sender, e);
    private void ApplyBufferDuration_Click(object sender, RoutedEventArgs e) => Main?.ApplyBufferDuration_Click(sender, e);
    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e) => Main?.HotkeyCaptureButton_Click(sender, e);
    private void CancelRecordHotkeyCaptureButton_Click(object sender, RoutedEventArgs e) => Main?.CancelRecordHotkeyCaptureButton_Click(sender, e);
    private void BookmarkHotkeyCaptureButton_Click(object sender, RoutedEventArgs e) => Main?.BookmarkHotkeyCaptureButton_Click(sender, e);
    private void ShowRecentClipsToggle_Click(object sender, RoutedEventArgs e) => Main?.ShowRecentClipsToggle_Click(sender, e);
    private void ShowStatusIndicatorToggle_Click(object sender, RoutedEventArgs e) => Main?.ShowStatusIndicatorToggle_Click(sender, e);
    private void StatusIndicatorOrientationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => Main?.StatusIndicatorOrientationSelector_SelectionChanged(sender, e);
    private void StatusIndicatorLocationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => Main?.StatusIndicatorLocationSelector_SelectionChanged(sender, e);
    private void StatusIndicatorPreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e) => Main?.StatusIndicatorPreviewBorder_SizeChanged(sender, e);
    private void ObsRemoteToggle_Click(object sender, RoutedEventArgs e) => Main?.ObsRemoteToggle_Click(sender, e);
    private void ApplyObsConnection_Click(object sender, RoutedEventArgs e) => Main?.ApplyObsConnection_Click(sender, e);
    private void CheckRemotePluginsButton_Click(object sender, RoutedEventArgs e) => Main?.CheckRemotePluginsButton_Click(sender, e);
    private void ShareClipsToggle_Click(object sender, RoutedEventArgs e) => Main?.ShareClipsToggle_Click(sender, e);
    private void DeauthorizeButton_Click(object sender, RoutedEventArgs e) => Main?.DeauthorizeButton_Click(sender, e);
    private void UnpairButton_Click(object sender, RoutedEventArgs e) => Main?.UnpairButton_Click(sender, e);
    private void ManualPairButton_Click(object sender, RoutedEventArgs e) => Main?.ManualPairButton_Click(sender, e);
    private void RefreshRemoteThumbnailsButton_Click(object sender, RoutedEventArgs e) => Main?.RefreshRemoteThumbnailsButton_Click(sender, e);
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
    private void ClearSettingsCacheButton_Click(object sender, RoutedEventArgs e) => Main?.ClearSettingsCacheButton_Click(sender, e);
    private void ClearClipsDirectoryButton_Click(object sender, RoutedEventArgs e) => Main?.ClearClipsDirectoryButton_Click(sender, e);
    private void UninstallBacktrackButton_Click(object sender, RoutedEventArgs e) => Main?.UninstallBacktrackButton_Click(sender, e);
    private void UninstallSourceRecordButton_Click(object sender, RoutedEventArgs e) => Main?.UninstallSourceRecordButton_Click(sender, e);
    private void UninstallReplaySliderButton_Click(object sender, RoutedEventArgs e) => Main?.UninstallReplaySliderButton_Click(sender, e);
    private void QuitApp_Click(object sender, RoutedEventArgs e) => Main?.QuitApp_Click(sender, e);
    private void SettingsScrollHost_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Main?.SettingsScrollHost_PreviewMouseDown(sender, e);
    private void SettingsScrollHost_PreviewMouseUp(object sender, MouseButtonEventArgs e) => Main?.SettingsScrollHost_PreviewMouseUp(sender, e);
    private void ExperimentalHeader_Click(object sender, MouseButtonEventArgs e) => Main?.ExperimentalHeader_Click(sender, e);
    private void DestructiveHeader_Click(object sender, MouseButtonEventArgs e) => Main?.DestructiveHeader_Click(sender, e);
}
